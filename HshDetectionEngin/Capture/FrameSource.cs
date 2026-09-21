using System.Diagnostics;
using System.Runtime.InteropServices;
using Emgu.CV;
using Emgu.CV.CvEnum;

namespace HshDetectionEngin.Capture;

/// <summary>
/// Low-latency live frame source.
/// Keeps the newest frame by default. When a positive buffer capacity is
/// configured, a bounded queue is used and the oldest frame is discarded when
/// the queue is full so processing still does not grow without a limit.
/// </summary>
public sealed class FrameSource : IFrameSource
{
    private readonly object _frameGate = new();
    private readonly object _lifecycleGate = new();
    private readonly object _captureGate = new();
    // OpenCV's Windows FFmpeg plugin reads OPENCV_FFMPEG_CAPTURE_OPTIONS
    // through the MSVCRT environment while VideoCapture opens. The variable
    // is process-wide, so a managed gate is required when multiple cameras
    // use different RTSP transports.
    private static readonly object FfmpegOpenGate = new();
    private readonly AutoResetEvent _frameReady = new(false);
    private Mat? _latestFrame;
    private readonly Queue<Mat> _bufferedFrames = new();
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private VideoCapture? _capture;
    // This is the requested lifecycle state, distinct from whether a native
    // reader thread is still unwinding. Keeping it under _lifecycleGate makes
    // an immediate Stop after Start reliably cancel a queued restart.
    private bool _desiredRunning;
    private bool _disposed;

    private string _source = "";
    private string _transport = "TCP";
    private int _reconnectDelayMs = 3000;
    private int _bufferCapacity;
    private long _captured;
    private long _dropped;
    // Incremented whenever Stop invalidates the current reader or a new
    // reader is created. It prevents a late native read from publishing a
    // frame after Stop has drained the queue.
    private long _frameSessionGeneration;

    // The built-in FFmpeg timeout properties are open-only. They avoid a stuck
    // native read outliving Stop() and make the reconnect policy predictable.
    private const int FfmpegOpenTimeoutMs = 5000;
    private const int FfmpegReadTimeoutMs = 5000;

    // RTSP sources can briefly lose packets or wait for a keyframe. A count of
    // ten retries at 100 ms used to tear down a session after roughly one
    // second, which caused a reconnect loop on otherwise recoverable streams.
    private const int MaxReadInterruptionMs = 10000;
    private const int ReadRetryDelayMs = 100;
    // Must be longer than either FFmpeg open/read timeout. VideoCapture is
    // released only by its owning reader thread after Grab/Retrieve returns.
    private const int StopJoinTimeoutMs = 6000;

    public event Action<string>? StateChanged;
    public event Action<Exception>? ErrorOccurred;
    /// <summary>
    /// Provides the currently captured frame to lightweight consumers before
    /// it is consumed by the inference queue. The Mat is borrowed and is
    /// valid only during the callback.
    /// </summary>
    public event Action<Mat>? FrameAvailable;

    public bool IsRunning { get; private set; }
    /// <summary>Uses FFmpeg's no-buffer/low-delay flags for gateway-fed live streams.</summary>
    public bool LowLatencyMode { get; set; }
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

    public void DrainQueue()
    {
        Mat? old;
        lock (_frameGate)
        {
            old = _latestFrame;
            _latestFrame = null;
            while (_bufferedFrames.Count > 0)
                _bufferedFrames.Dequeue().Dispose();
        }
        old?.Dispose();
    }

    public bool TryDequeueFrame(out Mat frame, int timeoutMs, CancellationToken externalCt)
    {
        frame = null!;
        if (externalCt.IsCancellationRequested || IsDisposed()) return false;

        var start = Stopwatch.GetTimestamp();
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
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var remaining = timeoutMs - elapsedMs;
            if (remaining <= 0) return false;

            int waitMs = (int)Math.Min(remaining, 100);
            try { _frameReady.WaitOne(waitMs); }
            catch (ObjectDisposedException) { return false; }
        }

        return false;
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

            // Do not create a new reader while an old native Grab() is still
            // unwinding. Otherwise the old reader's finally block can close
            // the newly opened capture.
            if (_thread is { IsAlive: true })
            {
                previousReaderIsStopping = true;
            }
            else
            {
                staleCts = StartReaderLocked();
            }
        }

        staleCts?.Dispose();
        if (previousReaderIsStopping)
            RaiseState("Previous camera reader is still stopping; it will restart automatically.");
    }

    public void Stop()
    {
        Thread? reader;
        CancellationTokenSource? cts;
        lock (_lifecycleGate)
        {
            // Always cancel a queued automatic restart, even when the prior
            // reader has just cleared _thread but has not yet returned.
            _desiredRunning = false;
            IsRunning = false;
            Interlocked.Increment(ref _frameSessionGeneration);
            reader = _thread;
            cts = _cts;
            try { cts?.Cancel(); } catch { }
        }

        // Do not dispose VideoCapture from this thread while Grab/Retrieve is
        // running. FFmpeg owns native memory during those calls; the reader
        // releases its local handle after the configured read timeout returns.
        try { _frameReady.Set(); } catch { }
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

    // Must be called with _lifecycleGate held. It never starts a second
    // native reader while the previous one is still alive.
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
            Name = "FrameSource",
            Priority = ThreadPriority.Normal
        };
        _thread.Start();
        return staleCts;
    }

    private void DisposeCapture(VideoCapture? capture)
    {
        if (capture is null) return;
        lock (_captureGate)
        {
            if (ReferenceEquals(_capture, capture))
                _capture = null;
        }
        try { capture.Dispose(); } catch { }
    }

    private bool TryActivateCapture(VideoCapture capture, CancellationToken ct)
    {
        VideoCapture? previous;
        lock (_captureGate)
        {
            if (ct.IsCancellationRequested) return false;
            previous = _capture;
            _capture = capture;
        }
        if (previous is not null && !ReferenceEquals(previous, capture))
        {
            try { previous.Dispose(); } catch { }
        }
        return true;
    }

    private void ReaderLoop(CancellationTokenSource cts, long frameSessionGeneration)
    {
        CancellationToken ct = cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                VideoCapture? capture = null;
                string activeTransport = _transport;
                try
                {
                    RaiseState($"Connecting: {GetDisplaySource()} ({_transport})");
                    if (!OpenCapture(out capture, out activeTransport))
                    {
                        RaiseError(new Exception($"Cannot open camera source: {GetDisplaySource()}"));
                        RaiseState("Connection failed");
                    }
                    else if (capture is not null && TryActivateCapture(capture, ct))
                    {
                        RaiseState(IsRtspSource(_source) ? $"Connected ({activeTransport})" : "Connected");
                        ReadFrames(capture, ct, activeTransport, frameSessionGeneration);
                    }
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
                        if (IsRtspSource(_source))
                            RaiseState($"RTSP read failed; reconnecting with configured {_transport} transport.");
                    }
                }
                finally
                {
                    DisposeCapture(capture);
                }

                if (ct.IsCancellationRequested) break;
                RaiseState($"Reconnecting in {_reconnectDelayMs / 1000.0:0.#}s ...");
                try { ct.WaitHandle.WaitOne(_reconnectDelayMs); } catch { }
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

                // Start the queued reader while still holding the lifecycle
                // gate. Stop() can therefore cancel it before it becomes
                // visible, rather than racing the old reader's finally.
                staleCts = StartReaderLocked();
            }
            completedCts?.Dispose();
            staleCts?.Dispose();
        }
    }

    private bool OpenCapture(out VideoCapture? openedCapture, out string activeTransport)
    {
        openedCapture = null;
        activeTransport = _transport;
        string source = _source;

        if (int.TryParse(source, out int cameraIndex))
        {
            var localCapture = new VideoCapture(cameraIndex, VideoCapture.API.DShow);
            if (!localCapture.IsOpened)
            {
                localCapture.Dispose();
                return false;
            }

            openedCapture = localCapture;
            return true;
        }

        if (IsRtspSource(source))
        {
            // Transport is an explicit camera setting. Do not silently flip a
            // stable TCP session to UDP after a transient read failure.
            string transport = _transport;
            RaiseState($"Trying RTSP over {transport} ...");
            VideoCapture? capture = TryOpenFfmpeg(source, transport);
            if (capture is null) return false;

            openedCapture = capture;
            activeTransport = transport;
            return true;
        }

        // Files and non-RTSP URLs can use FFmpeg without RTSP-specific options.
        VideoCapture? ffmpegCapture = TryOpenFfmpeg(source, null);
        if (ffmpegCapture is not null)
        {
            openedCapture = ffmpegCapture;
            return true;
        }

        // Fallback for sources where the default OpenCV backend is more
        // appropriate. RTSP intentionally does not reach this branch.
        VideoCapture? captureFallback = null;
        try
        {
            captureFallback = new VideoCapture(source);
            if (!captureFallback.IsOpened) return false;
            openedCapture = captureFallback;
            captureFallback = null;
            return true;
        }
        catch (Exception ex)
        {
            RaiseError(new Exception($"OpenCV could not open camera source ({ex.GetType().Name})."));
            return false;
        }
        finally
        {
            captureFallback?.Dispose();
        }
    }

    private VideoCapture? TryOpenFfmpeg(string source, string? rtspTransport)
    {
        VideoCapture? capture = null;
        try
        {
            capture = CreateFfmpegCapture(source, rtspTransport);
            if (!capture.IsOpened) return null;
            VideoCapture result = capture;
            capture = null;
            return result;
        }
        catch (Exception ex)
        {
            RaiseError(new Exception($"FFmpeg could not open camera source ({ex.GetType().Name})."));
            return null;
        }
        finally
        {
            capture?.Dispose();
        }
    }

    private VideoCapture CreateFfmpegCapture(string source, string? rtspTransport)
    {
        var properties = new[]
        {
            Tuple.Create(CapProp.OpenTimeoutMsec, FfmpegOpenTimeoutMs),
            Tuple.Create(CapProp.ReadTimeoutMsec, FfmpegReadTimeoutMs)
        };

        // This gate covers every FFmpeg open. A non-RTSP source opened while
        // the environment variable is temporarily set for RTSP must not
        // inherit another camera's transport option.
        lock (FfmpegOpenGate)
        {
            if (rtspTransport is null)
                return new VideoCapture(source, VideoCapture.API.Ffmpeg, properties);

            const string optionsVariable = "OPENCV_FFMPEG_CAPTURE_OPTIONS";
            string? previousOptions = GetNativeEnvironmentVariable(optionsVariable);
            try
            {
                string options = $"rtsp_transport;{rtspTransport.ToLowerInvariant()}";
                if (LowLatencyMode)
                    options += "|fflags;nobuffer|flags;low_delay|max_delay;0";
                SetNativeEnvironmentVariable(optionsVariable, options);
                return new VideoCapture(source, VideoCapture.API.Ffmpeg, properties);
            }
            finally
            {
                SetNativeEnvironmentVariable(optionsVariable, previousOptions);
            }
        }
    }

    [DllImport("msvcrt.dll", EntryPoint = "getenv", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr NativeGetEnvironmentVariable(string name);

    [DllImport("msvcrt.dll", EntryPoint = "_putenv_s", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int NativeSetEnvironmentVariable(string name, string value);

    private static string? GetNativeEnvironmentVariable(string name)
    {
        IntPtr value = NativeGetEnvironmentVariable(name);
        return value == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(value);
    }

    private static void SetNativeEnvironmentVariable(string name, string? value)
    {
        // In the CRT, an empty value removes a variable. This restores an
        // initially absent option after the FFmpeg constructor has returned.
        int error = NativeSetEnvironmentVariable(name, value ?? string.Empty);
        if (error != 0)
            throw new InvalidOperationException($"Could not set native environment variable '{name}' (error {error}).");
    }

    // Event subscribers belong to the host/UI. A faulty subscriber must not
    // terminate the native reader or prevent the remaining subscribers from
    // receiving a status update.
    private void RaiseState(string state)
    {
        Action<string>? subscribers = StateChanged;
        if (subscribers is null) return;

        foreach (Delegate subscriber in subscribers.GetInvocationList())
        {
            try { ((Action<string>)subscriber)(state); }
            catch { }
        }
    }

    private void RaiseError(Exception error)
    {
        Action<Exception>? subscribers = ErrorOccurred;
        if (subscribers is null) return;

        foreach (Delegate subscriber in subscribers.GetInvocationList())
        {
            try { ((Action<Exception>)subscriber)(error); }
            catch { }
        }
    }

    private void RaiseFrameAvailable(Mat frame)
    {
        Action<Mat>? subscribers = FrameAvailable;
        if (subscribers is null) return;

        foreach (Delegate subscriber in subscribers.GetInvocationList())
        {
            try { ((Action<Mat>)subscriber)(frame); }
            catch { }
        }
    }

    private bool PublishFrame(Mat frame, long frameSessionGeneration)
    {
        // The reader owns this Mat until it is queued below, so preview
        // listeners can safely inspect it without holding _frameGate.
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

    private void ReadFrames(
        VideoCapture capture,
        CancellationToken ct,
        string activeTransport,
        long frameSessionGeneration)
    {
        long interruptionStartedAt = 0;
        int interruptionAttempts = 0;

        while (CanAcceptFrames(ct, frameSessionGeneration))
        {
            bool grabbed = false;
            Exception? readError = null;
            try
            {
                grabbed = capture.Grab();
            }
            catch (Exception ex)
            {
                readError = ex;
            }

            if (!grabbed)
            {
                if (!HandleReadFailure(ct, capture, activeTransport, "grab", readError,
                    ref interruptionStartedAt, ref interruptionAttempts)) break;
                continue;
            }

            if (!CanAcceptFrames(ct, frameSessionGeneration)) break;

            Mat? frame = null;
            try
            {
                frame = new Mat();
                bool retrieved = false;
                try
                {
                    retrieved = capture.Retrieve(frame) && !frame.IsEmpty;
                }
                catch (Exception ex)
                {
                    readError = ex;
                }

                if (!retrieved)
                {
                    if (!HandleReadFailure(ct, capture, activeTransport, "retrieve", readError,
                        ref interruptionStartedAt, ref interruptionAttempts)) break;
                    continue;
                }

                if (interruptionStartedAt != 0)
                    RaiseState("RTSP stream recovered.");

                interruptionStartedAt = 0;
                interruptionAttempts = 0;
                if (!CanAcceptFrames(ct, frameSessionGeneration)) break;
                if (!PublishFrame(frame, frameSessionGeneration)) break;
                Interlocked.Increment(ref _captured);
                frame = null; // ownership transferred to the latest-frame slot or bounded queue
            }
            finally
            {
                frame?.Dispose();
            }
        }
    }

    private bool HandleReadFailure(
        CancellationToken ct,
        VideoCapture capture,
        string activeTransport,
        string operation,
        Exception? readError,
        ref long interruptionStartedAt,
        ref int interruptionAttempts)
    {
        if (ct.IsCancellationRequested) return false;

        interruptionAttempts++;
        if (interruptionStartedAt == 0)
        {
            interruptionStartedAt = Stopwatch.GetTimestamp();
            RaiseState(
                $"Temporary RTSP {operation} interruption; waiting up to {MaxReadInterruptionMs / 1000.0:0.#}s before reconnecting...");
        }

        double interruptedForMs = Stopwatch.GetElapsedTime(interruptionStartedAt).TotalMilliseconds;
        if (interruptedForMs >= MaxReadInterruptionMs)
        {
            string backend = GetBackendName(capture);
            string lastError = readError is null ? "none" : readError.GetType().Name;
            throw new InvalidOperationException(
                $"RTSP {operation} returned no frame for {interruptedForMs / 1000.0:0.#}s " +
                $"({interruptionAttempts} attempts; backend {backend}; transport {activeTransport}; " +
                $"captured {CapturedFrames}; last error {lastError}).");
        }

        try { return !ct.WaitHandle.WaitOne(ReadRetryDelayMs); }
        catch (ObjectDisposedException) { return false; }
    }

    private static string GetBackendName(VideoCapture capture)
    {
        try
        {
            return string.IsNullOrWhiteSpace(capture.BackendName) ? "FFmpeg" : capture.BackendName;
        }
        catch
        {
            return "FFmpeg";
        }
    }

    private bool CanAcceptFrames(CancellationToken ct, long frameSessionGeneration) =>
        !ct.IsCancellationRequested && IsCurrentFrameSession(frameSessionGeneration);

    private bool IsCurrentFrameSession(long frameSessionGeneration) =>
        Volatile.Read(ref _frameSessionGeneration) == frameSessionGeneration;

    private bool IsDisposed()
    {
        lock (_lifecycleGate)
            return _disposed;
    }

    private static bool IsRtspSource(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals("rtsps", StringComparison.OrdinalIgnoreCase));

    private string GetDisplaySource()
    {
        if (int.TryParse(_source, out int cameraIndex)) return $"camera {cameraIndex}";
        if (!Uri.TryCreate(_source, UriKind.Absolute, out Uri? uri)) return "camera source";

        string port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        string path = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? string.Empty : uri.AbsolutePath;
        return $"{uri.Scheme}://{uri.Host}{port}{path}";
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        _frameReady.Dispose();
        GC.SuppressFinalize(this);
    }
}
