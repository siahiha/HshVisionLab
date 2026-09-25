using System.Text.Json;
using System.Text.Json.Nodes;

namespace HshDetectionService;

public sealed class ServiceSettingsDocument
{
    public int SchemaVersion { get; set; } = 2;
    public long Revision { get; set; }
    public string ServiceNodeId { get; set; } = Guid.NewGuid().ToString("N");
    public ServiceHttpSettings Http { get; set; } = new();
    public ServiceSecuritySettings Security { get; set; } = new();
    public ServiceRuntimeSettings Runtime { get; set; } = new();
    public ServiceAssociationSettings Association { get; set; } = new();
    public ServiceRetentionSettings Retention { get; set; } = new();
    public List<TriggerDefinition> Triggers { get; set; } = [];
    public List<InvocationDefinition> Invocations { get; set; } = [];
}

public sealed class ServiceHttpSettings
{
    public string[] ListenUrls { get; set; } = ["http://127.0.0.1:5080"];
    public string[] CorsOrigins { get; set; } = ["http://127.0.0.1:5173", "http://localhost:5173"];
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

public sealed class ServiceAssociationSettings
{
    /// <summary>Upper bound for temporal Plate/Face correlation.</summary>
    public int MaxWindowMs { get; set; } = 1500;
    public bool RequireSameRoi { get; set; } = true;
}

public sealed class ServiceRetentionSettings
{
    public int EventDays { get; set; } = 30;
    public int ArtifactDays { get; set; } = 7;
    public int WebhookRetryDays { get; set; } = 3;
}

/// <summary>
/// A generic outbound invocation. It intentionally is not named Webhook because
/// the same event can be delivered to an HTTP API or an external SQL command.
/// </summary>
public sealed class InvocationDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Invocation";
    public bool Enabled { get; set; } = true;
    public string Type { get; set; } = "Web"; // Web or Sql
    public string WorkflowId { get; set; } = string.Empty;
    public int StepOrder { get; set; }
    public bool DependsOnPrevious { get; set; }
    public List<string> CameraIds { get; set; } = [];
    public List<string> EventTypes { get; set; } = [];
    public bool? Triggered { get; set; }
    public List<string> TriggerIds { get; set; } = [];
    public float? MinimumConfidence { get; set; }
    public string? PlateTextEquals { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 30;
    public InvocationWebSettings Web { get; set; } = new();
    public InvocationSqlSettings Sql { get; set; } = new();
    public List<InvocationMapping> Mappings { get; set; } = [];
}

public sealed class InvocationWebSettings
{
    public string Url { get; set; } = string.Empty;
    public string Method { get; set; } = "POST";
    public string ContentType { get; set; } = "application/json";
    public string AuthenticationType { get; set; } = "None";
    public string AuthenticationValue { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class InvocationSqlSettings
{
    public string Provider { get; set; } = "Sqlite"; // Sqlite or SqlServer
    public string ConnectionString { get; set; } = string.Empty;
    public string CommandText { get; set; } = string.Empty;
    public string CommandType { get; set; } = "Text"; // Text or StoredProcedure
}

public sealed class InvocationMapping
{
    public string Target { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? DefaultValue { get; set; }
}

public sealed record InvocationJobRecord(
    long JobId,
    long EventSequence,
    string EventId,
    string InvocationId,
    string Status,
    int AttemptCount,
    DateTime? NextAttemptUtc,
    string? LastError,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

public sealed record InvocationLogRecord(
    long LogId,
    long JobId,
    long EventSequence,
    string EventId,
    string InvocationId,
    string InvocationName,
    int StepOrder,
    string Status,
    int Attempt,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    string Method,
    string Target,
    string? RequestPayload,
    int? ResponseStatusCode,
    string? ResponseBody,
    string? Error,
    DateTime? OccurredAtUtc = null);

public sealed record InvocationTestResult(
    bool Success,
    string InvocationId,
    string InvocationName,
    string Method,
    string Target,
    int? ResponseStatusCode,
    string? ResponseBody,
    string? Error,
    string? RequestPayload);

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

/// <summary>
/// Per-connection event policy. It never changes camera inference settings;
/// it only controls which canonical events a client receives.
/// </summary>
public sealed class ClientSubscription
{
    /// <summary>All, Plate, or KnownFace.</summary>
    public string Mode { get; set; } = "All";
    public List<string> CameraIds { get; set; } = [];
    public List<string> RoiIds { get; set; } = [];
    public bool FaceRequired { get; set; }
    public bool PlateRequired { get; set; }
    public bool IncludeFace { get; set; } = true;
    public bool IncludePlate { get; set; } = true;
    public bool IncludeUnknownFace { get; set; } = true;
    public bool IncludeArtifacts { get; set; } = true;
    public int WindowMs { get; set; } = 1500;
    public int CooldownSeconds { get; set; }

    public ClientSubscription Normalize()
    {
        Mode = Mode.Trim();
        if (!Mode.Equals("All", StringComparison.OrdinalIgnoreCase) &&
            !Mode.Equals("Plate", StringComparison.OrdinalIgnoreCase) &&
            !Mode.Equals("KnownFace", StringComparison.OrdinalIgnoreCase))
            Mode = "All";

        CameraIds ??= [];
        RoiIds ??= [];
        WindowMs = Math.Clamp(WindowMs, 0, 10_000);
        CooldownSeconds = Math.Clamp(CooldownSeconds, 0, 3600);
        return this;
    }
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
