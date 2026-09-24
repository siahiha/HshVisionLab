using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using System.Diagnostics;

namespace HshDetectionEngin;

/// <summary>
/// Owns the per-ROI pipeline graph for one camera. CameraRuntime remains
/// responsible for capture, motion gating and lifecycle; this class is only
/// responsible for building and executing processing pipelines.
/// </summary>
internal sealed class CameraPipelineCoordinator : IDisposable
{
    internal sealed class PipelineExecutionResult
    {
        public List<AnalysisDetection> Detections { get; } = [];
        public List<ProcessingOverlay> Overlays { get; } = [];
        public bool OverlaysUpdated { get; set; }
    }

    private sealed record PipelineBinding(
        IProcessingPipeline Pipeline,
        CameraProcessingSettings Settings,
        IReadOnlyDictionary<string, object?> DetectionMetadata);

    private sealed record RoiPipelineGroup(
        string ProcessingMode,
        List<PipelineBinding> Pipelines);

    private sealed record RoiRunPlan(
        CameraRuntime.RuntimeRoi Roi,
        RoiPipelineGroup Group);

    private sealed record PipelineRunOutput(
        IReadOnlyList<AnalysisDetection> Detections,
        IReadOnlyList<ProcessingOverlay> Overlays,
        bool OverlaysUpdated);

    private sealed class RoiExecutionResult
    {
        public List<AnalysisDetection> Detections { get; } = [];
        public List<ProcessingOverlay> Overlays { get; } = [];
        public bool OverlaysUpdated { get; set; }
    }

    private readonly CameraSettings _settings;
    private readonly ProcessingRegistry _registry;
    private readonly Action<string, bool> _reportStatus;
    private readonly Action<double> _setInferenceMs;
    private readonly object _gate = new();
    private PipelineGraph _graph = new(new Dictionary<string, RoiPipelineGroup>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// An immutable pipeline graph which can be replaced atomically. Inference
    /// leases the graph while it is running, so a settings rebuild can never
    /// dispose an ONNX session underneath an active detection pass. The web
    /// layer only touches the short coordinator lock when it reads the graph;
    /// it never waits for inference to finish.
    /// </summary>
    private sealed class PipelineGraph
    {
        private readonly object _gate = new();
        private readonly ManualResetEventSlim _idle = new(true);
        private int _activeRuns;
        private bool _retired;
        private bool _disposed;

        public PipelineGraph(Dictionary<string, RoiPipelineGroup> pipelines)
        {
            Pipelines = pipelines;
        }

        public Dictionary<string, RoiPipelineGroup> Pipelines { get; }

        public int ActivePipelineCount => Pipelines.Values.Sum(group => group.Pipelines.Count);

        public bool HasConfiguredPipelines => Pipelines.Values.Any(group => group.Pipelines.Count > 0);

        public bool TryAcquire()
        {
            lock (_gate)
            {
                if (_retired) return false;
                _activeRuns++;
                _idle.Reset();
                return true;
            }
        }

        public void Release()
        {
            bool dispose;
            lock (_gate)
            {
                if (_activeRuns > 0) _activeRuns--;
                if (_activeRuns != 0) return;
                _idle.Set();
                dispose = _retired;
            }

            if (dispose) DisposePipelinesOnce();
        }

        public void RetireAndDispose()
        {
            lock (_gate)
            {
                _retired = true;
                if (_activeRuns == 0)
                {
                    // No active reader can appear after retirement.
                }
            }

            _idle.Wait();
            DisposePipelinesOnce();
            _idle.Dispose();
        }

        private void DisposePipelinesOnce()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            DisposePipelines(Pipelines.Values.Select(group => group.Pipelines));
        }
    }

    public CameraPipelineCoordinator(
        CameraSettings settings,
        ProcessingRegistry registry,
        Action<string, bool> reportStatus,
        Action<double> setInferenceMs)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _reportStatus = reportStatus ?? throw new ArgumentNullException(nameof(reportStatus));
        _setInferenceMs = setInferenceMs ?? throw new ArgumentNullException(nameof(setInferenceMs));
    }

    public ProcessingRegistry Registry => _registry;

    public int ActivePipelineCount
    {
        get { lock (_gate) return _graph.ActivePipelineCount; }
    }

    public void Rebuild()
    {
        var rebuilt = new Dictionary<string, RoiPipelineGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (NamedRoi configuredRoi in _settings.Rois.Where(roi => roi.Enabled))
        {
            string targetName = string.IsNullOrWhiteSpace(configuredRoi.Name) ? "ROI" : configuredRoi.Name;
            string roiKey = GetRoiKey(configuredRoi);
            var pipelines = new List<PipelineBinding>();
            IReadOnlyList<CameraProcessingSettings> processing = configuredRoi.Processing ?? [];

            foreach (CameraProcessingSettings item in processing.Where(item => item.Enabled))
            {
                if (!_registry.TryGet(item.Kind, out IProcessingModule? module) || module is null)
                {
                    _reportStatus($"Processing module '{item.Type}' is not registered.", true);
                    continue;
                }

                try
                {
                    if (!string.IsNullOrWhiteSpace(module.AvailabilityMessage))
                    {
                        _reportStatus(module.AvailabilityMessage, true);
                        continue;
                    }

                    var context = new ProcessingCreationContext
                    {
                        Camera = _settings,
                        TargetName = targetName
                    };
                    IReadOnlyDictionary<string, object?> metadata = module.GetDetectionMetadata(item);
                    IEnumerable<IProcessingPipeline> created = module.CreatePipelines(context, item);
                    pipelines.AddRange(created
                        .Where(pipeline => pipeline is not null)
                        .Select(pipeline => new PipelineBinding(pipeline, item, metadata)));
                }
                catch (Exception ex)
                {
                    _reportStatus(ex.Message, true);
                }
            }

            rebuilt[roiKey] = new RoiPipelineGroup(
                RoiProcessingModes.Normalize(configuredRoi.ProcessingMode),
                pipelines);
        }

        PipelineGraph replacement = new(rebuilt);
        PipelineGraph previous;
        lock (_gate)
        {
            previous = _graph;
            _graph = replacement;
        }

        // Do not hold the coordinator lock while an old inference pass drains.
        previous.RetireAndDispose();
    }

    public bool HasConfiguredPipelines()
    {
        lock (_gate) return _graph.HasConfiguredPipelines;
    }

    public PipelineExecutionResult Run(Mat frame, IReadOnlyList<CameraRuntime.RuntimeRoi> rois)
    {
        var execution = new PipelineExecutionResult();
        PipelineGraph graph;
        lock (_gate) graph = _graph;
        if (!graph.TryAcquire())
        {
            _setInferenceMs(0);
            return execution;
        }

        try
        {
            if (graph.Pipelines.Count == 0)
            {
                _setInferenceMs(0);
                return execution;
            }

            Stopwatch runTimer = Stopwatch.StartNew();
            var plans = new List<RoiRunPlan>();
            for (int index = 0; index < rois.Count; index++)
            {
                CameraRuntime.RuntimeRoi roi = rois[index];
                if (roi.Polygon.Length < 3 || roi.Bounds.Width < 32 || roi.Bounds.Height < 32) continue;
                string roiKey = GetRoiKey(roi);
                if (!graph.Pipelines.TryGetValue(roiKey, out RoiPipelineGroup? group) &&
                    !graph.Pipelines.TryGetValue(roi.Name, out group)) continue;
                if (group.Pipelines.Count == 0) continue;
                plans.Add(new RoiRunPlan(roi, group));
            }

            // ROI execution is independent, so each active ROI gets its own
            // worker slot. The camera thread waits for all slots before it
            // publishes one coherent frame result.
            var results = new RoiExecutionResult?[plans.Count];
            Parallel.For(0, plans.Count, index =>
            {
                RoiRunPlan plan = plans[index];
                results[index] = RunRoi(frame, plan.Roi, plan.Group);
            });

            foreach (RoiExecutionResult? result in results)
            {
                if (result is null) continue;
                execution.Detections.AddRange(result.Detections);
                execution.Overlays.AddRange(result.Overlays);
                execution.OverlaysUpdated |= result.OverlaysUpdated;
            }

            runTimer.Stop();
            _setInferenceMs(runTimer.Elapsed.TotalMilliseconds);
            return execution;
        }
        finally
        {
            graph.Release();
        }
    }

    public void Dispose()
    {
        PipelineGraph previous;
        lock (_gate)
        {
            previous = _graph;
            _graph = new PipelineGraph(new Dictionary<string, RoiPipelineGroup>(StringComparer.OrdinalIgnoreCase));
        }

        previous.RetireAndDispose();
    }

    private RoiExecutionResult RunRoi(
        Mat frame,
        CameraRuntime.RuntimeRoi roi,
        RoiPipelineGroup group)
    {
        using var roiMat = new Mat(frame, roi.Bounds);
        using var masked = MaskOutsidePolygon(roiMat, ToLocalPolygon(roi.Polygon, roi.Bounds));
        using Mat input = masked.Clone();

        return string.Equals(
            group.ProcessingMode,
            RoiProcessingModes.Parallel,
            StringComparison.OrdinalIgnoreCase)
            ? RunRoiParallel(input, roi, group.Pipelines, frame.Size)
            : RunRoiSequential(input, roi, group.Pipelines, frame.Size);
    }

    private RoiExecutionResult RunRoiSequential(
        Mat input,
        CameraRuntime.RuntimeRoi roi,
        IReadOnlyList<PipelineBinding> pipelines,
        Size originalFrameSize)
    {
        var execution = new RoiExecutionResult();
        Mat current = input.Clone();
        var previous = new List<AnalysisDetection>();
        try
        {
            foreach (PipelineBinding binding in pipelines.ToArray())
            {
                try
                {
                    using var result = binding.Pipeline.Process(new ProcessingContext
                    {
                        Image = current,
                        CameraId = _settings.Id,
                        CameraName = _settings.Name,
                        Timestamp = DateTime.UtcNow,
                        SourceBounds = roi.Bounds,
                        OriginalFrameSize = originalFrameSize,
                        PreviousDetections = previous.ToArray()
                    });

                    previous = result.Detections
                        .Select(detection => AttachProcessingSettings(detection, binding, roi.Id, roi.Name))
                        .ToList();
                    AddPipelineOutput(execution, previous, result.Overlays, result.OverlaysUpdated, roi);

                    Mat? next = result.TakeNextImage();
                    if (next is not null)
                    {
                        current.Dispose();
                        current = next;
                    }
                }
                catch (Exception ex)
                {
                    _reportStatus(
                        $"Pipeline '{binding.Pipeline.Name}' failed for ROI '{roi.Name}': {ex.Message}",
                        true);
                }
            }
        }
        catch (Exception ex)
        {
            _reportStatus($"Pipeline execution failed for ROI '{roi.Name}': {ex.Message}", true);
        }
        finally
        {
            current.Dispose();
        }

        return execution;
    }

    private RoiExecutionResult RunRoiParallel(
        Mat input,
        CameraRuntime.RuntimeRoi roi,
        IReadOnlyList<PipelineBinding> pipelines,
        Size originalFrameSize)
    {
        var outputs = new PipelineRunOutput?[pipelines.Count];
        Parallel.For(0, pipelines.Count, index =>
        {
            PipelineBinding binding = pipelines[index];
            try
            {
                using Mat pipelineInput = input.Clone();
                using var result = binding.Pipeline.Process(new ProcessingContext
                {
                    Image = pipelineInput,
                    CameraId = _settings.Id,
                    CameraName = _settings.Name,
                    Timestamp = DateTime.UtcNow,
                    SourceBounds = roi.Bounds,
                    OriginalFrameSize = originalFrameSize,
                    PreviousDetections = []
                });

                outputs[index] = new PipelineRunOutput(
                    result.Detections
                        .Select(detection => AttachProcessingSettings(detection, binding, roi.Id, roi.Name))
                        .ToArray(),
                    result.Overlays.ToArray(),
                    result.OverlaysUpdated);
            }
            catch (Exception ex)
            {
                _reportStatus(
                    $"Pipeline '{binding.Pipeline.Name}' failed for ROI '{roi.Name}': {ex.Message}",
                    true);
            }
        });

        var execution = new RoiExecutionResult();
        foreach (PipelineRunOutput? output in outputs)
        {
            if (output is null) continue;
            AddPipelineOutput(execution, output.Detections, output.Overlays, output.OverlaysUpdated, roi);
        }

        return execution;
    }

    private static void AddPipelineOutput(
        RoiExecutionResult target,
        IReadOnlyList<AnalysisDetection> detections,
        IReadOnlyList<ProcessingOverlay> overlays,
        bool overlaysUpdated,
        CameraRuntime.RuntimeRoi roi)
    {
        target.Detections.AddRange(detections.Select(detection => detection with
        {
            Bounds = new Rectangle(
                detection.Bounds.X + roi.Bounds.X,
                detection.Bounds.Y + roi.Bounds.Y,
                detection.Bounds.Width,
                detection.Bounds.Height)
        }));
        target.Overlays.AddRange(overlays.Select(overlay => MapOverlayToFrame(overlay, roi.Bounds)));
        target.OverlaysUpdated |= overlaysUpdated || overlays.Count > 0;
    }

    private static AnalysisDetection AttachProcessingSettings(
        AnalysisDetection detection,
        PipelineBinding binding,
        string roiId,
        string roiName)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (detection.Metadata is not null)
        {
            foreach ((string key, object? value) in detection.Metadata)
                metadata[key] = value;
        }

        metadata["ProcessingItemId"] = binding.Settings.Id;
        metadata["ProcessingItemName"] = binding.Settings.Name;
        metadata["ProcessingItemType"] = binding.Settings.Type;
        metadata["RoiId"] = roiId;
        metadata["RoiName"] = roiName;
        int historyCooldownSeconds = binding.Settings.Kind == ProcessingType.Face
            ? binding.Settings.GetOptions<FaceProcessingOptions>().EventCooldownSeconds
            : binding.Settings.Kind == ProcessingType.Plate
                ? binding.Settings.GetOptions<PlateProcessingOptions>().EventCooldownSeconds
                : 0;
        metadata["HistoryEventCooldownSeconds"] = Math.Clamp(historyCooldownSeconds, 0, 3600);
        foreach ((string key, object? value) in binding.DetectionMetadata)
            metadata[key] = value;

        return detection with { Metadata = metadata };
    }

    private static ProcessingOverlay MapOverlayToFrame(
        ProcessingOverlay overlay,
        Rectangle roiBounds)
    {
        Point[] points = overlay.Points
            .Select(point => new Point(
                point.X + roiBounds.X,
                point.Y + roiBounds.Y))
            .ToArray();

        Rectangle bounds = overlay.Bounds.IsEmpty
            ? Rectangle.Empty
            : new Rectangle(
                overlay.Bounds.X + roiBounds.X,
                overlay.Bounds.Y + roiBounds.Y,
                overlay.Bounds.Width,
                overlay.Bounds.Height);

        return overlay with
        {
            Points = points,
            Bounds = bounds
        };
    }

    private static void DisposePipelines(IEnumerable<List<PipelineBinding>> groups)
    {
        foreach (List<PipelineBinding> pipelines in groups)
        {
            foreach (PipelineBinding binding in pipelines)
            {
                try { binding.Pipeline.Dispose(); } catch { }
            }
        }
    }

    private static string GetRoiKey(NamedRoi roi) =>
        string.IsNullOrWhiteSpace(roi.Id) ? roi.Name : roi.Id;

    private static string GetRoiKey(CameraRuntime.RuntimeRoi roi) =>
        string.IsNullOrWhiteSpace(roi.Id) ? roi.Name : roi.Id;

    private static Point[] ToLocalPolygon(PointF[] polygon, Rectangle bounds) =>
        polygon.Select(point => new Point(
            Math.Clamp((int)Math.Round(point.X - bounds.X), 0, Math.Max(0, bounds.Width - 1)),
            Math.Clamp((int)Math.Round(point.Y - bounds.Y), 0, Math.Max(0, bounds.Height - 1))))
            .ToArray();

    private static Mat MaskOutsidePolygon(Mat source, Point[] polygon)
    {
        if (polygon.Length < 3) return source.Clone();

        var masked = new Mat(source.Rows, source.Cols, source.Depth, source.NumberOfChannels);
        masked.SetTo(new MCvScalar(0, 0, 0));
        using var mask = new Mat(source.Rows, source.Cols, DepthType.Cv8U, 1);
        mask.SetTo(new MCvScalar(0));
        using var contours = new VectorOfVectorOfPoint();
        using var points = new VectorOfPoint(polygon);
        contours.Push(points);
        CvInvoke.FillPoly(mask, contours, new MCvScalar(255));
        source.CopyTo(masked, mask);
        return masked;
    }
}
