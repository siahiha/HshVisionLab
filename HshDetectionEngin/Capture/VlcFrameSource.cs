using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Vlc.DotNet.Core;
using Vlc.DotNet.Core.Interops;
using Vlc.DotNet.Core.Interops.Signatures;

namespace HshDetectionEngin.Capture;

/// <summary>
/// RTSP frame source backed by libVLC/Live555. This is intended for cameras
/// whose RTSP stream is healthy in VLC but intermittently stalls in the
/// OpenCV FFmpeg plugin.
/// </summary>
public sealed class VlcFrameSource : IFrameSource
{
    private const int InitialFrameTimeoutMs = 15000;
    private const int FrameSilenceTimeoutMs = 15000;
    private const int ReadRetryDelayMs = 200;
    private const int StopJoinTimeoutMs = 8000;

    private readonly object _frameGate = new();
    private readonly object _lifecycleGate = new();
    private readonly object _videoGate = new();
    private readonly AutoResetEvent _frameReady = new(false);
    private readonly AutoResetEvent _wakeReader = new(false);
    private readonly Queue<Mat> _bufferedFrames = new();
    private readonly LockVideoCallback _lockVideoCallback;
    private readonly UnlockVideoCallback _unlockVideoCallback;
    private readonly DisplayVideoCallback _displayVideoCallback;
    private readonly VideoFormatCallback _videoFormatCallback;
    private readonly CleanupVideoCallback _cleanupVideoCallback;

    private Mat? _latestFrame;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _desiredRunning;
    private bool _disposed;
    private string _source = string.Empty;
    private string _transport = "TCP";
    private int _reconnectDelayMs = 3000;
    private int _bufferCapacity;
    private long _captured;
    private long _dropped;
    private long _frameSessionGeneration;
    private long _activeFrameSessionGeneration;
    private long _lastFrameTicks;
    private int _reconnectRequested;
    private string? _reconnectReason;

    // libVLC owns writes to this buffer between LockVideo and DisplayVideo.
    // The format callback is allowed to replace it when resolution changes.
    private IntPtr _pixelBuffer;
    private int _pixelWidth;
    private int _pixelHeight;
    private int _pixelPitch;

    public VlcFrameSource()
    {
        // Keep delegates strongly referenced for the complete native player
        // lifetime. Native callbacks otherwise become invalid after GC.
        _lockVideoCallback = LockVideo;
        _unlockVideoCallback = UnlockVideo;
        _displayVideoCallback = DisplayVideo;
        _videoFormatCallback = ConfigureVideo;
        _cleanupVideoCallback = CleanupVideo;
    }

    public event Action<string>? StateChanged;
    public event Action<Exception>? ErrorOccurred;
    public event Action<Mat>? FrameAvailable;

    public bool IsRunning { get; private set; }
    public int QueueCount
    {
        get
        {
            lock (_frameGate)
                return _bufferCapacity > 0 ? _bufferedFrames.Count : (_latestFrame is null ? 0 : 1);
        }
    }

    public long CapturedFrames => Interlocked.Read(ref _captured);
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public string Source => _source;

    public void Configure(string source, string transport, int bufferCount, int reconnectDelayMs)
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _source = source.Trim();
            _transport = string.Equals(transport, "UDP", StringComparison.OrdinalIgnoreCase) ? "UDP" : "TCP";
            _bufferCapacity = Math.Clamp(bufferCount, 0, 100);
            _reconnectDelayMs = Math.Clamp(reconnectDelayMs, 250, 60000);
        }
    }

    public void Start()
    {
        if (string.IsNullOrWhiteSpace(_source)) return;

        CancellationTokenSource? staleCts = null;
        bool previousReaderIsStopping = false;
        lock (_lifecycleGate)
        {
            if (_disposed || IsRunning) return;
            _desiredRunning = true;
            if (_thread is { IsAlive: true })
                previousReaderIsStopping = true;
            else
                staleCts = StartReaderLocked();
        }

        staleCts?.Dispose();
        if (previousReaderIsStopping)
            RaiseState("Previous LibVLC reader is still stopping; it will restart automatically.");
    }

    public void Stop()
    {
        Thread? reader;
        CancellationTokenSource? cts;
        lock (_lifecycleGate)
        {
            _desiredRunning = false;
            IsRunning = false;
            Interlocked.Increment(ref _frameSessionGeneration);
            Interlocked.Exchange(ref _activeFrameSessionGeneration, 0);
            reader = _thread;
            cts = _cts;
            try { cts?.Cancel(); } catch { }
        }

        try { _wakeReader.Set(); } catch (ObjectDisposedException) { }
        try { _frameReady.Set(); } catch (ObjectDisposedException) { }
        try
        {
            if (reader is not null && !ReferenceEquals(reader, Thread.CurrentThread))
                reader.Join(StopJoinTimeoutMs);
        }
        catch { }

        CancellationTokenSource? completedCts = null;
        lock (_lifecycleGate)
        {
            if (ReferenceEquals(_thread, reader) && (reader is null || !reader.IsAlive))
            {
                _thread = null;
                if (ReferenceEquals(_cts, cts))
                {
                    _cts = null;
                    completedCts = cts;
                }
            }
        }

        completedCts?.Dispose();
        DrainQueue();
        RaiseState("Disconnected");
    }

    public bool TryDequeueFrame(out Mat frame, int timeoutMs, CancellationToken externalCt)
    {
        frame = null!;
        if (externalCt.IsCancellationRequested || IsDisposed()) return false;

        long startedAt = Stopwatch.GetTimestamp();
        while (!externalCt.IsCancellationRequested)
        {
            lock (_frameGate)
            {
                if (_bufferCapacity > 0)
                {
                    if (_bufferedFrames.Count > 0)
                    {
                        frame = _bufferedFrames.Dequeue();
                        return true;
                    }
                }
                else if (_latestFrame is not null)
                {
                    frame = _latestFrame;
                    _latestFrame = null;
                    return true;
                }
            }

            if (timeoutMs <= 0) return false;
            double remainingMs = timeoutMs - Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            if (remainingMs <= 0) return false;
            try { _frameReady.WaitOne((int)Math.Min(remainingMs, 100)); }
            catch (ObjectDisposedException) { return false; }
        }

        return false;
    }

    private CancellationTokenSource? StartReaderLocked()
    {
        if (_disposed || !_desiredRunning || string.IsNullOrWhiteSpace(_source) ||
            _thread is { IsAlive: true })
            return null;

        CancellationTokenSource? staleCts = _cts;
        _cts = new CancellationTokenSource();
        CancellationTokenSource cts = _cts;
        long frameSessionGeneration = Interlocked.Increment(ref _frameSessionGeneration);
        IsRunning = true;
        _thread = new Thread(() => ReaderLoop(cts, frameSessionGeneration))
        {
            IsBackground = true,
            Name = "VlcFrameSource",
            Priority = ThreadPriority.Normal
        };
        _thread.Start();
        return staleCts;
    }

    private void ReaderLoop(CancellationTokenSource cts, long frameSessionGeneration)
    {
        CancellationToken ct = cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                VlcMediaPlayer? player = null;
                string source;
                string transport;
                lock (_lifecycleGate)
                {
                    source = _source;
                    transport = _transport;
                }

                try
                {
                    Interlocked.Exchange(ref _reconnectRequested, 0);
                    Interlocked.Exchange(ref _reconnectReason, null);
                    Interlocked.Exchange(ref _lastFrameTicks, 0);
                    Interlocked.Exchange(ref _activeFrameSessionGeneration, frameSessionGeneration);

                    RaiseState($"Connecting with LibVLC: {GetDisplaySource(source)} ({transport})");
                    player = CreateAndInitializePlayer();
                    if (ct.IsCancellationRequested) break;
                    player.Play(source, BuildMediaOptions(transport));
                    WaitForFrames(ct, frameSessionGeneration, transport);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        RaiseError(ex);
                        RaiseState($"LibVLC stream failed; reconnecting with configured {transport} transport.");
                    }
                }
                finally
                {
                    Interlocked.CompareExchange(ref _activeFrameSessionGeneration, 0, frameSessionGeneration);
                    DisposePlayer(player);
                    ReleasePixelBuffer();
                }

                if (ct.IsCancellationRequested) break;
                RaiseState($"Reconnecting in {_reconnectDelayMs / 1000.0:0.#}s ...");
                try { ct.WaitHandle.WaitOne(_reconnectDelayMs); }
                catch (ObjectDisposedException) { break; }
            }
        }
        finally
        {
            CancellationTokenSource? completedCts = null;
            CancellationTokenSource? staleCts = null;
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_cts, cts))
                {
                    IsRunning = false;
                    _cts = null;
                    completedCts = cts;
                }
                if (ReferenceEquals(_thread, Thread.CurrentThread))
                    _thread = null;
                staleCts = StartReaderLocked();
            }

            completedCts?.Dispose();
            staleCts?.Dispose();
        }
    }

    private static VlcMediaPlayer CreatePlayer()
    {
        string? vlcDirectory = ResolveVlcDirectory();
        if (vlcDirectory is null)
            throw new InvalidOperationException(
                "LibVLC runtime was not found. Install VLC 3.x x64 or set VLC_HOME to its installation folder.");

        // Video callbacks supply the decoded frames; no audio device or UI is needed.
        return new VlcMediaPlayer(new DirectoryInfo(vlcDirectory),
        [
            "--intf=dummy",
            "--no-audio",
            "--no-video-title-show",
            "--quiet",
            "--avcodec-hw=none"
        ]);
    }

    private void InitializePlayer(VlcMediaPlayer player)
    {
        player.SetVideoFormatCallbacks(_videoFormatCallback, _cleanupVideoCallback);
        player.SetVideoCallbacks(_lockVideoCallback, _unlockVideoCallback, _displayVideoCallback, IntPtr.Zero);
        player.EncounteredError += Player_EncounteredError;
        player.EndReached += Player_EndReached;
    }

    private VlcMediaPlayer CreateAndInitializePlayer()
    {
        VlcMediaPlayer player = CreatePlayer();
        try
        {
            InitializePlayer(player);
            return player;
        }
        catch
        {
            player.Dispose();
            throw;
        }
    }

    private void WaitForFrames(CancellationToken ct, long frameSessionGeneration, string transport)
    {
        long startedAt = Stopwatch.GetTimestamp();
        bool connected = false;
        while (!ct.IsCancellationRequested && IsCurrentFrameSession(frameSessionGeneration))
        {
            if (Volatile.Read(ref _reconnectRequested) != 0)
                throw new InvalidOperationException(Volatile.Read(ref _reconnectReason) ?? "LibVLC reported a stream error.");

            long lastFrameTicks = Interlocked.Read(ref _lastFrameTicks);
            if (lastFrameTicks != 0)
            {
                if (!connected)
                {
                    connected = true;
                    RaiseState($"Connected (LibVLC {transport})");
                }

                if (Stopwatch.GetElapsedTime(lastFrameTicks).TotalMilliseconds >= FrameSilenceTimeoutMs)
                    throw new InvalidOperationException(
                        $"LibVLC produced no frame for {FrameSilenceTimeoutMs / 1000.0:0.#}s.");
            }
            else if (Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds >= InitialFrameTimeoutMs)
            {
                throw new InvalidOperationException(
                    $"LibVLC did not produce an initial frame within {InitialFrameTimeoutMs / 1000.0:0.#}s.");
            }

            try { _wakeReader.WaitOne(ReadRetryDelayMs); }
            catch (ObjectDisposedException) { return; }
        }

        ct.ThrowIfCancellationRequested();
    }

    private static string[] BuildMediaOptions(string transport)
    {
        var options = new List<string>
        {
            ":network-caching=300",
            ":live-caching=300",
            ":drop-late-frames",
            ":skip-frames"
        };
        if (string.Equals(transport, "TCP", StringComparison.OrdinalIgnoreCase))
            options.Add(":rtsp-tcp");
        return options.ToArray();
    }

    private void Player_EncounteredError(object? sender, VlcMediaPlayerEncounteredErrorEventArgs e) =>
        RequestReconnect("LibVLC reported an RTSP/decoder error.");

    private void Player_EndReached(object? sender, VlcMediaPlayerEndReachedEventArgs e) =>
        RequestReconnect("LibVLC reported that the RTSP stream ended.");

    private void RequestReconnect(string reason)
    {
        Interlocked.Exchange(ref _reconnectReason, reason);
        Interlocked.Exchange(ref _reconnectRequested, 1);
        try { _wakeReader.Set(); } catch (ObjectDisposedException) { }
    }

    private void DisposePlayer(VlcMediaPlayer? player)
    {
        if (player is null) return;
        try { player.EncounteredError -= Player_EncounteredError; } catch { }
        try { player.EndReached -= Player_EndReached; } catch { }
        try { player.Stop(); } catch { }
        try { player.Dispose(); } catch { }
    }

    private uint ConfigureVideo(out IntPtr userData, IntPtr chroma, ref uint width, ref uint height,
        ref uint pitches, ref uint lines)
    {
        userData = IntPtr.Zero;
        try
        {
            if (width == 0 || height == 0 || width > 8192 || height > 8192) return 0;
            int frameWidth = checked((int)width);
            int frameHeight = checked((int)height);
            int pitch = checked((frameWidth * 4 + 31) & ~31);
            int allocationBytes = checked(pitch * frameHeight);

            lock (_videoGate)
            {
                ReleasePixelBufferLocked();
                _pixelBuffer = Marshal.AllocHGlobal(allocationBytes);
                _pixelWidth = frameWidth;
                _pixelHeight = frameHeight;
                _pixelPitch = pitch;
                userData = _pixelBuffer;
            }

            FourCCConverter.ToFourCC("RV32", chroma);
            pitches = (uint)pitch;
            lines = (uint)frameHeight;
            return 1;
        }
        catch (Exception ex)
        {
            RequestReconnect($"LibVLC frame buffer initialization failed ({ex.GetType().Name}).");
            return 0;
        }
    }

    private IntPtr LockVideo(IntPtr userData, IntPtr planes)
    {
        if (userData == IntPtr.Zero || planes == IntPtr.Zero) return IntPtr.Zero;
        try { Marshal.WriteIntPtr(planes, userData); }
        catch { return IntPtr.Zero; }
        return userData;
    }

    private static void UnlockVideo(IntPtr userData, IntPtr picture, IntPtr[] planes)
    {
        // libVLC has completed its write before DisplayVideo is invoked.
    }

    private void DisplayVideo(IntPtr userData, IntPtr picture)
    {
        Mat? frame = null;
        try
        {
            long frameSessionGeneration = Interlocked.Read(ref _activeFrameSessionGeneration);
            if (frameSessionGeneration == 0 || !IsCurrentFrameSession(frameSessionGeneration)) return;

            lock (_videoGate)
            {
                if (userData == IntPtr.Zero || userData != _pixelBuffer ||
                    _pixelWidth <= 0 || _pixelHeight <= 0 || _pixelPitch <= 0)
                    return;

                using var rawFrame = new Mat(
                    _pixelHeight, _pixelWidth, DepthType.Cv8U, 4, _pixelBuffer, _pixelPitch);
                frame = new Mat();
                CvInvoke.CvtColor(rawFrame, frame, ColorConversion.Bgra2Bgr);
            }

            if (!IsCurrentFrameSession(frameSessionGeneration) || !PublishFrame(frame, frameSessionGeneration))
                return;

            Interlocked.Increment(ref _captured);
            Interlocked.Exchange(ref _lastFrameTicks, Stopwatch.GetTimestamp());
            frame = null; // ownership has moved into the newest-frame slot or queue.
        }
        catch (Exception ex)
        {
            RequestReconnect($"LibVLC frame conversion failed ({ex.GetType().Name}).");
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private void CleanupVideo(ref IntPtr userData)
    {
        lock (_videoGate)
        {
            if (userData == _pixelBuffer)
                ReleasePixelBufferLocked();
            userData = IntPtr.Zero;
        }
    }

    private void ReleasePixelBuffer()
    {
        lock (_videoGate)
            ReleasePixelBufferLocked();
    }

    private void ReleasePixelBufferLocked()
    {
        if (_pixelBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_pixelBuffer);
            _pixelBuffer = IntPtr.Zero;
        }
        _pixelWidth = 0;
        _pixelHeight = 0;
        _pixelPitch = 0;
    }

    private bool PublishFrame(Mat frame, long frameSessionGeneration)
    {
        if (!IsCurrentFrameSession(frameSessionGeneration)) return false;
        RaiseFrameAvailable(frame);

        Mat? old = null;
        lock (_frameGate)
        {
            if (!IsCurrentFrameSession(frameSessionGeneration)) return false;
            if (_bufferCapacity > 0)
            {
                _bufferedFrames.Enqueue(frame);
                if (_bufferedFrames.Count > _bufferCapacity)
                    old = _bufferedFrames.Dequeue();
            }
            else
            {
                old = _latestFrame;
                _latestFrame = frame;
            }
        }

        if (old is not null)
        {
            old.Dispose();
            Interlocked.Increment(ref _dropped);
        }

        try { _frameReady.Set(); } catch (ObjectDisposedException) { }
        return true;
    }

    private void DrainQueue()
    {
        Mat? latest;
        lock (_frameGate)
        {
            latest = _latestFrame;
            _latestFrame = null;
            while (_bufferedFrames.Count > 0)
                _bufferedFrames.Dequeue().Dispose();
        }
        latest?.Dispose();
    }

    private bool IsCurrentFrameSession(long frameSessionGeneration) =>
        Interlocked.Read(ref _frameSessionGeneration) == frameSessionGeneration;

    private bool IsDisposed()
    {
        lock (_lifecycleGate)
            return _disposed;
    }

    private static string? ResolveVlcDirectory()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("VLC_HOME"),
            Environment.GetEnvironmentVariable("VLC_INSTALL_DIR"),
            TryReadVlcInstallDirectory(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC")
        };

        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                string path = Path.GetFullPath(candidate);
                if (File.Exists(Path.Combine(path, "libvlc.dll")) &&
                    File.Exists(Path.Combine(path, "libvlccore.dll")) &&
                    Directory.Exists(Path.Combine(path, "plugins")))
                    return path;
            }
            catch { }
        }
        return null;
    }

    private static string? TryReadVlcInstallDirectory()
    {
        try
        {
            return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\VideoLAN\VLC", "InstallDir", null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string GetDisplaySource(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)) return "camera source";
        string port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        string path = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? string.Empty : uri.AbsolutePath;
        return $"{uri.Scheme}://{uri.Host}{port}{path}";
    }

    private void RaiseState(string state)
    {
        Action<string>? subscribers = StateChanged;
        if (subscribers is null) return;
        foreach (Delegate subscriber in subscribers.GetInvocationList())
        {
            try { ((Action<string>)subscriber)(state); } catch { }
        }
    }

    private void RaiseError(Exception error)
    {
        Action<Exception>? subscribers = ErrorOccurred;
        if (subscribers is null) return;
        foreach (Delegate subscriber in subscribers.GetInvocationList())
        {
            try { ((Action<Exception>)subscriber)(error); } catch { }
        }
    }

    private void RaiseFrameAvailable(Mat frame)
    {
        Action<Mat>? subscribers = FrameAvailable;
        if (subscribers is null) return;
        foreach (Delegate subscriber in subscribers.GetInvocationList())
        {
            try { ((Action<Mat>)subscriber)(frame); } catch { }
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        ReleasePixelBuffer();
        _frameReady.Dispose();
        _wakeReader.Dispose();
        GC.SuppressFinalize(this);
    }
}
