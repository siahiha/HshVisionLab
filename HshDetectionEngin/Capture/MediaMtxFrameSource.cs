using Emgu.CV;

namespace HshDetectionEngin.Capture;

/// <summary>
/// Camera receiver backed by MediaMTX. The detector reads the gateway's local
/// RTSP output, while browsers can subscribe to the same path through WHEP.
/// </summary>
public sealed class MediaMtxFrameSource : IFrameSource
{
    private readonly string _cameraId;
    private readonly MediaMtxRuntime _runtime;
    private readonly FrameSource _localReader = new();
    private string _source = string.Empty;
    private bool _configured;
    private event Action<Exception>? ErrorHandlers;

    public MediaMtxFrameSource(string cameraId, MediaMtxRuntime? runtime = null)
    {
        _cameraId = cameraId;
        _runtime = runtime ?? MediaMtxRuntime.Shared;
        _localReader.LowLatencyMode = true;
    }

    public event Action<string>? StateChanged { add => _localReader.StateChanged += value; remove => _localReader.StateChanged -= value; }
    public event Action<Exception>? ErrorOccurred { add => ErrorHandlers += value; remove => ErrorHandlers -= value; }
    public event Action<Mat>? FrameAvailable { add => _localReader.FrameAvailable += value; remove => _localReader.FrameAvailable -= value; }
    public bool IsRunning => _localReader.IsRunning;
    public int QueueCount => _localReader.QueueCount;
    public long CapturedFrames => _localReader.CapturedFrames;
    public long DroppedFrames => _localReader.DroppedFrames;
    public string Source => _source;

    public void Configure(string source, string transport, int bufferCount, int reconnectDelayMs)
    {
        _source = source;
        try
        {
            _runtime.EnsurePathAsync(_cameraId, source, transport).GetAwaiter().GetResult();
            // MediaMTX is the low-latency receiver. Never replay an old frame
            // from a processing queue here; the composite preview and the
            // inference loop must always consume the newest available frame.
            _localReader.Configure(_runtime.GetLocalRtspUri(_cameraId).ToString(), "TCP", 0, reconnectDelayMs);
            _configured = true;
        }
        catch (Exception error)
        {
            ErrorHandlers?.Invoke(error);
            throw;
        }
    }

    public void Start()
    {
        if (!_configured) throw new InvalidOperationException("MediaMTX frame source must be configured before Start().");
        _localReader.Start();
    }

    public void Stop() => _localReader.Stop();

    public bool TryDequeueFrame(out Mat frame, int timeoutMs, CancellationToken cancellationToken)
        => _localReader.TryDequeueFrame(out frame, timeoutMs, cancellationToken);

    public void Dispose() => _localReader.Dispose();
}
