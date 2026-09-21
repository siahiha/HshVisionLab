using System.Text.Json;
using System.Text.Json.Nodes;

namespace HshDetectionService;

public sealed class ServiceSettingsDocument
{
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; }
    public string ServiceNodeId { get; set; } = Guid.NewGuid().ToString("N");
    public ServiceHttpSettings Http { get; set; } = new();
    public ServiceSecuritySettings Security { get; set; } = new();
    public ServiceRuntimeSettings Runtime { get; set; } = new();
    public ServiceRetentionSettings Retention { get; set; } = new();
    public List<TriggerDefinition> Triggers { get; set; } = [];
}

public sealed class ServiceHttpSettings
{
    public string[] ListenUrls { get; set; } = ["http://127.0.0.1:5080"];
}

public sealed class ServiceSecuritySettings
{
    public string ApiKey { get; set; } = Guid.NewGuid().ToString("N");
    public bool AllowLoopbackWithoutApiKey { get; set; } = true;
}

public sealed class ServiceRuntimeSettings
{
    public bool AutoStartCameras { get; set; } = true;
    public int PreviewFps { get; set; } = 15;
    public int MaxEventQueueLength { get; set; } = 10000;
}

public sealed class ServiceRetentionSettings
{
    public int EventDays { get; set; } = 30;
    public int ArtifactDays { get; set; } = 7;
    public int WebhookRetryDays { get; set; } = 3;
}

public sealed class TriggerDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Trigger";
    public bool Enabled { get; set; } = true;
    public List<string> CameraIds { get; set; } = [];
    public List<string> TaskIds { get; set; } = [];
    public List<string> Kinds { get; set; } = [];
    public string? LabelEquals { get; set; }
    public string? IdentityId { get; set; }
    public string? PlateTextEquals { get; set; }
    public float? MinimumConfidence { get; set; }
    public int CooldownSeconds { get; set; } = 0;
    public List<TriggerActionDefinition> Actions { get; set; } = [];
}

public sealed class TriggerActionDefinition
{
    public string Type { get; set; } = "LiveEvent";
    public string? Target { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class DetectionEventEnvelope
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public long Sequence { get; set; }
    public int PayloadVersion { get; set; } = 1;
    public string EventType { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public JsonObject Source { get; set; } = [];
    public JsonObject Trigger { get; set; } = [];
    public Dictionary<string, JsonObject> Components { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<EventArtifactDescriptor> Artifacts { get; set; } = [];
}

public sealed class EventArtifactDescriptor
{
    public string ArtifactId { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = string.Empty;
    public string ContentType { get; set; } = "image/jpeg";
    public int Width { get; set; }
    public int Height { get; set; }
    public long SourceFrameSequence { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime RetentionUntilUtc { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
}

public sealed record ServiceOperationResult(bool Accepted, string Message, long Revision, string? OperationId = null);

public static class ServiceJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
