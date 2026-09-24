using System.Drawing;
using System.Text.Json;

namespace HshDetectionEngin.Plate;

/// <summary>
/// The normalized result shared by every plate OCR decoder.
/// A decoder may provide character bounds when its model detects glyph boxes;
/// sequence and glyph-classification models can leave bounds null.
/// </summary>
internal sealed record PlateOcrCharacter(string Symbol, float Confidence, RectangleF? Bounds = null);

internal sealed record PlateOcrResult(
    string Text,
    float Confidence,
    IReadOnlyList<PlateOcrCharacter>? CharacterDetails = null)
{
    public IReadOnlyList<PlateOcrCharacter> Characters { get; } =
        CharacterDetails ?? Array.Empty<PlateOcrCharacter>();
}

/// <summary>Metadata contract for a model which can read an Iranian plate crop.</summary>
public sealed record PlateOcrModelDescriptor(
    string Name,
    string Decoder,
    string Alphabet,
    int InputWidth,
    int InputHeight);

/// <summary>
/// Catalogs OCR models by an explicit sidecar manifest. Filename inference is
/// retained only for the old shipped models which predate the manifest.
/// </summary>
public static class PlateOcrModelCatalog
{
    public const string Task = "plate_ocr";

    private static readonly HashSet<string> SupportedDecoders = new(StringComparer.OrdinalIgnoreCase)
    {
        "crnn_ctc",
        "cnn_glyph",
        "yolo_character"
    };

    public static bool IsOcrModel(string modelPath) => TryDescribe(modelPath, out _);

    public static IReadOnlyList<string> EnumerateModelNames(IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        return directories
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.onnx", SearchOption.TopDirectoryOnly))
            .Where(IsOcrModel)
            .Select(path => Path.GetFileName(path) ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool TryDescribe(string modelPath, out PlateOcrModelDescriptor descriptor)
    {
        descriptor = null!;
        if (string.IsNullOrWhiteSpace(modelPath) ||
            !modelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            return false;

        string? resolved = Path.IsPathRooted(modelPath)
            ? modelPath
            : PlateModelPaths.Find(modelPath);
        if (resolved is null || !File.Exists(resolved)) return false;

        if (TryReadManifest(resolved, out descriptor)) return true;

        // Compatibility for the models already distributed before the sidecar
        // contract was introduced. New models must provide a manifest.
        string stem = Path.GetFileNameWithoutExtension(resolved);
        string? decoder = stem.Contains("chars_best_v26", StringComparison.OrdinalIgnoreCase)
            ? "yolo_character"
            : stem.Contains("ocr_cnn", StringComparison.OrdinalIgnoreCase)
                ? "cnn_glyph"
                : stem.Contains("ocr_crnn", StringComparison.OrdinalIgnoreCase)
                    ? "crnn_ctc"
                    : null;
        if (decoder is null) return false;

        descriptor = new PlateOcrModelDescriptor(
            Path.GetFileName(resolved), decoder, "persian_plate", 0, 0);
        return true;
    }

    private static bool TryReadManifest(string modelPath, out PlateOcrModelDescriptor descriptor)
    {
        descriptor = null!;
        foreach (string manifestPath in ManifestCandidates(modelPath))
        {
            if (!File.Exists(manifestPath)) continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                JsonElement root = document.RootElement;
                string task = ReadString(root, "task");
                string decoder = ReadString(root, "decoder");
                if (!string.Equals(task, Task, StringComparison.OrdinalIgnoreCase) ||
                    !SupportedDecoders.Contains(decoder))
                    return false;

                descriptor = new PlateOcrModelDescriptor(
                    Path.GetFileName(modelPath),
                    decoder,
                    ReadString(root, "alphabet", "persian_plate"),
                    ReadInt(root, "inputWidth"),
                    ReadInt(root, "inputHeight"));
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        return false;
    }

    private static IEnumerable<string> ManifestCandidates(string modelPath)
    {
        yield return Path.ChangeExtension(modelPath, ".ocr.json");
        string stem = Path.GetFileNameWithoutExtension(modelPath);
        if (stem.EndsWith("_int8", StringComparison.OrdinalIgnoreCase))
        {
            string baseStem = stem[..^5];
            yield return Path.Combine(Path.GetDirectoryName(modelPath) ?? string.Empty, baseStem + ".ocr.json");
        }
    }

    private static string ReadString(JsonElement root, string property, string fallback = "") =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int ReadInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : 0;
}
