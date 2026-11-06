namespace HshDetectionEngin;

/// <summary>Image-space motion, shared by all modules that emit stable track IDs.</summary>
public sealed class MotionDirectionSettings
{
    public bool Enabled { get; set; } = true;
    public int WindowMs { get; set; } = 600;
    public int MinimumObservations { get; set; } = 3;
    public int MinimumObservationMs { get; set; } = 200;
    public float MinimumDisplacementPixels { get; set; } = 3;
    public int TrackTimeoutMs { get; set; } = 2000;
    public bool LineEnabled { get; set; }
    // Coordinates in the processing ROI bounding rectangle, in [0,1].
    public float LineStartX { get; set; } = .1f;
    public float LineStartY { get; set; } = .5f;
    public float LineEndX { get; set; } = .9f;
    public float LineEndY { get; set; } = .5f;
    public bool PositiveCrossingIsEntry { get; set; } = true;

    public MotionDirectionSettings Clone() => (MotionDirectionSettings)MemberwiseClone();

    public void Validate()
    {
        if (WindowMs < 50 || WindowMs > 10000 || MinimumObservations < 2 || MinimumObservations > 100 ||
            MinimumObservationMs < 0 || MinimumObservationMs > WindowMs ||
            !float.IsFinite(MinimumDisplacementPixels) || MinimumDisplacementPixels <= 0 || MinimumDisplacementPixels > 1000 ||
            TrackTimeoutMs <= WindowMs || TrackTimeoutMs > 60000)
            throw new InvalidOperationException("Invalid motion observation window, displacement or track timeout.");
        float[] coordinates = [LineStartX, LineStartY, LineEndX, LineEndY];
        if (coordinates.Any(v => !float.IsFinite(v) || v < 0 || v > 1) ||
            LineEnabled && Math.Abs(LineStartX - LineEndX) + Math.Abs(LineStartY - LineEndY) < .001f)
            throw new InvalidOperationException("The crossing line must have two distinct endpoints inside the ROI.");
    }
}
