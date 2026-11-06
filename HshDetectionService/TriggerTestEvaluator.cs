using System.Text.Json.Nodes;

namespace HshDetectionService;

public sealed record TriggerTestEvaluationResult(
    bool Matched,
    string Reason,
    string EventId,
    long Sequence,
    string EventType,
    string Scenario,
    string CameraId,
    string? PlateText,
    IReadOnlyList<string> FailedChecks);

/// <summary>
/// Dry-run evaluation of a trigger against the latest persisted event.
/// It never writes TriggerHistory and never executes an action.
/// </summary>
public static class TriggerTestEvaluator
{
    public static TriggerTestEvaluationResult Evaluate(TriggerDefinition trigger, DetectionEventEnvelope envelope)
    {
        var failures = new List<string>();
        string cameraId = Text(envelope.Source, "cameraId");
        List<ComponentValue> components = envelope.Components
            .Select(item => new ComponentValue(item.Key, item.Value))
            .ToList();
        List<ComponentValue> relevant = components
            .Where(component => MatchesKinds(trigger.Kinds, component))
            .ToList();

        if (!trigger.Enabled) failures.Add("Trigger is disabled.");
        if (trigger.CameraIds.Count > 0 && !trigger.CameraIds.Contains(cameraId, StringComparer.OrdinalIgnoreCase))
            failures.Add("Camera does not match the trigger scope.");
        if (trigger.TaskIds.Count > 0 && !TaskMatches(trigger, envelope))
            failures.Add("Task does not match the trigger scope.");
        if (trigger.Kinds.Count > 0 && relevant.Count == 0)
            failures.Add("No detection component matches the trigger kind.");
        if (!string.IsNullOrWhiteSpace(trigger.LabelEquals) &&
            !relevant.Any(component => EqualsIgnoreCase(Text(component.Value, "label"), trigger.LabelEquals)))
            failures.Add("Label does not match.");
        if (!string.IsNullOrWhiteSpace(trigger.PlateTextEquals) &&
            !EqualsIgnoreCase(PlateText(envelope), trigger.PlateTextEquals))
            failures.Add("Plate text does not match.");
        if (!string.IsNullOrWhiteSpace(trigger.IdentityId) &&
            !relevant.Any(component => EqualsIgnoreCase(IdentityId(component.Value), trigger.IdentityId)))
            failures.Add("Identity does not match.");
        if (trigger.MinimumConfidence is float minimum &&
            (relevant.Count == 0 || relevant.Any(component => Number(component.Value, "confidence") < minimum)))
            failures.Add($"Confidence is below {minimum:0.###}.");

        string? plateText = PlateText(envelope);
        bool matched = failures.Count == 0;
        return new TriggerTestEvaluationResult(
            matched,
            matched ? "The latest event matches this trigger." : string.Join(" ", failures),
            envelope.EventId,
            envelope.Sequence,
            envelope.EventType,
            envelope.Scenario,
            cameraId,
            plateText,
            failures);
    }

    private static bool TaskMatches(TriggerDefinition trigger, DetectionEventEnvelope envelope)
    {
        string taskId = Text(envelope.Source, "taskId");
        if (trigger.TaskIds.Contains(taskId, StringComparer.OrdinalIgnoreCase)) return true;
        if (envelope.Source["taskIds"] is not JsonArray taskIds) return false;
        return taskIds.Any(value => trigger.TaskIds.Contains(value?.GetValue<string>() ?? string.Empty, StringComparer.OrdinalIgnoreCase));
    }

    private static bool MatchesKinds(IReadOnlyList<string> expectedKinds, ComponentValue component)
    {
        if (expectedKinds.Count == 0) return true;
        bool isPlate = component.Key.Equals("plate", StringComparison.OrdinalIgnoreCase);
        bool isFace = component.Key.Equals("face", StringComparison.OrdinalIgnoreCase);
        bool isPalm = component.Key.Equals("palm", StringComparison.OrdinalIgnoreCase);
        string actual = Text(component.Value, "kind");
        return expectedKinds.Any(expected =>
            (isPlate && (IsPlateKind(expected) || IsPlateFaceKind(expected) || IsPlatePalmKind(expected) || IsPlateFacePalmKind(expected) || actual.Equals(expected, StringComparison.OrdinalIgnoreCase))) ||
            (isFace && (IsFaceKind(expected) || IsPlateFaceKind(expected) || IsPlateFacePalmKind(expected) ||
                        (expected.Equals("FaceRecognized", StringComparison.OrdinalIgnoreCase) && IsRecognized(component.Value)) ||
                        (expected.Equals("FaceUnknown", StringComparison.OrdinalIgnoreCase) && !IsRecognized(component.Value)))) ||
            (isPalm && (IsPalmKind(expected) || IsPlatePalmKind(expected) || IsPlateFacePalmKind(expected) ||
                        (expected.Equals("PalmRecognized", StringComparison.OrdinalIgnoreCase) && IsRecognized(component.Value)) ||
                        (expected.Equals("PalmUnknown", StringComparison.OrdinalIgnoreCase) && !IsRecognized(component.Value)))));
    }

    private static bool IsRecognized(JsonObject component) =>
        Text(component, "recognitionStatus").Equals("Matched", StringComparison.OrdinalIgnoreCase);

    private static string? PlateText(DetectionEventEnvelope envelope) =>
        envelope.Components.TryGetValue("plate", out JsonObject? component)
            ? Text(component, "plateText") is { Length: > 0 } value ? value : Text(component, "label")
            : null;

    private static string? IdentityId(JsonObject component) =>
        component["recognition"] is JsonObject recognition ? Text(recognition, "personId") : null;

    private static string Text(JsonObject value, string property) =>
        value[property]?.GetValue<string>() ?? string.Empty;

    private static double Number(JsonObject value, string property) =>
        value[property]?.GetValue<double>() ?? 0;

    private static bool EqualsIgnoreCase(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsPlateKind(string value) =>
        value.Equals("PlateRecognition", StringComparison.OrdinalIgnoreCase) || value.Equals("Plate", StringComparison.OrdinalIgnoreCase);

    private static bool IsFaceKind(string value) =>
        value.Equals("FaceRecognition", StringComparison.OrdinalIgnoreCase) || value.Equals("Face", StringComparison.OrdinalIgnoreCase);

    private static bool IsPalmKind(string value) =>
        value.Equals("PalmRecognition", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("PalmRecognized", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("PalmUnknown", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Palm", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlateFaceKind(string value) =>
        value.Equals("PlateFaceMatch", StringComparison.OrdinalIgnoreCase) || value.Equals("PlateFaceAssociation", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlatePalmKind(string value) =>
        value.Equals("PlatePalmMatch", StringComparison.OrdinalIgnoreCase) || value.Equals("PlatePalmAssociation", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlateFacePalmKind(string value) =>
        value.Equals("PlateFacePalmMatch", StringComparison.OrdinalIgnoreCase) || value.Equals("PlateFacePalmAssociation", StringComparison.OrdinalIgnoreCase);

    private sealed record ComponentValue(string Key, JsonObject Value);
}
