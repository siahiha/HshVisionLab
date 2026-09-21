namespace HshDetectionEngin.Face;

internal static class FaceModelPaths
{
    public static string? Find(string configuredName)
    {
        string name = Path.GetFileName(configuredName);
        if (string.IsNullOrWhiteSpace(name)) name = "face_yunet_2023mar.onnx";

        string packageName = Path.ChangeExtension(name, ".hshmodel");
        List<string> candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Models", "Face", packageName),
            Path.Combine(AppContext.BaseDirectory, "Models", packageName),
            Path.Combine(AppContext.BaseDirectory, "Modules", "Face", "Models", packageName),
            Path.Combine(AppContext.BaseDirectory, "Models", "Face", name),
            Path.Combine(AppContext.BaseDirectory, "Models", name)
        ];

        // Keep Debug/Visual Studio runs working without copying the repository's
        // packaged models into bin. Published deployments use Models beside the exe.
        AddDevelopmentModelPath(candidates, packageName, name);

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void AddDevelopmentModelPath(List<string> candidates, string packageName, string rawName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int i = 0; i < 7 && directory is not null; i++, directory = directory.Parent)
        {
            string modelDirectory = Path.Combine(directory.FullName, "HshDetectionEngin.Face", "Models");
            candidates.Add(Path.Combine(modelDirectory, packageName));
            candidates.Add(Path.Combine(modelDirectory, rawName));
        }
    }
}
