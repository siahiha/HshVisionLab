using System.Text.Json;

namespace HshDetectionEngin;

/// <summary>Versioned preprocessing contract shipped beside each color model.</summary>
public sealed class VehicleColorModelDescriptor
{
    public int SchemaVersion { get; set; } = 1;
    public string InputName { get; set; } = "input";
    public string OutputName { get; set; } = "color";
    public int InputWidth { get; set; } = 72;
    public int InputHeight { get; set; } = 72;
    public string ColorOrder { get; set; } = "BGR";
    public float Scale { get; set; } = 1;
    public float[] Mean { get; set; } = [0, 0, 0];
    public float[] Std { get; set; } = [1, 1, 1];
    public string OutputKind { get; set; } = "Probabilities";
    public int OutputOffset { get; set; }
    public string[] Labels { get; set; } = [];
    public void Validate()
    {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(InputName) || string.IsNullOrWhiteSpace(OutputName) ||
            InputWidth < 4 || InputWidth > 1024 || InputHeight < 4 || InputHeight > 1024 ||
            ColorOrder is not ("RGB" or "BGR") || OutputKind is not ("Probabilities" or "Logits" or "IndependentProbabilities") ||
            !float.IsFinite(Scale) || Scale <= 0 || Mean is null || Std is null || Labels is null || Mean.Length != 3 || Std.Length != 3 ||
            Mean.Any(v => !float.IsFinite(v)) || Std.Any(v => !float.IsFinite(v) || v <= 0) ||
            OutputOffset < 0 || OutputOffset > 1024 || Labels.Length < 2 || Labels.Length > 64 ||
            Labels.Any(string.IsNullOrWhiteSpace) || Labels.Distinct(StringComparer.Ordinal).Count() != Labels.Length)
            throw new InvalidDataException("Invalid vehicle color model manifest.");
    }
}

public sealed record VehicleModelInfo(string Name, string Path, string Capability, int InputWidth, int InputHeight,
    VehicleColorModelDescriptor? ColorContract);

public static class VehicleModelCatalog
{
    public static IReadOnlyList<VehicleModelInfo> Enumerate()
    {
        var result = new List<VehicleModelInfo>();
        foreach (string directory in Directories("VehicleColor"))
        foreach (string path in Directory.EnumerateFiles(directory, "*.hshmodel"))
        {
            try
            {
                var descriptor = ReadColorContract(path);
                result.Add(new(Path.ChangeExtension(Path.GetFileName(path), ".onnx"), path, "VehicleColor",
                    descriptor.InputWidth, descriptor.InputHeight, descriptor));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ArgumentException) { }
        }
        foreach (string directory in Directories("VehicleDetection"))
        {
            string path = System.IO.Path.Combine(directory, "yolox_nano.hshmodel");
            if (File.Exists(path)) result.Add(new("yolox_nano.onnx", path, "VehicleDetection", 416, 416, null));
        }
        return result.GroupBy(m => $"{m.Capability}:{m.Name}", StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
    }

    internal static VehicleModelInfo Resolve(string file, string capability) => Enumerate().FirstOrDefault(m =>
        m.Capability == capability && (string.Equals(m.Name, Path.ChangeExtension(Path.GetFileName(file), ".onnx"), StringComparison.OrdinalIgnoreCase)))
        ?? throw new FileNotFoundException($"Selected {capability} model or its contract was not found.");

    internal static VehicleColorModelDescriptor ReadColorContract(string package)
    {
        string path = Path.ChangeExtension(package, ".color.json");
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Vehicle color manifest is too large.");
        var descriptor = JsonSerializer.Deserialize<VehicleColorModelDescriptor>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Empty color manifest.");
        descriptor.Validate();
        return descriptor;
    }

    private static IEnumerable<string> Directories(string family)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (DirectoryInfo? root = new(AppContext.BaseDirectory); root is not null; root = root.Parent)
        {
            foreach (string path in new[] { Path.Combine(root.FullName, "Models", family),
                Path.Combine(root.FullName, "HshDetectionEngin", "Models", family) })
                if (Directory.Exists(path) && seen.Add(path)) yield return path;
        }
    }
}
