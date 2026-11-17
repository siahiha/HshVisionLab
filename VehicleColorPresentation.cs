using System.Text.Json;

namespace HshVisionLab;

internal static class VehicleColorPresentation
{
    public static string FromMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata?.GetValueOrDefault("VehicleColorStatus") is not string status) return string.Empty;
        string color = metadata.GetValueOrDefault("VehicleColor")?.ToString() ?? "Unknown";
        float score = metadata.GetValueOrDefault("VehicleColorScore") is float value ? value : 0;
        return Format(status, color, score, metadata.GetValueOrDefault("VehicleColorCandidate")?.ToString());
    }

    public static string FromPayload(string payload)
    {
        try { using var document = JsonDocument.Parse(payload); return Read(document.RootElement, 0); }
        catch (JsonException) { return string.Empty; }
    }

    private static string Read(JsonElement root, int depth)
    {
        if (root.ValueKind != JsonValueKind.Object || depth > 2) return string.Empty;
        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty("VehicleColorStatus", out var status) && status.ValueKind == JsonValueKind.String)
        {
            string color = metadata.TryGetProperty("VehicleColor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "Unknown";
            float score = metadata.TryGetProperty("VehicleColorScore", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetSingle(out float value) ? value : 0;
            string? candidate = metadata.TryGetProperty("VehicleColorCandidate", out var candidateElement) && candidateElement.ValueKind == JsonValueKind.String
                ? candidateElement.GetString() : null;
            return Format(status.GetString()!, color, score, candidate);
        }
        // Association envelopes retain the original plate event as a JSON string.
        if (root.TryGetProperty("plate", out var plate) && plate.ValueKind == JsonValueKind.String && plate.GetString() is string nested && nested.TrimStart().StartsWith('{'))
        { using var document = JsonDocument.Parse(nested); return Read(document.RootElement, depth + 1); }
        return string.Empty;
    }

    private static string Format(string status, string color, float score, string? candidate) =>
        status == "Accepted" ? $"{UiLocalization.T("Vehicle body color")}: {UiLocalization.T(color)} ({score:P0})" :
        status == "Sampling" && !string.IsNullOrEmpty(candidate) ?
            $"{UiLocalization.T("Vehicle body color")}: {UiLocalization.T(candidate)} ({UiLocalization.T("Initial color estimate")})" :
        $"{UiLocalization.T("Vehicle body color")}: {UiLocalization.T("Unknown color")}";
}
