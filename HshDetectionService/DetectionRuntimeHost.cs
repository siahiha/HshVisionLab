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
    private readonly ConcurrentDictionary<string, LatestFrameSlot> _latestFrames = new(StringComparer.OrdinalIgnoreCase);
    private Channel<DetectionWork> _eventQueue = CreateEventQueue(10_000);
    private readonly object _associationGate = new();
    private readonly List<PendingComponent> _pendingComponents = [];
    private readonly Dictionary<string, DateTime> _emittedAssociationTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _emittedComponentTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _associationTimer;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _eventWorker;
    private FaceDatabase? _faceDatabase;
    private FaceModule? _faceModule;
    private ProcessingRegistry? _registry;
    private LicenseValidationResult? _license;
    private AppSettings _settings = new();
    private bool _started;
    private long _droppedEventCount;

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
        _associationTimer = new Timer(_ => FlushExpiredAssociations(), null, Timeout.Infinite, Timeout.Infinite);
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
    public long DroppedEventCount => Interlocked.Read(ref _droppedEventCount);

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
            _eventQueue = CreateEventQueue(service.Runtime?.MaxEventQueueLength ?? 10_000);
            _license = LicenseValidator.Load(_paths.LicensePath);
            _faceDatabase = FaceDatabase.Load(_paths.FaceDatabasePath);
            _faceModule = new FaceModule(_faceDatabase, _license);
            _registry = new ProcessingRegistry();
            _registry.Register(PlateModule.CreateRegistration(_license));
            _registry.Register(_faceModule.CreateRegistration());

            _eventWorker = Task.Run(() => ProcessEventQueueAsync(_shutdown.Token), CancellationToken.None);
            _associationTimer.Change(500, 500);
            foreach (CameraSettings cameraSettings in _settings.Cameras.ToArray())
                AddOrReplaceCamera(cameraSettings, start: service.Runtime?.AutoStartCameras ?? true);

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
        _associationTimer.Change(Timeout.Infinite, Timeout.Infinite);
        FlushExpiredAssociations(force: true);
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
        _associationTimer.Dispose();
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

        ClearAssociationState();

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
        camera.PipelineResultsReady += Camera_PipelineResultsReady;
        camera.StatusChanged += Camera_StatusChanged;
        return camera;
    }

    private void DetachCamera(Camera camera)
    {
        camera.FrameReady -= Camera_FrameReady;
        camera.PipelineResultsReady -= Camera_PipelineResultsReady;
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
        finally
        {
            // CameraRuntime creates a fresh preview bitmap for this event.
            // The service clones what it needs above, so the event payload
            // must be released here after all subscribers have consumed it.
            frame.Dispose();
        }
    }

    private void Camera_PipelineResultsReady(CameraRuntime camera, IReadOnlyList<AnalysisDetection> detections)
    {
        AnalysisDetection[] accepted = detections
            .Where(detection => (detection.Kind == AnalysisKind.Plate || detection.Kind == AnalysisKind.Face) && IsAccepted(detection))
            .ToArray();
        accepted = FilterRepeatedDetections(camera.Settings.Id, accepted);
        if (accepted.Length == 0) return;

        using Bitmap? raw = camera.TryGetLatestRawFrame(out long rawSequence);
        if (raw is null) return;
        long sourceFrameSequence = rawSequence > 0
            ? rawSequence
            : _latestFrames.TryGetValue(camera.Settings.Id, out LatestFrameSlot? slot)
                ? slot.Sequence
                : camera.FrameSource.CapturedFrames;

        var current = new List<PendingComponent>(accepted.Length);
        foreach (AnalysisDetection detection in accepted)
        {
            Rectangle bounds = Rectangle.Intersect(detection.Bounds, new Rectangle(Point.Empty, raw.Size));
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;
            current.Add(new PendingComponent(
                camera.Settings.Id,
                detection,
                raw.Clone(bounds, PixelFormat.Format24bppRgb),
                new Bitmap(raw),
                sourceFrameSequence,
                DateTime.UtcNow,
                GetRoiKey(detection),
                GetComponentKey(detection)));
        }

        if (current.Count == 0) return;
        ProcessAssociations(current);
    }

    private void ProcessAssociations(List<PendingComponent> current)
    {
        var ready = new List<DetectionWork>();
        DateTime now = DateTime.UtcNow;
        lock (_associationGate)
        {
            FlushExpiredAssociationsLocked(now, ready);
            var remaining = new List<PendingComponent>(current);

            // Prefer an exact same-frame association. It is the strongest
            // evidence and prevents PlateFirst/FaceFirst duplicate records.
            foreach (PendingComponent plate in remaining.Where(item => item.Detection.Kind == AnalysisKind.Plate).ToArray())
            {
                PendingComponent[] faces = remaining
                    .Where(item => item.Detection.Kind == AnalysisKind.Face && SameAssociationScope(item, plate))
                    .ToArray();
                if (faces.Length != 1) continue;
                PendingComponent face = faces[0];
                remaining.Remove(plate);
                remaining.Remove(face);
                ready.Add(new DetectionWork(plate.CameraId, [plate, face], "SameFrame"));
            }

            // A pending component can be completed by the opposite component
            // in a later processed frame. Ambiguous multi-person/multi-plate
            // cases are deliberately left unpaired to avoid false matches.
            foreach (PendingComponent item in remaining.ToArray())
            {
                PendingComponent[] opposite = _pendingComponents
                    .Where(previous => previous.CameraId.Equals(item.CameraId, StringComparison.OrdinalIgnoreCase) &&
                        SameAssociationScope(previous, item) &&
                        previous.Detection.Kind != item.Detection.Kind &&
                        (now - previous.Timestamp).TotalMilliseconds <= AssociationWindowMs)
                    .ToArray();
                if (opposite.Length != 1) continue;
                PendingComponent previous = opposite[0];
                _pendingComponents.Remove(previous);
                remaining.Remove(item);
                ready.Add(new DetectionWork(item.CameraId, [previous, item], "TemporalAssociation"));
            }

            foreach (PendingComponent item in remaining)
            {
                PendingComponent? duplicate = _pendingComponents.FirstOrDefault(previous =>
                    previous.CameraId.Equals(item.CameraId, StringComparison.OrdinalIgnoreCase) &&
                    previous.RoiKey.Equals(item.RoiKey, StringComparison.OrdinalIgnoreCase) &&
                    previous.ComponentKey.Equals(item.ComponentKey, StringComparison.OrdinalIgnoreCase));
                if (duplicate is not null)
                {
                    _pendingComponents.Remove(duplicate);
                    item.PreserveStartTime(duplicate.Timestamp);
                }
                duplicate?.Dispose();
                _pendingComponents.Add(item);
            }
        }

        foreach (DetectionWork work in ready) EnqueueEvent(work);
    }

    private void FlushExpiredAssociations()
    {
        var ready = new List<DetectionWork>();
        lock (_associationGate) FlushExpiredAssociationsLocked(DateTime.UtcNow, ready);
        foreach (DetectionWork work in ready) EnqueueEvent(work);
    }

    private void FlushExpiredAssociations(bool force)
    {
        if (force) FlushExpiredAssociations();
    }

    private void ClearAssociationState()
    {
        lock (_associationGate)
        {
            foreach (PendingComponent item in _pendingComponents) item.Dispose();
            _pendingComponents.Clear();
            _emittedAssociationTimes.Clear();
            _emittedComponentTimes.Clear();
        }
    }

    private void FlushExpiredAssociationsLocked(DateTime now, List<DetectionWork> ready)
    {
        foreach (PendingComponent item in _pendingComponents
            .Where(item => (now - item.Timestamp).TotalMilliseconds > AssociationWindowMs)
            .ToArray())
        {
            _pendingComponents.Remove(item);
            ready.Add(new DetectionWork(item.CameraId, [item], "Standalone"));
        }
    }

    private void EnqueueEvent(DetectionWork work)
    {
        bool duplicate;
        lock (_associationGate)
        {
            DateTime now = DateTime.UtcNow;
            string associationKey = string.Join("|", work.Components
                .Select(item => $"{item.CameraId}:{item.RoiKey}:{item.ComponentKey}")
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
            bool associationDuplicate = _emittedAssociationTimes.TryGetValue(associationKey, out DateTime previous) &&
                (now - previous).TotalSeconds < 5;
            duplicate = associationDuplicate;
            if (!duplicate)
            {
                _emittedAssociationTimes[associationKey] = now;
                foreach (PendingComponent component in work.Components)
                    _emittedComponentTimes[string.Concat(component.CameraId, ":", component.RoiKey, ":", component.ComponentKey)] = now;
            }
            foreach (string oldKey in _emittedAssociationTimes
                .Where(item => (now - item.Value).TotalHours > 1)
                .Select(item => item.Key).ToArray())
                _emittedAssociationTimes.Remove(oldKey);
            foreach (string oldKey in _emittedComponentTimes
                .Where(item => (now - item.Value).TotalMinutes > 1)
                .Select(item => item.Key).ToArray())
                _emittedComponentTimes.Remove(oldKey);
        }

        if (duplicate)
        {
            work.Dispose();
            return;
        }

        if (!_eventQueue.Writer.TryWrite(work))
        {
            Interlocked.Increment(ref _droppedEventCount);
            _logger.LogWarning("Detection event queue is full; dropping an event for camera {CameraId}.", work.CameraId);
            work.Dispose();
        }
    }


    private static Channel<DetectionWork> CreateEventQueue(int configuredCapacity)
    {
        int capacity = Math.Clamp(configuredCapacity, 100, 100_000);
        return Channel.CreateBounded<DetectionWork>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    private int AssociationWindowMs => Math.Clamp(_settingsStore.Service.Association?.MaxWindowMs ?? 1500, 0, 10_000);
    private bool RequireSameRoi => _settingsStore.Service.Association?.RequireSameRoi ?? true;

    private bool SameAssociationScope(PendingComponent left, PendingComponent right) =>
        !RequireSameRoi || left.RoiKey.Equals(right.RoiKey, StringComparison.OrdinalIgnoreCase);

    private bool SameAssociationScope(string leftRoiKey, string rightRoiKey) =>
        !RequireSameRoi || leftRoiKey.Equals(rightRoiKey, StringComparison.OrdinalIgnoreCase);

    private AnalysisDetection[] FilterRepeatedDetections(string cameraId, AnalysisDetection[] detections)
    {
        if (detections.Length == 0) return detections;

        DateTime now = DateTime.UtcNow;
        lock (_associationGate)
        {
            // Do not allocate a full 2560x1920 Bitmap for every face on every
            // inference tick. Keep the first component in the association
            // window and only capture again when it can form a new pair.
            return detections.Where(detection =>
            {
                string roiKey = GetRoiKey(detection);
                string componentKey = GetComponentKey(detection);
                bool hasOppositeInBatch = detections.Any(other =>
                    other.Kind != detection.Kind &&
                    SameAssociationScope(roiKey, GetRoiKey(other)));
                bool pendingSame = _pendingComponents.Any(previous =>
                    previous.CameraId.Equals(cameraId, StringComparison.OrdinalIgnoreCase) &&
                    previous.RoiKey.Equals(roiKey, StringComparison.OrdinalIgnoreCase) &&
                    previous.ComponentKey.Equals(componentKey, StringComparison.OrdinalIgnoreCase));
                string emittedKey = string.Concat(cameraId, ":", roiKey, ":", componentKey);
                bool emittedRecently = _emittedComponentTimes.TryGetValue(emittedKey, out DateTime emittedAt) &&
                    (now - emittedAt).TotalSeconds < 5;

                return hasOppositeInBatch || (!pendingSame && !emittedRecently);
            }).ToArray();
        }
    }

    private static string GetRoiKey(AnalysisDetection detection) =>
        GetMetadataString(detection, "RoiId") ?? GetMetadataString(detection, "RoiName") ?? string.Empty;

    private static string GetComponentKey(AnalysisDetection detection)
    {
        string kind = detection.Kind.ToString();
        string track = detection.TrackId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        string identity = GetMetadataString(detection, "IdentityId") ?? string.Empty;
        string plate = GetMetadataString(detection, "PlateText") ?? detection.Label;
        return kind == nameof(AnalysisKind.Plate)
            ? $"{kind}:{track}:{plate}"
            : $"{kind}:{track}:{identity}:{detection.Label}";
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
        CameraSettings camera = _settings.Cameras.FirstOrDefault(item => item.Id.Equals(work.CameraId, StringComparison.OrdinalIgnoreCase))
            ?? new CameraSettings { Id = work.CameraId };
        TriggerEvaluation triggerEvaluation = EvaluateTriggers(service.Triggers, camera.Id, work.Components);
        if (triggerEvaluation.Matched.Count == 0 && triggerEvaluation.Suppressed.Count > 0)
            return;

        int detectionCooldownSeconds = GetDetectionHistoryCooldownSeconds(work.Components);
        string historyKey = BuildCanonicalHistoryKey(work);
        if (triggerEvaluation.Matched.Count == 0 && detectionCooldownSeconds > 0 &&
            _eventStore.HasRecentDetectionEvent(historyKey, DateTime.UtcNow.AddSeconds(-detectionCooldownSeconds)))
            return;

        DetectionEventEnvelope envelope = BuildEvent(work, eventId, service, triggerEvaluation);

        var savedFullFrames = new HashSet<long>();
        bool firstFrame = true;
        foreach (PendingComponent component in work.Components)
        {
            if (savedFullFrames.Add(component.SourceFrameSequence))
            {
                envelope.Artifacts.Add(_artifactStore.SaveBitmap(
                    eventId,
                    firstFrame ? "FullFrameRaw" : "AssociatedFrameRaw",
                    component.FullFrame,
                    component.SourceFrameSequence,
                    retention));
                firstFrame = false;
            }

            string cropType = component.Detection.Kind == AnalysisKind.Face ? "DetectionCrop" : "PlateCrop";
            envelope.Artifacts.Add(_artifactStore.SaveBitmap(eventId, cropType, component.Crop, component.SourceFrameSequence, retention));

            CameraSettings artifactCamera = _settings.Cameras.FirstOrDefault(item => item.Id.Equals(work.CameraId, StringComparison.OrdinalIgnoreCase))
                ?? new CameraSettings { Id = work.CameraId };
            using Bitmap? roi = CropForDetection(component.FullFrame, component.Detection, artifactCamera);
            if (roi is not null)
                envelope.Artifacts.Add(_artifactStore.SaveBitmap(eventId, "RoiRaw", roi, component.SourceFrameSequence, retention));

            if (TryGetMetadataBytes(component.Detection, "AlignedFaceJpeg", out byte[]? alignedFace) && alignedFace is not null)
            {
                using var stream = new MemoryStream(alignedFace, writable: false);
                using var decoded = new Bitmap(stream);
                using var aligned = new Bitmap(decoded);
                envelope.Artifacts.Add(_artifactStore.SaveBitmap(eventId, "FaceAlignedCrop", aligned, component.SourceFrameSequence, retention));
            }
        }

        DetectionEventEnvelope stored = _eventStore.Append(envelope, historyKey);
        await DetectionHub.PublishAsync(_hub, stored, cancellationToken);
    }

    private DetectionEventEnvelope BuildEvent(
        DetectionWork work,
        string eventId,
        ServiceSettingsDocument service,
        TriggerEvaluation triggerEvaluation)
    {
        CameraSettings camera = _settings.Cameras.FirstOrDefault(item => item.Id.Equals(work.CameraId, StringComparison.OrdinalIgnoreCase)) ?? new CameraSettings { Id = work.CameraId };
        PendingComponent primary = work.Components[0];
        string taskId = GetMetadataString(primary.Detection, "ProcessingItemId") ?? string.Empty;
        string taskName = GetMetadataString(primary.Detection, "ProcessingItemName") ?? string.Empty;
        string roiName = GetMetadataString(primary.Detection, "RoiName") ?? string.Empty;
        string roiId = GetMetadataString(primary.Detection, "RoiId") ?? string.Empty;
        bool hasPlate = work.Components.Any(item => item.Detection.Kind == AnalysisKind.Plate);
        bool hasFace = work.Components.Any(item => item.Detection.Kind == AnalysisKind.Face);

        var envelope = new DetectionEventEnvelope
        {
            EventId = eventId,
            EventType = hasPlate && hasFace
                ? "PlateFaceMatched"
                : hasFace
                    ? (IsUnknown(primary.Detection) ? "FaceUnknown" : "FaceRecognized")
                    : "PlateDetected",
            Scenario = hasPlate && hasFace
                ? "PlateFaceAssociation"
                : hasFace ? "FaceRecognition" : "PlateOnly",
            OccurredAtUtc = work.Timestamp,
            ReceivedAtUtc = DateTime.UtcNow,
            Source = new JsonObject
            {
                ["serviceNodeId"] = service.ServiceNodeId,
                ["cameraId"] = camera.Id,
                ["cameraName"] = camera.Name,
                ["taskId"] = taskId,
                ["taskName"] = taskName,
                ["taskIds"] = new JsonArray(work.Components.Select(item => GetMetadataString(item.Detection, "ProcessingItemId")).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value)).ToArray()),
                ["roiId"] = roiId,
                ["roiName"] = roiName,
                ["roiIds"] = new JsonArray(work.Components.Select(item => GetMetadataString(item.Detection, "RoiId")).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value)).ToArray()),
                ["sourceFrameSequence"] = primary.SourceFrameSequence,
                ["sourceFrameSequences"] = new JsonArray(work.Components.Select(item => JsonValue.Create(item.SourceFrameSequence)).Distinct().ToArray()),
                ["associationType"] = work.AssociationType,
                ["associationAgeMs"] = (int)Math.Max(0, Math.Round((work.Components.Max(item => item.Timestamp) - work.Components.Min(item => item.Timestamp)).TotalMilliseconds)),
                ["frameWidth"] = primary.FullFrame.Width,
                ["frameHeight"] = primary.FullFrame.Height
            },
            Trigger = new JsonObject
            {
                ["matched"] = triggerEvaluation.Matched.Count > 0,
                ["cooldownApplied"] = triggerEvaluation.CooldownApplied,
                ["matchingTriggerIds"] = new JsonArray(triggerEvaluation.Matched.Select(trigger => JsonValue.Create(trigger.Id)).ToArray()),
                ["suppressedTriggerIds"] = new JsonArray(triggerEvaluation.Suppressed.Select(trigger => JsonValue.Create(trigger.Id)).ToArray()),
                ["matchingTriggerKeys"] = TriggerKeysJson(triggerEvaluation.MatchedKeys),
                ["suppressedTriggerKeys"] = TriggerKeysJson(triggerEvaluation.SuppressedKeys)
            }
        };

        foreach (PendingComponent component in work.Components)
        {
            string key = component.Detection.Kind == AnalysisKind.Face ? "face" : "plate";
            envelope.Components[key] = BuildDetectionComponent(component.Detection, component.FullFrame.Size);
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
            component["label"] = !recognized ? "Face" : unknown ? "Unknown" : detection.Label;
            component["recognitionStatus"] = !recognized ? "NotAttempted" : unknown ? "Unknown" : "Matched";
            component["recognition"] = new JsonObject
            {
                ["personId"] = unknown || !recognized ? null : identityId,
                ["name"] = unknown || !recognized ? null : detection.Label,
                ["personNumber"] = unknown || !recognized ? 0 : GetMetadataInt(detection, "PersonNumber"),
                ["isUnknown"] = GetMetadataBool(detection, "IsUnknown") || unknown,
                ["similarity"] = recognized && !unknown ? similarity : null,
                ["minimumSimilarity"] = GetMetadataFloat(detection, "FaceRecordConfidence"),
                ["matchedSampleId"] = unknown || !recognized ? null : GetMetadataString(detection, "MatchedSampleId")
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

    private static bool TriggerMatchesComponents(
        TriggerDefinition trigger,
        string cameraId,
        IReadOnlyList<PendingComponent> components)
    {
        if (!trigger.Enabled) return false;
        if (trigger.CameraIds.Count > 0 && !trigger.CameraIds.Contains(cameraId, StringComparer.OrdinalIgnoreCase)) return false;

        bool pairRequired = trigger.Kinds.Any(IsPlateFaceKind);
        bool plateRequired = pairRequired || trigger.Kinds.Any(IsPlateKind);
        bool faceRequired = pairRequired || trigger.Kinds.Any(IsFaceKind);
        if (!plateRequired && !faceRequired)
        {
            return components.Any(item =>
            {
                AnalysisDetection detection = item.Detection;
                string taskId = GetMetadataString(detection, "ProcessingItemId") ?? string.Empty;
                return MatchesTrigger(trigger, cameraId, taskId, detection.Kind.ToString(), detection.Label, detection.Confidence, detection);
            });
        }

        IReadOnlyList<PendingComponent> plates = components.Where(item => item.Detection.Kind == AnalysisKind.Plate).ToArray();
        IReadOnlyList<PendingComponent> faces = components.Where(item => item.Detection.Kind == AnalysisKind.Face).ToArray();
        if (plateRequired && plates.Count == 0) return false;
        if (faceRequired && faces.Count == 0) return false;

        IEnumerable<PendingComponent> required = plateRequired && faceRequired
            ? plates.Concat(faces)
            : plateRequired ? plates : faces;
        PendingComponent[] requiredComponents = required.ToArray();
        if (trigger.TaskIds.Count > 0 && !requiredComponents.Any(item =>
                trigger.TaskIds.Contains(GetMetadataString(item.Detection, "ProcessingItemId") ?? string.Empty, StringComparer.OrdinalIgnoreCase)))
            return false;
        if (!string.IsNullOrWhiteSpace(trigger.LabelEquals) && !requiredComponents.Any(item =>
                string.Equals(trigger.LabelEquals, item.Detection.Label, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (trigger.MinimumConfidence is float minimum && requiredComponents.Any(item => item.Detection.Confidence < minimum)) return false;
        if (!string.IsNullOrWhiteSpace(trigger.PlateTextEquals) && !plates.Any(item =>
                string.Equals(trigger.PlateTextEquals, GetMetadataString(item.Detection, "PlateText") ?? item.Detection.Label, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!string.IsNullOrWhiteSpace(trigger.IdentityId) && !faces.Any(item =>
                string.Equals(trigger.IdentityId, GetMetadataString(item.Detection, "IdentityId"), StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    private static bool IsPlateFaceKind(string kind) =>
        kind.Equals("PlateFaceMatch", StringComparison.OrdinalIgnoreCase) ||
        kind.Equals("PlateFaceAssociation", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlateKind(string kind) =>
        kind.Equals("PlateRecognition", StringComparison.OrdinalIgnoreCase) ||
        kind.Equals("Plate", StringComparison.OrdinalIgnoreCase);

    private static bool IsFaceKind(string kind) =>
        kind.Equals("FaceRecognition", StringComparison.OrdinalIgnoreCase) ||
        kind.Equals("Face", StringComparison.OrdinalIgnoreCase);

    private static string BuildTriggerHistoryKey(
        TriggerDefinition trigger,
        string cameraId,
        IReadOnlyList<PendingComponent> components)
    {
        bool pairRequired = trigger.Kinds.Any(IsPlateFaceKind);
        bool plateRequired = pairRequired || trigger.Kinds.Any(IsPlateKind);
        bool faceRequired = pairRequired || trigger.Kinds.Any(IsFaceKind);
        IEnumerable<PendingComponent> selected = plateRequired || faceRequired
            ? components.Where(item =>
                (plateRequired && item.Detection.Kind == AnalysisKind.Plate) ||
                (faceRequired && item.Detection.Kind == AnalysisKind.Face))
            : components;

        string roi = string.Join(",", selected
            .Select(item => NormalizeTriggerKeyPart(item.RoiKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        string plates = string.Join(",", selected
            .Where(item => item.Detection.Kind == AnalysisKind.Plate)
            .Select(item => NormalizeTriggerKeyPart(GetMetadataString(item.Detection, "PlateText") ?? item.Detection.Label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        string faces = string.Join(",", selected
            .Where(item => item.Detection.Kind == AnalysisKind.Face)
            .Select(item => NormalizeTriggerKeyPart(GetMetadataString(item.Detection, "IdentityId") ?? item.Detection.Label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        return string.Join("\u001f", NormalizeTriggerKeyPart(cameraId), roi, plates, faces);
    }

    private static string NormalizeTriggerKeyPart(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static int GetDetectionHistoryCooldownSeconds(IReadOnlyList<PendingComponent> components) =>
        components.Select(item => GetMetadataInt(item.Detection, "HistoryEventCooldownSeconds"))
            .DefaultIfEmpty(0)
            .Max(value => Math.Clamp(value, 0, 3600));

    private static string BuildCanonicalHistoryKey(DetectionWork work)
    {
        string roi = string.Join(",", work.Components
            .Select(item => NormalizeTriggerKeyPart(item.RoiKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        string plates = string.Join(",", work.Components
            .Where(item => item.Detection.Kind == AnalysisKind.Plate)
            .Select(item => NormalizeTriggerKeyPart(GetMetadataString(item.Detection, "PlateText") ?? item.Detection.Label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        string faces = string.Join(",", work.Components
            .Where(item => item.Detection.Kind == AnalysisKind.Face)
            .Select(item => NormalizeTriggerKeyPart(GetMetadataString(item.Detection, "IdentityId") ?? item.Detection.Label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        return string.Join("\u001f", NormalizeTriggerKeyPart(work.CameraId), roi, plates, faces);
    }

    private static JsonObject TriggerKeysJson(IReadOnlyDictionary<string, string> keys)
    {
        var result = new JsonObject();
        foreach ((string triggerId, string key) in keys) result[triggerId] = key;
        return result;
    }

    private TriggerEvaluation EvaluateTriggers(
        IReadOnlyList<TriggerDefinition> triggers,
        string cameraId,
        IReadOnlyList<PendingComponent> components)
    {
        var matched = new List<TriggerDefinition>();
        var suppressed = new List<TriggerDefinition>();
        var matchedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var suppressedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        DateTime now = DateTime.UtcNow;
        foreach (TriggerDefinition trigger in triggers)
        {
            if (!TriggerMatchesComponents(trigger, cameraId, components)) continue;
            string triggerKey = BuildTriggerHistoryKey(trigger, cameraId, components);
            int cooldownSeconds = Math.Clamp(trigger.CooldownSeconds, 0, 3600);
            if (cooldownSeconds > 0 && _eventStore.HasRecentTriggerEvent(trigger.Id, triggerKey, now.AddSeconds(-cooldownSeconds)))
            {
                suppressed.Add(trigger);
                suppressedKeys[trigger.Id] = triggerKey;
                continue;
            }
            matched.Add(trigger);
            matchedKeys[trigger.Id] = triggerKey;
        }
        return new TriggerEvaluation(matched, suppressed, matchedKeys, suppressedKeys);
    }

    private static bool IsAccepted(AnalysisDetection detection)
    {
        // An explicit module decision is authoritative. In particular, an
        // invalid OCR plate may still have high detector confidence, but it
        // must not be persisted, trigger an action, or be sent to clients.
        if (detection.Metadata?.TryGetValue("Accepted", out object? acceptedValue) == true &&
            acceptedValue is bool accepted)
            return accepted;

        return detection.Confidence >= GetMetadataFloat(detection, "OverlayThreshold");
    }
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

    private sealed class PendingComponent : IDisposable
    {
        public PendingComponent(
            string cameraId,
            AnalysisDetection detection,
            Bitmap crop,
            Bitmap fullFrame,
            long sourceFrameSequence,
            DateTime timestamp,
            string roiKey,
            string componentKey)
        {
            CameraId = cameraId;
            Detection = detection;
            Crop = crop;
            FullFrame = fullFrame;
            SourceFrameSequence = sourceFrameSequence;
            Timestamp = timestamp;
            RoiKey = roiKey;
            ComponentKey = componentKey;
        }
        public string CameraId { get; }
        public AnalysisDetection Detection { get; }
        public Bitmap Crop { get; }
        public Bitmap FullFrame { get; }
        public long SourceFrameSequence { get; }
        public DateTime Timestamp { get; private set; }
        public string RoiKey { get; }
        public string ComponentKey { get; }
        public void PreserveStartTime(DateTime timestamp) => Timestamp = timestamp;
        public void Dispose() { Crop.Dispose(); FullFrame.Dispose(); }
    }

    private sealed class DetectionWork : IDisposable
    {
        public DetectionWork(string cameraId, IReadOnlyList<PendingComponent> components, string associationType)
        {
            CameraId = cameraId;
            Components = components;
            AssociationType = associationType;
            Timestamp = components.Min(item => item.Timestamp);
        }
        public string CameraId { get; }
        public IReadOnlyList<PendingComponent> Components { get; }
        public string AssociationType { get; }
        public DateTime Timestamp { get; }
        public void Dispose()
        {
            foreach (PendingComponent component in Components) component.Dispose();
        }
    }

    private sealed record TriggerEvaluation(
        IReadOnlyList<TriggerDefinition> Matched,
        IReadOnlyList<TriggerDefinition> Suppressed,
        IReadOnlyDictionary<string, string> MatchedKeys,
        IReadOnlyDictionary<string, string> SuppressedKeys)
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
