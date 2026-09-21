using HshDetectionEngin.Licensing;

namespace HshDetectionEngin.Face;

public sealed class FaceModule
{
    private readonly FaceDatabase? _database;
    private readonly LicenseValidationResult? _license;

    public const string Id = "face";
    public string DisplayName => "Face Detection, Tracking and Recognition";
    public string ProtectedModelDirectory => Path.Combine("Models", "Face");

    public FaceModule() { }

    public FaceModule(FaceDatabase database, LicenseValidationResult license)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _license = license ?? throw new ArgumentNullException(nameof(license));
    }

    public ProcessingModuleRegistration CreateRegistration()
    {
        if (_database is null || _license is null)
            throw new InvalidOperationException("FaceModule requires a database and license before registration.");

        return new ProcessingModuleRegistration(
            ProcessingType.Face,
            "Face detection",
            AnalysisKind.Face,
            (_, item) =>
            {
                FacePipeline? pipeline = CreatePipeline(item);
                return pipeline is null ? Array.Empty<IProcessingPipeline>() : [pipeline];
            },
            typeof(FaceProcessingOptions),
            "face",
            item =>
            {
                FaceProcessingOptions options = item.GetOptions<FaceProcessingOptions>();
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["OverlayThreshold"] = options.Confidence,
                    ["FaceRecordConfidence"] = options.RecordConfidence,
                    ["FaceEventCooldownSeconds"] = options.EventCooldownSeconds
                };
            },
            availabilityMessage: _license.Allows(LicensedFeature.Face) ? null : _license.Message);
    }

    public FacePipeline? CreatePipeline(
        CameraProcessingSettings item,
        int? maxFpsOverride = null,
        bool requireRecognition = false)
    {
        if (_database is null || _license is null || !_license.Allows(LicensedFeature.Face))
            return null;

        FaceProcessingOptions options = item.GetOptions<FaceProcessingOptions>();
        string? modelPath = FaceModelPaths.Find(options.ModelFile);
        if (modelPath is null) return null;

        string? recognitionPath = FaceModelPaths.Find(options.RecognitionModelFile);
        if (requireRecognition && (!options.RecognitionEnabled || recognitionPath is null))
            return null;
        if (!options.RecognitionEnabled || recognitionPath is null) recognitionPath = string.Empty;

        return new FacePipeline(
            modelPath,
            options.Confidence,
            new FacePipelineOptions
            {
                InputWidth = options.InputSize,
                InputHeight = options.InputSize,
                ConfidenceThreshold = options.Confidence,
                MatchIouThreshold = options.MatchIou,
                MaxMisses = options.TrackMaxMisses,
                Preprocessing = FacePreprocessor.Parse(options.Preprocessing),
                MaxFps = maxFpsOverride ?? item.MaxFps,
                Threads = item.Threads,
                NmsThreshold = options.NmsThreshold,
                TopK = options.TopK,
                UnknownMatchThreshold = options.UnknownMatchThreshold
            },
            recognitionPath,
            _database,
            options.RecognitionThreshold,
            _license);
    }

    public FacePipeline CreatePipeline(string modelPath, float confidence = 0.8f,
        string? recognitionModelPath = null, FaceDatabase? database = null, float recognitionThreshold = 0.40f,
        LicenseValidationResult? license = null)
        => new(modelPath, confidence, recognitionModelPath: recognitionModelPath,
            database: database, recognitionThreshold: recognitionThreshold, license: license);
}
