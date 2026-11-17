using System.Diagnostics;
using System.Drawing;

namespace HshDetectionService;

/// <summary>
/// A camera owns one encoder and one pending frame. Encoding another camera
/// cannot delay this worker, and additional viewers reuse its encoded output.
/// </summary>
internal sealed class CameraVideoPump : IDisposable
{
    private sealed record PendingFrame(Bitmap Image, long Sequence, long ReceivedTimestamp);

    private readonly object _gate = new();
    private readonly HashSet<IVideoPeer> _viewers = new(ReferenceEqualityComparer.Instance);
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly Func<IVideoFrameEncoder> _encoderFactory;
    private readonly Func<long> _clock;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PendingFrame? _pending;
    private bool _stopping;
    private int _inFlight;
    private long _receivedFrames, _encodedFrames, _replacedFrames, _duplicateFrames, _encodedBytes, _sentFrames, _sendErrors;
    private long _lastOfferedSequence, _lastEncodedSequence, _lastFrameTimestamp, _fpsWindowStarted, _fpsWindowFrames;
    private double _lastEncodeMs, _encodedFps;
    private int _width, _height;
    private string? _lastError;

    public CameraVideoPump(string cameraId, Func<IVideoFrameEncoder>? encoderFactory = null, Func<long>? timestampProvider = null)
    {
        CameraId = cameraId;
        _encoderFactory = encoderFactory ?? (() => new Vp8CameraFrameEncoder());
        _clock = timestampProvider ?? Stopwatch.GetTimestamp;
        _thread = new Thread(Run) { IsBackground = true, Name = "WebRtcCameraEncoder", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public string CameraId { get; }
    public Task Completion => _completion.Task;
    public bool HasViewers { get { lock (_gate) return !_stopping && _viewers.Count > 0; } }

    public void AddViewer(IVideoPeer viewer)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            _viewers.Add(viewer);
        }
    }

    public void RemoveViewer(IVideoPeer viewer)
    {
        lock (_gate)
        {
            _viewers.Remove(viewer);
            if (_viewers.Count == 0)
            {
                _pending?.Image.Dispose();
                _pending = null;
            }
        }
    }

    public void NotifyViewerReady() => UpdateDemand();
    public void UpdateDemand()
    {
        lock (_gate)
        {
            if (!_stopping) _wake.Set();
        }
    }

    public Size? GetDemand()
    {
        lock (_gate) return GetDemandLocked();
    }

    private Size? GetDemandLocked()
    {
        if (_stopping) return null;
        IVideoPeer[] ready = _viewers.Where(viewer => viewer.IsReady).ToArray();
        if (ready.Length == 0) return null;
        return new Size(ready.Max(viewer => viewer.MaxFrameSize.Width), ready.Max(viewer => viewer.MaxFrameSize.Height));
    }

    /// <summary>The caller retains ownership. Only one clone is made, while at least one viewer is ready.</summary>
    public bool PushFrame(Bitmap source, long sequence, long receivedTimestamp)
    {
        lock (_gate)
        {
            if (_stopping || !_viewers.Any(viewer => viewer.IsReady)) return false;
            _receivedFrames++;
            if (sequence <= _lastOfferedSequence)
            {
                _duplicateFrames++;
                return false;
            }
            if (_pending is not null)
            {
                _pending.Image.Dispose();
                _pending = null;
                _replacedFrames++;
            }
            _pending = new PendingFrame(new Bitmap(source), sequence, receivedTimestamp > 0 ? receivedTimestamp : _clock());
            _lastOfferedSequence = sequence;
            _wake.Set();
            return true;
        }
    }

    public WebRtcCameraDiagnostics GetDiagnostics()
    {
        lock (_gate)
        {
            double age = _lastFrameTimestamp > 0 ? Math.Max(0, (_clock() - _lastFrameTimestamp) * 1000.0 / Stopwatch.Frequency) : 0;
            return new WebRtcCameraDiagnostics(CameraId, _viewers.Count, _viewers.Count(viewer => viewer.IsReady),
                _width, _height, _receivedFrames, _encodedFrames, _replacedFrames, _duplicateFrames,
                _lastEncodeMs, age, _encodedFps, _encodedBytes, _sentFrames, _sendErrors, _lastError,
                _inFlight, _pending is null ? 0 : 1, _lastEncodedSequence);
        }
    }

    private void Run()
    {
        IVideoFrameEncoder? encoder = null;
        try
        {
            while (true)
            {
                _wake.WaitOne();
                PendingFrame? frame;
                Size bounds;
                IVideoPeer[] viewers;
                lock (_gate)
                {
                    if (_stopping) break;
                    frame = _pending;
                    _pending = null;
                    if (frame is null) continue;
                    viewers = _viewers.Where(viewer => viewer.IsReady).ToArray();
                    bounds = GetDemandLocked() ?? new Size(1280, 720);
                    _inFlight = 1;
                }

                try
                {
                    if (viewers.Length == 0) continue;
                    encoder ??= _encoderFactory();
                    long startedAt = _clock();
                    EncodedVideoFrame encoded = encoder.Encode(frame.Image, bounds, viewers.Any(viewer => viewer.NeedsKeyFrame));
                    long completedAt = _clock();
                    lock (_gate)
                    {
                        _encodedFrames++;
                        _encodedBytes += encoded.Sample.Length;
                        _width = encoded.Width;
                        _height = encoded.Height;
                        _lastEncodeMs = Math.Max(0, (completedAt - startedAt) * 1000.0 / Stopwatch.Frequency);
                        _lastEncodedSequence = frame.Sequence;
                        _lastFrameTimestamp = frame.ReceivedTimestamp;
                        _lastError = null;
                        if (_fpsWindowStarted == 0) _fpsWindowStarted = startedAt;
                        _fpsWindowFrames++;
                        double seconds = (completedAt - _fpsWindowStarted) / (double)Stopwatch.Frequency;
                        if (seconds >= 1)
                        {
                            _encodedFps = _fpsWindowFrames / seconds;
                            _fpsWindowStarted = completedAt;
                            _fpsWindowFrames = 0;
                        }
                    }
                    foreach (IVideoPeer viewer in viewers)
                    {
                        lock (_gate) { if (_stopping) break; }
                        try
                        {
                            if (viewer.TrySend(encoded, frame.Sequence, frame.ReceivedTimestamp))
                            {
                                lock (_gate) _sentFrames++;
                            }
                        }
                        catch (Exception error)
                        {
                            lock (_gate) { _sendErrors++; _lastError = "SendFailed:" + error.GetType().Name; }
                        }
                    }
                }
                catch (Exception error)
                {
                    lock (_gate) _lastError = "EncodeFailed:" + error.GetType().Name;
                }
                finally
                {
                    frame.Image.Dispose();
                    lock (_gate) _inFlight = 0;
                }
            }
        }
        finally
        {
            try { encoder?.Dispose(); } catch { }
            lock (_gate)
            {
                _stopping = true;
                _pending?.Image.Dispose();
                _pending = null;
                _viewers.Clear();
                _wake.Dispose();
            }
            _completion.TrySetResult();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            _pending?.Image.Dispose();
            _pending = null;
            _viewers.Clear();
            _wake.Set();
        }
        // The worker releases its codec and scratch buffers after the one
        // in-flight encode ends. Never block a peer callback waiting for itself.
    }
}
