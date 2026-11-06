namespace HshDetectionEngin;

public sealed class RecognitionDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Recognition";
    public bool Enabled { get; set; } = true;
    public long Revision { get; set; } = 1;
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public AnalysisKind Kind { get; set; } = AnalysisKind.Face;
    public string ModelFile { get; set; } = "face_recognition_sface_2021dec.onnx";
    public float Confidence { get; set; } = .40f;
    public float UnknownMatchThreshold { get; set; } = .35f;
    public int Threads { get; set; } = 1;
    public override string ToString() => Name;
}

public sealed class SampleBufferSettings
{
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public AnalysisKind Kind { get; set; } = AnalysisKind.Face;
    public int Capacity { get; set; } = 32;
    public long MaxMemoryBytes { get; set; } = 128L * 1024 * 1024;
    public int MaxSampleAgeMs { get; set; } = 5000;
    public int MinTrackObservations { get; set; } = 1;
    public int MinEnqueueIntervalMs { get; set; } = 250;
    public int RetryDelayMs { get; set; } = 500;
    public int MaxAttemptsPerTrack { get; set; } = 5;
    public int RefreshIntervalMs { get; set; }
}

public sealed class TrackingCompletionSettings
{
    public long MaxMemoryBytes { get; set; } = 256L * 1024 * 1024;
    public List<SampleBufferSettings> SampleTypes { get; set; } =
    [new() { Kind = AnalysisKind.Face }, new() { Kind = AnalysisKind.Plate, Capacity = 64 }];

    public static void Validate(CameraSettings camera)
    {
        var settings = camera.TrackingCompletion;
        if (settings.MaxMemoryBytes <= 0 || settings.SampleTypes.Any(s => s is null ||
            s.Kind is not (AnalysisKind.Face or AnalysisKind.Plate) || s.Capacity < 1 || s.Capacity > 4096 ||
            s.MaxMemoryBytes < 1 || s.MaxSampleAgeMs < 1 || s.MinTrackObservations < 1 ||
            s.MinEnqueueIntervalMs < 0 || s.RetryDelayMs < 0 || s.MaxAttemptsPerTrack < 1 || s.RefreshIntervalMs < 0) ||
            settings.SampleTypes.GroupBy(s => s.Kind).Any(g => g.Count() != 1) ||
            settings.SampleTypes.Sum(s => (decimal)s.MaxMemoryBytes) > settings.MaxMemoryBytes)
            throw new InvalidOperationException("Invalid sample buffer capacity, memory budget or retry settings.");
        if (camera.RecognitionDefinitions.Any(d => d is null || string.IsNullOrWhiteSpace(d.Id) ||
            string.IsNullOrWhiteSpace(d.Name) || d.Kind is not (AnalysisKind.Face or AnalysisKind.Plate) ||
            d.Revision < 1 || !float.IsFinite(d.Confidence) || !float.IsFinite(d.UnknownMatchThreshold) ||
            d.Confidence <= 0 || d.Confidence > 1 || d.UnknownMatchThreshold <= 0 || d.UnknownMatchThreshold > 1 ||
            d.Threads < 1 || string.IsNullOrWhiteSpace(d.ModelFile)) ||
            camera.RecognitionDefinitions.GroupBy(d => d.Id, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new InvalidOperationException("Invalid or duplicate camera recognition definition.");
        foreach (var roi in camera.Rois.Where(r => r.Enabled))
        foreach (var task in roi.Processing.Where(t => t.Enabled))
        {
            if (task.ExecutionMode == "Inline") continue;
            if (task.ExecutionMode != "TrackAndQueue") throw new InvalidOperationException("Unknown recognition execution mode.");
            var kind = task.Kind == ProcessingType.Face ? AnalysisKind.Face : task.Kind == ProcessingType.Plate ? AnalysisKind.Plate : AnalysisKind.Unknown;
            if (kind == AnalysisKind.Unknown || !settings.SampleTypes.Any(s => s.Kind == kind) ||
                !camera.RecognitionDefinitions.Any(d => d.Enabled && d.Id == task.RecognitionDefinitionId && d.Kind == kind))
                throw new InvalidOperationException($"{roi.Name} / {task.Name}: select an enabled recognition destination of the same type.");
        }
    }
}
