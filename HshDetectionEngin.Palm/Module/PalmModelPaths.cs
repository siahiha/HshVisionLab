namespace HshDetectionEngin.Palm;

internal static class PalmModelPaths
{
    public static string? Find(string configuredName)
    {
        string name = Path.GetFileName(configuredName);
        if (string.IsNullOrWhiteSpace(name)) name = "palm_blazepalm_full.onnx";
        List<string> candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Models", "Palm", name),
            Path.Combine(AppContext.BaseDirectory, "Models", name),
            Path.Combine(AppContext.BaseDirectory, "Modules", "Palm", "Models", name)
        ];
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, "HshDetectionEngin.Palm", "Models", name));
        return candidates.FirstOrDefault(File.Exists);
    }
}
