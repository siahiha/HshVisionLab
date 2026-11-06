using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using Emgu.CV;
using Emgu.CV.CvEnum;
using HshDetectionEngin.Face;
using HshDetectionEngin.Identity;
using HshDetectionEngin.Licensing;
using HshDetectionEngin.Palm;
using HshDetectionEngin.Plate;

namespace HshVisionLab;

public sealed partial class MainForm : Form
{
    private readonly AppSettings _appSettings;
    private readonly FaceDatabase _faceDatabase;
    private readonly IdentityDatabase _identityDatabase;
    private readonly string _identityDatabasePath;
    private readonly PalmDatabase _palmDatabase;
    private readonly LicenseValidationResult _license;
    private readonly FaceModule _faceModule;
    private readonly PalmModule _palmModule;
    private readonly ProcessingRegistry _processingCatalog;
    private readonly DesktopEventStore _eventStore;
    private readonly DesktopAutomationEngine _automation;
    private readonly Dictionary<string, CameraRuntime> _cameras = new();
    private readonly Dictionary<string, Bitmap> _latestFrames = new();
    private readonly HashSet<string> _pendingFrameUpdates = new();
    private readonly object _frameGate = new();
    private readonly object _plateGate = new();
    private readonly Dictionary<string, Dictionary<string, DateTime>> _recentPlates = new();
    private bool _updatingCameraGrid;

    private CameraRuntime? _active;
    private bool _roiEditMode;
    private readonly List<PointF> _roiEditingPoints = [];
    private string? _editingRoiName;
    private Size _lastFrameSize = Size.Empty;
    private bool _startupInitialized;

    /// <param name="deferStartupInitialization">When true, show the form before loading camera/model runtimes.</param>
    public MainForm(bool deferStartupInitialization = true)
    {
        _appSettings = AppSettings.Load();
        _identityDatabasePath = Path.Combine(AppContext.BaseDirectory, "identity-database.db");
        _license = LicenseValidator.Load(Path.Combine(AppContext.BaseDirectory, "license.hshlic"));
        _identityDatabase = IdentityDatabase.Load(
            _identityDatabasePath,
            Path.Combine(AppContext.BaseDirectory, "face-database.db"),
            Path.Combine(AppContext.BaseDirectory, "palm-database.db"));
        _faceDatabase = FaceDatabase.FromStore(_identityDatabase);
        _palmDatabase = PalmDatabase.FromStore(_identityDatabase);
        _faceModule = new FaceModule(_faceDatabase, _license);
        _palmModule = new PalmModule(_palmDatabase, _license);
        _processingCatalog = CreateProcessingCatalog(_faceModule, _palmModule, _license);
        _eventStore = new DesktopEventStore();
        _automation = new DesktopAutomationEngine(_eventStore, new DesktopAutomationStore());
        _automation.StatusChanged += Automation_StatusChanged;
        _eventStore.Prune(_automation.Store.Settings.EventRetentionDays, _automation.Store.Settings.ArtifactRetentionDays);
        BuildUi();
        UiLocalization.Apply(this);
        _btnLanguage.Text = UiLocalization.IsPersian ? "English" : "فارسی";
        WireEvents();
        StartUiTimer();

        if (deferStartupInitialization)
        {
            Shown += MainForm_Shown;
        }
        else
        {
            InitializeStartup();
        }
    }

    private void MainForm_Shown(object? sender, EventArgs e)
    {
        Shown -= MainForm_Shown;
        BeginInvoke(InitializeStartup);
    }

    private void InitializeStartup()
    {
        if (_startupInitialized || IsDisposed) return;
        _startupInitialized = true;
        try
        {
            LoadCameras();
        }
        catch (Exception ex)
        {
            PushStatus($"Startup initialization failed: {ex.Message}", true);
        }
    }

    private void WireEvents()
    {
        _cameraGrid.SelectionChanged += CameraGrid_SelectionChanged;
        _cameraGrid.CellContentClick += CameraGrid_CellContentClick;
        _cameraGrid.CellDoubleClick += CameraGrid_CellDoubleClick;
        _picView.MouseClick += PicView_MouseClick;
        _picView.Paint += PicView_Paint;
    }

    private void LoadCameras()
    {
        foreach (CameraSettings settings in _appSettings.Cameras.ToArray())
        {
            AddRuntime(settings);
        }

        UpdateCameraGrid();
        RebuildMultiCameraGrid();

        CameraRuntime? selected = _cameras.Values.FirstOrDefault(
            camera => camera.Settings.Id == _appSettings.SelectedCameraId);

        if (selected is not null)
        {
            SelectCamera(selected);
        }
        else if (_cameras.Count > 0)
        {
            SelectCamera(_cameras.Values.First());
        }
        else
        {
            ClearSelectedCameraView();
        }

        RebuildAllHistoryCards();
    }

    private void StartUiTimer()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) =>
        {
            UpdateCameraGrid();
            UpdateStatsUi();
        };
        timer.Start();
    }

    private void AddRuntime(CameraSettings settings)
    {
        settings.EnsureProcessingDefaults();
        if (string.IsNullOrWhiteSpace(settings.Id))
        {
            settings.Id = Guid.NewGuid().ToString("N");
        }

        if (_cameras.ContainsKey(settings.Id))
        {
            return;
        }

        var runtime = new Camera(
            settings,
            processingRegistry: _processingCatalog,
            license: _license,
            enableTrackingCompletion: true);
        runtime.FrameReady += Runtime_FrameReady;
        runtime.PlateDetected += Runtime_PlateDetected;
        runtime.AnalysisDetected += Runtime_AnalysisDetected;
        runtime.StatusChanged += Runtime_StatusChanged;
        _cameras.Add(settings.Id, runtime);
    }

    private static ProcessingRegistry CreateProcessingCatalog(FaceModule faceModule, PalmModule palmModule, LicenseValidationResult license)
    {
        var catalog = new ProcessingRegistry();
        catalog.Register(PlateModule.CreateRegistration(license));
        catalog.Register(faceModule.CreateRegistration());
        catalog.Register(palmModule.CreateRegistration());
        return catalog;
    }

    private void Runtime_FrameReady(CameraRuntime camera, Bitmap frame)
    {
        bool scheduleUpdate;
        lock (_frameGate)
        {
            if (_latestFrames.TryGetValue(camera.Settings.Id, out Bitmap? old))
            {
                old.Dispose();
            }

            _latestFrames[camera.Settings.Id] = new Bitmap(frame);
            scheduleUpdate = _pendingFrameUpdates.Add(camera.Settings.Id);
        }

        // Coalesce UI work: one queued callback consumes the newest frame.
        // This prevents BeginInvoke backlog when detection or painting is busy.
        if (scheduleUpdate)
        {
            SafeInvoke(() => ApplyLatestFrame(camera));
        }

        frame.Dispose();
    }

    private void ApplyLatestFrame(CameraRuntime camera)
    {
        Bitmap? next;
        lock (_frameGate)
        {
            _pendingFrameUpdates.Remove(camera.Settings.Id);
            next = _latestFrames.TryGetValue(camera.Settings.Id, out Bitmap? latest)
                ? new Bitmap(latest)
                : null;
        }

        if (next is null || IsDisposed)
        {
            next?.Dispose();
            return;
        }

        if (_cameraTilePictures.TryGetValue(camera.Settings.Id, out PictureBox? tilePicture))
        {
            Bitmap? oldTile = tilePicture.Image as Bitmap;
            tilePicture.Image = next;
            oldTile?.Dispose();

            if (_active == camera && _isCameraMaximized && !_picView.IsDisposed)
            {
                Bitmap? oldView = _picView.Image as Bitmap;
                _picView.Image = new Bitmap(next);
                oldView?.Dispose();
                _lastFrameSize = camera.LastFrameSize;
                _picView.Invalidate();
            }
        }
        else
        {
            next.Dispose();
        }
    }

    private void Runtime_PlateDetected(CameraRuntime camera, CameraRuntime.HistoryItem item)
    {
        bool showPanel = ShouldAddPlateToHistory(camera, item.Text, item.OwnerKey, item.Timestamp);

        var candidate = new DesktopEventRecord
        {
            CameraId = camera.Settings.Id,
            CameraName = camera.Settings.Name,
            EventType = "PlateDetected",
            RoiId = item.Detection?.Metadata?.GetValueOrDefault("RoiId")?.ToString() ?? string.Empty,
            Scenario = "Plate",
            Label = item.Text,
            PlateText = item.Text,
            Confidence = item.Confidence,
            OccurredAtUtc = item.Timestamp.ToUniversalTime()
        };
        bool shouldPersist = ShouldPersistDesktopEvent(candidate, out bool triggerMatched, out DesktopHistoryDecision historyDecision);
        bool stageForAssociation = !shouldPersist && DesktopHistorySubscriptionMatcher.NeedsAssociationStaging(_automation.Store.Settings.HistorySubscription, candidate);
        if (!shouldPersist && !stageForAssociation)
        {
            return;
        }

        if (shouldPersist && showPanel)
        {
            Bitmap panelCrop = new(item.Crop);
            SafeInvoke(() => AddDetectionCard(panelCrop, null, camera.Settings.Name, item.Text, item.Confidence, item.Timestamp, historyDecision.DisplayAxis));
        }

        using Bitmap? rawFrame = item.SourceFrame is not null ? new Bitmap(item.SourceFrame) : camera.TryGetLatestRawFrame(out _);
        DesktopEventRecord record = _eventStore.Append(
            camera.Settings.Id,
            camera.Settings.Name,
            string.Empty,
            "PlateDetected",
            "Plate",
            item.Text,
            item.Text,
            null,
            item.Confidence,
            item.Timestamp,
            item.Crop,
            rawFrame,
            new { camera = camera.Settings.Name, plate = item.Text, ownerKey = item.OwnerKey, confidence = item.Confidence, metadata = item.Detection?.Metadata },
            roiId: item.Detection?.Metadata?.GetValueOrDefault("RoiId")?.ToString() ?? string.Empty,
            includeArtifacts: stageForAssociation || triggerMatched || historyDecision.IncludeArtifacts,
            isStaging: stageForAssociation,
            displayAxis: historyDecision.DisplayAxis);
        if (!record.IsStaging) _automation.Process(record);
        DesktopEventRecord? association = _eventStore.TryCreatePlateFaceAssociation(record, GetAssociationWindowMs(record.CameraId), EvaluateDesktopEvent);
        if (association is not null)
        {
            _automation.Process(association);
            SafeInvoke(() => AddEventCard(association));
        }
    }

    private void Runtime_AnalysisDetected(CameraRuntime camera, CameraRuntime.AnalysisHistoryItem item)
    {
        string label = item.Detection.Label is "Unknown" or "face"
            ? item.Detection.Kind == AnalysisKind.Palm ? "Unknown palm" : "Unknown face"
            : item.Detection.Label;
        if (item.Detection.TrackId is int trackId) label = $"{label} #{trackId}";
        string scenario = item.Detection.Kind.ToString();
        string? identity = item.Detection.Label.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ? null : item.Detection.Label;
        string roiId = item.Detection.Metadata?.TryGetValue("RoiId", out object? roiValue) == true
            ? roiValue?.ToString() ?? string.Empty
            : string.Empty;
        var candidate = new DesktopEventRecord
        {
            CameraId = camera.Settings.Id,
            CameraName = camera.Settings.Name,
            RoiId = roiId,
            EventType = $"{scenario}Detected",
            Scenario = scenario,
            Label = label,
            Identity = identity,
            Confidence = item.Detection.Confidence,
            OccurredAtUtc = item.Timestamp.ToUniversalTime()
        };
        bool shouldPersist = ShouldPersistDesktopEvent(candidate, out bool triggerMatched, out DesktopHistoryDecision historyDecision);
        bool stageForAssociation = !shouldPersist && DesktopHistorySubscriptionMatcher.NeedsAssociationStaging(_automation.Store.Settings.HistorySubscription, candidate);
        if (!shouldPersist && !stageForAssociation)
        {
            return;
        }

        if (shouldPersist)
        {
            Bitmap panelCrop = new(item.Crop);
            SafeInvoke(() => AddDetectionCard(panelCrop, null, camera.Settings.Name, label, item.Detection.Confidence, item.Timestamp, historyDecision.DisplayAxis));
        }

        using Bitmap? rawFrame = item.SourceFrame is not null ? new Bitmap(item.SourceFrame) : camera.TryGetLatestRawFrame(out _);
        DesktopEventRecord record = _eventStore.Append(
            camera.Settings.Id,
            camera.Settings.Name,
            string.Empty,
            $"{scenario}Detected",
            scenario,
            label,
            null,
            identity,
            item.Detection.Confidence,
            item.Timestamp,
            item.Crop,
            rawFrame,
            new { camera = camera.Settings.Name, kind = scenario, label, confidence = item.Detection.Confidence, bounds = item.Detection.Bounds, trackId = item.Detection.TrackId, metadata = item.Detection.Metadata },
            roiId: roiId,
            includeArtifacts: stageForAssociation || triggerMatched || historyDecision.IncludeArtifacts,
            isStaging: stageForAssociation,
            displayAxis: historyDecision.DisplayAxis);
        if (!record.IsStaging) _automation.Process(record);
        DesktopEventRecord? association = _eventStore.TryCreatePlateFaceAssociation(record, GetAssociationWindowMs(record.CameraId), EvaluateDesktopEvent);
        if (association is not null)
        {
            _automation.Process(association);
            SafeInvoke(() => AddEventCard(association));
        }
    }

    private bool ShouldPersistDesktopEvent(
        DesktopEventRecord candidate,
        out bool triggerMatched,
        out DesktopHistoryDecision historyDecision)
    {
        historyDecision = EvaluateDesktopEvent(candidate, out triggerMatched);
        return historyDecision.Record;
    }

    private DesktopHistoryDecision EvaluateDesktopEvent(DesktopEventRecord candidate)
        => EvaluateDesktopEvent(candidate, out _);

    private DesktopHistoryDecision EvaluateDesktopEvent(
        DesktopEventRecord candidate,
        out bool triggerMatched)
    {
        DesktopHistoryDecision historyDecision = DesktopHistorySubscriptionMatcher.Evaluate(
            _automation.Store.Settings.HistorySubscription,
            candidate);
        triggerMatched = _automation.HasEnabledTriggerMatch(candidate);
        if (!historyDecision.Record && !triggerMatched) return new DesktopHistoryDecision(false, historyDecision.CooldownSeconds, false, historyDecision.DisplayAxis);
        if (!triggerMatched && _eventStore.HasRecent(candidate, historyDecision.CooldownSeconds)) return new DesktopHistoryDecision(false, historyDecision.CooldownSeconds, false, historyDecision.DisplayAxis);
        return new DesktopHistoryDecision(true, historyDecision.CooldownSeconds, triggerMatched || historyDecision.IncludeArtifacts, historyDecision.DisplayAxis);
    }

    private int GetAssociationWindowMs(string cameraId)
    {
        int configured = _automation.Store.Settings.HistorySubscription.Profiles
            .Where(profile => profile.CameraIds is null || profile.CameraIds.Count == 0 || profile.CameraIds.Contains(cameraId, StringComparer.OrdinalIgnoreCase))
            .Select(profile => profile.WindowMs)
            .Where(value => value > 0)
            .DefaultIfEmpty(1500)
            .Max();
        return Math.Clamp(configured, 1, 10_000);
    }

    private void Automation_StatusChanged(string message, bool isError)
        => SafeInvoke(() => PushStatus(message, isError));

    private void ManageIdentityDatabase()
    {
        using var form = new IdentityDatabaseForm(_identityDatabase, _faceDatabase, _palmDatabase,
            RegisterFaceFromImage, AddFaceSampleToPerson, ImportFacesFromFolder,
            RegisterPalmFromImage, AddPalmSampleToPerson);
        form.ShowDialog(this);
    }

    private void ShowHistoryManager()
    {
        using var form = new HistoryManagementForm(_eventStore, ClearRuntimeDetectionHistory);
        form.ShowDialog(this);
        RebuildAllHistoryCards();
    }

    private void ClearRuntimeDetectionHistory(DateTime? fromUtc, DateTime? toUtc)
    {
        foreach (CameraRuntime camera in _cameras.Values)
            camera.ClearDetectionHistory(fromUtc, toUtc);
        RebuildAllHistoryCards();
    }

    private void ShowAutomationManager()
    {
        using var form = new AutomationManagerForm(_automation.Store, () => _cameras.Values.ToArray());
        form.ShowDialog(this);
    }

    private bool TryGetPalmProcessingContext(out CameraRuntime? camera, out CameraProcessingSettings? processing)
    {
        IEnumerable<CameraRuntime> candidates = _active is null
            ? _cameras.Values
            : new[] { _active }.Concat(_cameras.Values.Where(item => !ReferenceEquals(item, _active)));

        foreach (CameraRuntime candidate in candidates)
        {
            if (!candidate.Settings.Enabled) continue;
            candidate.Settings.EnsureProcessingDefaults();
            CameraProcessingSettings? item = candidate.Settings.Rois
                .SelectMany(roi => roi.Processing)
                .FirstOrDefault(value => value.Enabled && value.Kind == ProcessingType.Palm);
            if (item is not null)
            {
                camera = candidate;
                processing = item;
                return true;
            }
        }

        camera = null;
        processing = null;
        return false;
    }

    private bool RegisterPalmFromImage(string name, string imagePath)
    {
        if (!TryGetPalmProcessingContext(out _, out CameraProcessingSettings? processing) || processing is null)
        {
            MessageBox.Show(this, "Enable Palm processing on at least one camera first.", "Palm database", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        PalmPipeline? pipeline = _palmModule.CreatePipeline(processing, maxFpsOverride: 0, requireRecognition: true);
        if (pipeline is null)
        {
            MessageBox.Show(this, "Palm detection or recognition model is unavailable.", "Palm database", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        try
        {
            using Mat image = CvInvoke.Imread(imagePath, ImreadModes.AnyColor);
            using (pipeline) pipeline.RegisterIdentity(name, image, Path.GetFileName(imagePath));
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Palm database", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private bool AddPalmSampleToPerson(string personId, string imagePath)
    {
        PalmIdentity? person = _palmDatabase.Identities.FirstOrDefault(item => item.Id == personId);
        if (person is null) throw new InvalidOperationException("The selected palm identity no longer exists.");
        if (!TryGetPalmProcessingContext(out _, out CameraProcessingSettings? processing) || processing is null)
            throw new InvalidOperationException("Enable Palm processing on at least one camera first.");
        PalmPipeline? pipeline = _palmModule.CreatePipeline(processing, maxFpsOverride: 0, requireRecognition: true)
            ?? throw new InvalidOperationException("Palm detection or recognition model is unavailable.");
        using (pipeline)
        using (Mat image = CvInvoke.Imread(imagePath, ImreadModes.AnyColor))
        {
            PalmEnrollment enrollment = pipeline.CreateEnrollment(image);
            _palmDatabase.RegisterSample(person.Name, enrollment.Embedding, enrollment.PalmImage,
                Path.GetFileName(imagePath), personId: person.Id, detectionConfidence: enrollment.DetectionConfidence);
        }
        return true;
    }

    private bool TryGetFaceProcessingContext(out CameraRuntime? camera, out CameraProcessingSettings? processing)
    {
        IEnumerable<CameraRuntime> candidates = _active is null
            ? _cameras.Values
            : new[] { _active }.Concat(_cameras.Values.Where(item => !ReferenceEquals(item, _active)));

        foreach (CameraRuntime candidate in candidates)
        {
            if (!candidate.Settings.Enabled) continue;
            candidate.Settings.EnsureProcessingDefaults();
            CameraProcessingSettings? item = candidate.Settings.Rois
                .SelectMany(roi => roi.Processing)
                .FirstOrDefault(value => value.Enabled && value.Kind == ProcessingType.Face);
            if (item is not null)
            {
                camera = candidate;
                processing = item;
                if (item.ExecutionMode == "TrackAndQueue")
                {
                    var definition = candidate.Settings.RecognitionDefinitions.FirstOrDefault(d => d.Enabled && d.Kind == AnalysisKind.Face && d.Id == item.RecognitionDefinitionId);
                    if (definition is null) continue;
                    processing = item.Clone();
                    processing.ExecutionMode = "Inline";
                    var options = processing.GetOptions<FaceProcessingOptions>();
                    options.RecognitionEnabled = true;
                    options.RecognitionModelFile = definition.ModelFile;
                    options.RecognitionThreshold = definition.Confidence;
                    options.UnknownMatchThreshold = definition.UnknownMatchThreshold;
                    processing.Threads = definition.Threads;
                    processing.SetOptions(options);
                }
                return true;
            }
        }

        camera = null;
        processing = null;
        return false;
    }

    private bool RegisterFaceFromImage(string name, string imagePath)
    {
        if (!TryGetFaceProcessingContext(out CameraRuntime? camera, out CameraProcessingSettings? processing) || camera is null || processing is null)
        {
            MessageBox.Show(this, "Enable Face processing on at least one camera first.", "Face database", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        FacePipeline? pipeline = _faceModule.CreatePipeline(processing, maxFpsOverride: 0, requireRecognition: true);
        if (pipeline is null)
        {
            MessageBox.Show(this, "Face detection or recognition model is unavailable.", "Face database", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        try
        {
            using Mat image = CvInvoke.Imread(imagePath, ImreadModes.AnyColor);
            using (pipeline)
            {
                pipeline.RegisterIdentity(name, image, _identityDatabasePath, Path.GetFileName(imagePath));
            }
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Face database", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private bool AddFaceSampleToPerson(string personId, string imagePath)
    {
        FaceIdentity? person = _faceDatabase.Identities.FirstOrDefault(item => item.Id == personId);
        if (person is null) throw new InvalidOperationException("The selected person no longer exists.");
        if (person.Samples.Count >= FaceDatabase.MaxSamplesPerPerson)
            throw new InvalidOperationException($"Person '{person.Name}' already has the maximum of {FaceDatabase.MaxSamplesPerPerson} samples.");
        if (!TryGetFaceProcessingContext(out _, out CameraProcessingSettings? processing) || processing is null)
            throw new InvalidOperationException("Enable Face processing on at least one camera first.");

        FacePipeline? pipeline = _faceModule.CreatePipeline(processing, maxFpsOverride: 0, requireRecognition: true);
        if (pipeline is null)
            throw new InvalidOperationException("Face detection or recognition model is unavailable.");

        using (pipeline)
        using (Mat image = CvInvoke.Imread(imagePath, ImreadModes.AnyColor))
        {
            FaceEnrollment enrollment = pipeline.CreateEnrollment(image);
            _faceDatabase.RegisterSample(person.Name, enrollment.Embedding, enrollment.FaceImage,
                Path.GetFileName(imagePath), personId: person.Id, detectionConfidence: enrollment.DetectionConfidence);
            _faceDatabase.Save(_identityDatabasePath);
        }
        return true;
    }

    private FaceFolderImportResult ImportFacesFromFolder(string name, string folderPath)
    {
        if (!TryGetFaceProcessingContext(out CameraRuntime? camera, out CameraProcessingSettings? processing) || camera is null || processing is null)
            return new FaceFolderImportResult(0, 0, "Enable Face processing on at least one camera first.");

        FacePipeline? pipeline = _faceModule.CreatePipeline(processing, maxFpsOverride: 0, requireRecognition: true);
        if (pipeline is null)
            return new FaceFolderImportResult(0, 0, "Face detection or recognition model is unavailable.");

        string[] files = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
            .Where(x => IsImageFile(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        int imported = 0;
        int failed = 0;
        var errors = new List<string>();
        try
        {
            using (pipeline)
            {
                foreach (string file in files)
                {
                    try
                    {
                        using Mat image = CvInvoke.Imread(file, ImreadModes.AnyColor);
                        FaceEnrollment enrollment = pipeline.CreateEnrollment(image);
                        _faceDatabase.RegisterSample(name, enrollment.Embedding, enrollment.FaceImage,
                            Path.GetFileName(file), detectionConfidence: enrollment.DetectionConfidence);
                        imported++;
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("maximum of", StringComparison.OrdinalIgnoreCase))
                    {
                        failed += files.Length - imported - failed;
                        errors.Add(ex.Message);
                        break;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return new FaceFolderImportResult(imported, failed, ex.Message);
        }

        string details = errors.Count == 0
            ? $"Imported {imported} image(s)."
            : $"Imported {imported}, skipped {failed}.\r\n{string.Join("\r\n", errors.Take(8))}";
        return new FaceFolderImportResult(imported, failed, details);
    }

    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp";

    private bool ShouldAddPlateToHistory(CameraRuntime camera, string plate, string ownerKey, DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(plate))
        {
            return false;
        }

        lock (_plateGate)
        {
            if (!_recentPlates.TryGetValue(camera.Settings.Id, out Dictionary<string, DateTime>? plates))
            {
                plates = new Dictionary<string, DateTime>();
                _recentPlates[camera.Settings.Id] = plates;
            }

            foreach (string oldPlate in plates.Keys.ToList())
            {
                if (timestamp - plates[oldPlate] > TimeSpan.FromSeconds(30))
                {
                    plates.Remove(oldPlate);
                }
            }

            string historyKey = $"{ownerKey}:{plate}";
            if (plates.TryGetValue(historyKey, out DateTime lastSeen) &&
                timestamp - lastSeen <= TimeSpan.FromSeconds(30))
            {
                return false;
            }

            plates[historyKey] = timestamp;
            return true;
        }
    }

    private void Runtime_StatusChanged(CameraRuntime camera, string message, bool error)
    {
        SafeInvoke(() => UpdateCameraTileLabel(camera));

        if (_active == camera)
        {
            PushStatus(message, error);
        }
    }

    private void ShowMaximizedCamera(CameraRuntime camera)
    {
        _active = camera;
        _maximizedCameraId = camera.Settings.Id;
        _isCameraMaximized = true;
        UpdateRoiPanelVisibility();
        _btnBackToThumbnails.Visible = true;
        _lblViewTitle.Text = $"{camera.Settings.Name}  •  double-click image to return to thumbnails";
        _multiViewGrid.Visible = false;
        _picView.Visible = true;
        UpdateMaximizedView();
        SelectCameraGridRow(camera);
    }

    private void UpdateMaximizedView()
    {
        if (!_isCameraMaximized || _maximizedCameraId is null)
        {
            return;
        }

        if (!_cameras.TryGetValue(_maximizedCameraId, out CameraRuntime? camera))
        {
            ShowThumbnails();
            return;
        }

        lock (_frameGate)
        {
            Bitmap? old = _picView.Image as Bitmap;
            _picView.Image = _latestFrames.TryGetValue(camera.Settings.Id, out Bitmap? latest)
                ? new Bitmap(latest)
                : null;
            old?.Dispose();
            _lastFrameSize = camera.LastFrameSize;
        }

        _picView.Invalidate();
    }

    private void ShowThumbnails()
    {
        ExitRoiEditMode();
        _isCameraMaximized = false;
        _maximizedCameraId = null;
        UpdateRoiPanelVisibility();
        _btnBackToThumbnails.Visible = false;
        _lblViewTitle.Text = "Camera thumbnails  •  double-click a camera to maximize";
        _picView.Visible = false;
        _multiViewGrid.Visible = true;
        Bitmap? old = _picView.Image as Bitmap;
        _picView.Image = null;
        old?.Dispose();
        RebuildMultiCameraGrid();
    }

    private void ClearSelectedCameraView()
    {
        _active = null;
        _appSettings.SelectedCameraId = null;
        RefreshRoiList();
        ExitRoiEditMode();
        Bitmap? old = _picView.Image as Bitmap;
        _picView.Image = null;
        old?.Dispose();
        _lastFrameSize = Size.Empty;
        ShowThumbnails();
        PushStatus("No camera configured. Click '+ Add camera'.");
    }

    private void PushStatus(string message, bool error = false)
    {
        SafeInvoke(() =>
        {
            _stState.Text = message;
            _stState.ForeColor = error ? Color.Salmon : Color.Gainsboro;
        });
    }

    private void SafeInvoke(Action action)
    {
        try
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch
        {
            // UI can be closing while a camera thread publishes its final frame.
        }
    }

    public static bool IsValidIranianPlate(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate))
        {
            return false;
        }

        plate = plate.Trim()
            .Replace('۰', '0')
            .Replace('۱', '1')
            .Replace('۲', '2')
            .Replace('۳', '3')
            .Replace('۴', '4')
            .Replace('۵', '5')
            .Replace('۶', '6')
            .Replace('۷', '7')
            .Replace('۸', '8')
            .Replace('۹', '9');

        return Regex.IsMatch(plate, @"^\d{2}[آ-ی]\d{3}\d{2}$");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            foreach (CameraRuntime camera in _cameras.Values)
            {
                camera.Stop();
            }

            SaveAll();
        }
        catch
        {
            // Keep shutdown resilient even if a camera is already disconnected.
        }

        lock (_frameGate)
        {
            foreach (Bitmap bitmap in _latestFrames.Values)
            {
                bitmap.Dispose();
            }

            _latestFrames.Clear();
            _pendingFrameUpdates.Clear();
        }

        if (_picView.Image is Bitmap image)
        {
            image.Dispose();
            _picView.Image = null;
        }

        foreach (CameraRuntime camera in _cameras.Values)
        {
            camera.Dispose();
        }

        _automation.Dispose();

        _identityDatabase.Dispose();

        base.OnFormClosing(e);
    }
}
