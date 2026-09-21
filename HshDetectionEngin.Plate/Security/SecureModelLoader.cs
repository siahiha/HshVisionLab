using System.Security.Cryptography;
using System.Text;

namespace HshDetectionEngin.Plate;

/// <summary>Loads signed/encrypted model packages for the current Engine process.</summary>
internal static class SecureModelLoader
{
    private const string DevelopmentLicense = "HSH-DETECTION-DEVELOPMENT-LICENSE-V1";

    internal static string Materialize(string packagePath)
    {
        byte[] package = File.ReadAllBytes(packagePath);
        if (package.Length <= 24 || Encoding.ASCII.GetString(package, 0, 8) != "HSHM0001")
            throw new InvalidDataException("Invalid protected model package.");

        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(
            Environment.GetEnvironmentVariable("HSH_DETECTION_LICENSE") ?? DevelopmentLicense));
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = package[8..24];
        using ICryptoTransform decryptor = aes.CreateDecryptor();
        byte[] onnx = decryptor.TransformFinalBlock(package, 24, package.Length - 24);

        string path = Path.Combine(Path.GetTempPath(), $"hsh-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(path, onnx);
        CryptographicOperations.ZeroMemory(onnx);
        return path;
    }
}
