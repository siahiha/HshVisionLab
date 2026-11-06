using System.Drawing.Imaging;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HshVisionLab;

/// <summary>
/// Local Windows-only event envelope. It deliberately does not depend on
/// HshDetectionService or its API contract.
/// </summary>
public sealed class DesktopEventRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long Sequence { get; set; }
    public string CameraId { get; set; } = string.Empty;
    public string CameraName { get; set; } = string.Empty;
    public string RoiName { get; set; } = string.Empty;
    public string RoiId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public int AssociationAgeMs { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? PlateText { get; set; }
    public string? Identity { get; set; }
    public float Confidence { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public string? CropPath { get; set; }
    public string? SecondaryCropPath { get; set; }
    public string? FullFramePath { get; set; }
    public string DisplayAxis { get; set; } = "Auto";
    public string PayloadJson { get; set; } = "{}";
    public bool IsStaging { get; set; }
    public bool TriggerMatched { get; set; }
    public List<string> TriggerIds { get; set; } = [];
}

public sealed class DesktopHistorySubscription
{
    public List<DesktopHistorySubscriptionProfile> Profiles { get; set; } = [];
}

public sealed class DesktopHistorySubscriptionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "History scenario";
    public string Mode { get; set; } = "All";
    public List<string> CameraIds { get; set; } = [];
    public List<string> RoiIds { get; set; } = [];
    public bool FaceRequired { get; set; }
    public bool PlateRequired { get; set; }
    public bool PalmRequired { get; set; }
    public bool IncludeFace { get; set; } = true;
    public bool IncludePlate { get; set; } = true;
    public bool IncludePalm { get; set; } = true;
    public bool IncludeUnknownFace { get; set; } = true;
    public bool IncludeUnknownPalm { get; set; } = true;
    public bool IncludeArtifacts { get; set; } = true;
    public int WindowMs { get; set; } = 1500;
    public int CooldownSeconds { get; set; }
}

public sealed record DesktopHistoryDecision(
    bool Record,
    int CooldownSeconds,
    bool IncludeArtifacts,
    string DisplayAxis = "Auto");

public static class DesktopHistorySubscriptionMatcher
{
    public static DesktopHistoryDecision Evaluate(DesktopHistorySubscription? subscription, DesktopEventRecord record)
    {
        DesktopHistorySubscriptionProfile[] profiles = subscription?.Profiles?.Where(profile => profile is not null).ToArray() ?? [];
        if (profiles.Length == 0) return new DesktopHistoryDecision(true, 0, true, InferDisplayAxis(record));

        DesktopHistorySubscriptionProfile[] scoped = profiles
            .Where(profile => profile.CameraIds is null || profile.CameraIds.Count == 0 || profile.CameraIds.Contains(record.CameraId, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (scoped.Length == 0) return new DesktopHistoryDecision(true, 0, true, InferDisplayAxis(record));

        DesktopHistorySubscriptionProfile[] matched = scoped.Where(profile => Matches(profile, record)).ToArray();
        if (matched.Length == 0) return new DesktopHistoryDecision(false, 0, true, InferDisplayAxis(record));
        return new DesktopHistoryDecision(
            matched.Any(profile => AllowsAnyComponent(profile, record)),
            matched.Max(profile => Math.Clamp(profile.CooldownSeconds, 0, 3600)),
            matched.Any(profile => profile.IncludeArtifacts),
            SelectDisplayAxis(matched, record));
    }

    public static bool NeedsAssociationStaging(DesktopHistorySubscription? subscription, DesktopEventRecord record)
    {
        if (subscription?.Profiles is null || !IsAssociationComponent(record)) return false;

        return subscription.Profiles
            .Where(profile => profile is not null)
            .Where(profile => profile.CameraIds is null || profile.CameraIds.Count == 0 || profile.CameraIds.Contains(record.CameraId, StringComparer.OrdinalIgnoreCase))
            .Any(profile =>
                (RequiresPlateFacePair(profile) && (HasPlate(record) || HasFace(record))) ||
                (RequiresPlatePalmPair(profile) && (HasPlate(record) || HasPalm(record))));
    }

    private static bool RequiresPlateFacePair(DesktopHistorySubscriptionProfile profile) =>
        (profile.PlateRequired && profile.FaceRequired) ||
        (profile.FaceRequired && string.Equals(profile.Mode, "Plate", StringComparison.OrdinalIgnoreCase)) ||
        (profile.PlateRequired && string.Equals(profile.Mode, "KnownFace", StringComparison.OrdinalIgnoreCase));

    private static bool RequiresPlatePalmPair(DesktopHistorySubscriptionProfile profile) =>
        (profile.PlateRequired && profile.PalmRequired) ||
        (profile.PalmRequired && string.Equals(profile.Mode, "Plate", StringComparison.OrdinalIgnoreCase)) ||
        (profile.PlateRequired && string.Equals(profile.Mode, "KnownPalm", StringComparison.OrdinalIgnoreCase));

    private static bool Matches(DesktopHistorySubscriptionProfile profile, DesktopEventRecord record)
    {
        profile.Mode = NormalizeMode(profile.Mode);
        profile.CameraIds ??= [];
        profile.RoiIds ??= [];
        if (profile.RoiIds.Count > 0 && !profile.RoiIds.Contains(record.RoiId, StringComparer.OrdinalIgnoreCase) &&
            !profile.RoiIds.Contains(record.RoiName, StringComparer.OrdinalIgnoreCase)) return false;
        if (profile.WindowMs > 0 && record.AssociationAgeMs > profile.WindowMs) return false;

        bool hasPlate = HasPlate(record);
        bool hasFace = HasFace(record);
        bool hasPalm = HasPalm(record);
        bool knownFace = hasFace && !string.IsNullOrWhiteSpace(record.Identity);
        bool knownPalm = hasPalm && !string.IsNullOrWhiteSpace(record.Identity);
        bool eligibleFace = hasFace && (profile.IncludeUnknownFace || knownFace);
        bool eligiblePalm = hasPalm && (profile.IncludeUnknownPalm || knownPalm);
        if (profile.FaceRequired && !eligibleFace || profile.PlateRequired && !hasPlate || profile.PalmRequired && !eligiblePalm) return false;

        return profile.Mode switch
        {
            "Plate" => hasPlate,
            "Palm" => hasPalm,
            "KnownFace" => knownFace,
            "KnownPalm" => knownPalm,
            _ => hasPlate || eligibleFace || eligiblePalm
        };
    }

    private static bool AllowsAnyComponent(DesktopHistorySubscriptionProfile profile, DesktopEventRecord record) =>
        (HasPlate(record) && profile.IncludePlate) ||
        (HasFace(record) && profile.IncludeFace && (profile.IncludeUnknownFace || !string.IsNullOrWhiteSpace(record.Identity))) ||
        (HasPalm(record) && profile.IncludePalm && (profile.IncludeUnknownPalm || !string.IsNullOrWhiteSpace(record.Identity)));

    private static bool HasPlate(DesktopEventRecord record) =>
        !string.IsNullOrWhiteSpace(record.PlateText) || record.Scenario.Contains("Plate", StringComparison.OrdinalIgnoreCase);

    private static bool HasFace(DesktopEventRecord record) =>
        record.Scenario.Equals("Face", StringComparison.OrdinalIgnoreCase) || record.Scenario.Contains("Face", StringComparison.OrdinalIgnoreCase);

    private static bool HasPalm(DesktopEventRecord record) =>
        record.Scenario.Equals("Palm", StringComparison.OrdinalIgnoreCase) || record.Scenario.Contains("Palm", StringComparison.OrdinalIgnoreCase);

    private static bool IsAssociationComponent(DesktopEventRecord record) =>
        record.Scenario.Equals("Plate", StringComparison.OrdinalIgnoreCase) ||
        record.Scenario.Equals("Face", StringComparison.OrdinalIgnoreCase) ||
        record.Scenario.Equals("Palm", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeMode(string? mode) => mode?.Trim() switch
    {
        "Plate" => "Plate",
        "Palm" => "Palm",
        "KnownFace" => "KnownFace",
        "KnownPalm" => "KnownPalm",
        _ => "All"
    };

    private static string SelectDisplayAxis(
        IReadOnlyList<DesktopHistorySubscriptionProfile> profiles,
        DesktopEventRecord record)
    {
        string? explicitAxis = profiles
            .Select(profile => NormalizeMode(profile.Mode))
            .Select(mode => mode switch
            {
                "Plate" => "Plate",
                "KnownFace" => "Face",
                "KnownPalm" => "Palm",
                _ => null
            })
            .FirstOrDefault(axis => axis is not null);
        return explicitAxis ?? InferDisplayAxis(record);
    }

    private static string InferDisplayAxis(DesktopEventRecord record)
    {
        if (HasPlate(record)) return "Plate";
        if (HasFace(record)) return "Face";
        if (HasPalm(record)) return "Palm";
        return "Auto";
    }
}

public sealed class DesktopTriggerDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Trigger";
    public bool Enabled { get; set; } = true;
    public string Scenario { get; set; } = "Plate";
    public List<string> CameraIds { get; set; } = [];
    public string? LabelEquals { get; set; }
    public string? PlateTextEquals { get; set; }
    public string? IdentityEquals { get; set; }
    public float? MinimumConfidence { get; set; }
    public int CooldownSeconds { get; set; }
    public List<DesktopTriggerActionDefinition> Actions { get; set; } = [];
}

public sealed class DesktopTriggerActionDefinition
{
    public string Type { get; set; } = "WindowsNotification";
    public string? Target { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class DesktopInvocationDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Invocation";
    public bool Enabled { get; set; } = true;
    public string Type { get; set; } = "Web";
    public List<string> CameraIds { get; set; } = [];
    public List<string> EventTypes { get; set; } = [];
    public float? MinimumConfidence { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
    public int MaxRetries { get; set; } = 2;
    public int RetryDelaySeconds { get; set; } = 5;
    public string Url { get; set; } = string.Empty;
    public string Method { get; set; } = "POST";
    public string? ApiKey { get; set; }
    public string SqlitePath { get; set; } = string.Empty;
    public string CommandText { get; set; } = string.Empty;
}

public sealed class DesktopInvocationLog
{
    public long Id { get; set; }
    public string InvocationId { get; set; } = string.Empty;
    public string InvocationName { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Attempt { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string? Response { get; set; }
    public string? Error { get; set; }
}

public sealed class DesktopAutomationSettings
{
    public DesktopHistorySubscription HistorySubscription { get; set; } = new();
    public List<DesktopTriggerDefinition> Triggers { get; set; } = [];
    public List<DesktopInvocationDefinition> Invocations { get; set; } = [];
    public int EventRetentionDays { get; set; } = 30;
    public int ArtifactRetentionDays { get; set; } = 30;
}

public sealed class DesktopEventStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private readonly string _recordsPath = Path.Combine(AppContext.BaseDirectory, "desktop-events.json");
    private readonly string _artifactDirectory = Path.Combine(AppContext.BaseDirectory, "desktop-event-artifacts");
    private List<DesktopEventRecord> _records;
    private long _nextSequence;

    public DesktopEventStore()
    {
        _records = LoadList(_recordsPath);
        _nextSequence = _records.Count == 0 ? 0 : _records.Max(item => item.Sequence);
        Directory.CreateDirectory(_artifactDirectory);
    }

    public IReadOnlyList<DesktopEventRecord> Query(string? search = null, string? scenario = null, int limit = 500)
    {
        lock (_gate)
        {
            string needle = search?.Trim() ?? string.Empty;
            return _records
                .Where(item => !item.IsStaging)
                .Where(item => string.IsNullOrWhiteSpace(scenario) || scenario.Equals("All", StringComparison.OrdinalIgnoreCase) || item.Scenario.Equals(scenario, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.IsNullOrWhiteSpace(needle) || string.Join(" ", item.CameraName, item.RoiName, item.EventType, item.Scenario, item.Label, item.PlateText, item.Identity).Contains(needle, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Sequence)
                .Take(Math.Clamp(limit, 1, 5000))
                .Select(Clone)
                .ToArray();
        }
    }

    public DesktopEventRecord? Get(string id)
    {
        lock (_gate)
        {
            DesktopEventRecord? item = _records.FirstOrDefault(value => value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return item is null ? null : Clone(item);
        }
    }

    public void Update(DesktopEventRecord record)
    {
        lock (_gate)
        {
            int index = _records.FindIndex(item => item.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) { _records[index] = Clone(record); SaveRecords(); }
        }
    }

    public DesktopEventRecord Append(
        string cameraId,
        string cameraName,
        string roiName,
        string eventType,
        string scenario,
        string label,
        string? plateText,
        string? identity,
        float confidence,
        DateTime occurredAt,
        Bitmap? crop,
        Bitmap? fullFrame,
        object payload,
        string? roiId = null,
        bool includeArtifacts = true,
        int associationAgeMs = 0,
        bool isStaging = false,
        Bitmap? secondaryCrop = null,
        string displayAxis = "Auto")
    {
        lock (_gate)
        {
            var item = new DesktopEventRecord
            {
                Sequence = ++_nextSequence,
                CameraId = cameraId,
                CameraName = cameraName,
                RoiName = roiName,
                RoiId = roiId ?? string.Empty,
                EventType = eventType,
                Scenario = scenario,
                AssociationAgeMs = Math.Max(0, associationAgeMs),
                Label = label,
                PlateText = plateText,
                Identity = identity,
                Confidence = confidence,
                OccurredAtUtc = occurredAt.ToUniversalTime(),
                PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
                DisplayAxis = displayAxis,
                IsStaging = isStaging
            };

            if (includeArtifacts)
            {
                string stem = $"{item.Sequence:000000}_{item.Id}";
                item.CropPath = SaveImage(crop, $"{stem}_crop.png");
                item.SecondaryCropPath = SaveImage(secondaryCrop, $"{stem}_secondary.png");
                item.FullFramePath = SaveImage(fullFrame, $"{stem}_frame.png");
            }
            _records.Insert(0, item);
            SaveRecords();
            return Clone(item);
        }
    }

    public bool HasRecent(DesktopEventRecord candidate, int cooldownSeconds)
    {
        if (cooldownSeconds <= 0) return false;
        DateTime since = candidate.OccurredAtUtc.AddSeconds(-cooldownSeconds);
        string? candidatePlate = GetPlateCooldownKey(candidate);
        lock (_gate)
        {
            return _records.Any(item => item.CameraId.Equals(candidate.CameraId, StringComparison.OrdinalIgnoreCase) &&
                !item.IsStaging &&
                item.OccurredAtUtc >= since &&
                item.OccurredAtUtc <= candidate.OccurredAtUtc &&
                (candidatePlate is not null
                    ? string.Equals(GetPlateCooldownKey(item), candidatePlate, StringComparison.OrdinalIgnoreCase)
                    : item.Scenario.Equals(candidate.Scenario, StringComparison.OrdinalIgnoreCase) &&
                      item.Label.Equals(candidate.Label, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static string? GetPlateCooldownKey(DesktopEventRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.PlateText)) return record.PlateText.Trim();
        if (record.Scenario.Contains("Plate", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(record.Label))
            return record.Label.Trim();
        return null;
    }

    public DesktopEventRecord? TryCreatePlateFaceAssociation(
        DesktopEventRecord current,
        int maxWindowMs = 1500,
        Func<DesktopEventRecord, DesktopHistoryDecision>? evaluate = null)
    {
        if (!current.Scenario.Equals("Plate", StringComparison.OrdinalIgnoreCase) && !current.Scenario.Equals("Face", StringComparison.OrdinalIgnoreCase) && !current.Scenario.Equals("Palm", StringComparison.OrdinalIgnoreCase)) return null;
        lock (_gate)
        {
            RemoveStaleStaging(current, maxWindowMs);
            DesktopEventRecord? other = _records
                .Where(item => item.CameraId.Equals(current.CameraId, StringComparison.OrdinalIgnoreCase) && item.Id != current.Id)
                .Where(item => (item.Scenario.Equals("Plate", StringComparison.OrdinalIgnoreCase) || item.Scenario.Equals("Face", StringComparison.OrdinalIgnoreCase) || item.Scenario.Equals("Palm", StringComparison.OrdinalIgnoreCase)) && !item.Scenario.Equals(current.Scenario, StringComparison.OrdinalIgnoreCase))
                .Where(item => item.Scenario.Equals("Plate", StringComparison.OrdinalIgnoreCase) || current.Scenario.Equals("Plate", StringComparison.OrdinalIgnoreCase))
                .Where(item => Math.Abs((item.OccurredAtUtc - current.OccurredAtUtc).TotalMilliseconds) <= maxWindowMs)
                .OrderBy(item => Math.Abs((item.OccurredAtUtc - current.OccurredAtUtc).TotalMilliseconds))
                .FirstOrDefault();
            if (other is null) return null;
            if (_records.Any(item => !item.IsStaging && (item.Scenario.Equals("PlateFace", StringComparison.OrdinalIgnoreCase) || item.Scenario.Equals("PlatePalm", StringComparison.OrdinalIgnoreCase)) && item.CameraId.Equals(current.CameraId, StringComparison.OrdinalIgnoreCase) && Math.Abs((item.OccurredAtUtc - current.OccurredAtUtc).TotalMilliseconds) <= maxWindowMs && item.Label.Contains(current.Label, StringComparison.OrdinalIgnoreCase)))
            {
                RemoveStaging(current, other);
                return null;
            }

            DesktopEventRecord plate = current.Scenario.Equals("Plate", StringComparison.OrdinalIgnoreCase) ? current : other;
            DesktopEventRecord identity = current.Scenario.Equals("Plate", StringComparison.OrdinalIgnoreCase) ? other : current;
            string pairScenario = identity.Scenario.Equals("Palm", StringComparison.OrdinalIgnoreCase) ? "PlatePalm" : "PlateFace";
            string pairEvent = identity.Scenario.Equals("Palm", StringComparison.OrdinalIgnoreCase) ? "PlatePalmAssociated" : "PlateFaceAssociated";
            int associationAgeMs = (int)Math.Abs((current.OccurredAtUtc - other.OccurredAtUtc).TotalMilliseconds);
            DateTime occurredAtUtc = current.OccurredAtUtc > other.OccurredAtUtc ? current.OccurredAtUtc : other.OccurredAtUtc;
            string label = $"{plate.Label} + {identity.Label}";
            string? plateText = plate.PlateText ?? plate.Label;
            string? identityValue = identity.Identity ?? identity.Label;
            DesktopEventRecord associationCandidate = new()
            {
                CameraId = current.CameraId,
                CameraName = current.CameraName,
                RoiName = current.RoiName,
                RoiId = current.RoiId,
                EventType = pairEvent,
                Scenario = pairScenario,
                AssociationAgeMs = associationAgeMs,
                Label = label,
                PlateText = plateText,
                Identity = identityValue,
                Confidence = Math.Min(plate.Confidence, identity.Confidence),
                OccurredAtUtc = occurredAtUtc,
                PayloadJson = JsonSerializer.Serialize(new { plate = plate.PayloadJson, identity = identity.PayloadJson, associationWindowMs = maxWindowMs }, JsonOptions)
            };
            DesktopHistoryDecision decision = evaluate?.Invoke(associationCandidate) ?? new DesktopHistoryDecision(true, 0, true);
            if (!decision.Record)
            {
                RemoveStaging(current, other);
                return null;
            }

            using Bitmap? plateCrop = LoadImage(plate.CropPath);
            using Bitmap? identityCrop = LoadImage(identity.CropPath);
            bool facePrimary = string.Equals(decision.DisplayAxis, "Face", StringComparison.OrdinalIgnoreCase);
            using Bitmap? crop = facePrimary ? identityCrop ?? plateCrop : plateCrop ?? identityCrop;
            bool showFaceThumbnail = !facePrimary && plateCrop is not null && identityCrop is not null && pairScenario.Equals("PlateFace", StringComparison.OrdinalIgnoreCase);
            using Bitmap? secondaryCrop = showFaceThumbnail ? identityCrop : null;
            using Bitmap? frame = LoadImage(current.FullFramePath) ?? LoadImage(other.FullFramePath);
            DesktopEventRecord association = Append(current.CameraId, current.CameraName, current.RoiName, pairEvent, pairScenario, label, plateText, identityValue, associationCandidate.Confidence, occurredAtUtc, crop, frame, new { plate = plate.PayloadJson, identity = identity.PayloadJson, associationWindowMs = maxWindowMs }, current.RoiId, decision.IncludeArtifacts, associationAgeMs, displayAxis: decision.DisplayAxis, secondaryCrop: secondaryCrop);
            RemoveStaging(current, other);
            return association;
        }
    }

    private void RemoveStaging(DesktopEventRecord current, DesktopEventRecord other)
    {
        string[] ids = [current.Id, other.Id];
        List<DesktopEventRecord> staging = _records.Where(item => item.IsStaging && ids.Contains(item.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (staging.Count == 0) return;
        foreach (DesktopEventRecord item in staging) DeleteArtifacts(item);
        _records.RemoveAll(item => item.IsStaging && ids.Contains(item.Id, StringComparer.OrdinalIgnoreCase));
        SaveRecords();
    }

    private void RemoveStaleStaging(DesktopEventRecord current, int maxWindowMs)
    {
        List<DesktopEventRecord> stale = _records
            .Where(item => item.IsStaging && Math.Abs((item.OccurredAtUtc - current.OccurredAtUtc).TotalMilliseconds) > maxWindowMs)
            .ToList();
        if (stale.Count == 0) return;
        foreach (DesktopEventRecord item in stale) DeleteArtifacts(item);
        _records.RemoveAll(item => item.IsStaging && stale.Any(value => value.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)));
        SaveRecords();
    }

    public int Delete(DateTime? fromUtc = null, DateTime? toUtc = null)
    {
        lock (_gate)
        {
            DateTime? from = fromUtc?.ToUniversalTime();
            DateTime? to = toUtc?.ToUniversalTime();
            List<DesktopEventRecord> removed = _records.Where(item => (!from.HasValue || item.OccurredAtUtc >= from.Value) && (!to.HasValue || item.OccurredAtUtc <= to.Value)).ToList();
            foreach (DesktopEventRecord item in removed) DeleteArtifacts(item);
            _records = _records.Except(removed).ToList();
            SaveRecords();
            return removed.Count;
        }
    }

    public void Prune(int retentionDays, int artifactRetentionDays)
    {
        if (retentionDays <= 0 && artifactRetentionDays <= 0) return;
        DateTime now = DateTime.UtcNow;
        DateTime? eventCutoff = retentionDays > 0 ? now.AddDays(-retentionDays) : null;
        DateTime? artifactCutoff = artifactRetentionDays > 0 ? now.AddDays(-artifactRetentionDays) : null;
        lock (_gate)
        {
            if (eventCutoff.HasValue)
            {
                List<DesktopEventRecord> removed = _records.Where(item => item.OccurredAtUtc < eventCutoff.Value).ToList();
                foreach (DesktopEventRecord item in removed) DeleteArtifacts(item);
                _records = _records.Except(removed).ToList();
            }
            if (artifactCutoff.HasValue)
            {
                foreach (DesktopEventRecord item in _records.Where(item => item.OccurredAtUtc < artifactCutoff.Value))
                {
                    DeleteFile(item.CropPath);
                    DeleteFile(item.SecondaryCropPath);
                    DeleteFile(item.FullFramePath);
                    item.CropPath = null;
                    item.SecondaryCropPath = null;
                    item.FullFramePath = null;
                }
            }
            SaveRecords();
        }
    }

    public static string ResolvePath(string? path) => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    private string? SaveImage(Bitmap? image, string name)
    {
        if (image is null) return null;
        string path = Path.Combine(_artifactDirectory, name);
        image.Save(path, ImageFormat.Png);
        return Path.GetRelativePath(AppContext.BaseDirectory, path);
    }

    private static Bitmap? LoadImage(string? path)
    {
        string full = ResolvePath(path);
        if (!File.Exists(full)) return null;
        try { using var image = Image.FromFile(full); return new Bitmap(image); } catch { return null; }
    }

    private void DeleteArtifacts(DesktopEventRecord item)
    {
        DeleteFile(item.CropPath);
        DeleteFile(item.SecondaryCropPath);
        DeleteFile(item.FullFramePath);
    }

    private static void DeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(ResolvePath(path))) File.Delete(ResolvePath(path)); } catch { }
    }

    private void SaveRecords()
    {
        string temporary = _recordsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_records, JsonOptions));
        File.Move(temporary, _recordsPath, true);
    }

    private static List<DesktopEventRecord> LoadList(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<DesktopEventRecord>>(File.ReadAllText(path), JsonOptions) ?? [] : []; }
        catch { return []; }
    }

    private static DesktopEventRecord Clone(DesktopEventRecord source) => JsonSerializer.Deserialize<DesktopEventRecord>(JsonSerializer.Serialize(source, JsonOptions), JsonOptions) ?? new DesktopEventRecord();
}

public sealed class DesktopAutomationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "desktop-automation.json");
    private readonly string _logsPath = Path.Combine(AppContext.BaseDirectory, "desktop-invocation-logs.json");
    public DesktopAutomationSettings Settings { get; private set; }
    public List<DesktopInvocationLog> Logs { get; private set; }

    public DesktopAutomationStore()
    {
        Settings = Load<DesktopAutomationSettings>(_settingsPath) ?? new DesktopAutomationSettings();
        Settings.HistorySubscription ??= new DesktopHistorySubscription();
        Settings.HistorySubscription.Profiles ??= [];
        foreach (DesktopHistorySubscriptionProfile profile in Settings.HistorySubscription.Profiles)
        {
            profile.Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id.Trim();
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "History scenario" : profile.Name.Trim();
            profile.CameraIds ??= [];
            profile.RoiIds ??= [];
            profile.WindowMs = Math.Clamp(profile.WindowMs, 0, 10_000);
            profile.CooldownSeconds = Math.Clamp(profile.CooldownSeconds, 0, 3600);
        }
        Settings.Triggers ??= [];
        foreach (DesktopTriggerDefinition trigger in Settings.Triggers)
        {
            trigger.Id = string.IsNullOrWhiteSpace(trigger.Id) ? Guid.NewGuid().ToString("N") : trigger.Id.Trim();
            trigger.Name = string.IsNullOrWhiteSpace(trigger.Name) ? "Trigger" : trigger.Name.Trim();
            trigger.CameraIds ??= [];
            trigger.Actions ??= [];
            trigger.CooldownSeconds = Math.Clamp(trigger.CooldownSeconds, 0, 3600);
        }
        Settings.Invocations ??= [];
        Logs = LoadLogs(_logsPath);
    }

    public void Save()
    {
        lock (_gate) Write(_settingsPath, Settings);
    }

    public void SaveLogs()
    {
        lock (_gate) Write(_logsPath, Logs.Take(1000).ToList());
    }

    public void AddLog(DesktopInvocationLog log)
    {
        lock (_gate)
        {
            log.Id = Logs.Count == 0 ? 1 : Logs.Max(item => item.Id) + 1;
            Logs.Insert(0, log);
            SaveLogs();
        }
    }

    private static T? Load<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : null; }
        catch { return null; }
    }

    private static List<DesktopInvocationLog> LoadLogs(string path) => Load<List<DesktopInvocationLog>>(path) ?? [];

    private static void Write<T>(string path, T value)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, path, true);
    }
}

public sealed class DesktopAutomationEngine : IDisposable
{
    private readonly DesktopAutomationStore _store;
    private readonly DesktopEventStore _events;
    private readonly HttpClient _http = new();
    private readonly object _cooldownGate = new();
    private readonly Dictionary<string, DateTime> _triggerTimes = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public event Action<string, bool>? StatusChanged;
    public DesktopAutomationStore Store => _store;

    public DesktopAutomationEngine(DesktopEventStore events, DesktopAutomationStore store)
    {
        _events = events;
        _store = store;
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public bool HasEnabledTriggerMatch(DesktopEventRecord record) =>
        _store.Settings.Triggers.Any(trigger => Matches(trigger, record));

    public void Process(DesktopEventRecord record)
    {
        if (_disposed) return;
        DesktopTriggerDefinition[] matched = _store.Settings.Triggers
            .Where(trigger => Matches(trigger, record))
            .Where(trigger => IsOutsideCooldown(trigger, record))
            .ToArray();
        record.TriggerIds.AddRange(matched.Select(item => item.Id));
        record.TriggerMatched = matched.Length > 0;
        _events.Update(record);

        foreach (DesktopTriggerDefinition trigger in matched)
        {
            foreach (DesktopTriggerActionDefinition action in trigger.Actions.Where(item => item.Enabled))
                _ = ExecuteActionAsync(action, trigger, record);
        }

        foreach (DesktopInvocationDefinition invocation in _store.Settings.Invocations.Where(item => item.Enabled && Matches(item, record)))
            _ = ExecuteInvocationAsync(invocation, record);
    }

    private async Task ExecuteActionAsync(DesktopTriggerActionDefinition action, DesktopTriggerDefinition trigger, DesktopEventRecord record)
    {
        try
        {
            if (action.Type.Equals("Webhook", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(action.Target))
            {
                using var content = JsonContent.Create(record);
                using HttpResponseMessage response = await _http.PostAsync(action.Target, content).ConfigureAwait(false);
                StatusChanged?.Invoke($"Trigger '{trigger.Name}': {(int)response.StatusCode}", !response.IsSuccessStatusCode);
            }
            else
            {
                StatusChanged?.Invoke($"Trigger '{trigger.Name}' matched: {record.Label}", false);
            }
        }
        catch (Exception ex) { StatusChanged?.Invoke($"Trigger '{trigger.Name}' failed: {ex.Message}", true); }
    }

    private async Task ExecuteInvocationAsync(DesktopInvocationDefinition definition, DesktopEventRecord record)
    {
        int attempts = Math.Max(0, definition.MaxRetries) + 1;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            DateTime started = DateTime.UtcNow;
            try
            {
                string responseText;
                string method;
                string target;
                if (definition.Type.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(definition.SqlitePath)) throw new InvalidOperationException("SQLite database path is empty.");
                    if (string.IsNullOrWhiteSpace(definition.CommandText)) throw new InvalidOperationException("SQLite command text is empty.");
                    string databasePath = DesktopEventStore.ResolvePath(definition.SqlitePath);
                    var builder = new SqliteConnectionStringBuilder { DataSource = databasePath };
                    await using var connection = new SqliteConnection(builder.ConnectionString);
                    await connection.OpenAsync().ConfigureAwait(false);
                    await using SqliteCommand command = connection.CreateCommand();
                    command.CommandText = definition.CommandText;
                    command.Parameters.AddWithValue("$eventId", record.Id);
                    command.Parameters.AddWithValue("$cameraId", record.CameraId);
                    command.Parameters.AddWithValue("$cameraName", record.CameraName);
                    command.Parameters.AddWithValue("$eventType", record.EventType);
                    command.Parameters.AddWithValue("$scenario", record.Scenario);
                    command.Parameters.AddWithValue("$label", record.Label);
                    command.Parameters.AddWithValue("$plateText", (object?)record.PlateText ?? DBNull.Value);
                    command.Parameters.AddWithValue("$identity", (object?)record.Identity ?? DBNull.Value);
                    command.Parameters.AddWithValue("$confidence", record.Confidence);
                    command.Parameters.AddWithValue("$occurredAtUtc", record.OccurredAtUtc.ToString("O"));
                    command.Parameters.AddWithValue("$payloadJson", record.PayloadJson);
                    int affected = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                    responseText = $"Rows affected: {affected}";
                    method = "SQLITE";
                    target = databasePath;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(definition.Url)) throw new InvalidOperationException("Invocation URL is empty.");
                    using var request = new HttpRequestMessage(new HttpMethod(string.IsNullOrWhiteSpace(definition.Method) ? "POST" : definition.Method), definition.Url);
                    request.Content = JsonContent.Create(record);
                    if (!string.IsNullOrWhiteSpace(definition.ApiKey)) request.Headers.TryAddWithoutValidation("X-Hsh-Api-Key", definition.ApiKey);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(definition.TimeoutSeconds, 1, 300)));
                    using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
                    method = definition.Method;
                    target = definition.Url;
                }
                _store.AddLog(new DesktopInvocationLog { InvocationId = definition.Id, InvocationName = definition.Name, EventId = record.Id, Status = "Succeeded", Attempt = attempt, StartedAtUtc = started, CompletedAtUtc = DateTime.UtcNow, Method = method, Target = target, Response = responseText });
                StatusChanged?.Invoke($"Invocation '{definition.Name}' succeeded", false);
                return;
            }
            catch (Exception ex)
            {
                _store.AddLog(new DesktopInvocationLog { InvocationId = definition.Id, InvocationName = definition.Name, EventId = record.Id, Status = attempt == attempts ? "Failed" : "Retrying", Attempt = attempt, StartedAtUtc = started, CompletedAtUtc = DateTime.UtcNow, Method = definition.Method, Target = definition.Type.Equals("Sqlite", StringComparison.OrdinalIgnoreCase) ? definition.SqlitePath : definition.Url, Error = ex.Message });
                if (attempt < attempts) await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, definition.RetryDelaySeconds))).ConfigureAwait(false);
                else StatusChanged?.Invoke($"Invocation '{definition.Name}' failed: {ex.Message}", true);
            }
        }
    }

    private bool IsOutsideCooldown(DesktopTriggerDefinition trigger, DesktopEventRecord record)
    {
        if (trigger.CooldownSeconds <= 0) return true;
        string key = $"{trigger.Id}:{record.CameraId}:{record.Label}:{record.Identity}";
        lock (_cooldownGate)
        {
            if (_triggerTimes.TryGetValue(key, out DateTime previous) && record.OccurredAtUtc - previous < TimeSpan.FromSeconds(trigger.CooldownSeconds)) return false;
            _triggerTimes[key] = record.OccurredAtUtc;
            return true;
        }
    }

    private static bool Matches(DesktopTriggerDefinition trigger, DesktopEventRecord record)
    {
        if (!trigger.Enabled || (trigger.CameraIds.Count > 0 && !trigger.CameraIds.Contains(record.CameraId, StringComparer.OrdinalIgnoreCase))) return false;
        if (!trigger.Scenario.Equals("Any", StringComparison.OrdinalIgnoreCase) && !record.Scenario.Equals(trigger.Scenario, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(trigger.LabelEquals) && !trigger.LabelEquals.Equals(record.Label, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(trigger.PlateTextEquals) && !trigger.PlateTextEquals.Equals(record.PlateText, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(trigger.IdentityEquals) && !trigger.IdentityEquals.Equals(record.Identity, StringComparison.OrdinalIgnoreCase)) return false;
        return !trigger.MinimumConfidence.HasValue || record.Confidence >= trigger.MinimumConfidence.Value;
    }

    private static bool Matches(DesktopInvocationDefinition invocation, DesktopEventRecord record)
    {
        if (invocation.CameraIds.Count > 0 && !invocation.CameraIds.Contains(record.CameraId, StringComparer.OrdinalIgnoreCase)) return false;
        if (invocation.EventTypes.Count > 0 && !invocation.EventTypes.Contains(record.EventType, StringComparer.OrdinalIgnoreCase) && !invocation.EventTypes.Contains(record.Scenario, StringComparer.OrdinalIgnoreCase)) return false;
        return !invocation.MinimumConfidence.HasValue || record.Confidence >= invocation.MinimumConfidence.Value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
