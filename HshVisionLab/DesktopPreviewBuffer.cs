using System.Diagnostics;
using HshDetectionEngin;

namespace HshVisionLab;

/// <summary>Owns one preview bitmap until it is replaced or its displayed view is released.</summary>
public sealed class DesktopPreviewFrame : IDisposable
{
    private Bitmap? _image;
    private int _painted;

    public DesktopPreviewFrame(Bitmap image, CameraRuntime.PreviewFrameTiming timing)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(timing);
        _image = image;
        Timing = timing;
    }

    /// <summary>A borrowed image. The frame remains responsible for disposing it.</summary>
    public Bitmap Image => _image ?? throw new ObjectDisposedException(nameof(DesktopPreviewFrame));
    public CameraRuntime.PreviewFrameTiming Timing { get; }

    public bool TryMarkPainted() => Interlocked.Exchange(ref _painted, 1) == 0;

    public void Dispose() => Interlocked.Exchange(ref _image, null)?.Dispose();
}

public sealed record PreviewDisplayMetrics(
    double LastRenderMs,
    double LastDisplayMs,
    double MeanDisplayMs,
    double P95DisplayMs,
    double DisplayFps,
    long SampleCount);

/// <summary>
/// Holds only the newest waiting preview and coalesces producers into one queued UI callback.
/// The producer transfers ownership on every Offer; Take transfers ownership to the UI.
/// </summary>
public sealed class DesktopPreviewBuffer : IDisposable
{
    private const int SampleCapacity = 120;
    private readonly object _gate = new();
    private readonly double[] _displaySamples = new double[SampleCapacity];
    private readonly long[] _paintTimestamps = new long[SampleCapacity];
    private DesktopPreviewFrame? _pending;
    private bool _enabled = true;
    private bool _disposed;
    private bool _callbackPending;
    private long _replacedFrames;
    private int _sampleCount;
    private int _nextSample;
    private double _lastRenderMs;
    private double _lastDisplayMs;
    private long _nextMetricsRefresh;
    private PreviewDisplayMetrics _metrics = new(0, 0, 0, 0, 0, 0);

    public bool Enabled { get { lock (_gate) return _enabled && !_disposed; } }
    public long ReplacedFrames { get { lock (_gate) return _replacedFrames; } }

    public bool Offer(DesktopPreviewFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        DesktopPreviewFrame? release;
        bool schedule = false;
        lock (_gate)
        {
            if (!_enabled || _disposed)
            {
                release = frame;
            }
            else
            {
                release = _pending;
                if (release is not null) _replacedFrames++;
                _pending = frame;
                schedule = !_callbackPending;
                _callbackPending = true;
            }
        }
        release?.Dispose();
        return schedule;
    }

    public DesktopPreviewFrame? Take()
    {
        lock (_gate)
        {
            DesktopPreviewFrame? frame = _pending;
            _pending = null;
            _callbackPending = false;
            return frame;
        }
    }

    public void SetEnabled(bool enabled)
    {
        DesktopPreviewFrame? release = null;
        lock (_gate)
        {
            _enabled = enabled && !_disposed;
            if (!_enabled)
            {
                release = _pending;
                _pending = null;
                // A previously queued callback still exists. Keeping its marker
                // prevents a quick hide/show from adding another callback.
            }
        }
        release?.Dispose();
    }

    /// <summary>Release the pending image when posting its UI callback failed.</summary>
    public void CancelPending()
    {
        DesktopPreviewFrame? release;
        lock (_gate)
        {
            release = _pending;
            _pending = null;
            _callbackPending = false;
        }
        release?.Dispose();
    }

    /// <summary>Record the first paint of this frame; repeated repaints do not inflate FPS.</summary>
    public void RecordPaint(DesktopPreviewFrame frame, long paintedTicks)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.TryMarkPainted()) return;
        CameraRuntime.PreviewFrameTiming timing = frame.Timing;
        if (timing.ReceivedTimestamp <= 0 || timing.ReadyTimestamp < timing.ReceivedTimestamp ||
            paintedTicks < timing.ReadyTimestamp) return;

        lock (_gate)
        {
            if (!_enabled || _disposed) return;
            _lastRenderMs = timing.ReceiveToReadyMs;
            _lastDisplayMs = (paintedTicks - timing.ReceivedTimestamp) * 1000d / Stopwatch.Frequency;
            _displaySamples[_nextSample] = _lastDisplayMs;
            _paintTimestamps[_nextSample] = paintedTicks;
            _nextSample = (_nextSample + 1) % SampleCapacity;
            _sampleCount = Math.Min(SampleCapacity, _sampleCount + 1);
        }
    }

    public PreviewDisplayMetrics Metrics
    {
        get
        {
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                if (now < _nextMetricsRefresh) return _metrics;
                _nextMetricsRefresh = now + Stopwatch.Frequency / 2;
                if (_sampleCount == 0) return _metrics = new(0, 0, 0, 0, 0, 0);

                var sorted = new double[_sampleCount];
                double total = 0;
                int recentCount = 0;
                long earliest = long.MaxValue, latest = 0;
                long cutoff = now - Stopwatch.Frequency;
                for (int index = 0; index < _sampleCount; index++)
                {
                    sorted[index] = _displaySamples[index];
                    total += sorted[index];
                    long timestamp = _paintTimestamps[index];
                    if (timestamp < cutoff || timestamp > now) continue;
                    recentCount++;
                    earliest = Math.Min(earliest, timestamp);
                    latest = Math.Max(latest, timestamp);
                }
                Array.Sort(sorted);
                int p95Index = Math.Max(0, (int)Math.Ceiling(_sampleCount * .95) - 1);
                double fps = recentCount > 1 && latest > earliest
                    ? (recentCount - 1) * (double)Stopwatch.Frequency / (latest - earliest)
                    : 0;
                return _metrics = new(_lastRenderMs, _lastDisplayMs, total / _sampleCount,
                    sorted[p95Index], fps, _sampleCount);
            }
        }
    }

    public void ResetMetrics()
    {
        lock (_gate)
        {
            _sampleCount = _nextSample = 0;
            _lastRenderMs = _lastDisplayMs = 0;
            _nextMetricsRefresh = 0;
            _metrics = new(0, 0, 0, 0, 0, 0);
        }
    }

    public void Dispose()
    {
        DesktopPreviewFrame? release;
        lock (_gate)
        {
            _disposed = true;
            _enabled = false;
            release = _pending;
            _pending = null;
            _callbackPending = false;
        }
        release?.Dispose();
    }
}
