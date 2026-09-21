using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using HshDetectionEngin;
using HshDetectionEngin.Capture;

namespace HshDetectionService;

/// <summary>
/// Proxies browser WHEP requests through the service so the browser never
/// receives a private RTSP URL or needs direct access to the MediaMTX port.
/// A session is keyed by cameraId + viewerId, which makes reconnects explicit
/// and prevents one viewer from closing another viewer's peer connection.
/// </summary>
public sealed class MediaMtxWebRtcProxy : IDisposable
{
    private readonly MediaMtxRuntime _runtime;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ConcurrentDictionary<string, WhepSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public MediaMtxWebRtcProxy(MediaMtxRuntime runtime) => _runtime = runtime;

    public async Task HandleAsync(HttpContext context, string cameraId, string viewerId, ServiceSettingsStore settings, CancellationToken cancellationToken)
    {
        SetCors(context);
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (string.IsNullOrWhiteSpace(cameraId) || string.IsNullOrWhiteSpace(viewerId) || cameraId.Length > 128 || viewerId.Length > 128)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "cameraId and viewerId are required.").ConfigureAwait(false);
            return;
        }

        CameraSettings? camera = settings.Detection.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
        if (camera is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "Camera was not found.").ConfigureAwait(false);
            return;
        }

        if (!string.Equals(camera.CaptureBackend, "MediaMTX", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context, StatusCodes.Status409Conflict, "WHEP is available only when the camera receiver is MediaMTX.").ConfigureAwait(false);
            return;
        }

        string key = $"{cameraId}:{viewerId}";
        try
        {
            switch (context.Request.Method.ToUpperInvariant())
            {
                case "POST":
                    await PostAsync(context, camera, key, viewerId, cancellationToken).ConfigureAwait(false);
                    break;
                case "PATCH":
                    await PatchAsync(context, key, cancellationToken).ConfigureAwait(false);
                    break;
                case "DELETE":
                    await DeleteAsync(context, key, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    context.Response.Headers.Allow = "OPTIONS, POST, PATCH, DELETE";
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        }
        catch (Exception error)
        {
            await WriteErrorAsync(context, StatusCodes.Status502BadGateway, error.Message).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        foreach (WhepSession session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        _httpClient.Dispose();
    }

    private async Task PostAsync(HttpContext context, CameraSettings camera, string key, string viewerId, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength == 0)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "An SDP offer is required.").ConfigureAwait(false);
            return;
        }

        await _runtime.EnsurePathAsync(camera.Id, camera.SourceUrl, camera.Transport, cancellationToken, requireRtsp: false).ConfigureAwait(false);
        if (_sessions.TryRemove(key, out WhepSession? previous))
        {
            try { await SendWithoutBodyAsync(HttpMethod.Delete, previous.Location, cancellationToken).ConfigureAwait(false); } catch { }
            previous.Dispose();
        }

        Uri endpoint = _runtime.GetWhepUri(camera.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StreamContent(context.Request.Body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/sdp");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sdp"));
        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await CopyResponseAsync(context, response, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        Uri location = response.Headers.Location is Uri returned
            ? (returned.IsAbsoluteUri ? returned : new Uri(endpoint, returned))
            : endpoint;
        _sessions[key] = new WhepSession(location);
        string publicLocation = $"/api/v1/streams/{Uri.EscapeDataString(camera.Id)}/webrtc/whep/{Uri.EscapeDataString(viewerId)}";
        await CopyResponseAsync(context, response, publicLocation, cancellationToken).ConfigureAwait(false);
    }

    private async Task PatchAsync(HttpContext context, string key, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(key, out WhepSession? session))
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "WHEP session was not found.").ConfigureAwait(false);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Patch, session.Location)
        {
            Content = new StreamContent(context.Request.Body)
        };
        if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await CopyResponseAsync(context, response, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteAsync(HttpContext context, string key, CancellationToken cancellationToken)
    {
        if (!_sessions.TryRemove(key, out WhepSession? session))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        try
        {
            using HttpResponseMessage response = await SendWithoutBodyAsync(HttpMethod.Delete, session.Location, cancellationToken).ConfigureAwait(false);
            await CopyResponseAsync(context, response, null, cancellationToken).ConfigureAwait(false);
        }
        finally { session.Dispose(); }
    }

    private async Task<HttpResponseMessage> SendWithoutBodyAsync(HttpMethod method, Uri location, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, location);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyResponseAsync(HttpContext context, HttpResponseMessage response, string? replacementLocation, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = (int)response.StatusCode;
        if (replacementLocation is not null) context.Response.Headers.Location = replacementLocation;
        else if (response.Headers.Location is not null) context.Response.Headers.Location = response.Headers.Location.ToString();
        if (response.Headers.TryGetValues("Link", out IEnumerable<string>? links)) context.Response.Headers.Link = string.Join(", ", links);
        if (response.Content.Headers.ContentType is MediaTypeHeaderValue contentType)
            context.Response.ContentType = contentType.ToString();
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > 0) await context.Response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsJsonAsync(new { error = message });
    }

    private static void SetCors(HttpContext context)
    {
        context.Response.Headers.AccessControlAllowOrigin = context.Request.Headers.Origin.Count > 0
            ? context.Request.Headers.Origin.ToString()
            : "*";
        context.Response.Headers.AccessControlAllowMethods = "OPTIONS, POST, PATCH, DELETE";
        context.Response.Headers.AccessControlAllowHeaders = "Content-Type, Accept, Authorization, X-Hsh-Api-Key";
        context.Response.Headers.AccessControlExposeHeaders = "Location, Link";
        context.Response.Headers.Vary = "Origin";
    }

    private sealed class WhepSession : IDisposable
    {
        public WhepSession(Uri location) => Location = location;
        public Uri Location { get; }
        public void Dispose() { }
    }
}
