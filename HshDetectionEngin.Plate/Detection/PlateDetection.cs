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
    public string ModelPath { get; set; } = string.Empty;
    public int InputWidth { get; set; } = 640;
    public int InputHeight { get; set; } = 640;
    public float ConfThreshold { get; set; } = 0.30f;
    /// <summary>Optional glyph threshold for combined plate/character models.</summary>
    public float? CharacterConfThreshold { get; set; }
    public float NmsIoUThreshold { get; set; } = 0.45f;
    public int IntraOpThreads { get; set; } = 2;
    public bool AutoOptimizeModel { get; set; }
    /// <summary>Maps a single-class plate detector to the legacy plate class id.</summary>
    public int? ForcedClassId { get; set; }
}
