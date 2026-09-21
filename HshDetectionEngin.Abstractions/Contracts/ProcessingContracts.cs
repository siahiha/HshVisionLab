using Emgu.CV;

namespace HshDetectionEngin;

public interface IFrameSource : IDisposable
{
    event Action<string>? StateChanged;
    event Action<Exception>? ErrorOccurred;
    event Action<Mat>? FrameAvailable;
    bool IsRunning { get; }
    int QueueCount { get; }
    long CapturedFrames { get; }
    long DroppedFrames { get; }
    string Source { get; }
    void Configure(string source, string transport, int bufferCount, int reconnectDelayMs);
    void Start();
    void Stop();
    bool TryDequeueFrame(out Mat frame, int timeoutMs, CancellationToken cancellationToken);
}

public enum AnalysisKind { Unknown, Plate, Face, Object, Vehicle, Custom }

/// <summary>Geometry that a processing module wants to draw on the preview.</summary>
public enum ProcessingOverlayKind
{
    Polyline,
    Polygon,
    Points,
    Circle,
    Rectangle
}

/// <summary>
/// A lightweight, ROI-local visual primitive. It is intentionally separate
/// from AnalysisDetection so visual geometry does not enter history/events.
/// Points and Bounds are relative to the pipeline ROI; the runtime translates
/// them to the source frame before drawing.
/// </summary>
public sealed record ProcessingOverlay
{
    public ProcessingOverlayKind Kind { get; init; }
    public IReadOnlyList<Point> Points { get; init; } = [];
    public Rectangle Bounds { get; init; }
    public int Radius { get; init; } = 3;
    public Color Color { get; init; } = Color.Lime;
    public int Thickness { get; init; } = 1;
    public bool Filled { get; init; }
    public string? Key { get; init; }
}

public sealed record AnalysisDetection(
    AnalysisKind Kind,
    string Label,
    float Confidence,
    Rectangle Bounds,
    int? TrackId = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed class ProcessingContext
{
    public required Mat Image { get; init; }
    public required string CameraId { get; init; }
    public required string CameraName { get; init; }
    public required DateTime Timestamp { get; init; }
    public required Rectangle SourceBounds { get; init; }
    public required Size OriginalFrameSize { get; init; }
    public IReadOnlyList<AnalysisDetection> PreviousDetections { get; init; } = [];
}

public sealed class PipelineResult : IDisposable
{
    private bool _disposed;
    private Mat? _nextImage;
    public IReadOnlyList<AnalysisDetection> Detections { get; init; } = [];
    public IReadOnlyList<ProcessingOverlay> Overlays { get; init; } = [];
    public bool OverlaysUpdated { get; init; }
    public object? Value { get; init; }
    public Mat? NextImage { get => _nextImage; init => _nextImage = value; }

    /// <summary>Transfers ownership of the next image to the caller.</summary>
    public Mat? TakeNextImage()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PipelineResult));
        Mat? image = _nextImage;
        _nextImage = null;
        return image;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _nextImage?.Dispose();
        if (Value is IDisposable disposable) disposable.Dispose();
    }
}

public interface IProcessingPipeline : IDisposable
{
    string Name { get; }
    PipelineResult Process(ProcessingContext context);
}

public interface IFrameRenderer
{
    Bitmap Render(Mat originalFrame, IReadOnlyList<AnalysisDetection> detections, CameraSettings settings);

    /// <summary>
    /// Backward-compatible render hook for visual primitives emitted by modules.
    /// Existing renderers keep working; new renderers can override this overload.
    /// </summary>
    Bitmap Render(
        Mat originalFrame,
        IReadOnlyList<AnalysisDetection> detections,
        CameraSettings settings,
        IReadOnlyList<ProcessingOverlay> overlays) =>
        Render(originalFrame, detections, settings);
}
