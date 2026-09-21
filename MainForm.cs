using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using Emgu.CV;
using Emgu.CV.CvEnum;
using HshDetectionEngin.Face;
using HshDetectionEngin.Licensing;
using HshDetectionEngin.Plate;

namespace HshVisionLab;

public sealed partial class MainForm : Form
{
    private readonly AppSettings _appSettings;
    private readonly FaceDatabase _faceDatabase;
    private readonly string _faceDatabasePath;
    private readonly LicenseValidationResult _license;
    private readonly FaceModule _faceModule;
    private readonly ProcessingRegistry _processingCatalog;
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
        _faceDatabasePath = Path.Combine(AppContext.BaseDirectory, "face-database.db");
        _license = LicenseValidator.Load(Path.Combine(AppContext.BaseDirectory, "license.hshlic"));
        _faceDatabase = FaceDatabase.Load(_faceDatabasePath);
        _faceModule = new FaceModule(_faceDatabase, _license);
        _processingCatalog = CreateProcessingCatalog(_faceModule, _license);
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
            license: _license);
        runtime.FrameReady += Runtime_FrameReady;
        runtime.PlateDetected += Runtime_PlateDetected;
        runtime.AnalysisDetected += Runtime_AnalysisDetected;
        runtime.StatusChanged += Runtime_StatusChanged;
        _cameras.Add(settings.Id, runtime);
    }

    private static ProcessingRegistry CreateProcessingCatalog(FaceModule faceModule, LicenseValidationResult license)
    {
        var catalog = new ProcessingRegistry();
        catalog.Register(PlateModule.CreateRegistration(license));
        catalog.Register(faceModule.CreateRegistration());
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
        if (!ShouldAddPlateToHistory(camera, item.Text, item.OwnerKey, item.Timestamp))
        {
            return;
        }

        SafeInvoke(() => AddPlateCard(
            new Bitmap(item.Crop),
            camera.Settings.Name,
            item.Text,
            item.Confidence,
            item.Timestamp));
    }

    private void Runtime_AnalysisDetected(CameraRuntime camera, CameraRuntime.AnalysisHistoryItem item)
    {
        Bitmap crop = new(item.Crop);
        string label = item.Detection.Label is "Unknown" or "face" ? "Unknown face" : item.Detection.Label;
        if (item.Detection.TrackId is int trackId) label = $"{label} #{trackId}";
        SafeInvoke(() => AddPlateCard(crop, camera.Settings.Name, label, item.Detection.Confidence, item.Timestamp));
    }

    private void ManageFaceDatabase()
    {
        using var form = new FaceDatabaseForm(_faceDatabase, RegisterFaceFromImage, ImportFacesFromFolder, AddFaceSampleToPerson);
        form.ShowDialog(this);
    }

    private bool TryGetFaceProcessingContext(out CameraRuntime? camera, out CameraProcessingSettings? processing)
    {
        IEnumerable<CameraRuntime> candidates = _active is null
            ? _cameras.Values
            : new[] { _active }.Concat(_cameras.Values.Where(item => !ReferenceEquals(item, _active)));

        foreach (CameraRuntime candidate in candidates)
        {
            candidate.Settings.EnsureProcessingDefaults();
            CameraProcessingSettings? item = candidate.Settings.Rois
                .SelectMany(roi => roi.Processing)
                .FirstOrDefault(value => value.Enabled && value.Kind == ProcessingType.Face);
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
                pipeline.RegisterIdentity(name, image, _faceDatabasePath, Path.GetFileName(imagePath));
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
            _faceDatabase.Save(_faceDatabasePath);
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

        _faceDatabase.Dispose();

        base.OnFormClosing(e);
    }
}
