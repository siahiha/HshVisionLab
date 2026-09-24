using System.Text.Json;
using System.Text.Json.Nodes;

namespace HshDetectionEngin;

public sealed class RoiPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public RoiPoint() { }
    public RoiPoint(double x, double y) { X = x; Y = y; }
}

public sealed class NamedRoi
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "ROI 1";
    public bool Enabled { get; set; } = true;
    /// <summary>Execution mode for processing items owned by this ROI.</summary>
    public string ProcessingMode { get; set; } = RoiProcessingModes.Sequential;
    public List<RoiPoint> Points { get; set; } = [];
    public List<CameraProcessingSettings> Processing { get; set; } = [];
}

public static class RoiProcessingModes
{
    public const string Sequential = "Sequential";
    public const string Parallel = "Parallel";

    public static string Normalize(string? value) =>
        string.Equals(value, Parallel, StringComparison.OrdinalIgnoreCase)
            ? Parallel
            : Sequential;
}

public sealed partial class CameraProcessingSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "Plate";
    public string Name { get; set; } = "Plate detection";
    public bool Enabled { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public ProcessingType Kind => ProcessingType.Parse(Type);

    /// <summary>Settings common to every processing module.</summary>
    public int MaxFps { get; set; } = 8;
    public int Threads { get; set; } = 1;
    public override string ToString() => $"{Name} {(Enabled ? "[Enabled]" : "[Disabled]")}";

    public CameraProcessingSettings Clone() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Type = Type,
        Name = Name,
        Enabled = Enabled,
        MaxFps = MaxFps,
        Threads = Threads,
        Options = Options?.DeepClone()?.AsObject() ?? []
    };
}

/// <summary>All settings that belong to one camera. Every camera owns an independent instance.</summary>
public class CameraSettings
{
    public const int ProcessingSchemaVersionCurrent = 3;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Camera 1";
    public string SourceUrl { get; set; } = "rtsp://192.168.1.100:554/stream";
    public string ModelFile { get; set; } = "best.onnx";
    public bool PlateEnabled { get; set; } = true;
    public List<CameraProcessingSettings> Processing { get; set; } = [];
    public string FaceModelFile { get; set; } = "face_yunet_2023mar.onnx";
    public bool FaceEnabled { get; set; }
    public int FaceInputSize { get; set; } = 640;
    public float FaceConfidence { get; set; } = 0.8f;
    public float FaceRecordConfidence { get; set; } = 0.8f;
    public bool FaceRecognitionEnabled { get; set; } = true;
    public string FaceRecognitionModelFile { get; set; } = "face_recognition_sface_2021dec.onnx";
    public float FaceRecognitionThreshold { get; set; } = 0.40f;
    public float FaceMatchIou { get; set; } = 0.25f;
    public int FaceTrackMaxMisses { get; set; } = 10;
    public string FacePreprocessing { get; set; } = "None";
    public int FaceMaxFps { get; set; } = 8;
    public float FaceNmsThreshold { get; set; } = 0.3f;
    public int FaceTopK { get; set; } = 5000;
    public float FaceUnknownMatchThreshold { get; set; } = 0.35f;
    public int FaceEventCooldownSeconds { get; set; } = 60;
    public string Transport { get; set; } = "TCP";
    /// <summary>Frame receiver to use: FFmpeg, LibVLC, or MediaMTX.</summary>
    public string CaptureBackend { get; set; } = "FFmpeg";
    /// <summary>Capture queue capacity shared by the Plate and Face pipelines. 0 keeps only the newest frame.</summary>
    public int BufferCount { get; set; } = 0;
    public int ReconnectDelaySec { get; set; } = 3;
    public float Confidence { get; set; } = 0.35f;
    public float NmsIoU { get; set; } = 0.45f;
    public int MaxFps { get; set; } = 8;
    public int InputSize { get; set; } = 416;
    public int Threads { get; set; } = 1;
    public bool DrawBoxes { get; set; } = true;
    /// <summary>How long the latest detection overlay remains visible, in milliseconds.</summary>
    public int DetectionOverlayHoldMs { get; set; } = 2500;
    public bool MotionGateEnabled { get; set; } = true;
    public int MotionFps { get; set; } = 8;
    public double MotionThreshold { get; set; } = 20;
    public double MotionChangedPercent { get; set; } = 0.7;
    /// <summary>Motion polygon scale relative to the detection ROI. 100 = same size.</summary>
    public double MotionRoiScalePercent { get; set; } = 85;
    public int MotionHoldMs { get; set; } = 1200;
    public int ActiveDetectionFps { get; set; } = 8;
    public int IdleDetectionFps { get; set; } = 0;
    public double RoiLeft { get; set; } = 0.0;
    public double RoiTop { get; set; } = 0.0;
    public double RoiRight { get; set; } = 1.0;
    public double RoiBottom { get; set; } = 1.0;
    public bool RoiEnabled { get; set; } = true;
    public List<RoiPoint> RoiPolygon { get; set; } = [];
    public List<NamedRoi> Rois { get; set; } = [];
    public int TrackMaxMisses { get; set; } = 6;
    public string PlatePreprocessing { get; set; } = "Standard";
    public int ProcessingSchemaVersion { get; set; } = ProcessingSchemaVersionCurrent;

    public CameraSettings Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonOpts);
        return JsonSerializer.Deserialize<CameraSettings>(json, JsonOpts) ?? new CameraSettings();
    }

    public void EnsureProcessingDefaults()
    {
        Processing ??= [];
        Rois ??= [];
        RoiPolygon ??= [];
        NormalizeProcessingItems(Processing);

        // ROI names are the configuration key used by older clients and are
        // still exposed in event metadata. Normalize them here so an empty or
        // duplicated name can never disconnect a web ROI from its pipeline.
        var usedRoiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int roiIndex = 0; roiIndex < Rois.Count; roiIndex++)
        {
            NamedRoi roi = Rois[roiIndex];
            roi.Id = string.IsNullOrWhiteSpace(roi.Id) ? Guid.NewGuid().ToString("N") : roi.Id;
            string requestedName = string.IsNullOrWhiteSpace(roi.Name) ? $"ROI {roiIndex + 1}" : roi.Name.Trim();
            string normalizedName = requestedName;
            int suffix = 2;
            while (!usedRoiNames.Add(normalizedName))
                normalizedName = $"{requestedName} ({suffix++})";
            roi.Name = normalizedName;
            roi.ProcessingMode = RoiProcessingModes.Normalize(roi.ProcessingMode);
            roi.Processing ??= [];
            NormalizeProcessingItems(roi.Processing);
        }
        bool migratedProcessingSchema = ProcessingSchemaVersion < ProcessingSchemaVersionCurrent;
        bool hasLegacyProcessing = Processing.Count > 0;

        // Older files stored the detection polygon directly on CameraSettings.
        // Materialize it before migrating camera-level processing so the legacy
        // processing items can be attached to the same named ROI. If an old
        // processing list has no polygon, preserve its behavior with the old
        // rectangle/full-frame bounds instead of dropping the list.
        if (Rois.Count == 0 && (hasLegacyProcessing || RoiPolygon.Count >= 3))
        {
            List<RoiPoint> points = RoiPolygon
                .Where(point => point is not null)
                .Select(point => new RoiPoint(Math.Clamp(point.X, 0, 1), Math.Clamp(point.Y, 0, 1)))
                .ToList();
            if (points.Count < 3 && hasLegacyProcessing)
            {
                double left = Math.Clamp(RoiLeft, 0, 0.98);
                double top = Math.Clamp(RoiTop, 0, 0.98);
                double right = Math.Clamp(RoiRight, left + 0.01, 1);
                double bottom = Math.Clamp(RoiBottom, top + 0.01, 1);
                points =
                [
                    new RoiPoint(left, top),
                    new RoiPoint(right, top),
                    new RoiPoint(right, bottom),
                    new RoiPoint(left, bottom)
                ];
            }

            if (points.Count >= 3)
            {
                Rois.Add(new NamedRoi
                {
                    Name = "ROI 1",
                    Enabled = RoiEnabled,
                    Points = points
                });
            }
        }

        // Processing is now ROI-only. Migrate any old camera-level list to the
        // existing ROIs, then remove it so a camera without an ROI never runs
        // inference on the full frame.
        if (Processing.Count > 0)
        {
            CameraProcessingSettings[] legacyProcessing = Processing
                .Where(item => item is not null)
                .Select(CloneProcessingItem)
                .ToArray();

            if (Rois.Count > 0 && legacyProcessing.Length > 0)
            {
                foreach (NamedRoi roi in Rois)
                {
                    roi.Processing ??= [];
                    foreach (CameraProcessingSettings item in legacyProcessing)
                    {
                        if (!roi.Processing.Any(existing => existing.Id == item.Id))
                        {
                            roi.Processing.Add(item.Clone());
                        }
                    }
                }

                // Only clear the old list after every item has a destination.
                Processing.Clear();
            }
        }

        if (migratedProcessingSchema)
        {
            CameraProcessingSettings[] legacyDefaults = [];
            if (PlateEnabled) legacyDefaults = [CreateProcessingItem(ProcessingType.Plate)];
            if (FaceEnabled) legacyDefaults = [.. legacyDefaults, CreateProcessingItem(ProcessingType.Face)];

            foreach (NamedRoi roi in Rois)
            {
                roi.Processing ??= [];
                if (roi.Processing.Count == 0 && legacyDefaults.Length > 0)
                {
                    roi.Processing = legacyDefaults.Select(item => item.Clone()).ToList();
                }

                for (int index = 0; index < roi.Processing.Count; index++)
                {
                    roi.Processing[index] = CloneProcessingItem(roi.Processing[index]);
                }
            }
        }

        ProcessingSchemaVersion = ProcessingSchemaVersionCurrent;

        foreach (NamedRoi roi in Rois)
        {
            roi.Processing ??= [];
        }

        bool anyPlate = Processing.Concat(Rois.SelectMany(roi => roi.Processing))
            .Any(item => item.Enabled && item.Kind == ProcessingType.Plate);
        bool anyFace = Processing.Concat(Rois.SelectMany(roi => roi.Processing))
            .Any(item => item.Enabled && item.Kind == ProcessingType.Face);
        PlateEnabled = anyPlate;
        FaceEnabled = anyFace;
    }

    private void NormalizeProcessingItems(List<CameraProcessingSettings> items)
    {
        foreach (CameraProcessingSettings item in items.Where(item => item is not null))
        {
            item.Options ??= [];
            item.Type = NormalizeProcessingType(item.Type);
            item.Name = string.IsNullOrWhiteSpace(item.Name)
                ? item.Kind == ProcessingType.Face ? "Face detection" : "Plate detection"
                : item.Name.Trim();
            item.MaxFps = Math.Clamp(
                item.MaxFps <= 0 ? item.Kind == ProcessingType.Face ? FaceMaxFps : MaxFps : item.MaxFps,
                1,
                60);
            item.Threads = Math.Clamp(item.Threads <= 0 ? Threads : item.Threads, 1, 8);
            item.MigrateLegacyOptions();
        }
    }

    private static string NormalizeProcessingType(string? value)
    {
        ProcessingType parsed = ProcessingType.Parse(value);
        if (parsed == ProcessingType.Face) return ProcessingType.Face.Value;
        if (parsed == ProcessingType.Plate || string.IsNullOrWhiteSpace(parsed.Value)) return ProcessingType.Plate.Value;
        return parsed.Value;
    }

    private static CameraProcessingSettings CloneProcessingItem(CameraProcessingSettings item)
    {
        // A legacy processing item already contains the user's model and
        // thresholds. Migration must preserve those values; camera settings
        // are used only when a brand-new item is created below.
        return item.Clone();
    }

    public CameraProcessingSettings CreateProcessingItem(ProcessingType type, string? name = null)
    {
        ProcessingType normalizedType = string.IsNullOrWhiteSpace(type.Value) ? ProcessingType.Plate : type;
        bool face = normalizedType == ProcessingType.Face;
        return new CameraProcessingSettings
        {
            Type = normalizedType.Value,
            Name = name ?? (face ? "Face detection" : normalizedType.Value),
            Enabled = true,
            MaxFps = face ? FaceMaxFps : MaxFps,
            Threads = Threads,
            Options = face
                ? CreateFaceProcessingOptions()
                : normalizedType == ProcessingType.Plate
                    ? CreatePlateProcessingOptions()
                    : []
        };
    }

    private JsonObject CreatePlateProcessingOptions() =>
        JsonSerializer.SerializeToNode(new PlateProcessingOptions
        {
            ModelFile = ModelFile,
            InputSize = InputSize,
            Preprocessing = PlatePreprocessing,
            Confidence = Confidence,
            NmsIoU = NmsIoU,
            TrackMaxMisses = TrackMaxMisses
        }, JsonOpts)?.AsObject() ?? [];

    private JsonObject CreateFaceProcessingOptions() =>
        JsonSerializer.SerializeToNode(new FaceProcessingOptions
        {
            ModelFile = FaceModelFile,
            InputSize = FaceInputSize,
            Preprocessing = FacePreprocessing,
            Confidence = FaceConfidence,
            RecordConfidence = FaceRecordConfidence,
            RecognitionEnabled = FaceRecognitionEnabled,
            RecognitionModelFile = FaceRecognitionModelFile,
            RecognitionThreshold = FaceRecognitionThreshold,
            MatchIou = FaceMatchIou,
            TrackMaxMisses = FaceTrackMaxMisses,
            NmsThreshold = FaceNmsThreshold,
            TopK = FaceTopK,
            UnknownMatchThreshold = FaceUnknownMatchThreshold,
            EventCooldownSeconds = FaceEventCooldownSeconds
        }, JsonOpts)?.AsObject() ?? [];

    public CameraSettings CreateEffectiveSettings(CameraProcessingSettings item)
    {
        CameraSettings effective = Clone();
        effective.MaxFps = item.MaxFps;
        effective.Threads = item.Threads;
        if (item.Kind == ProcessingType.Face)
        {
            FaceProcessingOptions face = item.GetOptions<FaceProcessingOptions>();
            effective.FaceModelFile = face.ModelFile;
            effective.FaceInputSize = face.InputSize;
            effective.FacePreprocessing = face.Preprocessing;
            effective.FaceConfidence = face.Confidence;
            effective.FaceRecordConfidence = face.RecordConfidence;
            effective.FaceRecognitionEnabled = face.RecognitionEnabled;
            effective.FaceRecognitionModelFile = face.RecognitionModelFile;
            effective.FaceRecognitionThreshold = face.RecognitionThreshold;
            effective.FaceMatchIou = face.MatchIou;
            effective.FaceTrackMaxMisses = face.TrackMaxMisses;
            effective.FaceNmsThreshold = face.NmsThreshold;
            effective.FaceTopK = face.TopK;
            effective.FaceUnknownMatchThreshold = face.UnknownMatchThreshold;
            effective.FaceEventCooldownSeconds = face.EventCooldownSeconds;
        }
        else
        {
            PlateProcessingOptions plate = item.GetOptions<PlateProcessingOptions>();
            effective.ModelFile = plate.ModelFile;
            effective.InputSize = plate.InputSize;
            effective.PlatePreprocessing = plate.Preprocessing;
            effective.Confidence = plate.Confidence;
            effective.NmsIoU = plate.NmsIoU;
            effective.TrackMaxMisses = plate.TrackMaxMisses;
        }
        return effective;
    }

    public List<CameraProcessingSettings> CreateRoiProcessingDefaults()
    {
        return Processing
            .Select(item => item.Clone())
            .ToList();
    }

    public IReadOnlyList<CameraProcessingSettings> GetProcessingForTarget(string targetName)
    {
        return Rois.FirstOrDefault(roi => roi.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))?.Processing ?? [];
    }

    public bool IsProcessingEnabledForRoi(string roiName, ProcessingType type)
    {
        if (Rois.Count == 0)
        {
            return Processing.Any(item => item.Enabled &&
                item.Kind == type);
        }

        NamedRoi? roi = Rois.FirstOrDefault(item =>
            item.Name.Equals(roiName, StringComparison.OrdinalIgnoreCase));
        return roi is not null && roi.Processing.Any(item => item.Enabled &&
            item.Kind == type);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
}

public sealed class AppSettings
{
    // New multi-camera configuration. Each item is completely independent.
    public List<CameraSettings> Cameras { get; set; } = [];
    public string? SelectedCameraId { get; set; }

    // Legacy single-camera fields are retained so old settings.json files migrate cleanly.
    public string SourceUrl { get; set; } = "rtsp://192.168.1.100:554/stream";
    public string ModelFile { get; set; } = "best.onnx";
    public bool PlateEnabled { get; set; } = true;
    public string FaceModelFile { get; set; } = "face_yunet_2023mar.onnx";
    public bool FaceEnabled { get; set; }
    public int FaceInputSize { get; set; } = 320;
    public float FaceConfidence { get; set; } = 0.8f;
    public float FaceRecordConfidence { get; set; } = 0.8f;
    public bool FaceRecognitionEnabled { get; set; } = true;
    public string FaceRecognitionModelFile { get; set; } = "face_recognition_sface_2021dec.onnx";
    public float FaceRecognitionThreshold { get; set; } = 0.40f;
    public float FaceMatchIou { get; set; } = 0.25f;
    public int FaceTrackMaxMisses { get; set; } = 10;
    public string FacePreprocessing { get; set; } = "None";
    public int FaceMaxFps { get; set; } = 8;
    public float FaceNmsThreshold { get; set; } = 0.3f;
    public int FaceTopK { get; set; } = 5000;
    public float FaceUnknownMatchThreshold { get; set; } = 0.35f;
    public int FaceEventCooldownSeconds { get; set; } = 60;
    public string Transport { get; set; } = "TCP";
    public string CaptureBackend { get; set; } = "FFmpeg";
    /// <summary>Capture queue capacity shared by the Plate and Face pipelines. 0 keeps only the newest frame.</summary>
    public int BufferCount { get; set; } = 0;
    public int ReconnectDelaySec { get; set; } = 3;
    public float Confidence { get; set; } = 0.35f;
    public float NmsIoU { get; set; } = 0.45f;
    public int MaxFps { get; set; } = 8;
    public int InputSize { get; set; } = 416;
    public int Threads { get; set; } = 1;
    public bool DrawBoxes { get; set; } = true;
    public int DetectionOverlayHoldMs { get; set; } = 2500;
    public bool MotionGateEnabled { get; set; } = true;
    public int MotionFps { get; set; } = 8;
    public double MotionThreshold { get; set; } = 20;
    public double MotionChangedPercent { get; set; } = 0.7;
    public double MotionRoiScalePercent { get; set; } = 85;
    public int MotionHoldMs { get; set; } = 1200;
    public int ActiveDetectionFps { get; set; } = 8;
    public int IdleDetectionFps { get; set; } = 0;
    public double RoiLeft { get; set; } = 0;
    public double RoiTop { get; set; } = 0;
    public double RoiRight { get; set; } = 1;
    public double RoiBottom { get; set; } = 1;
    public bool RoiEnabled { get; set; } = true;
    public List<RoiPoint> RoiPolygon { get; set; } = [];
    public List<NamedRoi> Rois { get; set; } = [];
    public int TrackMaxMisses { get; set; } = 6;
    public string PlatePreprocessing { get; set; } = "Standard";

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
                loaded.Cameras ??= [];
                using var document = JsonDocument.Parse(json);
                MigrateLegacyThreadSetting(document.RootElement, loaded);
                if (document.RootElement.TryGetProperty("Cameras", out JsonElement camerasElement) && camerasElement.ValueKind == JsonValueKind.Array)
                {
                    JsonElement[] cameraJson = camerasElement.EnumerateArray().ToArray();
                    for (int index = 0; index < loaded.Cameras.Count && index < cameraJson.Length; index++)
                    {
                        if (!cameraJson[index].TryGetProperty("ProcessingSchemaVersion", out _))
                        {
                            loaded.Cameras[index].ProcessingSchemaVersion = 0;
                        }
                    }
                }
                if (loaded.Cameras.Count == 0)
                {
                    var legacy = new CameraSettings
                    {
                        Name = "Camera 1", SourceUrl = loaded.SourceUrl, ModelFile = loaded.ModelFile, PlateEnabled = loaded.PlateEnabled,
                        FaceModelFile = loaded.FaceModelFile, FaceEnabled = loaded.FaceEnabled, FaceInputSize = loaded.FaceInputSize,
                        FaceConfidence = loaded.FaceConfidence, FaceMatchIou = loaded.FaceMatchIou, FaceTrackMaxMisses = loaded.FaceTrackMaxMisses,
                        FaceRecordConfidence = loaded.FaceRecordConfidence,
                        FacePreprocessing = loaded.FacePreprocessing,
                        FaceMaxFps = loaded.FaceMaxFps,
                        FaceNmsThreshold = loaded.FaceNmsThreshold, FaceTopK = loaded.FaceTopK,
                        FaceUnknownMatchThreshold = loaded.FaceUnknownMatchThreshold, FaceEventCooldownSeconds = loaded.FaceEventCooldownSeconds,
                        FaceRecognitionEnabled = loaded.FaceRecognitionEnabled, FaceRecognitionModelFile = loaded.FaceRecognitionModelFile,
                        FaceRecognitionThreshold = loaded.FaceRecognitionThreshold,
                        Transport = loaded.Transport,
                        CaptureBackend = loaded.CaptureBackend,
                        BufferCount = loaded.BufferCount, ReconnectDelaySec = loaded.ReconnectDelaySec,
                        Confidence = loaded.Confidence, NmsIoU = loaded.NmsIoU, MaxFps = loaded.MaxFps,
                        InputSize = loaded.InputSize, Threads = loaded.Threads, DrawBoxes = loaded.DrawBoxes,
                        DetectionOverlayHoldMs = loaded.DetectionOverlayHoldMs,
                        MotionGateEnabled = loaded.MotionGateEnabled, MotionFps = loaded.MotionFps,
                        MotionThreshold = loaded.MotionThreshold, MotionChangedPercent = loaded.MotionChangedPercent,
                        MotionRoiScalePercent = loaded.MotionRoiScalePercent,
                        MotionHoldMs = loaded.MotionHoldMs, ActiveDetectionFps = loaded.ActiveDetectionFps,
                        IdleDetectionFps = loaded.IdleDetectionFps, RoiLeft = loaded.RoiLeft, RoiTop = loaded.RoiTop,
                        RoiRight = loaded.RoiRight, RoiBottom = loaded.RoiBottom, RoiEnabled = loaded.RoiEnabled,
                        RoiPolygon = loaded.RoiPolygon ?? [], Rois = loaded.Rois ?? [], TrackMaxMisses = loaded.TrackMaxMisses,
                        PlatePreprocessing = loaded.PlatePreprocessing,
                        ProcessingSchemaVersion = 0
                    };
                    legacy.EnsureProcessingDefaults();
                    loaded.Cameras.Add(legacy);
                    loaded.SelectedCameraId = legacy.Id;
                }
                else
                {
                    foreach (var c in loaded.Cameras)
                    {
                        c.Id = string.IsNullOrWhiteSpace(c.Id) ? Guid.NewGuid().ToString("N") : c.Id;
                        c.FaceModelFile ??= "face_yunet_2023mar.onnx";
                        c.FaceInputSize = c.FaceInputSize <= 0 ? 640 : c.FaceInputSize;
                        c.FaceConfidence = c.FaceConfidence <= 0 ? 0.8f : c.FaceConfidence;
                        c.FaceRecordConfidence = c.FaceRecordConfidence <= 0 ? 0.8f : c.FaceRecordConfidence;
                        c.FaceRecognitionModelFile ??= "face_recognition_sface_2021dec.onnx";
                        c.FaceRecognitionThreshold = c.FaceRecognitionThreshold <= 0 ? 0.40f : c.FaceRecognitionThreshold;
                        c.CaptureBackend = string.Equals(c.CaptureBackend, "LibVLC", StringComparison.OrdinalIgnoreCase)
                            ? "LibVLC"
                            : string.Equals(c.CaptureBackend, "MediaMTX", StringComparison.OrdinalIgnoreCase)
                                ? "MediaMTX"
                                : "FFmpeg";
                        c.FaceMatchIou = c.FaceMatchIou <= 0 ? 0.25f : c.FaceMatchIou;
                        c.FaceTrackMaxMisses = c.FaceTrackMaxMisses <= 0 ? 10 : c.FaceTrackMaxMisses;
                        c.FacePreprocessing ??= "None";
                        c.FaceMaxFps = c.FaceMaxFps <= 0 ? 8 : c.FaceMaxFps;
                        c.FaceNmsThreshold = c.FaceNmsThreshold <= 0 ? 0.3f : c.FaceNmsThreshold;
                        c.FaceTopK = c.FaceTopK <= 0 ? 5000 : c.FaceTopK;
                        c.FaceUnknownMatchThreshold = c.FaceUnknownMatchThreshold <= 0 ? 0.35f : c.FaceUnknownMatchThreshold;
                        c.FaceEventCooldownSeconds = c.FaceEventCooldownSeconds <= 0 ? 60 : c.FaceEventCooldownSeconds;
                        c.Rois ??= []; c.RoiPolygon ??= [];
                        c.EnsureProcessingDefaults();
                    }
                    loaded.SelectedCameraId ??= loaded.Cameras[0].Id;
                }
                return loaded;
            }
        }
        catch { }

        return new AppSettings();
    }

    private static void MigrateLegacyThreadSetting(JsonElement root, AppSettings settings)
    {
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("FaceThreads", out JsonElement legacyRootThreads) &&
            !root.TryGetProperty("Threads", out _) && legacyRootThreads.TryGetInt32(out int rootThreads))
        {
            settings.Threads = rootThreads;
        }

        if (!root.TryGetProperty("Cameras", out JsonElement cameras) || cameras.ValueKind != JsonValueKind.Array)
            return;

        for (int i = 0; i < settings.Cameras.Count && i < cameras.GetArrayLength(); i++)
        {
            JsonElement camera = cameras[i];
            if (camera.ValueKind != JsonValueKind.Object || camera.TryGetProperty("Threads", out _)) continue;
            if (camera.TryGetProperty("FaceThreads", out JsonElement legacyThreads) && legacyThreads.TryGetInt32(out int threads))
                settings.Cameras[i].Threads = threads;
        }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts)); }
        catch { }
    }
}

