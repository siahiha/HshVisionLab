namespace HshDetectionEngin;

/// <summary>Heuristic body-color sampling anchored to the original plate detector box.</summary>
public sealed class VehicleColorSettings
{
    public bool Enabled { get; set; } = true;
    public bool DrawSampleRegion { get; set; } = true;
    public bool MedianFilterEnabled { get; set; } = true;
    public float HeightMultiplier { get; set; } = 3;
    public float WidthMultiplier { get; set; } = 1;
    public float GapHeightMultiplier { get; set; }
    // Legacy multi-frame fields are retained for JSON compatibility and ignored by the single-frame analyzer.
    public int MinimumSamples { get; set; } = 3;
    public int MaximumAttempts { get; set; } = 12;
    public int SampleIntervalMs { get; set; } = 150;
    public int TrackTimeoutMs { get; set; } = 3000;
    public float AcceptanceScore { get; set; } = .60f;
    public float MinimumAgreement { get; set; } = .75f;
    public float MaximumLabDistance { get; set; } = 25;
    public float MinimumRegionCoverage { get; set; } = .80f;
    public float ModelConfidence { get; set; } = .60f;
    public float ModelMinimumMargin { get; set; } = .15f;
    public VehicleColorSettings Clone() => (VehicleColorSettings)MemberwiseClone();

    public void Validate()
    {
        if (!float.IsFinite(HeightMultiplier) || HeightMultiplier < .25f || HeightMultiplier > 10 ||
            !float.IsFinite(WidthMultiplier) || WidthMultiplier < .25f || WidthMultiplier > 5 ||
            !float.IsFinite(GapHeightMultiplier) || GapHeightMultiplier < 0 || GapHeightMultiplier > 5 ||
            TrackTimeoutMs < 1 || TrackTimeoutMs > 60000 ||
            !float.IsFinite(ModelConfidence) || ModelConfidence <= 0 || ModelConfidence > 1 ||
            !float.IsFinite(ModelMinimumMargin) || ModelMinimumMargin < 0 || ModelMinimumMargin > 1 ||
            !float.IsFinite(MinimumRegionCoverage) || MinimumRegionCoverage <= 0 || MinimumRegionCoverage > 1)
            throw new InvalidOperationException("Invalid vehicle color sampling settings.");
    }
}
