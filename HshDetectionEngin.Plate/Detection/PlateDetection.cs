namespace HshDetectionEngin.Plate;

internal sealed record PlateDetection(
    float X,
    float Y,
    float Width,
    float Height,
    int ClassId,
    string Label,
    float Score);

internal sealed class YoloOptions
{
    public string ModelPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Models", "best.onnx");
    public int InputWidth { get; set; } = 640;
    public int InputHeight { get; set; } = 640;
    public float ConfThreshold { get; set; } = 0.30f;
    public float NmsIoUThreshold { get; set; } = 0.45f;
    public int IntraOpThreads { get; set; } = 2;
    public bool AutoOptimizeModel { get; set; } = true;
    /// <summary>Maps a single-class plate detector to the legacy plate class id.</summary>
    public int? ForcedClassId { get; set; }
}
