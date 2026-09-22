using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;

namespace HshDetectionEngin.Face;

/// <summary>Reads the fixed detector input declared by a packaged Face model.</summary>
public static class FaceModelInspector
{
    private static readonly ConcurrentDictionary<string, Lazy<int?>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static int? TryGetSquareInputSize(string configuredName)
    {
        string name = Path.GetFileName(configuredName);
        if (string.IsNullOrWhiteSpace(name)) name = "face_yunet_2023mar.onnx";

        return Cache.GetOrAdd(
            name,
            static key => new Lazy<int?>(() => Inspect(key), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    /// <summary>
    /// Returns the known catalog size without opening an ONNX session. YuNet
    /// models supported by this module use the fixed 640x640 detector input.
    /// </summary>
    public static int GetCatalogSquareInputSize(string configuredName)
    {
        string name = Path.GetFileName(configuredName);
        if (!string.IsNullOrWhiteSpace(name) &&
            Cache.TryGetValue(name, out Lazy<int?>? cached) && cached.IsValueCreated && cached.Value is int inspected && inspected > 0)
            return inspected;

        return 640;
    }

    private static int? Inspect(string name)
    {
        string? temporaryModel = null;
        try
        {
            string? resolvedPath = FaceModelPaths.Find(name);
            if (resolvedPath is null) return null;

            string modelPath = resolvedPath;
            if (resolvedPath.EndsWith(".hshmodel", StringComparison.OrdinalIgnoreCase))
            {
                temporaryModel = Materialize(resolvedPath);
                modelPath = temporaryModel;
            }

            using var session = new InferenceSession(modelPath);
            var input = session.InputMetadata.Values.FirstOrDefault();
            if (input is null || input.Dimensions.Length < 4) return null;

            int height = input.Dimensions[^2];
            int width = input.Dimensions[^1];
            return height > 0 && width > 0 && height == width ? height : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (temporaryModel is not null)
            {
                try { File.Delete(temporaryModel); } catch { }
            }
        }
    }

    private const string DevelopmentLicense = "HSH-DETECTION-DEVELOPMENT-LICENSE-V1";

    private static string Materialize(string packagePath)
    {
        byte[] package = File.ReadAllBytes(packagePath);
        if (package.Length <= 24 || Encoding.ASCII.GetString(package, 0, 8) != "HSHM0001")
            throw new InvalidDataException("Invalid protected face model package.");

        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(
            Environment.GetEnvironmentVariable("HSH_DETECTION_LICENSE") ?? DevelopmentLicense));
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = package[8..24];
        byte[] model = aes.CreateDecryptor().TransformFinalBlock(package, 24, package.Length - 24);
        string path = Path.Combine(Path.GetTempPath(), $"hsh-face-inspect-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(path, model);
        CryptographicOperations.ZeroMemory(model);
        return path;
    }
}
