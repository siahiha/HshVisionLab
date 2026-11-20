namespace HshDetectionEngin;

/// <summary>Optional whole-vehicle color classification after a qualifying plate box.</summary>
public sealed class VehicleColorSettings
{
    public bool Enabled { get; set; } = true;
    public bool DrawSampleRegion { get; set; } = true;
    public bool MedianFilterEnabled { get; set; } = true;
    public string ModelFile { get; set; } = "vehicle-attributes-recognition-barrier-0039.onnx";
    public string VehicleDetectorModelFile { get; set; } = "yolox_nano.onnx";
    public float VehicleDetectionConfidence { get; set; } = .30f;
    public float VehicleNmsIoU { get; set; } = .45f;
    public int MaxFps { get; set; } = 2;
    // Old crop geometry and acceptance fields remain readable for existing JSON.
    // They are ignored: model argmax is supplementary and has no acceptance threshold.
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
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(ModelFile) || string.IsNullOrWhiteSpace(VehicleDetectorModelFile) ||
            !float.IsFinite(VehicleDetectionConfidence) || VehicleDetectionConfidence <= 0 || VehicleDetectionConfidence > 1 ||
            !float.IsFinite(VehicleNmsIoU) || VehicleNmsIoU <= 0 || VehicleNmsIoU > 1 ||
            MaxFps < 1 || MaxFps > 10 || TrackTimeoutMs < 1 || TrackTimeoutMs > 60000)
            throw new InvalidOperationException("Invalid vehicle color settings.");
    }
}
