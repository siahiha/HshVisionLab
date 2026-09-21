using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace HshDetectionEngin.Capture;

/// <summary>
/// Runtime options for the single MediaMTX instance shared by all cameras in
/// one application. Environment variables are deliberately used here so the
/// WinForms app and the Windows service can use the same deployment settings.
/// </summary>
public sealed class MediaMtxOptions
{
    public string ExecutablePath { get; init; } = string.Empty;
    public string ConfigPath { get; init; } = string.Empty;
    public int RtspPort { get; init; } = 8554;
    public int WebRtcPort { get; init; } = 8889;
    public int WebRtcUdpPort { get; init; } = 8189;
    public int ControlPort { get; init; } = 9997;
    public string AdditionalHosts { get; init; } = string.Empty;

    public Uri ControlBaseUri => new($"http://127.0.0.1:{ControlPort}/");
    public Uri WebRtcBaseUri => new($"http://127.0.0.1:{WebRtcPort}/");

    public static MediaMtxOptions FromEnvironment()
    {
        return new MediaMtxOptions
        {
            ExecutablePath = Environment.GetEnvironmentVariable("HSH_MEDIAMTX_PATH") ?? string.Empty,
            ConfigPath = Environment.GetEnvironmentVariable("HSH_MEDIAMTX_CONFIG") ?? string.Empty,
            RtspPort = ReadPort("HSH_MEDIAMTX_RTSP_PORT", 8554),
            WebRtcPort = ReadPort("HSH_MEDIAMTX_WEBRTC_PORT", 8889),
            WebRtcUdpPort = ReadPort("HSH_MEDIAMTX_WEBRTC_UDP_PORT", 8189),
            ControlPort = ReadPort("HSH_MEDIAMTX_API_PORT", 9997),
            AdditionalHosts = Environment.GetEnvironmentVariable("HSH_MEDIAMTX_ADDITIONAL_HOSTS") ?? string.Empty
        };
    }

    private static int ReadPort(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value is > 0 and <= 65535
            ? value
            : fallback;
}

/// <summary>
/// Owns or reuses one MediaMTX process and manages camera paths through its
/// Control API. The upstream camera is never opened by this class more than
/// once per path; additional viewers consume the same MediaMTX path.
/// </summary>
public sealed class MediaMtxRuntime : IDisposable
{
    private static readonly Lazy<MediaMtxRuntime> SharedHolder = new(() => new MediaMtxRuntime(MediaMtxOptions.FromEnvironment()));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _httpClient;
    private readonly MediaMtxOptions _options;
    private Process? _process;
    private bool _ownsProcess;
    private bool _started;
    private bool _rtspReady;
    private bool _disposed;

    public MediaMtxRuntime(MediaMtxOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = new HttpClient { BaseAddress = options.ControlBaseUri, Timeout = TimeSpan.FromSeconds(5) };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public static MediaMtxRuntime Shared => SharedHolder.Value;
    public MediaMtxOptions Options => _options;
    public bool IsRunning => _started && (_process is null || !_process.HasExited);

    public string GetPathName(string cameraId)
    {
        if (string.IsNullOrWhiteSpace(cameraId)) throw new ArgumentException("Camera id is required.", nameof(cameraId));
        var builder = new StringBuilder("camera-");
        foreach (char character in cameraId.Trim())
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? char.ToLowerInvariant(character) : '-');
        return builder.ToString().TrimEnd('-');
    }

    public Uri GetLocalRtspUri(string cameraId)
        => new($"rtsp://127.0.0.1:{_options.RtspPort}/{GetPathName(cameraId)}");

    public Uri GetWhepUri(string cameraId)
        => new(_options.WebRtcBaseUri, $"{GetPathName(cameraId)}/whep");

    public async Task EnsurePathAsync(string cameraId, string sourceUrl, string transport, CancellationToken cancellationToken = default, bool requireRtsp = true)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)) throw new ArgumentException("RTSP source URL is required.", nameof(sourceUrl));
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? source) || !string.Equals(source.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("MediaMTX requires a valid RTSP source URL.", nameof(sourceUrl));

        await EnsureStartedAsync(cancellationToken, requireRtsp).ConfigureAwait(false);
        string path = GetPathName(cameraId);
        var payload = new Dictionary<string, object?>
        {
            ["source"] = sourceUrl,
            ["sourceOnDemand"] = true,
            ["sourceOnDemandStartTimeout"] = "10s",
            ["sourceOnDemandCloseAfter"] = "2s",
            // TCP is intentional: it avoids UDP packet loss and is the lowest
            // jitter option for the RTSP hop into the gateway.
            ["rtspTransport"] = "tcp"
        };

        HttpResponseMessage response = await SendJsonAsync(HttpMethod.Patch, $"v3/config/paths/patch/{Uri.EscapeDataString(path)}", payload, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            response = await SendJsonAsync(HttpMethod.Post, $"v3/config/paths/add/{Uri.EscapeDataString(path)}", payload, cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"MediaMTX path '{path}' failed ({(int)response.StatusCode}): {await ReadErrorAsync(response).ConfigureAwait(false)}");
        }
    }

    public async Task RemovePathAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        if (!_started) return;
        string path = GetPathName(cameraId);
        using HttpResponseMessage response = await _httpClient.DeleteAsync($"v3/config/paths/delete/{Uri.EscapeDataString(path)}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not HttpStatusCode.NotFound && !response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MediaMTX path '{path}' could not be removed ({(int)response.StatusCode}).");
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default, bool requireRtsp = true)
    {
        ThrowIfDisposed();
        if (IsRunning && (!requireRtsp || _rtspReady)) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsRunning && (!requireRtsp || _rtspReady)) return;

            // A service and the desktop application may be running together.
            // Reuse an already listening MediaMTX instead of starting a second
            // process on the same ports.
            MediaMtxProbe existing = await ProbeControlApiAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Responding)
            {
                if (requireRtsp && !existing.RtspReady)
                    existing = await TryEnableRtspAsync(existing, cancellationToken).ConfigureAwait(false) ?? existing;
                if (!existing.WebRtcReady || (requireRtsp && !existing.RtspReady))
                    throw new InvalidOperationException(existing.ErrorMessage);
                _started = true;
                _ownsProcess = false;
                _rtspReady = existing.RtspReady;
                return;
            }

            string executable = ResolveExecutablePath();
            string config = ResolveConfigPath();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(config)!);
                // An explicitly supplied config belongs to the deployment and
                // may contain network/security choices. Only generate the
                // managed config when no custom file was supplied or it does
                // not exist.
                if (string.IsNullOrWhiteSpace(_options.ConfigPath) || !File.Exists(config))
                    File.WriteAllText(config, BuildConfig());
            }
            catch when (string.IsNullOrWhiteSpace(_options.ConfigPath))
            {
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HshVision", "MediaMTX", "mediamtx-managed.yml");
                Directory.CreateDirectory(Path.GetDirectoryName(config)!);
                File.WriteAllText(config, BuildConfig());
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add(config);
            _process = Process.Start(startInfo) ?? throw new InvalidOperationException("MediaMTX process could not be started.");
            _process.Exited += (_, _) => _started = false;
            _process.EnableRaisingEvents = true;
            _ownsProcess = true;

            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MediaMtxProbe started = await ProbeControlApiAsync(cancellationToken).ConfigureAwait(false);
                if (started.Responding && started.WebRtcReady && (!requireRtsp || started.RtspReady))
                {
                    _started = true;
                    _rtspReady = started.RtspReady;
                    return;
                }

                if (_process.HasExited)
                    throw new InvalidOperationException($"MediaMTX exited with code {_process.ExitCode}. {await ReadProcessErrorAsync(_process).ConfigureAwait(false)}");
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("MediaMTX Control API did not become ready within 8 seconds.");
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _started = false;
            _rtspReady = false;
            if (_ownsProcess && _process is { HasExited: false })
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
            }
            _process?.Dispose();
            _process = null;
            _ownsProcess = false;
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        _httpClient.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<MediaMtxProbe> ProbeControlApiAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync("v3/config/global/get", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new MediaMtxProbe(false, false, false, 0, 0, $"MediaMTX Control API returned {(int)response.StatusCode} for its global configuration.");

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            JsonElement root = document.RootElement;
            bool webRtc = root.TryGetProperty("webrtc", out JsonElement webRtcElement) && webRtcElement.ValueKind == JsonValueKind.True;
            bool rtsp = root.TryGetProperty("rtsp", out JsonElement rtspElement) && rtspElement.ValueKind == JsonValueKind.True;
            int rtspPort = ReadAddressPort(root, "rtspAddress");
            int webRtcPort = ReadAddressPort(root, "webrtcAddress");
            bool rtspReady = rtsp && (rtspPort <= 0 || rtspPort == _options.RtspPort) &&
                             await IsTcpListenerReadyAsync(rtspPort <= 0 ? _options.RtspPort : rtspPort, cancellationToken).ConfigureAwait(false);
            bool webRtcReady = webRtc && (webRtcPort <= 0 || webRtcPort == _options.WebRtcPort) &&
                               await IsTcpListenerReadyAsync(webRtcPort <= 0 ? _options.WebRtcPort : webRtcPort, cancellationToken).ConfigureAwait(false);
            string message = $"MediaMTX is already running, but its listeners do not match HshVision. " +
                             $"RTSP enabled={rtsp}, RTSP port={rtspPort}, expected={_options.RtspPort}; " +
                             $"WebRTC enabled={webRtc}, WebRTC port={webRtcPort}, expected={_options.WebRtcPort}. " +
                             "Stop the old mediamtx.exe or configure the HshVision MediaMTX ports to match it.";
            return new MediaMtxProbe(true, rtspReady, webRtcReady, rtspPort, webRtcPort, message);
        }
        catch (HttpRequestException) { return new MediaMtxProbe(false, false, false, 0, 0, string.Empty); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return new MediaMtxProbe(false, false, false, 0, 0, string.Empty); }
    }

    private async Task<MediaMtxProbe?> TryEnableRtspAsync(MediaMtxProbe probe, CancellationToken cancellationToken)
    {
        // An older instance may have API/WebRTC enabled but RTSP disabled.
        // MediaMTX supports hot-patching global configuration, so repair this
        // compatible case instead of silently sending the local reader into a
        // reconnect loop. Never change a server that uses another RTSP port.
        if (probe.RtspReady || probe.RtspPort != _options.RtspPort) return probe;
        var payload = new { rtsp = true, rtspTransports = new[] { "tcp" } };
        using HttpResponseMessage response = await SendJsonAsync(HttpMethod.Patch, "v3/config/global/patch", payload, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return probe;

        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            MediaMtxProbe updated = await ProbeControlApiAsync(cancellationToken).ConfigureAwait(false);
            if (updated.Responding && updated.RtspReady) return updated;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return probe;
    }

    private static int ReadAddressPort(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            return 0;
        string address = value.GetString() ?? string.Empty;
        int separator = address.LastIndexOf(':');
        return separator >= 0 && int.TryParse(address[(separator + 1)..], out int port) ? port : 0;
    }

    private static async Task<bool> IsTcpListenerReadyAsync(int port, CancellationToken cancellationToken)
    {
        if (port is <= 0 or > 65535) return false;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).AsTask().WaitAsync(TimeSpan.FromMilliseconds(350), cancellationToken).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
        catch (TimeoutException) { return false; }
    }

    private sealed record MediaMtxProbe(bool Responding, bool RtspReady, bool WebRtcReady, int RtspPort, int WebRtcPort, string ErrorMessage);

    private async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        try { return (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim(); }
        catch { return response.ReasonPhrase ?? "unknown error"; }
    }

    private static async Task<string> ReadProcessErrorAsync(Process process)
    {
        try { return (await process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false)).Trim(); }
        catch { return string.Empty; }
    }

    private string ResolveExecutablePath()
    {
        IEnumerable<string> candidates =
        [
            _options.ExecutablePath,
            Path.Combine(AppContext.BaseDirectory, "mediamtx.exe"),
            Path.Combine(AppContext.BaseDirectory, "MediaMTX", "mediamtx.exe"),
            Path.Combine(AppContext.BaseDirectory, "third_party", "mediamtx", "mediamtx.exe"),
            Path.Combine(Environment.CurrentDirectory, "mediamtx.exe")
        ];
        string? path = candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
        return path ?? throw new FileNotFoundException("MediaMTX executable was not found. Set HSH_MEDIAMTX_PATH or place mediamtx.exe beside the application.");
    }

    private string ResolveConfigPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.ConfigPath)) return Path.GetFullPath(_options.ConfigPath);
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HshVision", "MediaMTX");
        return Path.Combine(root, "mediamtx-managed.yml");
    }

    private string BuildConfig()
    {
        var hosts = new List<string> { "127.0.0.1", "localhost" };
        hosts.AddRange(_options.AdditionalHosts.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        string hostList = string.Join(", ", hosts.Distinct(StringComparer.OrdinalIgnoreCase).Select(host => $"'{host.Replace("'", "")}'"));
        return string.Join(Environment.NewLine,
        [
            "# Generated by HshVision. Camera paths are managed through the Control API.",
            "logLevel: warn",
            "api: true",
            $"apiAddress: :{_options.ControlPort}",
            "rtsp: true",
            $"rtspAddress: :{_options.RtspPort}",
            "webrtc: true",
            $"webrtcAddress: :{_options.WebRtcPort}",
            $"webrtcLocalUDPAddress: :{_options.WebRtcUdpPort}",
            "webrtcLocalTCPAddress: ''",
            "webrtcAllowOrigins: ['*']",
            $"webrtcAdditionalHosts: [{hostList}]",
            "pathDefaults:",
            "  sourceOnDemand: true",
            "  sourceOnDemandStartTimeout: 10s",
            "  sourceOnDemandCloseAfter: 2s",
            "  rtspTransport: tcp",
            "paths: {}"
        ]) + Environment.NewLine;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MediaMtxRuntime));
    }
}
