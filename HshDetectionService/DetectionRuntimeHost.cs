using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Emgu.CV;
using Emgu.CV.CvEnum;
using HshDetectionEngin;
using HshDetectionEngin.Capture;
using HshDetectionEngin.Face;
using HshDetectionEngin.Licensing;
using HshDetectionEngin.Plate;
using Microsoft.AspNetCore.SignalR;

namespace HshDetectionService;

public sealed class DetectionRuntimeHost : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ServicePaths _paths;
    private readonly ServiceSettingsStore _settingsStore;
    private readonly EventStore _eventStore;
    private readonly ArtifactStore _artifactStore;
    private readonly ILogger<DetectionRuntimeHost> _logger;
    private readonly IHubContext<DetectionHub> _hub;
    private readonly WebRtcGateway _webrtc;
    private readonly Dictionary<string, Camera> _cameras = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _triggerGate = new();
    private readonly Dictionary<string, DateTime> _triggerLastFiredUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LatestFrameSlot> _latestFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<DetectionWork> _eventQueue = Channel.CreateUnbounded<DetectionWork>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _eventWorker;
    private FaceDatabase? _faceDatabase;
    private FaceModule? _faceModule;
    private ProcessingRegistry? _registry;
    private LicenseValidationResult? _license;
    private AppSettings _settings = new();
    private bool _started;

    public DetectionRuntimeHost(
        ServicePaths paths,
        ServiceSettingsStore settingsStore,
        EventStore eventStore,
        ArtifactStore artifactStore,
        IHubContext<DetectionHub> hub,
        WebRtcGateway webrtc,
        ILogger<DetectionRuntimeHost> logger)
    {
        _paths = paths;
        _settingsStore = settingsStore;
        _eventStore = eventStore;
        _artifactStore = artifactStore;
        _hub = hub;
        _webrtc = webrtc;
        _logger = logger;
    }

    public ServicePaths Paths => _paths;
    public EventStore Events => _eventStore;
    public ArtifactStore Artifacts => _artifactStore;
    public FaceDatabase FaceDatabase => _faceDatabase ?? throw new InvalidOperationException("Face database is not ready.");
    public FaceModule FaceModule => _faceModule ?? throw new InvalidOperationException("Face module is not ready.");
    public LicenseValidationResult License => _license ?? throw new InvalidOperationException("License is not ready.");
    public ProcessingRegistry ProcessingModules => _registry ?? throw new InvalidOperationException("Processing registry is not ready.");
    public bool IsReady { get; private set; }
    public string? ReadinessError { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        try
        {
            _paths.EnsureDirectories();
            _settings = _settingsStore.Detection;
            ServiceSettingsDocument service = _settingsStore.Service;
            _license = LicenseValidator.Load(_paths.LicensePath);
            _faceDatabase = FaceDatabase.Load(_paths.FaceDatabasePath);
            _faceModule = new FaceModule(_faceDatabase, _license);
            _registry = new ProcessingRegistry();
            _registry.Register(PlateModule.CreateRegistration(_license));
            _registry.Register(_faceModule.CreateRegistration());

            _eventWorker = Task.Run(() => ProcessEventQueueAsync(_shutdown.Token), CancellationToken.None);
            foreach (CameraSettings cameraSettings in _settings.Cameras.ToArray())
                AddOrReplaceCamera(cameraSettings, start: service.Runtime.AutoStartCameras);

            IsReady = true;
            ReadinessError = null;
            _logger.LogInformation("Detection service runtime started with {CameraCount} cameras. License: {LicenseMessage}", _cameras.Count, _license.Message);
        }
        catch (Exception ex)
        {
            ReadinessError = ex.Message;
            _logger.LogError(ex, "Detection runtime failed to start.");
            throw;
        }

        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started || _shutdown.IsCancellationRequested) return;
        IsReady = false;
        _shutdown.Cancel();
        _eventQueue.Writer.TryComplete();

        Camera[] cameras;
        lock (_gate) cameras = _cameras.Values.ToArray();
        foreach (Camera camera in cameras)
        {
            try { camera.Stop(); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to stop camera {CameraId}.", camera.Settings.Id); }
            DetachCamera(camera);
            camera.Dispose();
        }

        lock (_gate) _cameras.Clear();
        foreach ((string _, LatestFrameSlot slot) in _latestFrames) slot.Dispose();
        _latestFrames.Clear();
        if (_eventWorker is not null)
        {
            try { await _eventWorker.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); } catch { }
        }
        _faceDatabase?.Dispose();
        _eventStore.Dispose();
        try { await MediaMtxRuntime.Shared.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); } catch { }
    }

    public IReadOnlyList<CameraStatusDto> GetCameraStatuses()
    {
        lock (_gate)
        {
            return _cameras.Values.Select(camera => new CameraStatusDto(
                camera.Settings.Id,
                camera.Settings.Name,
                camera.IsRunning,
                camera.ProcessingFps,
                camera.LastInferenceMs,
                camera.DroppedFrames,
                camera.LastFrameSize.Width,
                camera.LastFrameSize.Height,
                camera.IsRunning ? "Running" : "Stopped",
                camera.ConfiguredRoiCount,
                camera.ConfiguredTaskCount,
                camera.ActivePipelineCount,
                camera.ActivePipelineCount > 0
                    ? "Ready"
                    : camera.ConfiguredTaskCount > 0 ? "Unavailable" : "NotConfigured",
                camera.Settings.CaptureBackend)).ToArray();
        }
    }

    public bool TryGetCamera(string cameraId, out Camera? camera)
    {
        lock (_gate) return _cameras.TryGetValue(cameraId, out camera);
    }

    public bool StartCamera(string cameraId)
    {
        if (!TryGetCamera(cameraId, out Camera? camera) || camera is null) return false;
        camera.Start();
        return true;
    }

    public bool StopCamera(string cameraId)
    {
        if (!TryGetCamera(cameraId, out Camera? camera) || camera is null) return false;
        camera.Stop();
        return true;
    }

    public bool RemoveCamera(string cameraId)
    {
        Camera? camera;
        lock (_gate)
        {
            if (!_cameras.Remove(cameraId, out camera)) return false;
            _settings.Cameras.RemoveAll(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
        }

        if (camera is not null)
        {
            DetachCamera(camera);
            camera.Dispose();
        }
        return true;
    }

    public void AddOrReplaceCamera(CameraSettings settings, bool start)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.EnsureProcessingDefaults();
        if (string.IsNullOrWhiteSpace(settings.Id)) settings.Id = Guid.NewGuid().ToString("N");

        Camera? previous = null;
        lock (_gate)
        {
            if (_cameras.Remove(settings.Id, out previous)) { }
            _settings.Cameras.RemoveAll(item => item.Id.Equals(settings.Id, StringComparison.OrdinalIgnoreCase));
            _settings.Cameras.Add(settings);
            Camera camera = CreateCamera(settings);
            _cameras[settings.Id] = camera;
            if (start) camera.Start();
        }

        if (previous is not null)
        {
            DetachCamera(previous);
            try { previous.Stop(); } catch { }
            previous.Dispose();
        }
    }

    public void ApplyDetectionSettings(AppSettings settings, bool startCameras)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Cameras ??= [];
        foreach (CameraSettings camera in settings.Cameras) camera.EnsureProcessingDefaults();

        Camera[] previous;
        lock (_gate) previous = _cameras.Values.ToArray();
        foreach (Camera camera in previous)
        {
            DetachCamera(camera);
            try { camera.Stop(); } catch { }
            camera.Dispose();
        }
        lock (_gate)
        {
            _cameras.Clear();
            _settings = settings;
        }
        foreach (CameraSettings cameraSettings in settings.Cameras.ToArray())
            AddOrReplaceCamera(cameraSettings, startCameras);
    }

    public Bitmap? GetLatestFrame(string cameraId)
    {
        if (!_latestFrames.TryGetValue(cameraId, out LatestFrameSlot? slot)) return null;
        return slot.Clone(out _);
    }

    public bool TryGetLatestFrame(string cameraId, out Bitmap? frame, out long sequence)
    {
        if (!_latestFrames.TryGetValue(cameraId, out LatestFrameSlot? slot))
        {
            frame = null;
            sequence = 0;
            return false;
        }
        frame = slot.Clone(out sequence);
        return frame is not null;
    }

    public async Task<FaceSample> EnrollFaceSampleAsync(string personId, string? personName, byte[] image, string fileName, CancellationToken cancellationToken)
    {
        if (image.Length == 0) throw new InvalidDataException("Face image is empty.");
        CameraProcessingSettings processing = FindFaceProcessingSettings();
        using FacePipeline pipeline = FaceModule.CreatePipeline(processing, maxFpsOverride: 0, requireRecognition: true)
            ?? throw new InvalidOperationException("Face detection or recognition model is unavailable.");

        string temporaryPath = Path.Combine(Paths.MediaDirectory, $"enroll-{Guid.NewGuid():N}{Path.GetExtension(fileName)}");
        await File.WriteAllBytesAsync(temporaryPath, image, cancellationToken);
        try
        {
            using Mat source = CvInvoke.Imread(temporaryPath, ImreadModes.AnyColor);
            FaceEnrollment enrollment = pipeline.CreateEnrollment(source);
            FaceIdentity? person = FaceDatabase.Identities.FirstOrDefault(item => item.Id == personId);
            if (person is null && string.IsNullOrWhiteSpace(personName)) throw new InvalidOperationException("A person id or name is required.");
            return FaceDatabase.RegisterSample(
                person?.Name ?? personName!, enrollment.Embedding, enrollment.FaceImage,
                Path.GetFileName(fileName), personId: person?.Id ?? personId,
                detectionConfidence: enrollment.DetectionConfidence);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private CameraProcessingSettings FindFaceProcessingSettings()
    {
        CameraProcessingSettings? item = _settings.Cameras
            .SelectMany(camera => camera.Rois.SelectMany(roi => roi.Processing))
            .FirstOrDefault(item => item.Enabled && item.Kind == ProcessingType.Face);
        return item ?? throw new InvalidOperationException("At least one enabled Face processing task is required for enrollment.");
    }

    private Camera CreateCamera(CameraSettings settings)
    {
        Camera camera = new(settings, processingRegistry: ProcessingModules, license: License);
        camera.FrameReady += Camera_FrameReady;
        camera.PlateDetected += Camera_PlateDetected;
        camera.AnalysisDetected += Camera_AnalysisDetected;
        camera.StatusChanged += Camera_StatusChanged;
        return camera;
    }

    private void DetachCamera(Camera camera)
    {
        camera.FrameReady -= Camera_FrameReady;
        camera.PlateDetected -= Camera_PlateDetected;
        camera.AnalysisDetected -= Camera_AnalysisDetected;
        camera.StatusChanged -= Camera_StatusChanged;
    }

    private void Camera_FrameReady(CameraRuntime camera, Bitmap frame)
    {
        try
        {
            Bitmap clone = new(frame);
            long sequence = camera.FrameSource.CapturedFrames;
            _latestFrames.AddOrUpdate(camera.Settings.Id,
                _ => new LatestFrameSlot(clone, sequence),
                (_, old) => { old.Replace(clone, sequence); return old; });
            // MediaMTX cameras are displayed through the raw WHEP path. Do
            // not clone and enqueue every preview frame into the composite
            // gateway unless a legacy/composite WebRTC viewer is connected.
            if (_webrtc.SessionCount > 0)
            {
                using Bitmap webRtcFrame = new(frame);
                _webrtc.PushFrame(camera.Settings.Id, webRtcFrame);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to store latest frame for {CameraId}.", camera.Settings.Id); }
    }

    private void Camera_PlateDetected(CameraRuntime camera, CameraRuntime.HistoryItem item)
        => QueueEvent(camera, item.Crop, null, item.Timestamp, AnalysisKind.Plate, item.Text, null);

    private void Camera_AnalysisDetected(CameraRuntime camera, CameraRuntime.AnalysisHistoryItem item)
        => QueueEvent(camera, item.Crop, item.Detection, item.Timestamp, item.Detection.Kind, item.Detection.Label, item.Detection);

    private void QueueEvent(CameraRuntime camera, Bitmap crop, AnalysisDetection? detection, DateTime timestamp, AnalysisKind kind, string label, AnalysisDetection? sourceDetection)
    {
        Bitmap? full = camera.TryGetLatestRawFrame(out long rawSequence);
        long sourceFrameSequence = rawSequence > 0
            ? rawSequence
            : _latestFrames.TryGetValue(camera.Settings.Id, out LatestFrameSlot? slot)
                ? slot.Sequence
                : camera.FrameSource.CapturedFrames;
        _eventQueue.Writer.TryWrite(new DetectionWork(
            camera.Settings.Id,
            kind,
            label,
            timestamp.ToUniversalTime(),
            sourceDetection,
            new Bitmap(crop),
            full,
            sourceFrameSequence));
    }

    private void Camera_StatusChanged(CameraRuntime camera, string message, bool isError)
    {
        if (isError) _logger.LogWarning("Camera {CameraId}: {Message}", camera.Settings.Id, message);
        else _logger.LogInformation("Camera {CameraId}: {Message}", camera.Settings.Id, message);
    }

    private async Task ProcessEventQueueAsync(CancellationToken cancellationToken)
    {
        await foreach (DetectionWork work in _eventQueue.Reader.ReadAllAsync(cancellationToken))
        {
            try { await PersistEventAsync(work, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) { _logger.LogError(ex, "Failed to persist detection event for camera {CameraId}.", work.CameraId); }
            finally { work.Dispose(); }
        }
    }

    private async Task PersistEventAsync(DetectionWork work, CancellationToken cancellationToken)
    {
        ServiceSettingsDocument service = _settingsStore.Service;
        string eventId = Guid.NewGuid().ToString("N");
        DateTime retention = DateTime.UtcNow.AddDays(Math.Max(1, service.Retention.ArtifactDays));
        DetectionEventEnvelope envelope = BuildEvent(work, eventId, service);

        if (work.FullFrame is not null)
            envelope.Artifacts.Add(_artifactStore.SaveBitmap(eventId, "FullFrameRaw", work.FullFrame, work.SourceFrameSequence, retention));
        envelope.Artifacts.Add(_artifactStore.SaveBitmap(eventId, work.Kind == AnalysisKind.Face ? "DetectionCrop" : "PlateCrop", work.Crop, work.SourceFrameSequence, retention));

        if (work.FullFrame is not null)
        {
            CameraSettings camera = _settings.Cameras.FirstOrDefault(item => item.Id.Equals(work.CameraId, StringComparison.OrdinalIgnoreCase))
                ?? new CameraSettings { Id = work.CameraId };
            using Bitmap? roi = CropForDetection(work.FullFrame, work.SourceDetection, camera);
            if (roi is not null)
                envelope.Artifacts.Add(_artifactStore.SaveBitmap(eventId, "RoiRaw", roi, work.SourceFrameSequence, retention));
        }

        if (TryGetMetadataBytes(work.SourceDetection, "AlignedFaceJpeg", out byte[]? alignedFace) && alignedFace is not null)
        {
            using var stream = new MemoryStream(alignedFace, writable: false);
            using var decoded = new Bitmap(stream);
            using var aligned = new Bitmap(decoded);
            envelope.Artifacts.Add(_artifactStore.SaveBitmap(eventId, "FaceAlignedCrop", aligned, work.SourceFrameSequence, retention));
        }

        DetectionEventEnvelope stored = _eventStore.Append(envelope);
        await DetectionHub.PublishAsync(_hub, stored, cancellationToken);
    }

    private DetectionEventEnvelope BuildEvent(DetectionWork work, string eventId, ServiceSettingsDocument service)
    {
        CameraSettings camera = _settings.Cameras.FirstOrDefault(item => item.Id.Equals(work.CameraId, StringComparison.OrdinalIgnoreCase)) ?? new CameraSettings { Id = work.CameraId };
        string taskId = GetMetadataString(work.SourceDetection, "ProcessingItemId") ?? string.Empty;
        string taskName = GetMetadataString(work.SourceDetection, "ProcessingItemName") ?? string.Empty;
        string roiName = GetMetadataString(work.SourceDetection, "RoiName") ?? string.Empty;
        string roiId = GetMetadataString(work.SourceDetection, "RoiId") ?? string.Empty;
        float confidence = work.SourceDetection?.Confidence ?? 0;
        string kind = work.Kind.ToString();
        TriggerEvaluation triggerEvaluation = EvaluateTriggers(service.Triggers, camera.Id, taskId, kind, work.Label, confidence, work.SourceDetection);

        var envelope = new DetectionEventEnvelope
        {
            EventId = eventId,
            EventType = work.Kind == AnalysisKind.Face
                ? (IsUnknown(work.SourceDetection) ? "FaceUnknown" : "FaceRecognized")
                : "PlateDetected",
            Scenario = work.Kind == AnalysisKind.Face ? "FaceRecognition" : "PlateOnly",
            OccurredAtUtc = work.Timestamp,
            ReceivedAtUtc = DateTime.UtcNow,
            Source = new JsonObject
            {
                ["serviceNodeId"] = service.ServiceNodeId,
                ["cameraId"] = camera.Id,
                ["cameraName"] = camera.Name,
                ["taskId"] = taskId,
                ["taskName"] = taskName,
                ["roiId"] = roiId,
                ["roiName"] = roiName,
                ["sourceFrameSequence"] = work.SourceFrameSequence,
                ["frameWidth"] = work.FullFrame?.Width ?? 0,
                ["frameHeight"] = work.FullFrame?.Height ?? 0
            },
            Trigger = new JsonObject
            {
                ["matched"] = triggerEvaluation.Matched.Count > 0,
                ["cooldownApplied"] = triggerEvaluation.CooldownApplied,
                ["matchingTriggerIds"] = new JsonArray(triggerEvaluation.Matched.Select(trigger => JsonValue.Create(trigger.Id)).ToArray()),
                ["suppressedTriggerIds"] = new JsonArray(triggerEvaluation.Suppressed.Select(trigger => JsonValue.Create(trigger.Id)).ToArray())
            }
        };

        if (work.SourceDetection is AnalysisDetection detection)
        {
            JsonObject component = BuildDetectionComponent(detection, work.FullFrame?.Size ?? Size.Empty);
            envelope.Components[kind.Equals("Face", StringComparison.OrdinalIgnoreCase) ? "face" : "plate"] = component;
        }
        else
        {
            envelope.Components["plate"] = new JsonObject
            {
                ["kind"] = "Plate",
                ["status"] = "Accepted",
                ["label"] = work.Label
            };
        }

        return envelope;
    }

    private static JsonObject BuildDetectionComponent(AnalysisDetection detection, Size frameSize)
    {
        JsonObject component = new()
        {
            ["componentId"] = Guid.NewGuid().ToString("N"),
            ["kind"] = detection.Kind.ToString(),
            ["status"] = IsAccepted(detection) ? "Accepted" : "Rejected",
            ["label"] = detection.Label,
            ["confidence"] = detection.Confidence,
            ["threshold"] = GetMetadataFloat(detection, "OverlayThreshold"),
            ["trackId"] = detection.TrackId,
            ["bounds"] = BoundsJson(detection.Bounds, frameSize)
        };

        if (detection.Kind == AnalysisKind.Face)
        {
            string? identityId = GetMetadataString(detection, "IdentityId");
            float similarity = GetMetadataFloat(detection, "Similarity");
            bool recognized = GetMetadataBool(detection, "Recognized");
            bool unknown = string.IsNullOrWhiteSpace(identityId) || detection.Label.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase);
            component["recognitionStatus"] = !recognized ? "NotAttempted" : unknown ? "Unknown" : "Matched";
            component["recognition"] = new JsonObject
            {
                ["personId"] = identityId,
                ["name"] = detection.Label,
                ["personNumber"] = GetMetadataInt(detection, "PersonNumber"),
                ["isUnknown"] = GetMetadataBool(detection, "IsUnknown") || unknown,
                ["similarity"] = similarity,
                ["minimumSimilarity"] = GetMetadataFloat(detection, "FaceRecordConfidence"),
                ["matchedSampleId"] = GetMetadataString(detection, "MatchedSampleId")
            };
        }
        else if (detection.Kind == AnalysisKind.Plate)
        {
            component["plateText"] = GetMetadataString(detection, "PlateText") ?? detection.Label;
            component["plateConfidence"] = detection.Confidence;
            component["plateThreshold"] = GetMetadataFloat(detection, "Threshold");
            component["isValidIranianPlate"] = GetMetadataBool(detection, "Accepted");
            component["hasCharacterDetails"] = GetMetadataBool(detection, "HasCharacterDetails");
            component["characters"] = GetMetadataNode(detection, "Characters") ?? new JsonArray();
        }

        return component;
    }

    private static Bitmap? CropForDetection(Bitmap source, AnalysisDetection? detection, CameraSettings camera)
    {
        string? roiName = GetMetadataString(detection, "RoiName");
        NamedRoi? roi = camera.Rois.FirstOrDefault(item => item.Enabled &&
            !string.IsNullOrWhiteSpace(roiName) && item.Name.Equals(roiName, StringComparison.OrdinalIgnoreCase));
        Rectangle area = roi is not null && roi.Points.Count >= 3
            ? PolygonBounds(roi.Points, source.Size)
            : detection?.Bounds ?? new Rectangle(0, 0, source.Width, source.Height);
        area = Rectangle.Intersect(area, new Rectangle(0, 0, source.Width, source.Height));
        if (area.Width <= 0 || area.Height <= 0) return null;
        return source.Clone(area, PixelFormat.Format24bppRgb);
    }

    private static Rectangle PolygonBounds(IReadOnlyList<RoiPoint> points, Size size)
    {
        int left = (int)Math.Floor(points.Min(point => Math.Clamp(point.X, 0, 1) * size.Width));
        int top = (int)Math.Floor(points.Min(point => Math.Clamp(point.Y, 0, 1) * size.Height));
        int right = (int)Math.Ceiling(points.Max(point => Math.Clamp(point.X, 0, 1) * size.Width));
        int bottom = (int)Math.Ceiling(points.Max(point => Math.Clamp(point.Y, 0, 1) * size.Height));
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static bool MatchesTrigger(TriggerDefinition trigger, string cameraId, string taskId, string kind, string label, float confidence, AnalysisDetection? detection = null)
    {
        if (!trigger.Enabled) return false;
        if (trigger.CameraIds.Count > 0 && !trigger.CameraIds.Contains(cameraId, StringComparer.OrdinalIgnoreCase)) return false;
        if (trigger.TaskIds.Count > 0 && !trigger.TaskIds.Contains(taskId, StringComparer.OrdinalIgnoreCase)) return false;
        if (trigger.Kinds.Count > 0 && !trigger.Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(trigger.LabelEquals) && !string.Equals(trigger.LabelEquals, label, StringComparison.OrdinalIgnoreCase)) return false;
        if (trigger.MinimumConfidence is float minimum && confidence < minimum) return false;
        if (!string.IsNullOrWhiteSpace(trigger.IdentityId) && !string.Equals(trigger.IdentityId, GetMetadataString(detection, "IdentityId"), StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(trigger.PlateTextEquals) && !string.Equals(trigger.PlateTextEquals, GetMetadataString(detection, "PlateText") ?? label, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private TriggerEvaluation EvaluateTriggers(
        IReadOnlyList<TriggerDefinition> triggers,
        string cameraId,
        string taskId,
        string kind,
        string label,
        float confidence,
        AnalysisDetection? detection)
    {
        var matched = new List<TriggerDefinition>();
        var suppressed = new List<TriggerDefinition>();
        DateTime now = DateTime.UtcNow;
        lock (_triggerGate)
        {
            foreach (TriggerDefinition trigger in triggers)
            {
                if (!MatchesTrigger(trigger, cameraId, taskId, kind, label, confidence, detection)) continue;
                if (trigger.CooldownSeconds > 0 && _triggerLastFiredUtc.TryGetValue(trigger.Id, out DateTime previous) &&
                    (now - previous).TotalSeconds < trigger.CooldownSeconds)
                {
                    suppressed.Add(trigger);
                    continue;
                }
                _triggerLastFiredUtc[trigger.Id] = now;
                matched.Add(trigger);
            }

            foreach (string triggerId in _triggerLastFiredUtc
                .Where(item => (now - item.Value).TotalHours > 24)
                .Select(item => item.Key).ToArray())
                _triggerLastFiredUtc.Remove(triggerId);
        }
        return new TriggerEvaluation(matched, suppressed);
    }

    private static bool IsAccepted(AnalysisDetection detection) => GetMetadataBool(detection, "Accepted") || detection.Confidence >= GetMetadataFloat(detection, "OverlayThreshold");
    private static bool IsUnknown(AnalysisDetection? detection) => detection is null || detection.Label.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase);
    private static string? GetMetadataString(AnalysisDetection? detection, string key) => detection?.Metadata?.TryGetValue(key, out object? value) == true ? value?.ToString() : null;
    private static bool GetMetadataBool(AnalysisDetection? detection, string key) => detection?.Metadata?.TryGetValue(key, out object? value) == true && value is bool flag && flag;
    private static float GetMetadataFloat(AnalysisDetection? detection, string key) => detection?.Metadata?.TryGetValue(key, out object? value) == true ? Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    private static int GetMetadataInt(AnalysisDetection? detection, string key) => detection?.Metadata?.TryGetValue(key, out object? value) == true ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    private static bool TryGetMetadataBytes(AnalysisDetection? detection, string key, out byte[]? bytes)
    {
        bytes = detection?.Metadata?.TryGetValue(key, out object? value) == true ? value as byte[] : null;
        return bytes is { Length: > 0 };
    }

    private static JsonNode? GetMetadataNode(AnalysisDetection detection, string key)
    {
        if (detection.Metadata?.TryGetValue(key, out object? value) != true || value is null) return null;
        return JsonSerializer.SerializeToNode(value, ServiceJson.Options);
    }

    private static JsonObject BoundsJson(Rectangle bounds, Size frameSize)
    {
        float width = Math.Max(1, frameSize.Width);
        float height = Math.Max(1, frameSize.Height);
        return new JsonObject
        {
            ["x"] = bounds.X,
            ["y"] = bounds.Y,
            ["width"] = bounds.Width,
            ["height"] = bounds.Height,
            ["xNormalized"] = bounds.X / width,
            ["yNormalized"] = bounds.Y / height,
            ["widthNormalized"] = bounds.Width / width,
            ["heightNormalized"] = bounds.Height / height
        };
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    private sealed class DetectionWork : IDisposable
    {
        public DetectionWork(string cameraId, AnalysisKind kind, string label, DateTime timestamp, AnalysisDetection? sourceDetection, Bitmap crop, Bitmap? fullFrame, long sourceFrameSequence)
        {
            CameraId = cameraId; Kind = kind; Label = label; Timestamp = timestamp; SourceDetection = sourceDetection; Crop = crop; FullFrame = fullFrame; SourceFrameSequence = sourceFrameSequence;
        }
        public string CameraId { get; }
        public AnalysisKind Kind { get; }
        public string Label { get; }
        public DateTime Timestamp { get; }
        public AnalysisDetection? SourceDetection { get; }
        public Bitmap Crop { get; }
        public Bitmap? FullFrame { get; }
        public long SourceFrameSequence { get; }
        public void Dispose() { Crop.Dispose(); FullFrame?.Dispose(); }
    }

    private sealed record TriggerEvaluation(IReadOnlyList<TriggerDefinition> Matched, IReadOnlyList<TriggerDefinition> Suppressed)
    {
        public bool CooldownApplied => Suppressed.Count > 0;
    }

    private sealed class LatestFrameSlot : IDisposable
    {
        private readonly object _gate = new();
        private Bitmap _frame;
        private long _sequence;
        public LatestFrameSlot(Bitmap frame, long sequence) { _frame = frame; _sequence = sequence; }
        public long Sequence { get { lock (_gate) return _sequence; } }
        public void Replace(Bitmap frame, long sequence) { lock (_gate) { Bitmap old = _frame; _frame = frame; _sequence = sequence; old.Dispose(); } }
        public Bitmap Clone(out long sequence) { lock (_gate) { sequence = _sequence; return new Bitmap(_frame); } }
        public void Dispose() { lock (_gate) _frame.Dispose(); }
    }
}

public sealed record CameraStatusDto(
    string Id,
    string Name,
    bool Running,
    double Fps,
    double InferenceMs,
    long DroppedFrames,
    int Width,
    int Height,
    string SourceState,
    int ConfiguredRoiCount,
    int ConfiguredTaskCount,
    int ActivePipelineCount,
    string ProcessingState,
    string CaptureBackend);
