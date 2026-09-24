using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using HshDetectionEngin.Capture;
using HshDetectionEngin.Detection;
using HshDetectionEngin.Licensing;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text;
using System.Text.Json;
using System.Drawing.Imaging;

namespace HshDetectionEngin;

/// <summary>Independent processing service for one camera. No state is shared with another camera.</summary>
public class CameraRuntime : IDisposable
{
    public CameraSettings Settings { get; }
    public IFrameSource FrameSource { get; private set; }
    private readonly bool _hasInjectedFrameSource;
    private string _frameSourceBackend;
    private readonly CameraPipelineCoordinator _pipelineCoordinator;
    private readonly Dictionary<string, MotionDetector> _motionDetectors = new();
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private Thread? _previewThread;
    private readonly object _previewFrameGate = new();
    private readonly AutoResetEvent _previewFrameReady = new(false);
    private Mat? _latestPreviewFrame;
    private readonly object _rawFrameGate = new();
    private Bitmap? _latestRawFrame;
    private long _latestRawFrameSequence;
    private volatile bool _running;
    private volatile bool _motionActive;
    private long _motionActiveUntilTicks;
    private long _nextPreviewTicks;
    private readonly Stopwatch _stats = Stopwatch.StartNew();
    private long _processed;
    private double _fps;
    private double _lastInferenceMs;
    private Size _lastFrameSize = Size.Empty;
    private readonly object _overlayGate = new();
    private readonly Dictionary<AnalysisKind, PlateOverlayInfo> _plateOverlay = new();
    private readonly Dictionary<AnalysisKind, AnalysisOverlayInfo> _analysisOverlay = new();
    private readonly Dictionary<string, DateTime> _analysisHistoryTimes = new();
    private readonly object _historyGate = new();
    private readonly List<HistoryItem> _history = [];
    private readonly object _analysisHistoryGate = new();
    private readonly List<AnalysisHistoryItem> _analysisHistory = [];
    private readonly object _renderGate = new();
    private IReadOnlyList<AnalysisDetection> _lastAnalysisDetections = [];
    private IReadOnlyList<ProcessingOverlay> _lastProcessingOverlays = [];
    private DateTime _lastProcessingOverlaysUpdatedUtc = DateTime.MinValue;

    public IFrameRenderer? Renderer { get; set; }
    public LicenseValidationResult License { get; }
    public ProcessingRegistry ProcessingModules => _pipelineCoordinator.Registry;

    public event Action<CameraRuntime, Bitmap>? FrameReady;
    public event Action<CameraRuntime, HistoryItem>? PlateDetected;
    public event Action<CameraRuntime, AnalysisHistoryItem>? AnalysisDetected;
    public event Action<CameraRuntime, IReadOnlyList<AnalysisDetection>>? PipelineResultsReady;
    public event Action<CameraRuntime, string, bool>? StatusChanged;
    public event Action<CameraRuntime>? StatsChanged;

    public bool IsRunning => _running;
    public override string ToString() => Settings.Name;
    public double ProcessingFps => _fps;
    public double LastInferenceMs => Volatile.Read(ref _lastInferenceMs);
    public Size LastFrameSize => _lastFrameSize;
    public long DroppedFrames => FrameSource.DroppedFrames;
    public int ConfiguredRoiCount => Settings.Rois.Count(roi => roi.Enabled);
    public int ConfiguredTaskCount => Settings.Rois
        .Where(roi => roi.Enabled)
        .SelectMany(roi => roi.Processing ?? [])
        .Count(item => item.Enabled);
    public int ActivePipelineCount => _pipelineCoordinator.ActivePipelineCount;
    public IReadOnlyList<HistoryItem> History { get { lock (_historyGate) return _history.ToArray(); } }
    public IReadOnlyList<AnalysisHistoryItem> AnalysisHistory { get { lock (_analysisHistoryGate) return _analysisHistory.ToArray(); } }
    public IReadOnlyList<HistoryItem> ArchivedHistory => CameraHistoryArchive.LoadPlate(Settings.Id);
    public IReadOnlyList<AnalysisHistoryItem> ArchivedAnalysisHistory => CameraHistoryArchive.LoadFace(Settings.Id);

    /// <summary>
    /// Returns the raw source frame captured for the latest detection pass.
    /// This is separate from FrameReady, which may contain ROI and detection
    /// drawings for the live preview.
    /// </summary>
    public Bitmap? TryGetLatestRawFrame(out long sequence)
    {
        lock (_rawFrameGate)
        {
            sequence = _latestRawFrameSequence;
            return _latestRawFrame is null ? null : new Bitmap(_latestRawFrame);
        }
    }

    private const int PreviewFps = 15;
    private int DetectionOverlayHoldMs => Math.Max(0, Settings.DetectionOverlayHoldMs);

    private bool IsOverlayActive(DateTime updatedUtc, DateTime now)
        => (now - updatedUtc).TotalMilliseconds <= DetectionOverlayHoldMs;

    internal sealed record RuntimeRoi(
        string Id,
        string Name,
        PointF[] Polygon,
        Rectangle Bounds,
        bool Enabled,
        string ProcessingMode);
    private sealed record PlateOverlayInfo(string Key, AnalysisDetection Detection, string Text, float Confidence, DateTime UpdatedUtc, Bitmap? Crop, bool Accepted);
    private sealed record AnalysisOverlayInfo(string Key, AnalysisDetection Detection, DateTime UpdatedUtc);
    public sealed record HistoryItem(Bitmap Crop, string Text, float Confidence, DateTime Timestamp, string OwnerKey);
    public sealed record AnalysisHistoryItem(Bitmap Crop, AnalysisDetection Detection, DateTime Timestamp);
    public sealed record LiveOverlayPoint(double X, double Y);
    public sealed record LiveOverlayRect(int X, int Y, int Width, int Height);
    public sealed record LiveOverlayRoi(string Id, string Name, bool Enabled, IReadOnlyList<LiveOverlayPoint> Points);
    public sealed record LiveOverlayDetection(
        string Kind,
        string Label,
        string? Text,
        float Confidence,
        LiveOverlayRect Bounds,
        int? TrackId,
        bool Accepted);
    public sealed record LiveOverlayPrimitive(
        string Kind,
        IReadOnlyList<LiveOverlayPoint> Points,
        LiveOverlayRect Bounds,
        int Radius,
        int Red,
        int Green,
        int Blue,
        int Thickness,
        bool Filled);
    public sealed record LiveOverlaySnapshot(
        int Width,
        int Height,
        IReadOnlyList<LiveOverlayRoi> Rois,
        IReadOnlyList<LiveOverlayRoi> MotionRois,
        IReadOnlyList<LiveOverlayDetection> Detections,
        IReadOnlyList<LiveOverlayPrimitive> ProcessingOverlays,
        DateTime UpdatedUtc);

    public CameraRuntime(
        CameraSettings settings,
        IFrameSource? frameSource = null,
        ProcessingRegistry? processingRegistry = null,
        LicenseValidationResult? license = null)
    {
        Settings = settings;
        License = license ?? LicenseValidator.Load(Path.Combine(AppContext.BaseDirectory, "license.hshlic"));
        _hasInjectedFrameSource = frameSource is not null;
        _frameSourceBackend = GetCaptureBackend(Settings);
        FrameSource = frameSource ?? CreateFrameSource(_frameSourceBackend);
        _pipelineCoordinator = new CameraPipelineCoordinator(
            Settings,
            processingRegistry ?? new ProcessingRegistry(),
            (message, isError) => StatusChanged?.Invoke(this, message, isError),
            value => Volatile.Write(ref _lastInferenceMs, value));
        AttachFrameSource(FrameSource);
        Settings.EnsureProcessingDefaults();
        RebuildRoiPipelines();
    }

    private static string GetCaptureBackend(CameraSettings settings)
    {
        if (string.Equals(settings.CaptureBackend, "LibVLC", StringComparison.OrdinalIgnoreCase)) return "LibVLC";
        if (string.Equals(settings.CaptureBackend, "MediaMTX", StringComparison.OrdinalIgnoreCase)) return "MediaMTX";
        return "FFmpeg";
    }

    private IFrameSource CreateFrameSource(string backend) =>
        string.Equals(backend, "LibVLC", StringComparison.OrdinalIgnoreCase)
            ? new VlcFrameSource()
            : string.Equals(backend, "MediaMTX", StringComparison.OrdinalIgnoreCase)
                ? new MediaMtxFrameSource(Settings.Id)
                : new FrameSource();

    private void AttachFrameSource(IFrameSource source)
    {
        source.StateChanged += FrameSource_StateChanged;
        source.ErrorOccurred += FrameSource_ErrorOccurred;
        source.FrameAvailable += FrameSource_FrameAvailable;
    }

    private void DetachFrameSource(IFrameSource source)
    {
        source.StateChanged -= FrameSource_StateChanged;
        source.ErrorOccurred -= FrameSource_ErrorOccurred;
        source.FrameAvailable -= FrameSource_FrameAvailable;
    }

    private void FrameSource_StateChanged(string state) => StatusChanged?.Invoke(this, state, false);
    private void FrameSource_ErrorOccurred(Exception error) => StatusChanged?.Invoke(this, error.Message, true);

    private void EnsureFrameSourceForSettings()
    {
        if (_hasInjectedFrameSource) return;

        string requestedBackend = GetCaptureBackend(Settings);
        if (string.Equals(requestedBackend, _frameSourceBackend, StringComparison.Ordinal)) return;

        IFrameSource previous = FrameSource;
        DetachFrameSource(previous);
        FrameSource = CreateFrameSource(requestedBackend);
        _frameSourceBackend = requestedBackend;
        AttachFrameSource(FrameSource);
        previous.Dispose();
    }

    private void FrameSource_FrameAvailable(Mat frame)
    {
        if (!_running || frame.IsEmpty)
        {
            return;
        }

        _lastFrameSize = frame.Size;

        long now = Stopwatch.GetTimestamp();
        long interval = Math.Max(1, Stopwatch.Frequency / PreviewFps);
        if (now < _nextPreviewTicks)
        {
            return;
        }

        _nextPreviewTicks = now + interval;

        // The source raises FrameAvailable on its RTSP reader thread. Keep
        // that thread free from resize/bitmap/UI work: publish only the newest
        // preview frame and let the dedicated preview worker render it.
        Mat? previewFrame = null;
        try
        {
            previewFrame = frame.Clone();
            Mat? old;
            lock (_previewFrameGate)
            {
                old = _latestPreviewFrame;
                _latestPreviewFrame = previewFrame;
                previewFrame = null;
            }

            old?.Dispose();
            _previewFrameReady.Set();
        }
        finally
        {
            previewFrame?.Dispose();
        }
    }

    /// <summary>
    /// Returns only the lightweight geometry/state needed to draw the current
    /// overlays over a raw MediaMTX browser stream. The video itself never
    /// passes through the processing renderer or the custom WebRTC encoder.
    /// </summary>
    public LiveOverlaySnapshot GetLiveOverlaySnapshot()
    {
        Size size = _lastFrameSize;
        DateTime now = DateTime.UtcNow;
        IReadOnlyList<LiveOverlayRoi> rois = [];
        IReadOnlyList<LiveOverlayRoi> motionRois = [];
        if (Settings.DrawBoxes && size.Width > 0 && size.Height > 0)
        {
            List<RuntimeRoi> detectionRois = GetNamedRois(size);
            rois = detectionRois.Select(roi => ToLiveOverlayRoi(roi, size)).ToArray();
            motionRois = GetMotionRois(size, detectionRois)
                .Select(roi => ToLiveOverlayRoi(roi, size))
                .ToArray();
        }

        lock (_overlayGate)
        {
            var detections = new List<LiveOverlayDetection>();
            if (Settings.DrawBoxes)
            {
                foreach (PlateOverlayInfo overlay in _plateOverlay.Values)
                {
                    if (!IsOverlayActive(overlay.UpdatedUtc, now)) continue;
                    detections.Add(ToLiveOverlayDetection(
                        overlay.Detection,
                        overlay.Accepted,
                        overlay.Text));
                }

                foreach (AnalysisOverlayInfo overlay in _analysisOverlay.Values)
                {
                    if (!IsOverlayActive(overlay.UpdatedUtc, now)) continue;
                    detections.Add(ToLiveOverlayDetection(
                        overlay.Detection,
                        IsDetectionAcceptedForOverlay(overlay.Detection),
                        null));
                }
            }

            IReadOnlyList<LiveOverlayPrimitive> processingOverlays =
                Settings.DrawBoxes && IsOverlayActive(_lastProcessingOverlaysUpdatedUtc, now)
                    ? _lastProcessingOverlays.Select(ToLiveOverlayPrimitive).ToArray()
                    : [];

            return new LiveOverlaySnapshot(
                size.Width,
                size.Height,
                rois,
                motionRois,
                detections,
                processingOverlays,
                now);
        }
    }

    private static LiveOverlayRoi ToLiveOverlayRoi(RuntimeRoi roi, Size size)
        => new(
            roi.Id,
            roi.Name,
            roi.Enabled,
            roi.Polygon.Select(point => new LiveOverlayPoint(
                point.X / Math.Max(1, size.Width - 1),
                point.Y / Math.Max(1, size.Height - 1))).ToArray());

    private static LiveOverlayDetection ToLiveOverlayDetection(
        AnalysisDetection detection,
        bool accepted,
        string? text)
        => new(
            detection.Kind.ToString(),
            detection.Label,
            text,
            detection.Confidence,
            new LiveOverlayRect(
                detection.Bounds.X,
                detection.Bounds.Y,
                detection.Bounds.Width,
                detection.Bounds.Height),
            detection.TrackId,
            accepted);

    private static LiveOverlayPrimitive ToLiveOverlayPrimitive(ProcessingOverlay overlay)
    {
        Color color = overlay.Color.IsEmpty ? Color.Lime : overlay.Color;
        return new LiveOverlayPrimitive(
            overlay.Kind.ToString(),
            overlay.Points.Select(point => new LiveOverlayPoint(point.X, point.Y)).ToArray(),
            new LiveOverlayRect(
                overlay.Bounds.X,
                overlay.Bounds.Y,
                overlay.Bounds.Width,
                overlay.Bounds.Height),
            overlay.Radius,
            color.R,
            color.G,
            color.B,
            Math.Max(1, overlay.Thickness),
            overlay.Filled);
    }

    /// <summary>Registers or replaces a processing capability for this camera.</summary>
    public void RegisterProcessingModule(IProcessingModule module)
    {
        _pipelineCoordinator.Registry.Register(module);
        RebuildRoiPipelines();
    }

    /// <summary>
    /// Compatibility adapter for older hosts. New integrations should register
    /// an IProcessingModule directly so every capability follows the same path.
    /// </summary>
    [Obsolete("RegisterProcessingModule should be used for new processing capabilities.")]
    public void ConfigureExternalPipelines(Func<string, CameraProcessingSettings, IEnumerable<IProcessingPipeline>>? factory)
    {
        if (factory is null)
        {
            _pipelineCoordinator.Registry.Remove(ProcessingType.Face);
        }
        else
        {
            RegisterProcessingModule(new ProcessingModuleRegistration(
                ProcessingType.Face,
                "Face detection",
                AnalysisKind.Face,
                (context, item) => factory(context.TargetName, item)));
        }

        RebuildRoiPipelines();
    }

    private void RebuildRoiPipelines()
        => _pipelineCoordinator.Rebuild();

    public void UpdateFromSettings()
    {
        Settings.EnsureProcessingDefaults();
        RebuildRoiPipelines();
    }

    public void Start()
    {
        if (_running) return;
        if (string.IsNullOrWhiteSpace(Settings.SourceUrl)) { StatusChanged?.Invoke(this, "Camera source is empty.", true); return; }
        EnsureFrameSourceForSettings();
        RebuildRoiPipelines();
        FrameSource.Configure(Settings.SourceUrl, Settings.Transport, Settings.BufferCount, Settings.ReconnectDelaySec * 1000);
        _motionActive = false; _motionActiveUntilTicks = 0;
        foreach (var m in _motionDetectors.Values) m.Dispose(); _motionDetectors.Clear();
        _processed = 0; _stats.Restart(); _nextPreviewTicks = Stopwatch.GetTimestamp();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _previewThread = new Thread(() => PreviewLoop(cts.Token)) { IsBackground = true, Name = $"PreviewLoop-{Settings.Name}", Priority = ThreadPriority.BelowNormal };
        // Keep the web/API thread pool responsive when an ONNX session is
        // configured with multiple intra-op workers. Native workers are still
        // controlled by the model's thread setting, but the camera scheduler
        // itself should yield to interactive/service traffic.
        _thread = new Thread(() => ProcessLoop(cts.Token)) { IsBackground = true, Name = $"DetectLoop-{Settings.Name}", Priority = ThreadPriority.BelowNormal };
        _running = true;
        _previewThread.Start();
        FrameSource.Start();
        _thread.Start();
        StatusChanged?.Invoke(this, $"Connecting to {Settings.SourceUrl} ...", false);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _previewFrameReady.Set(); } catch { }
        try { _thread?.Join(4000); } catch { }
        try { _previewThread?.Join(4000); } catch { }
        _thread = null;
        _previewThread = null;
        DrainPreviewFrame();
        _cts?.Dispose(); _cts = null;
        FrameSource.Stop();
        foreach (var m in _motionDetectors.Values) m.Dispose(); _motionDetectors.Clear();
        StatusChanged?.Invoke(this, "Stopped", false);
    }

    private void ProcessLoop(CancellationToken ct)
    {
        long nextDetectionTicks = 0, nextMotionTicks = 0;
        long motionIntervalTicks = Math.Max(1, Stopwatch.Frequency / Math.Max(1, Settings.MotionFps));
        while (!ct.IsCancellationRequested)
        {
            if (!FrameSource.TryDequeueFrame(out var frame, 500, ct)) continue;
            try
            {
                // Resolution belongs to the captured frame, not only to an inference pass.
                _lastFrameSize = frame.Size;
                long now = Stopwatch.GetTimestamp();
                var rois = GetNamedRois(frame.Size);
                var motionRois = GetMotionRois(frame.Size, rois);
                bool anyValidRoi = rois.Any(r => r.Polygon.Length >= 3 && r.Bounds.Width >= 32 && r.Bounds.Height >= 32);
                if (Settings.MotionGateEnabled && now >= nextMotionTicks)
                {
                    nextMotionTicks = now + motionIntervalTicks;
                    bool motion = false;
                    foreach (var r in motionRois)
                    {
                        if (r.Polygon.Length < 3 || r.Bounds.Width < 32 || r.Bounds.Height < 32) continue;
                        if (!_motionDetectors.TryGetValue(r.Name, out var md))
                        { md = new MotionDetector(320, 180, Settings.MotionThreshold, Settings.MotionChangedPercent); _motionDetectors[r.Name] = md; }
                        motion |= md.HasMotion(frame, r.Bounds, ToLocalPolygon(r.Polygon, r.Bounds));
                    }
                    if (motion) { _motionActive = true; _motionActiveUntilTicks = now + Stopwatch.Frequency * Math.Max(100, Settings.MotionHoldMs) / 1000; nextDetectionTicks = 0; }
                    else if (_motionActive && now > _motionActiveUntilTicks) { _motionActive = false; }
                }
                else if (!Settings.MotionGateEnabled) _motionActive = true;

                int configuredFps = _motionActive ? Math.Max(1, Settings.ActiveDetectionFps) : Settings.IdleDetectionFps;

                // CameraSettings.MaxFps is a legacy camera-level default used
                // when a new processing item is created. It must not become a
                // hidden cap for existing Plate/Face items. Each pipeline
                // applies its own CameraProcessingSettings.MaxFps in Process;
                // this loop only applies the intentional active/idle runtime
                // gate shared by the camera.
                int targetFps = configuredFps;
                bool runDetection = targetFps > 0 && now >= nextDetectionTicks;
                if (runDetection)
                {
                    nextDetectionTicks = now + Stopwatch.Frequency / targetFps;
                    Volatile.Write(ref _lastInferenceMs, 0);
                    if (anyValidRoi && HasConfiguredRoiPipelines())
                    {
                        // Pipelines may transform their input. Preserve a
                        // clean copy before running them for event artifacts.
                        using Mat rawFrame = frame.Clone();
                        CameraPipelineCoordinator.PipelineExecutionResult execution = RunPipelines(frame, rois);
                        if (execution.Detections.Count > 0)
                            StoreLatestRawFrame(rawFrame, FrameSource.CapturedFrames);
                        UpdatePlateOverlays(frame, execution.Detections);
                        UpdateAnalysisOverlays(execution.Detections);
                        UpdateProcessingOverlays(execution);
                        PublishPlateHistory(frame, execution.Detections);
                        PublishAnalysisHistory(frame, execution.Detections);
                        lock (_renderGate)
                        {
                            _lastAnalysisDetections = execution.Detections.ToArray();
                        }
                        if (execution.Detections.Count > 0) PipelineResultsReady?.Invoke(this, execution.Detections);
                        Interlocked.Increment(ref _processed);
                        _fps = _processed / Math.Max(0.001, _stats.Elapsed.TotalSeconds);
                        StatsChanged?.Invoke(this);
                    }
                }

            }
            catch (Exception ex) { StatusChanged?.Invoke(this, ex.Message, true); }
            finally { frame.Dispose(); }
        }
    }

    private void PreviewLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _previewFrameReady.WaitOne(250);
            if (ct.IsCancellationRequested) break;

            Mat? frame;
            lock (_previewFrameGate)
            {
                frame = _latestPreviewFrame;
                _latestPreviewFrame = null;
            }

            if (frame is null) continue;
            try { PublishPreview(frame); }
            finally { frame.Dispose(); }
        }
    }

    private void DrainPreviewFrame()
    {
        Mat? frame;
        lock (_previewFrameGate)
        {
            frame = _latestPreviewFrame;
            _latestPreviewFrame = null;
        }
        frame?.Dispose();
    }

    private void StoreLatestRawFrame(Mat frame, long sequence)
    {
        using Bitmap bitmap = frame.ToBitmap();
        Bitmap replacement = new(bitmap);
        lock (_rawFrameGate)
        {
            Bitmap? previous = _latestRawFrame;
            _latestRawFrame = replacement;
            _latestRawFrameSequence = sequence;
            previous?.Dispose();
        }
    }

    private CameraPipelineCoordinator.PipelineExecutionResult RunPipelines(Mat frame, List<RuntimeRoi> rois)
        => _pipelineCoordinator.Run(frame, rois);

    private bool HasConfiguredRoiPipelines()
        => _pipelineCoordinator.HasConfiguredPipelines();

    private static string GetProcessingItemKey(AnalysisDetection detection) =>
        detection.Metadata?.TryGetValue("ProcessingItemId", out object? value) == true &&
        value is string id && !string.IsNullOrWhiteSpace(id)
            ? id
            : "camera-default";

    private static string GetRoiKey(AnalysisDetection detection) =>
        detection.Metadata?.TryGetValue("RoiName", out object? value) == true &&
        value is string name && !string.IsNullOrWhiteSpace(name)
            ? name
            : "camera-default";

    private static string GetDetectionOwnerKey(AnalysisDetection detection) =>
        $"{GetRoiKey(detection)}:{GetProcessingItemKey(detection)}";

    private float GetFaceRecordConfidence(AnalysisDetection detection)
    {
        return detection.Metadata?.TryGetValue("FaceRecordConfidence", out object? value) == true
            ? Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture)
            : Settings.FaceRecordConfidence;
    }

    /// <summary>
    /// Shared visual status contract for processing modules. A module may
    /// provide Accepted and/or OverlayThreshold in detection metadata.
    /// </summary>
    private static bool IsDetectionAcceptedForOverlay(AnalysisDetection detection)
    {
        if (detection.Metadata?.TryGetValue("Accepted", out object? acceptedValue) == true &&
            acceptedValue is bool accepted)
            return accepted;

        return detection.Confidence >= GetDetectionOverlayThreshold(detection);
    }

    private static float GetDetectionOverlayThreshold(AnalysisDetection detection)
    {
        if (detection.Metadata?.TryGetValue("OverlayThreshold", out object? overlayValue) == true)
            return Convert.ToSingle(overlayValue, System.Globalization.CultureInfo.InvariantCulture);
        if (detection.Metadata?.TryGetValue("Threshold", out object? legacyValue) == true)
            return Convert.ToSingle(legacyValue, System.Globalization.CultureInfo.InvariantCulture);
        return detection.Kind == AnalysisKind.Face ? 0.80f : detection.Confidence;
    }

    private static MCvScalar GetDetectionOverlayColor(AnalysisDetection detection) =>
        IsDetectionAcceptedForOverlay(detection)
            ? new MCvScalar(0, 220, 0)
            : new MCvScalar(0, 0, 255);

    private int GetFaceEventCooldownSeconds(AnalysisDetection detection)
    {
        return detection.Metadata?.TryGetValue("FaceEventCooldownSeconds", out object? value) == true
            ? Math.Clamp(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture), 0, 3600)
            : Math.Clamp(Settings.FaceEventCooldownSeconds, 0, 3600);
    }

    private void PublishPreview(Mat frame)
    {
        try
        {
            IReadOnlyList<AnalysisDetection> results;
            IReadOnlyList<ProcessingOverlay> overlays;
            lock (_renderGate) results = _lastAnalysisDetections.ToArray();
            overlays = GetActiveProcessingOverlays();
            if (Renderer is not null)
            {
                FrameReady?.Invoke(this, Renderer.Render(frame, results, Settings, overlays));
                return;
            }

            int maxW = 1280, maxH = 720;
            using var preview = new Mat();
            double scale = Math.Min((double)maxW / frame.Width, (double)maxH / frame.Height);
            if (scale < 1) CvInvoke.Resize(frame, preview, new Size(Math.Max(1,(int)(frame.Width*scale)), Math.Max(1,(int)(frame.Height*scale))), 0,0,Inter.Linear);
            else frame.CopyTo(preview);

            // Draw only on the preview copy. The captured Mat remains clean
            // for inference, while the UI still receives its ROI/status marks.
            if (Settings.DrawBoxes)
            {
                DrawRois(preview, GetNamedRois(preview.Size));
                DrawOverlay(preview);
                DrawAnalysisDetections(preview, GetActiveAnalysisDetections(), frame.Size);
                DrawProcessingOverlays(preview, overlays, frame.Size);
            }

            var bmp = preview.ToBitmap();
            if (Settings.DrawBoxes)
            {
                DrawMotionRois(bmp, GetMotionRois(bmp.Size, GetNamedRois(bmp.Size)));
                DrawAnalysisLabels(bmp, GetActiveAnalysisDetections(), frame.Size);
                DrawPlateLabels(bmp, frame.Size);
            }
            FrameReady?.Invoke(this, bmp);
        }
        catch { }
    }

    private void UpdatePlateOverlays(Mat frame, IReadOnlyList<AnalysisDetection> detections)
    {
        AnalysisDetection? detection = detections
            .Where(item => item.Kind == AnalysisKind.Plate)
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
        if (detection is not null)
        {
            Rectangle bounds = Rectangle.Intersect(detection.Bounds, new Rectangle(Point.Empty, frame.Size));
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                CleanupPlateOverlays();
                return;
            }

            bool accepted = detection.Metadata?.TryGetValue("Accepted", out object? acceptedValue) == true && acceptedValue is bool acceptedFlag && acceptedFlag;
            string text = detection.Metadata?.TryGetValue("PlateText", out object? textValue) == true && textValue is string plateText
                ? plateText
                : detection.Label;
            string roiKey = GetRoiKey(detection);
            string processingKey = GetProcessingItemKey(detection);
            string key = detection.TrackId is int trackId
                ? $"plate:{roiKey}:{processingKey}:track:{trackId}"
                : $"plate:{roiKey}:{processingKey}:bounds:{bounds.X}:{bounds.Y}:{bounds.Width}:{bounds.Height}";

            using var cropView = new Mat(frame, bounds);
            using var crop = cropView.Clone();
            using Bitmap bitmap = crop.ToBitmap();
            lock (_overlayGate)
            {
                if (_plateOverlay.Remove(AnalysisKind.Plate, out PlateOverlayInfo? old)) old.Crop?.Dispose();
                _plateOverlay[AnalysisKind.Plate] = new PlateOverlayInfo(
                    key, detection, text, detection.Confidence, DateTime.UtcNow, new Bitmap(bitmap), accepted);
            }
        }

        CleanupPlateOverlays();
    }

    private void PublishPlateHistory(Mat frame, IReadOnlyList<AnalysisDetection> detections)
    {
        foreach (AnalysisDetection detection in detections.Where(d => d.Kind == AnalysisKind.Plate && IsAcceptedPlate(d)))
        {
            string text = GetPlateText(detection);
            if (string.IsNullOrWhiteSpace(text)) continue;

            Rectangle bounds = Rectangle.Intersect(detection.Bounds, new Rectangle(Point.Empty, frame.Size));
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;
            using var cropView = new Mat(frame, bounds);
            using var crop = cropView.Clone();
            using Bitmap bitmap = crop.ToBitmap();
            string ownerKey = GetDetectionOwnerKey(detection);
            var item = new HistoryItem(new Bitmap(bitmap), text, detection.Confidence, DateTime.Now, ownerKey);

            lock (_historyGate)
            {
                HistoryItem? oldSame = _history.FirstOrDefault(h => h.OwnerKey == ownerKey && h.Text == text &&
                    (item.Timestamp - h.Timestamp).TotalSeconds < 5);
                if (oldSame is not null)
                {
                    item.Crop.Dispose();
                    continue;
                }

                _history.Insert(0, item);
                while (_history.Count > 15)
                {
                    HistoryItem evicted = _history[^1];
                    if (!CameraHistoryArchive.TryAppendPlate(Settings.Id, evicted)) break;
                    _history.RemoveAt(_history.Count - 1);
                    evicted.Crop.Dispose();
                }
            }

            PlateDetected?.Invoke(this, item);
        }
    }

    private static bool IsAcceptedPlate(AnalysisDetection detection) =>
        detection.Metadata?.TryGetValue("Accepted", out object? value) == true && value is bool accepted && accepted;

    private static string GetPlateText(AnalysisDetection detection) =>
        detection.Metadata?.TryGetValue("PlateText", out object? value) == true && value is string text
            ? text
            : detection.Label;

    private void DrawOverlay(Mat frame)
    {
        string text=$"{Settings.Name} | FPS {_fps:0.0} | {LastInferenceMs:0.#} ms | {( _motionActive?"MOTION":"IDLE" )} | q:{FrameSource.QueueCount}";
        CvInvoke.PutText(frame,text,new Point(8,22),FontFace.HersheySimplex,.55,new MCvScalar(80,200,255),1,LineType.AntiAlias,true);
    }

    private void DrawAnalysisDetections(Mat frame, IReadOnlyList<AnalysisDetection> detections, Size sourceSize)
    {
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0) return;
        float sx = frame.Width / (float)sourceSize.Width;
        float sy = frame.Height / (float)sourceSize.Height;
        foreach (AnalysisDetection detection in detections)
        {
            if (detection.Kind == AnalysisKind.Plate) continue;
            Rectangle bounds = new(
                Math.Max(0, (int)(detection.Bounds.X * sx)),
                Math.Max(0, (int)(detection.Bounds.Y * sy)),
                Math.Max(1, (int)(detection.Bounds.Width * sx)),
                Math.Max(1, (int)(detection.Bounds.Height * sy)));
            MCvScalar color = GetDetectionOverlayColor(detection);
            CvInvoke.Rectangle(frame, bounds, color, 2);
        }
    }

    private static void DrawAnalysisLabels(Bitmap bitmap, IReadOnlyList<AnalysisDetection> detections, Size sourceSize)
    {
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0) return;

        float sx = bitmap.Width / (float)sourceSize.Width;
        float sy = bitmap.Height / (float)sourceSize.Height;
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using Font font = new("Segoe UI", Math.Clamp(bitmap.Width / 1250f, 10f, 16f), FontStyle.Bold, GraphicsUnit.Pixel);
        using Brush shadow = new SolidBrush(Color.FromArgb(220, 0, 0, 0));
        using StringFormat format = new(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoClip,
            LineAlignment = StringAlignment.Near
        };

        foreach (AnalysisDetection detection in detections)
        {
            if (detection.Kind == AnalysisKind.Plate) continue;

            Rectangle bounds = new(
                Math.Max(0, (int)(detection.Bounds.X * sx)),
                Math.Max(0, (int)(detection.Bounds.Y * sy)),
                Math.Max(1, (int)(detection.Bounds.Width * sx)),
                Math.Max(1, (int)(detection.Bounds.Height * sy)));
            string label = detection.TrackId is int trackId
                ? $"{detection.Label} #{trackId} {detection.Confidence:P0}"
                : $"{detection.Label} {detection.Confidence:P0}";
            int labelY = bounds.Bottom + (int)Math.Ceiling(font.GetHeight(graphics));
            if (labelY + font.Height > bitmap.Height) labelY = Math.Max(0, bounds.Y - font.Height - 2);

            RectangleF textArea = new(bounds.X, labelY, Math.Max(1, bitmap.Width - bounds.X - 2), font.Height + 4);
            using Brush foreground = new SolidBrush(
                IsDetectionAcceptedForOverlay(detection) ? Color.LimeGreen : Color.Red);
            DrawTextWithShadow(graphics, label, font, foreground, shadow, textArea, format);
        }
    }

    private void UpdateAnalysisOverlays(IReadOnlyList<AnalysisDetection> detections)
    {
        lock (_overlayGate)
        {
            foreach (AnalysisDetection detection in detections
                .Where(item => item.Kind != AnalysisKind.Plate)
                .GroupBy(item => item.Kind)
                .Select(group => group.OrderByDescending(item => item.Confidence).First()))
            {
                string processingKey = GetProcessingItemKey(detection);
                string key = detection.TrackId is int trackId
                    ? $"{processingKey}:{detection.Kind}:{trackId}"
                    : $"{processingKey}:{detection.Kind}:{detection.Label}:{detection.Bounds.X}:{detection.Bounds.Y}";
                _analysisOverlay[detection.Kind] = new AnalysisOverlayInfo(key, detection, DateTime.UtcNow);
            }
            DateTime now = DateTime.UtcNow;
            foreach (AnalysisKind kind in _analysisOverlay
                .Where(item => !IsOverlayActive(item.Value.UpdatedUtc, now))
                .Select(item => item.Key)
                .ToList())
                _analysisOverlay.Remove(kind);
        }
    }

    private IReadOnlyList<AnalysisDetection> GetActiveAnalysisDetections()
    {
        lock (_overlayGate)
        {
            DateTime now = DateTime.UtcNow;
            return _analysisOverlay.Values
                .Where(item => IsOverlayActive(item.UpdatedUtc, now))
                .Select(item => item.Detection)
                .ToArray();
        }
    }

    private void UpdateProcessingOverlays(CameraPipelineCoordinator.PipelineExecutionResult execution)
    {
        if (!execution.OverlaysUpdated) return;

        lock (_overlayGate)
        {
            _lastProcessingOverlays = execution.Overlays.ToArray();
            _lastProcessingOverlaysUpdatedUtc = DateTime.UtcNow;
        }
    }

    private IReadOnlyList<ProcessingOverlay> GetActiveProcessingOverlays()
    {
        lock (_overlayGate)
        {
            if (!IsOverlayActive(_lastProcessingOverlaysUpdatedUtc, DateTime.UtcNow))
                return [];

            return _lastProcessingOverlays;
        }
    }

    private static void DrawProcessingOverlays(
        Mat frame,
        IReadOnlyList<ProcessingOverlay> overlays,
        Size sourceSize)
    {
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0) return;

        float sx = frame.Width / (float)sourceSize.Width;
        float sy = frame.Height / (float)sourceSize.Height;

        foreach (ProcessingOverlay overlay in overlays)
        {
            Color colorValue = overlay.Color.IsEmpty ? Color.Lime : overlay.Color;
            MCvScalar color = new(colorValue.B, colorValue.G, colorValue.R);
            int thickness = Math.Max(1, overlay.Thickness);

            Point Map(Point point) => new(
                Math.Clamp((int)Math.Round(point.X * sx), 0, Math.Max(0, frame.Width - 1)),
                Math.Clamp((int)Math.Round(point.Y * sy), 0, Math.Max(0, frame.Height - 1)));

            switch (overlay.Kind)
            {
                case ProcessingOverlayKind.Polyline:
                case ProcessingOverlayKind.Polygon:
                    if (overlay.Points.Count >= 2)
                    {
                        using var points = new VectorOfPoint(overlay.Points.Select(Map).ToArray());
                        CvInvoke.Polylines(
                            frame,
                            points,
                            overlay.Kind == ProcessingOverlayKind.Polygon,
                            color,
                            thickness,
                            LineType.AntiAlias);
                    }
                    break;

                case ProcessingOverlayKind.Points:
                    foreach (Point point in overlay.Points)
                        CvInvoke.Circle(frame, Map(point), Math.Max(1, overlay.Radius), color, -1, LineType.AntiAlias);
                    break;

                case ProcessingOverlayKind.Circle:
                    if (overlay.Points.Count > 0)
                    {
                        int radius = Math.Max(1, (int)Math.Round(overlay.Radius * Math.Min(sx, sy)));
                        CvInvoke.Circle(
                            frame,
                            Map(overlay.Points[0]),
                            radius,
                            color,
                            overlay.Filled ? -1 : thickness,
                            LineType.AntiAlias);
                    }
                    break;

                case ProcessingOverlayKind.Rectangle:
                    if (!overlay.Bounds.IsEmpty)
                    {
                        Rectangle mapped = new(
                            (int)Math.Round(overlay.Bounds.X * sx),
                            (int)Math.Round(overlay.Bounds.Y * sy),
                            Math.Max(1, (int)Math.Round(overlay.Bounds.Width * sx)),
                            Math.Max(1, (int)Math.Round(overlay.Bounds.Height * sy)));
                        CvInvoke.Rectangle(
                            frame,
                            mapped,
                            color,
                            overlay.Filled ? -1 : thickness);
                    }
                    break;
            }
        }
    }

    private void PublishAnalysisHistory(Mat frame, IReadOnlyList<AnalysisDetection> detections)
    {
        foreach (AnalysisDetection detection in detections.Where(d =>
            d.Kind == AnalysisKind.Face &&
            IsDetectionAcceptedForOverlay(d) &&
            d.Confidence >= GetFaceRecordConfidence(d)))
        {
            DateTime now = DateTime.UtcNow;
            string processingKey = GetProcessingItemKey(detection);
            string trackKey = $"face:{processingKey}:track:{detection.TrackId?.ToString() ?? detection.Label}";
            string? identityKey = detection.Metadata?.TryGetValue("IdentityId", out object? identity) == true && identity is string identityText && !string.IsNullOrWhiteSpace(identityText)
                ? $"face:{processingKey}:identity:{identityText}" : null;
            lock (_overlayGate)
            {
                bool sameTrackRecently = _analysisHistoryTimes.TryGetValue(trackKey, out DateTime trackPrevious)
                    && (now - trackPrevious).TotalSeconds < 5;
                bool sameIdentityRecently = identityKey is not null && _analysisHistoryTimes.TryGetValue(identityKey, out DateTime identityPrevious)
                    && (now - identityPrevious).TotalSeconds < GetFaceEventCooldownSeconds(detection);
                if (sameTrackRecently || sameIdentityRecently) continue;
                _analysisHistoryTimes[trackKey] = now;
                if (identityKey is not null) _analysisHistoryTimes[identityKey] = now;
                int identityCooldownSeconds = GetFaceEventCooldownSeconds(detection);
                foreach (string oldKey in _analysisHistoryTimes
                    .Where(x =>
                    {
                        int retentionSeconds = x.Key.Contains(":identity:", StringComparison.Ordinal)
                            ? identityCooldownSeconds
                            : 5;
                        return (now - x.Value).TotalSeconds > retentionSeconds;
                    })
                    .Select(x => x.Key)
                    .ToList())
                    _analysisHistoryTimes.Remove(oldKey);
            }

            Rectangle bounds = Rectangle.Intersect(detection.Bounds, new Rectangle(Point.Empty, frame.Size));
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;
            using var cropView = new Mat(frame, bounds);
            using var crop = cropView.Clone();
            using Bitmap bitmap = crop.ToBitmap();
            var stored = new AnalysisHistoryItem(new Bitmap(bitmap), detection, DateTime.Now);
            lock (_analysisHistoryGate)
            {
                _analysisHistory.Insert(0, stored);
                while (_analysisHistory.Count > 15)
                {
                    AnalysisHistoryItem evicted = _analysisHistory[^1];
                    if (!CameraHistoryArchive.TryAppendFace(Settings.Id, evicted)) break;
                    _analysisHistory.RemoveAt(_analysisHistory.Count - 1);
                    evicted.Crop.Dispose();
                }
            }
            var item = new AnalysisHistoryItem(new Bitmap(bitmap), detection, DateTime.Now);
            try { AnalysisDetected?.Invoke(this, item); }
            finally { item.Crop.Dispose(); }
        }
    }

    private void DrawRois(Mat frame,List<RuntimeRoi> rois)
    {
        foreach(var r in rois) if(r.Polygon.Length>=3)
        { using var vp=new VectorOfPoint(r.Polygon.Select(p=>new Point((int)p.X,(int)p.Y)).ToArray()); CvInvoke.Polylines(frame,vp,true,new MCvScalar(0,180,255),2); CvInvoke.PutText(frame,r.Name,new Point((int)r.Polygon[0].X,(int)r.Polygon[0].Y),FontFace.HersheySimplex,.55,new MCvScalar(0,180,255),1); }
    }

    private static void DrawMotionRois(Bitmap bitmap, List<RuntimeRoi> rois)
    {
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using Pen pen = new(Color.FromArgb(255, 255, 190, 0), Math.Max(1.5f, bitmap.Width / 900f))
        {
            DashStyle = DashStyle.Dash,
            DashCap = DashCap.Round
        };

        foreach (var roi in rois)
        {
            if (roi.Polygon.Length >= 3)
            {
                graphics.DrawPolygon(pen, roi.Polygon);
            }
        }
    }

    private void DrawPlateLabels(Bitmap bitmap, Size sourceSize)
    {
        lock (_overlayGate)
        {
            if (_plateOverlay.Count == 0 || sourceSize.Width <= 0 || sourceSize.Height <= 0)
            {
                return;
            }

            float scaleX = bitmap.Width / (float)sourceSize.Width;
            float scaleY = bitmap.Height / (float)sourceSize.Height;

            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            float baseSize = Math.Clamp(bitmap.Width / 1250f, 10f, 16f);
            using Font plateFont = new("Segoe UI", baseSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using Font specFont = new("Segoe UI", Math.Max(9f, baseSize * 0.82f), FontStyle.Bold, GraphicsUnit.Pixel);
            using Brush shadowBrush = new SolidBrush(Color.FromArgb(220, 0, 0, 0));

            foreach (PlateOverlayInfo overlay in _plateOverlay.Values)
            {
                if (!IsOverlayActive(overlay.UpdatedUtc, DateTime.UtcNow)) continue;
                AnalysisDetection detection = overlay.Detection;

            RectangleF plateRectangle = new(
                detection.Bounds.X * scaleX,
                detection.Bounds.Y * scaleY,
                Math.Max(1f, detection.Bounds.Width * scaleX),
                Math.Max(1f, detection.Bounds.Height * scaleY));

            if (overlay.Crop is not null)
            {
                graphics.DrawImage(overlay.Crop, plateRectangle);
            }

            using Pen statusPen = new(
                overlay.Accepted ? Color.LimeGreen : Color.Red,
                Math.Max(2f, bitmap.Width / 700f));
            using Brush textBrush = new SolidBrush(overlay.Accepted ? Color.LimeGreen : Color.Red);
            graphics.DrawRectangle(statusPen, plateRectangle.X, plateRectangle.Y, plateRectangle.Width, plateRectangle.Height);

            string plateText = overlay.Text;
            string specifications = $"Th: {Settings.Confidence:0.00}  Co: {overlay.Confidence:0.00}";

            SizeF plateSize = graphics.MeasureString(plateText, plateFont);
            SizeF specificationSize = graphics.MeasureString(specifications, specFont);
            float labelWidth = Math.Max(plateSize.Width, specificationSize.Width) + 10f;
            float labelHeight = plateSize.Height + specificationSize.Height + 6f;

            float labelX = plateRectangle.Left;
            float labelY = plateRectangle.Bottom + 4f;

            if (labelX + labelWidth > bitmap.Width)
            {
                labelX = Math.Max(2f, bitmap.Width - labelWidth - 2f);
            }

            if (labelY + labelHeight > bitmap.Height)
            {
                labelY = Math.Max(2f, plateRectangle.Top - labelHeight - 4f);
            }

            // Plate number is deliberately outside the plate rectangle.
            // No character labels are drawn over the plate image.
            DrawTextWithShadow(graphics, plateText, plateFont, textBrush, shadowBrush, labelX, labelY);
            DrawTextWithShadow(
                graphics,
                specifications,
                specFont,
                textBrush,
                shadowBrush,
                labelX,
                labelY + plateSize.Height - 1f);
            }
        }
    }

    private static void DrawTextWithShadow(
        Graphics graphics,
        string text,
        Font font,
        Brush foreground,
        Brush shadow,
        float x,
        float y)
    {
        graphics.DrawString(text, font, shadow, x + 1f, y + 1f);
        graphics.DrawString(text, font, foreground, x, y);
    }

    private static void DrawTextWithShadow(
        Graphics graphics,
        string text,
        Font font,
        Brush foreground,
        Brush shadow,
        RectangleF layout,
        StringFormat format)
    {
        RectangleF shadowLayout = layout;
        shadowLayout.Offset(1f, 1f);
        graphics.DrawString(text, font, shadow, shadowLayout, format);
        graphics.DrawString(text, font, foreground, layout, format);
    }

    private void CleanupPlateOverlays()
    {
        lock (_overlayGate)
        {
            DateTime now = DateTime.UtcNow;
            foreach (AnalysisKind key in _plateOverlay
                .Where(x => !IsOverlayActive(x.Value.UpdatedUtc, now))
                .Select(x => x.Key).ToList())
            {
                if (_plateOverlay.Remove(key, out PlateOverlayInfo? old)) old.Crop?.Dispose();
            }
        }
    }

    private List<RuntimeRoi> GetNamedRois(Size size)
    {
        var result = new List<RuntimeRoi>();
        lock (Settings)
        {
            result.AddRange(Settings.Rois
                .Where(roi => roi.Enabled && roi.Points.Count >= 3)
                .Select(roi =>
                {
                    PointF[] polygon = roi.Points
                        .Select(point => new PointF(
                            (float)Math.Clamp(point.X, 0, 1) * (size.Width - 1),
                            (float)Math.Clamp(point.Y, 0, 1) * (size.Height - 1)))
                        .ToArray();
                    return new RuntimeRoi(
                        string.IsNullOrWhiteSpace(roi.Id) ? roi.Name : roi.Id,
                        string.IsNullOrWhiteSpace(roi.Name) ? "ROI" : roi.Name,
                        polygon,
                        GetPolygonBounds(polygon, size),
                        true,
                        RoiProcessingModes.Normalize(roi.ProcessingMode));
                })
                .Where(roi => roi.Bounds.Width >= 32 && roi.Bounds.Height >= 32));
        }

        return result;
    }

    private List<RuntimeRoi> GetMotionRois(Size size, List<RuntimeRoi> detectionRois)
    {
        double scale = Math.Clamp(Settings.MotionRoiScalePercent, 25, 300) / 100.0;
        if (Math.Abs(scale - 1.0) < 0.001)
        {
            return detectionRois;
        }

        return detectionRois.Select(roi =>
        {
            if (roi.Polygon.Length < 3)
            {
                return roi;
            }

            // Keep the stable centroid-based scaling. It preserves the exact
            // user-drawn polygon topology and avoids self-intersections on
            // concave ROIs.
            float centerX = roi.Polygon.Average(p => p.X);
            float centerY = roi.Polygon.Average(p => p.Y);
            PointF[] scaled = roi.Polygon.Select(p => new PointF(
                Math.Clamp(centerX + (float)((p.X - centerX) * scale), 0, size.Width - 1),
                Math.Clamp(centerY + (float)((p.Y - centerY) * scale), 0, size.Height - 1)))
                .ToArray();

            return roi with { Polygon = scaled, Bounds = GetPolygonBounds(scaled, size) };
        }).ToList();
    }
    private PointF[] GetRoiPolygon(Size size){if(!Settings.RoiEnabled)return [new(0,0),new(size.Width-1,0),new(size.Width-1,size.Height-1),new(0,size.Height-1)];if(Settings.RoiPolygon.Count>=3)return Settings.RoiPolygon.Select(p=>new PointF((float)Math.Clamp(p.X,0,1)*(size.Width-1),(float)Math.Clamp(p.Y,0,1)*(size.Height-1))).ToArray();double l=Math.Clamp(Settings.RoiLeft,0,1),t=Math.Clamp(Settings.RoiTop,0,1),r=Math.Clamp(Settings.RoiRight,l+.01,1),b=Math.Clamp(Settings.RoiBottom,t+.01,1);return[new((float)(size.Width*l),(float)(size.Height*t)),new((float)(size.Width*r),(float)(size.Height*t)),new((float)(size.Width*r),(float)(size.Height*b)),new((float)(size.Width*l),(float)(size.Height*b))];}
    private static Rectangle GetPolygonBounds(PointF[] p,Size s){if(p.Length<3)return Rectangle.Empty;float minX=p.Min(x=>x.X),maxX=p.Max(x=>x.X),minY=p.Min(x=>x.Y),maxY=p.Max(x=>x.Y);int x=Math.Clamp((int)Math.Floor(minX),0,s.Width-1),y=Math.Clamp((int)Math.Floor(minY),0,s.Height-1);int x2=Math.Clamp((int)Math.Ceiling(maxX),x+1,s.Width),y2=Math.Clamp((int)Math.Ceiling(maxY),y+1,s.Height);return new(x,y,Math.Max(1,x2-x),Math.Max(1,y2-y));}
    private static Point[] ToLocalPolygon(PointF[] p,Rectangle b)=>p.Select(x=>new Point(Math.Clamp((int)Math.Round(x.X-b.X),0,Math.Max(0,b.Width-1)),Math.Clamp((int)Math.Round(x.Y-b.Y),0,Math.Max(0,b.Height-1)))).ToArray();
    public void Dispose()
    {
        Stop();
        foreach (var h in History) h.Crop.Dispose();
        foreach (var h in AnalysisHistory) h.Crop.Dispose();
        lock (_rawFrameGate)
        {
            _latestRawFrame?.Dispose();
            _latestRawFrame = null;
            _latestRawFrameSequence = 0;
        }
        lock (_overlayGate)
        {
            foreach (var overlay in _plateOverlay.Values) overlay.Crop?.Dispose();
            _plateOverlay.Clear();
            _analysisOverlay.Clear();
            _lastProcessingOverlays = [];
            _lastProcessingOverlaysUpdatedUtc = DateTime.MinValue;
            _analysisHistoryTimes.Clear();
        }
        lock (_analysisHistoryGate) { _analysisHistory.Clear(); }
        _pipelineCoordinator.Dispose();
        FrameSource.Dispose();
        _previewFrameReady.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal static class CameraHistoryArchive
{
    private const int MaxEntriesPerCamera = 100;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false, PropertyNameCaseInsensitive = true };
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "history-archive.json");

    private sealed class Entry
    {
        public string CameraId { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public float Confidence { get; set; }
        public DateTime Timestamp { get; set; }
        public string OwnerKey { get; set; } = string.Empty;
        public int? TrackId { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string CropBase64 { get; set; } = string.Empty;
    }

    public static bool TryAppendPlate(string cameraId, CameraRuntime.HistoryItem item)
    {
        return TryAppend(new Entry
        {
            CameraId = cameraId,
            Kind = "Plate",
            Text = item.Text,
            Label = item.Text,
            Confidence = item.Confidence,
            Timestamp = item.Timestamp,
            OwnerKey = item.OwnerKey,
            CropBase64 = EncodeCrop(item.Crop)
        });
    }

    public static bool TryAppendFace(string cameraId, CameraRuntime.AnalysisHistoryItem item)
    {
        AnalysisDetection detection = item.Detection;
        return TryAppend(new Entry
        {
            CameraId = cameraId,
            Kind = detection.Kind.ToString(),
            Label = detection.Label,
            Confidence = detection.Confidence,
            Timestamp = item.Timestamp,
            OwnerKey = GetOwnerKey(detection),
            TrackId = detection.TrackId,
            X = detection.Bounds.X,
            Y = detection.Bounds.Y,
            Width = detection.Bounds.Width,
            Height = detection.Bounds.Height,
            CropBase64 = EncodeCrop(item.Crop)
        });
    }

    public static IReadOnlyList<CameraRuntime.HistoryItem> LoadPlate(string cameraId)
    {
        lock (Gate)
        {
            return ReadEntries()
                .Where(entry => entry.CameraId == cameraId && entry.Kind == "Plate")
                .OrderByDescending(entry => entry.Timestamp)
                .Select(entry => DecodeCrop(entry.CropBase64) is Bitmap crop
                    ? new CameraRuntime.HistoryItem(crop, entry.Text, entry.Confidence, entry.Timestamp, entry.OwnerKey)
                    : null)
                .Where(item => item is not null)
                .Cast<CameraRuntime.HistoryItem>()
                .ToArray();
        }
    }

    public static IReadOnlyList<CameraRuntime.AnalysisHistoryItem> LoadFace(string cameraId)
    {
        lock (Gate)
        {
            return ReadEntries()
                .Where(entry => entry.CameraId == cameraId && entry.Kind == "Face")
                .OrderByDescending(entry => entry.Timestamp)
                .Select(entry =>
                {
                    Bitmap? crop = DecodeCrop(entry.CropBase64);
                    if (crop is null) return null;
                    var detection = new AnalysisDetection(
                        AnalysisKind.Face,
                        entry.Label,
                        entry.Confidence,
                        new Rectangle(entry.X, entry.Y, entry.Width, entry.Height),
                        entry.TrackId);
                    return new CameraRuntime.AnalysisHistoryItem(crop, detection, entry.Timestamp);
                })
                .Where(item => item is not null)
                .Cast<CameraRuntime.AnalysisHistoryItem>()
                .ToArray();
        }
    }

    private static bool TryAppend(Entry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.CropBase64)) return false;
        lock (Gate)
        {
            try
            {
                List<Entry> entries = ReadEntries().ToList();
                entries.Add(entry);
                List<Entry> cameraEntries = entries
                    .Where(item => item.CameraId == entry.CameraId)
                    .OrderByDescending(item => item.Timestamp)
                    .Take(MaxEntriesPerCamera)
                    .ToList();
                entries.RemoveAll(item => item.CameraId == entry.CameraId);
                entries.AddRange(cameraEntries);

                string path = FilePath;
                string tempPath = path + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(entries, JsonOptions), Encoding.UTF8);
                File.Move(tempPath, path, true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static IReadOnlyList<Entry> ReadEntries()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string EncodeCrop(Bitmap bitmap)
    {
        try
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Jpeg);
            return Convert.ToBase64String(stream.ToArray());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Bitmap? DecodeCrop(string value)
    {
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(value));
            using var source = new Bitmap(stream);
            return new Bitmap(source);
        }
        catch
        {
            return null;
        }
    }

    private static string GetOwnerKey(AnalysisDetection detection)
    {
        string roi = detection.Metadata?.TryGetValue("RoiName", out object? roiValue) == true &&
            roiValue is string roiName && !string.IsNullOrWhiteSpace(roiName)
            ? roiName
            : "camera-default";
        string processing = detection.Metadata?.TryGetValue("ProcessingItemId", out object? processingValue) == true &&
            processingValue is string processingId && !string.IsNullOrWhiteSpace(processingId)
            ? processingId
            : "camera-default";
        return $"{roi}:{processing}";
    }
}



