using System.Text.Json;
using System.Text.Json.Nodes;

namespace HshDetectionEngin;

/// <summary>Settings owned by the plate processing module.</summary>
public sealed class PlateProcessingOptions
{
    public string ModelFile { get; set; } = "best.onnx";
    public int InputSize { get; set; } = 416;
    public string Preprocessing { get; set; } = "Standard";
    public float Confidence { get; set; } = 0.35f;
    public float NmsIoU { get; set; } = 0.45f;
    public int TrackMaxMisses { get; set; } = 6;
}

/// <summary>Settings owned by the face processing module.</summary>
public sealed class FaceProcessingOptions
{
    public string ModelFile { get; set; } = "face_yunet_2023mar.onnx";
    public int InputSize { get; set; } = 320;
    public string Preprocessing { get; set; } = "None";
    public float Confidence { get; set; } = 0.80f;
    public float RecordConfidence { get; set; } = 0.80f;
    public bool RecognitionEnabled { get; set; } = true;
    public string RecognitionModelFile { get; set; } = "face_recognition_sface_2021dec.onnx";
    public float RecognitionThreshold { get; set; } = 0.40f;
    public float MatchIou { get; set; } = 0.25f;
    public int TrackMaxMisses { get; set; } = 10;
    public float NmsThreshold { get; set; } = 0.30f;
    public int TopK { get; set; } = 5000;
    public float UnknownMatchThreshold { get; set; } = 0.35f;
    public int EventCooldownSeconds { get; set; } = 60;
}

public sealed partial class CameraProcessingSettings
{
    private static readonly JsonSerializerOptions OptionsJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// JSON is the persistence boundary for module-owned settings. A module
    /// parses this once while its pipeline is created, never in Process().
    /// </summary>
    public JsonObject Options { get; set; } = [];

    /// <summary>Captures pre-v3 properties so old settings can be migrated.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? LegacyProperties { get; set; }

    public T GetOptions<T>() where T : new()
    {
        try
        {
            return Options.Deserialize<T>(OptionsJson) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }

    public object? GetOptions(Type optionsType)
    {
        ArgumentNullException.ThrowIfNull(optionsType);
        try
        {
            return Options.Deserialize(optionsType, OptionsJson) ?? CreateOptionsInstance(optionsType);
        }
        catch (JsonException)
        {
            return CreateOptionsInstance(optionsType);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void SetOptions<T>(T value)
    {
        Options = JsonSerializer.SerializeToNode(value, OptionsJson)?.AsObject() ?? [];
    }

    public void SetOptions(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Options = JsonSerializer.SerializeToNode(value, value.GetType(), OptionsJson)?.AsObject() ?? [];
    }

    internal void MigrateLegacyOptions()
    {
        if (Options.Count > 0)
        {
            LegacyProperties = null;
            return;
        }

        bool face = Kind == ProcessingType.Face;
        if (face)
        {
            FaceProcessingOptions value = new()
            {
                ModelFile = Legacy("FaceModelFile", "face_yunet_2023mar.onnx"),
                InputSize = Legacy("FaceInputSize", 320),
                Preprocessing = Legacy("FacePreprocessing", "None"),
                Confidence = Legacy("FaceConfidence", 0.80f),
                RecordConfidence = Legacy("FaceRecordConfidence", 0.80f),
                RecognitionEnabled = Legacy("FaceRecognitionEnabled", true),
                RecognitionModelFile = Legacy("FaceRecognitionModelFile", "face_recognition_sface_2021dec.onnx"),
                RecognitionThreshold = Legacy("FaceRecognitionThreshold", 0.40f),
                MatchIou = Legacy("FaceMatchIou", 0.25f),
                TrackMaxMisses = Legacy("FaceTrackMaxMisses", 10),
                NmsThreshold = Legacy("FaceNmsThreshold", 0.30f),
                TopK = Legacy("FaceTopK", 5000),
                UnknownMatchThreshold = Legacy("FaceUnknownMatchThreshold", 0.35f),
                EventCooldownSeconds = Legacy("FaceEventCooldownSeconds", 60)
            };
            SetOptions(value);
        }
        else if (Kind == ProcessingType.Plate)
        {
            PlateProcessingOptions value = new()
            {
                ModelFile = Legacy("ModelFile", "best.onnx"),
                InputSize = Legacy("InputSize", 416),
                Preprocessing = Legacy("Preprocessing", "Standard"),
                Confidence = Legacy("Confidence", 0.35f),
                NmsIoU = Legacy("NmsIoU", 0.45f),
                TrackMaxMisses = Legacy("TrackMaxMisses", 6)
            };
            SetOptions(value);
        }
        else
        {
            // Unknown processing types own their own options contract. Do not
            // guess that a future module is a Plate module.
            LegacyProperties = null;
            return;
        }

        MaxFps = Legacy("MaxFps", MaxFps);
        Threads = Legacy("Threads", Threads);
        LegacyProperties = null;
    }

    private T Legacy<T>(string name, T fallback)
    {
        if (LegacyProperties is null) return fallback;
        KeyValuePair<string, JsonElement>? entry = LegacyProperties
            .FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return fallback;
        try { return entry.Value.Value.Deserialize<T>(OptionsJson) ?? fallback; }
        catch (JsonException) { return fallback; }
    }

    private static object? CreateOptionsInstance(Type optionsType)
    {
        try { return Activator.CreateInstance(optionsType); }
        catch { return null; }
    }
}
