using HshDetectionEngin.Licensing;

namespace HshDetectionEngin.Palm;

/// <summary>
/// Registers palm localization and optional palmprint identity recognition with
/// the common camera processing registry.
/// </summary>
public sealed class PalmModule
{
    private readonly PalmDatabase? _database;
    private readonly LicenseValidationResult? _license;

    public const string Id = "palm";
    public string DisplayName => "Palm Detection and Recognition";
    public string ProtectedModelDirectory => Path.Combine("Models", "Palm");

    public PalmModule(PalmDatabase database, LicenseValidationResult license)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _license = license ?? throw new ArgumentNullException(nameof(license));
    }

    public ProcessingModuleRegistration CreateRegistration()
    {
        if (_database is null || _license is null)
            throw new InvalidOperationException("PalmModule requires a database and license before registration.");

        return new ProcessingModuleRegistration(
            ProcessingType.Palm,
            "Palm detection and recognition",
            AnalysisKind.Palm,
            (_, item) =>
            {
                PalmPipeline? pipeline = CreatePipeline(item);
                return pipeline is null ? Array.Empty<IProcessingPipeline>() : [pipeline];
            },
            typeof(PalmProcessingOptions),
            "palm",
            item =>
            {
                PalmProcessingOptions options = item.GetOptions<PalmProcessingOptions>();
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["OverlayThreshold"] = options.DetectionConfidence,
                    ["PalmRecordConfidence"] = options.RecordConfidence,
                    ["PalmEventCooldownSeconds"] = options.EventCooldownSeconds
                };
            },
            availabilityMessage: _license.Allows(LicensedFeature.Palm)
                ? null
                : $"Palm processing is unavailable: {(_license.Message.Contains("Palm", StringComparison.OrdinalIgnoreCase) ? _license.Message : "the license does not include the Palm feature.")}");
    }

    public PalmPipeline? CreatePipeline(
        CameraProcessingSettings item,
        int? maxFpsOverride = null,
        bool requireRecognition = false)
    {
        if (_database is null || _license is null || !_license.Allows(LicensedFeature.Palm))
            return null;

        PalmProcessingOptions options = item.GetOptions<PalmProcessingOptions>();
        string? detectorPath = PalmModelPaths.Find(options.DetectorModelFile);
        if (detectorPath is null) return null;

        string? recognitionPath = PalmModelPaths.Find(options.RecognitionModelFile);
        if (requireRecognition && (!options.RecognitionEnabled || recognitionPath is null))
            return null;
        if (!options.RecognitionEnabled || recognitionPath is null) recognitionPath = null;

        return new PalmPipeline(
            detectorPath,
            new PalmPipelineOptions
            {
                DetectorKind = InferDetectorKind(options.DetectorModelFile, options.DetectorKind),
                DetectorInputSize = InferDetectorKind(options.DetectorModelFile, options.DetectorKind).Equals("RTMDet", StringComparison.OrdinalIgnoreCase)
                    ? 320
                    : options.DetectorInputSize,
                DetectionConfidence = options.DetectionConfidence,
                NmsIoU = options.NmsIoU,
                MaxHands = options.MaxHands,
                RecognitionInputSize = options.RecognitionInputSize,
                RecognitionEnabled = options.RecognitionEnabled,
                RecognitionThreshold = options.RecognitionThreshold,
                MatchIou = options.MatchIou,
                TrackMaxMisses = options.TrackMaxMisses
            },
            recognitionPath,
            _database,
            _license);
    }

    private static string InferDetectorKind(string modelFile, string configuredKind)
    {
        if (modelFile.Contains("rtmdet", StringComparison.OrdinalIgnoreCase) ||
            modelFile.Contains("hand", StringComparison.OrdinalIgnoreCase)) return "RTMDet";
        if (modelFile.Contains("blaze", StringComparison.OrdinalIgnoreCase) ||
            modelFile.Contains("palm", StringComparison.OrdinalIgnoreCase)) return "BlazePalm";
        return configuredKind;
    }
}
