using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace HshDetectionEngin.Licensing;

[Flags]
public enum LicensedFeature { None = 0, Plate = 1, Face = 2 }

public sealed class LicenseClaims
{
    public int Version { get; set; } = 1;
    public string LicenseId { get; set; } = Guid.NewGuid().ToString("N");
    public string CustomerName { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public DateTime NotBeforeUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; } = DateTime.UtcNow.AddYears(1);
    public LicensedFeature Features { get; set; }
}

public sealed class LicenseFile
{
    public string Payload { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}

/// <summary>
/// Portable customer activation request. It contains a device fingerprint only;
/// the vendor creates the cryptographic signature when issuing the license.
/// </summary>
public sealed class LicenseRequest
{
    public int Version { get; set; } = 1;
    public string MachineId { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public DateTime RequestedUtc { get; set; } = DateTime.UtcNow;
}

public static class LicenseRequestCodec
{
    public static LicenseRequest CreateCurrent() => new()
    {
        MachineId = MachineFingerprint.Current(),
        ComputerName = Environment.MachineName,
        RequestedUtc = DateTime.UtcNow
    };

    public static string Encode(LicenseRequest request) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request));

    public static LicenseRequest Decode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidDataException("Activation request is empty.");
        LicenseRequest request = JsonSerializer.Deserialize<LicenseRequest>(Convert.FromBase64String(code.Trim()))
            ?? throw new InvalidDataException("Activation request is invalid.");
        if (request.Version != 1 || string.IsNullOrWhiteSpace(request.MachineId))
            throw new InvalidDataException("Activation request is not supported.");
        return request;
    }

    public static void Save(string path, LicenseRequest request) => File.WriteAllText(path, Encode(request));

    public static LicenseRequest Load(string path) => Decode(File.ReadAllText(path));
}

public sealed class LicenseValidationResult
{
    internal LicenseValidationResult(bool valid, string message, LicenseClaims? claims) { IsValid = valid; Message = message; Claims = claims; }
    public bool IsValid { get; }
    public string Message { get; }
    public LicenseClaims? Claims { get; }
    public bool Allows(LicensedFeature feature) => IsValid && Claims is not null && Claims.Features.HasFlag(feature);
}

public static class MachineFingerprint
{
    public static string Current()
    {
        string? guid = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string;
        string source = string.IsNullOrWhiteSpace(guid) ? $"{Environment.MachineName}|{Environment.OSVersion}" : guid;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }
}

public static class LicenseIssuer
{
    public static void GenerateKeyPair(string privateKeyPath, string publicKeyPath)
    {
        using var rsa = RSA.Create(3072);
        Directory.CreateDirectory(string.IsNullOrWhiteSpace(Path.GetDirectoryName(privateKeyPath)) ? "." : Path.GetDirectoryName(privateKeyPath)!);
        File.WriteAllText(privateKeyPath, rsa.ExportRSAPrivateKeyPem());
        File.WriteAllText(publicKeyPath, rsa.ExportSubjectPublicKeyInfoPem());
    }

    public static void Issue(string privateKeyPath, LicenseClaims claims, string outputPath)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(claims);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(privateKeyPath));
        var license = new LicenseFile
        {
            Payload = Convert.ToBase64String(payload),
            Signature = Convert.ToBase64String(rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        };
        Directory.CreateDirectory(string.IsNullOrWhiteSpace(Path.GetDirectoryName(outputPath)) ? "." : Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(license, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public static class LicenseValidator
{
    // Replace this public key only when rotating the vendor signing key. The private key is never distributed.
    public const string PublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAtkNrFC36JQW4K3xl81qz
HzXhfEgs7F0483hUTRHBrPSBR/PsXgpVVJCZ44j8oL99Z1wunne4W98bZumMbAv0
etgasV2N3Qe411jQvNtApUSj+he+hK4OZeTGlnyISGIkI9U5xpWIUMmQrvOZafsk
PLDhy3jzBO5T/8GZ5S+SyarIa9q5Fwk0Q3j7Swsn1K+/RFJOdlhm9lFMOO/TP20Q
o09Uwd6d5Pv8t0GmyGXs1XekeUuyMamrwzSF829Fn19aYatfFWdhzBwk+Vhb0suc
3G5XIIT/2/AVwWUNrP3SSRohb97yxmSneOH9129I5Nz5a9om9sEbeBdUZMFxm077
bQtlmXe3anAsTkVvTTj/aoQxMbXk8duRnUw1Bf8b9V4BO5QUg8LW9LIKC1vdCNKe
3iM0DJNdGu28n8BdBsKhDTI8gIpTrqIOtWwyaDHud33T9erHGo7kv/XHdM65kYxV
vOTLlIrM+xIYMHp3RrnbNAVtJLME7PD5BjMV4GdGGNNdAgMBAAE=
-----END PUBLIC KEY-----
""";

    public static LicenseValidationResult Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(false, "License file was not found.", null);
            LicenseFile file = JsonSerializer.Deserialize<LicenseFile>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid license file.");
            byte[] payload = Convert.FromBase64String(file.Payload);
            byte[] signature = Convert.FromBase64String(file.Signature);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(PublicKeyPem);
            if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return new(false, "License signature is invalid.", null);
            LicenseClaims claims = JsonSerializer.Deserialize<LicenseClaims>(payload) ?? throw new InvalidDataException("Invalid license claims.");
            DateTime now = DateTime.UtcNow;
            if (claims.NotBeforeUtc > now) return new(false, "License is not active yet.", claims);
            if (claims.ExpiresUtc < now) return new(false, "License has expired.", claims);
            if (!string.Equals(claims.MachineId, MachineFingerprint.Current(), StringComparison.OrdinalIgnoreCase)) return new(false, "License is not valid for this machine.", claims);
            return new(true, "License is valid.", claims);
        }
        catch (Exception ex) { return new(false, ex.Message, null); }
    }

    public static void Require(LicenseValidationResult? license, LicensedFeature feature)
    {
        if (license is null || !license.Allows(feature)) throw new UnauthorizedAccessException($"A valid {feature} license is required.");
    }
}
