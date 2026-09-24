using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace HshDetectionService;

/// <summary>
/// Durable outbound delivery worker for Web and SQL invocations.
/// Detection persistence never waits for this worker.
/// </summary>
public sealed class InvocationDeliveryService : BackgroundService
{
    private readonly EventStore _events;
    private readonly ServiceSettingsStore _settings;
    private readonly ArtifactStore _artifacts;
    private readonly IHttpClientFactory _httpClients;
    private readonly ILogger<InvocationDeliveryService> _logger;

    public InvocationDeliveryService(
        EventStore events,
        ServiceSettingsStore settings,
        ArtifactStore artifacts,
        IHttpClientFactory httpClients,
        ILogger<InvocationDeliveryService> logger)
    {
        _events = events;
        _settings = settings;
        _artifacts = artifacts;
        _httpClients = httpClients;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _events.ResetRunningInvocationJobs();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<InvocationJobRecord> jobs = _events.ReadPendingInvocationJobs(DateTime.UtcNow, 50);
                if (jobs.Count == 0)
                {
                    await Task.Delay(500, stoppingToken);
                    continue;
                }

                foreach (InvocationJobRecord job in jobs)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await ProcessAsync(job, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Invocation delivery loop failed.");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Executes an invocation immediately for the caller's sample event. It is
    /// deliberately outside the durable queue and does not create a job/log.
    /// </summary>
    public async Task<InvocationTestResult?> TestLatestAsync(string invocationId, DetectionEventEnvelope envelope, CancellationToken cancellationToken)
    {
        InvocationDefinition? definition = _settings.Service.Invocations.FirstOrDefault(item => item.Id.Equals(invocationId, StringComparison.OrdinalIgnoreCase));
        if (definition is null) return null;
        InvocationExecutionResult result;
        string target = definition.Type.Equals("Sql", StringComparison.OrdinalIgnoreCase) ? definition.Sql.CommandText : definition.Web.Url;
        try
        {
            result = definition.Type.Equals("Sql", StringComparison.OrdinalIgnoreCase)
                ? await ExecuteSqlAsync(definition, envelope, cancellationToken)
                : await ExecuteWebAsync(definition, envelope, cancellationToken);
        }
        catch (Exception ex)
        {
            result = new InvocationExecutionResult(false, null, null, ex.Message, null);
        }
        string method = definition.Type.Equals("Sql", StringComparison.OrdinalIgnoreCase) ? "SQL" : definition.Web.Method;
        return new InvocationTestResult(result.Success, definition.Id, definition.Name, method, target, result.ResponseStatusCode, result.ResponseBody, result.Error, result.RequestPayload);
    }

    private async Task ProcessAsync(InvocationJobRecord job, CancellationToken cancellationToken)
    {
        ServiceSettingsDocument service = _settings.Service;
        InvocationDefinition? definition = service.Invocations.FirstOrDefault(item => item.Id.Equals(job.InvocationId, StringComparison.OrdinalIgnoreCase));
        DetectionEventEnvelope? envelope = _events.Get(job.EventId);
        if (definition is null || envelope is null || !definition.Enabled)
        {
            _events.MarkInvocationJob(job.JobId, "Skipped", job.AttemptCount, null, definition is null ? "Invocation definition was removed." : "Invocation is disabled.");
            return;
        }

        if (!Matches(definition, envelope))
        {
            await LogSkippedAsync(job, definition, envelope, "Event did not match invocation filters.");
            return;
        }

        if (definition.DependsOnPrevious && !string.IsNullOrWhiteSpace(definition.WorkflowId))
        {
            InvocationDefinition? previous = service.Invocations
                .Where(item => item.Enabled && item.WorkflowId.Equals(definition.WorkflowId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.StepOrder)
                .FirstOrDefault(item => item.StepOrder < definition.StepOrder);
            if (previous is not null)
            {
                string? previousStatus = _events.GetInvocationJobStatus(job.EventSequence, previous.Id);
                if (previousStatus is null or "Pending" or "Running") return;
                if (!previousStatus.Equals("Succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    await LogSkippedAsync(job, definition, envelope, $"Previous invocation '{previous.Name}' did not succeed.");
                    return;
                }
            }
        }

        int attempt = job.AttemptCount + 1;
        _events.MarkInvocationJob(job.JobId, "Running", attempt, null, null);
        DateTime started = DateTime.UtcNow;
        InvocationExecutionResult result;
        string method = definition.Type.Equals("Sql", StringComparison.OrdinalIgnoreCase) ? "SQL" : definition.Web.Method;
        string target = definition.Type.Equals("Sql", StringComparison.OrdinalIgnoreCase) ? definition.Sql.CommandText : definition.Web.Url;
        try
        {
            result = definition.Type.Equals("Sql", StringComparison.OrdinalIgnoreCase)
                ? await ExecuteSqlAsync(definition, envelope, cancellationToken)
                : await ExecuteWebAsync(definition, envelope, cancellationToken);
        }
        catch (Exception ex)
        {
            result = new InvocationExecutionResult(false, null, null, ex.Message, null);
        }

        _events.AddInvocationLog(new InvocationLogRecord(
            0, job.JobId, job.EventSequence, job.EventId, definition.Id, definition.Name, definition.StepOrder,
            result.Success ? "Succeeded" : "Failed", attempt, started, DateTime.UtcNow, method, target,
            Truncate(result.RequestPayload), result.ResponseStatusCode, Truncate(result.ResponseBody), Truncate(result.Error)));

        if (result.Success)
        {
            _events.MarkInvocationJob(job.JobId, "Succeeded", attempt, null, null);
            return;
        }

        if (attempt <= definition.MaxRetries)
        {
            DateTime retryAt = DateTime.UtcNow.AddSeconds(Math.Max(1, definition.RetryDelaySeconds) * Math.Pow(2, Math.Min(attempt - 1, 5)));
            _events.MarkInvocationJob(job.JobId, "Pending", attempt, retryAt, result.Error ?? result.ResponseBody ?? "Invocation failed.");
        }
        else
        {
            _events.MarkInvocationJob(job.JobId, "Failed", attempt, null, result.Error ?? result.ResponseBody ?? "Invocation failed.");
        }
    }

    private async Task LogSkippedAsync(InvocationJobRecord job, InvocationDefinition definition, DetectionEventEnvelope envelope, string reason)
    {
        _events.MarkInvocationJob(job.JobId, "Skipped", job.AttemptCount, null, reason);
        _events.AddInvocationLog(new InvocationLogRecord(
            0, job.JobId, job.EventSequence, job.EventId, definition.Id, definition.Name, definition.StepOrder,
            "Skipped", job.AttemptCount, DateTime.UtcNow, DateTime.UtcNow,
            definition.Type.Equals("Sql", StringComparison.OrdinalIgnoreCase) ? "SQL" : definition.Web.Method,
            definition.Type.Equals("Sql", StringComparison.OrdinalIgnoreCase) ? definition.Sql.CommandText : definition.Web.Url,
            null, null, null, reason));
        await Task.CompletedTask;
    }

    private async Task<InvocationExecutionResult> ExecuteWebAsync(InvocationDefinition definition, DetectionEventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definition.Web.Url)) return new(false, null, null, "Web URL is empty.", null);
        List<ResolvedMapping> mappings = ResolveMappings(definition, envelope);
        Dictionary<string, object?> values = mappings.ToDictionary(item => item.Target, item => item.Value, StringComparer.OrdinalIgnoreCase);
        using HttpRequestMessage request = new(new HttpMethod(definition.Web.Method), definition.Web.Url);
        foreach ((string key, string value) in definition.Web.Headers) request.Headers.TryAddWithoutValidation(key, value);
        if (!definition.Web.AuthenticationType.Equals("None", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(definition.Web.AuthenticationValue))
        {
            if (definition.Web.AuthenticationType.Equals("Bearer", StringComparison.OrdinalIgnoreCase)) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", definition.Web.AuthenticationValue);
            else if (definition.Web.AuthenticationType.Equals("ApiKey", StringComparison.OrdinalIgnoreCase)) request.Headers.TryAddWithoutValidation("X-Api-Key", definition.Web.AuthenticationValue);
            else if (definition.Web.AuthenticationType.Equals("Basic", StringComparison.OrdinalIgnoreCase)) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", definition.Web.AuthenticationValue);
        }

        string payload;
        if (definition.Web.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            if (mappings.Any(item => item.BinaryBytes is not null)) return new(false, null, null, "GET cannot carry binary fields. Use POST multipart/form-data or Base64.", null);
            UriBuilder uri = new(definition.Web.Url);
            string query = string.Join("&", values.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(ToText(item.Value))}"));
            uri.Query = string.IsNullOrWhiteSpace(uri.Query) ? query : uri.Query.TrimStart('?') + (query.Length == 0 ? string.Empty : "&" + query);
            request.RequestUri = uri.Uri;
            payload = query;
        }
        else if (mappings.Any(item => item.BinaryBytes is not null) && !definition.Web.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            // HttpRequestMessage owns and disposes its Content. Do not use a
            // block-scoped using here: the request is sent after this branch
            // and would otherwise receive an already-disposed multipart body.
            var multipart = new MultipartFormDataContent();
            foreach (ResolvedMapping mapping in mappings)
            {
                if (mapping.BinaryBytes is not null)
                {
                    var content = new ByteArrayContent(mapping.BinaryBytes);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mapping.ContentType);
                    multipart.Add(content, mapping.Target, mapping.Target + "." + FileExtension(mapping.ContentType));
                }
                else multipart.Add(new StringContent(ToText(mapping.Value), Encoding.UTF8), mapping.Target);
            }
            request.Content = multipart;
            payload = "multipart/form-data: " + string.Join(", ", mappings.Select(item => item.BinaryBytes is null ? item.Target : $"{item.Target} ({item.BinaryBytes.Length} bytes)"));
        }
        else if (definition.Web.ContentType.Contains("form", StringComparison.OrdinalIgnoreCase))
        {
            request.Content = new FormUrlEncodedContent(values.ToDictionary(item => item.Key, item => ToText(item.Value)));
            payload = string.Join("&", values.Select(item => $"{item.Key}={ToText(item.Value)}"));
        }
        else
        {
            JsonObject body = new();
            foreach (ResolvedMapping mapping in mappings)
            {
                // A byte[] property in a JSON DTO is represented by a standard
                // Base64 string. Keep multipart only for non-JSON content types.
                object? jsonValue = mapping.BinaryBytes is not null
                    ? Convert.ToBase64String(mapping.BinaryBytes)
                    : mapping.Value;
                SetPath(body, mapping.Target, jsonValue);
            }
            payload = body.ToJsonString(ServiceJson.Options);
            request.Content = new StringContent(payload, Encoding.UTF8, definition.Web.ContentType);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(definition.TimeoutSeconds));
        using HttpResponseMessage response = await _httpClients.CreateClient(nameof(InvocationDeliveryService)).SendAsync(request, timeout.Token);
        string responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
        return new(response.IsSuccessStatusCode, (int)response.StatusCode, responseBody, response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", payload);
    }

    private async Task<InvocationExecutionResult> ExecuteSqlAsync(InvocationDefinition definition, DetectionEventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definition.Sql.ConnectionString)) return new(false, null, null, "SQL connection string is empty.", null);
        if (string.IsNullOrWhiteSpace(definition.Sql.CommandText)) return new(false, null, null, "SQL command text is empty.", null);
        await using DbConnection connection = definition.Sql.Provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)
            ? new SqlConnection(definition.Sql.ConnectionString)
            : new SqliteConnection(definition.Sql.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = definition.Sql.CommandText;
        command.CommandType = definition.Sql.CommandType.Equals("StoredProcedure", StringComparison.OrdinalIgnoreCase) ? CommandType.StoredProcedure : CommandType.Text;
        List<ResolvedMapping> mappings = ResolveMappings(definition, envelope);
        Dictionary<string, object?> values = mappings.ToDictionary(item => item.Target, item => item.Value, StringComparer.OrdinalIgnoreCase);
        foreach ((string key, object? value) in values)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = key.StartsWith('@') ? key : "@" + key;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        int affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return new(true, null, $"Rows affected: {affected}", null, JsonSerializer.Serialize(values, ServiceJson.Options));
    }

    private List<ResolvedMapping> ResolveMappings(InvocationDefinition definition, DetectionEventEnvelope envelope)
    {
        var result = new List<ResolvedMapping>();
        foreach (InvocationMapping mapping in definition.Mappings)
        {
            string contentType = "application/octet-stream";
            ImagePayloadMode imageMode = GetImagePayloadMode(mapping.Source);
            byte[]? binary = imageMode == ImagePayloadMode.Binary ? ResolveBinarySource(envelope, mapping.Source, out contentType) : null;
            object? value = imageMode == ImagePayloadMode.Binary
                ? binary
                : ResolveSource(envelope, mapping.Source);
            if (value is null && mapping.DefaultValue is not null) value = mapping.DefaultValue;
            result.Add(new ResolvedMapping(mapping.Target.TrimStart('@'), value, binary, binary is null ? "application/octet-stream" : contentType));
        }
        return result;
    }

    private object? ResolveSource(DetectionEventEnvelope envelope, string source)
    {
        string key = source.Trim();
        if (key.Equals("occurredAtLocal", StringComparison.OrdinalIgnoreCase) || key.Equals("event.occurredAtLocal", StringComparison.OrdinalIgnoreCase))
            return envelope.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        if (key.Equals("receivedAtLocal", StringComparison.OrdinalIgnoreCase) || key.Equals("event.receivedAtLocal", StringComparison.OrdinalIgnoreCase))
            return envelope.ReceivedAtUtc.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        if (key.StartsWith("artifact:", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = key.Split(':', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? ReadArtifactBase64(envelope, parts[1], false) : null;
        }
        if (key.Equals("image.frame.rawBase64", StringComparison.OrdinalIgnoreCase) || key.Equals("image.fullFrame.rawBase64", StringComparison.OrdinalIgnoreCase)) return ReadArtifactBase64(envelope, "FullFrameRaw", false);
        if (key.Equals("image.frame.base64", StringComparison.OrdinalIgnoreCase) || key.Equals("image.fullFrame.base64", StringComparison.OrdinalIgnoreCase)) return ReadArtifactBase64(envelope, "FullFrameRaw", true);
        if (key.Equals("image.crop.plate.rawBase64", StringComparison.OrdinalIgnoreCase)) return ReadArtifactBase64(envelope, "PlateCrop", false);
        if (key.Equals("image.crop.plate.base64", StringComparison.OrdinalIgnoreCase)) return ReadArtifactBase64(envelope, "PlateCrop", true);
        if (key.Equals("image.crop.face.rawBase64", StringComparison.OrdinalIgnoreCase)) return ReadArtifactBase64(envelope, "DetectionCrop", false);
        if (key.Equals("image.crop.face.base64", StringComparison.OrdinalIgnoreCase)) return ReadArtifactBase64(envelope, "DetectionCrop", true);
        if (key.Equals("image.faceAlignedCrop.rawBase64", StringComparison.OrdinalIgnoreCase)) return ReadArtifactBase64(envelope, "FaceAlignedCrop", false);
        if (key.Equals("image.faceAlignedCrop.base64", StringComparison.OrdinalIgnoreCase)) return ReadArtifactBase64(envelope, "FaceAlignedCrop", true);
        if (key.StartsWith("event.", StringComparison.OrdinalIgnoreCase)) key = key[6..];
        JsonNode? root = JsonSerializer.SerializeToNode(envelope, ServiceJson.Options);
        JsonNode? current = root;
        foreach (string part in key.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is not JsonObject objectNode) return null;
            current = objectNode.FirstOrDefault(item => item.Key.Equals(part, StringComparison.OrdinalIgnoreCase)).Value;
        }
        if (current is JsonValue valueNode && valueNode.TryGetValue<object>(out object? value)) return value;
        return current;
    }

    private byte[]? ResolveBinarySource(DetectionEventEnvelope envelope, string source, out string contentType)
    {
        contentType = "application/octet-stream";
        string key = source.Trim();
        string? type = null;
        if (key.StartsWith("artifact:", StringComparison.OrdinalIgnoreCase)) type = key.Split(':', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
        else if (key.Equals("image.frame", StringComparison.OrdinalIgnoreCase) || key.Equals("image.fullFrame", StringComparison.OrdinalIgnoreCase)) type = "FullFrameRaw";
        else if (key.Equals("image.crop.plate", StringComparison.OrdinalIgnoreCase)) type = "PlateCrop";
        else if (key.Equals("image.crop.face", StringComparison.OrdinalIgnoreCase)) type = "DetectionCrop";
        else if (key.Equals("image.faceAlignedCrop", StringComparison.OrdinalIgnoreCase)) type = "FaceAlignedCrop";
        if (type is null) return null;
        EventArtifactDescriptor? artifact = envelope.Artifacts.FirstOrDefault(item => item.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
        if (artifact is null) return null;
        string path = _artifacts.Resolve(artifact);
        if (!File.Exists(path)) return null;
        contentType = artifact.ContentType;
        return File.ReadAllBytes(path);
    }

    private static ImagePayloadMode GetImagePayloadMode(string source)
    {
        string key = source.Trim();
        if (!key.StartsWith("image.", StringComparison.OrdinalIgnoreCase)) return ImagePayloadMode.None;
        if (key.EndsWith(".base64", StringComparison.OrdinalIgnoreCase)) return ImagePayloadMode.DataUri;
        return key.Equals("image.frame", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("image.fullFrame", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("image.crop.plate", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("image.crop.face", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("image.faceAlignedCrop", StringComparison.OrdinalIgnoreCase)
            ? ImagePayloadMode.Binary
            : ImagePayloadMode.None;
    }

    private string? ReadArtifactBase64(DetectionEventEnvelope envelope, string type, bool dataUri)
    {
        EventArtifactDescriptor? artifact = envelope.Artifacts.FirstOrDefault(item => item.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
        if (artifact is null) return null;
        string path = _artifacts.Resolve(artifact);
        if (!File.Exists(path)) return null;
        string base64 = Convert.ToBase64String(File.ReadAllBytes(path));
        return dataUri ? $"data:{artifact.ContentType};base64,{base64}" : base64;
    }

    private static bool Matches(InvocationDefinition definition, DetectionEventEnvelope envelope)
    {
        string cameraId = envelope.Source["cameraId"]?.GetValue<string>() ?? string.Empty;
        if (definition.CameraIds.Count > 0 && !definition.CameraIds.Any(item => item.Equals(cameraId, StringComparison.OrdinalIgnoreCase))) return false;
        if (definition.EventTypes.Count > 0 && !definition.EventTypes.Any(item => item.Equals(envelope.EventType, StringComparison.OrdinalIgnoreCase))) return false;
        bool triggered = envelope.Trigger["matched"]?.GetValue<bool>() ?? false;
        if (definition.Triggered.HasValue && definition.Triggered.Value != triggered) return false;
        if (definition.TriggerIds.Count > 0)
        {
            JsonArray? ids = envelope.Trigger["matchingTriggerIds"] as JsonArray;
            if (ids is null || !definition.TriggerIds.Any(expected => ids.Any(value => value?.GetValue<string>()?.Equals(expected, StringComparison.OrdinalIgnoreCase) == true))) return false;
        }
        if (definition.MinimumConfidence.HasValue)
        {
            double confidence = envelope.Components.Values.Select(component => component["confidence"]?.GetValue<double>() ?? 0).DefaultIfEmpty(0).Max();
            if (confidence < definition.MinimumConfidence.Value) return false;
        }
        if (!string.IsNullOrWhiteSpace(definition.PlateTextEquals) && !string.Equals(envelope.Components["plate"]?["plateText"]?.GetValue<string>(), definition.PlateTextEquals, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static void SetPath(JsonObject root, string path, object? value)
    {
        string[] parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        JsonObject current = root;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            if (current[parts[index]] is not JsonObject child) current[parts[index]] = child = new JsonObject();
            current = child;
        }
        current[parts[^1]] = JsonSerializer.SerializeToNode(value, ServiceJson.Options);
    }

    private static string ToText(object? value) => value switch
    {
        null => string.Empty,
        JsonNode node => node.ToJsonString(ServiceJson.Options),
        bool flag => flag ? "true" : "false",
        DateTime date => date.ToUniversalTime().ToString("O"),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };

    private static string? Truncate(string? value) => value is null ? null : value.Length <= 100_000 ? value : value[..100_000] + "…";

    private static string FileExtension(string contentType) => contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? "png" : contentType.Contains("webp", StringComparison.OrdinalIgnoreCase) ? "webp" : "jpg";

    private enum ImagePayloadMode { None, Binary, DataUri }

    private sealed record ResolvedMapping(string Target, object? Value, byte[]? BinaryBytes, string ContentType);

    private sealed record InvocationExecutionResult(bool Success, int? ResponseStatusCode, string? ResponseBody, string? Error, string? RequestPayload);
}
