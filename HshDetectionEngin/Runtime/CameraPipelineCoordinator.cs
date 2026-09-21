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

    private readonly CameraSettings _settings;
    private readonly ProcessingRegistry _registry;
    private readonly Action<string, bool> _reportStatus;
    private readonly Action<double> _setInferenceMs;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<PipelineBinding>> _pipelines = new(StringComparer.OrdinalIgnoreCase);

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
        get
        {
            lock (_gate) return _pipelines.Values.Sum(pipelines => pipelines.Count);
        }
    }

    public void Rebuild()
    {
        var rebuilt = new Dictionary<string, List<PipelineBinding>>(StringComparer.OrdinalIgnoreCase);
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

            rebuilt[roiKey] = pipelines;
        }

        lock (_gate)
        {
            DisposePipelines(_pipelines.Values);
            _pipelines.Clear();
            foreach ((string name, List<PipelineBinding> pipelines) in rebuilt)
                _pipelines[name] = pipelines;
        }
    }

    public bool HasConfiguredPipelines()
    {
        lock (_gate)
        {
            return _pipelines.Values.Any(pipelines => pipelines.Count > 0);
        }
    }

    public PipelineExecutionResult Run(Mat frame, IReadOnlyList<CameraRuntime.RuntimeRoi> rois)
    {
        var execution = new PipelineExecutionResult();
        lock (_gate)
        {
            double maxPipelineMs = 0;
            if (_pipelines.Count == 0)
            {
                _setInferenceMs(0);
                return execution;
            }

            foreach (CameraRuntime.RuntimeRoi roi in rois)
            {
                if (roi.Polygon.Length < 3 || roi.Bounds.Width < 32 || roi.Bounds.Height < 32) continue;
                string roiKey = GetRoiKey(roi);
                if (!_pipelines.TryGetValue(roiKey, out List<PipelineBinding>? pipelines) &&
                    !_pipelines.TryGetValue(roi.Name, out pipelines)) continue;
                if (pipelines.Count == 0) continue;

                using var roiMat = new Mat(frame, roi.Bounds);
                using var masked = MaskOutsidePolygon(roiMat, ToLocalPolygon(roi.Polygon, roi.Bounds));
                Mat current = masked.Clone();
                var previous = new List<AnalysisDetection>();
                try
                {
                    foreach (PipelineBinding binding in pipelines.ToArray())
                    {
                        Stopwatch pipelineTimer = Stopwatch.StartNew();
                        try
                        {
                            using var result = binding.Pipeline.Process(new ProcessingContext
                            {
                                Image = current,
                                CameraId = _settings.Id,
                                CameraName = _settings.Name,
                                Timestamp = DateTime.UtcNow,
                                SourceBounds = roi.Bounds,
                                OriginalFrameSize = frame.Size,
                                PreviousDetections = previous.ToArray()
                            });

                            previous = result.Detections
                                .Select(detection => AttachProcessingSettings(detection, binding, roi.Id, roi.Name))
                                .ToList();
                            execution.Detections.AddRange(previous.Select(detection => detection with
                            {
                                Bounds = new Rectangle(
                                    detection.Bounds.X + roi.Bounds.X,
                                    detection.Bounds.Y + roi.Bounds.Y,
                                    detection.Bounds.Width,
                                    detection.Bounds.Height)
                            }));

                            foreach (ProcessingOverlay overlay in result.Overlays)
                                execution.Overlays.Add(MapOverlayToFrame(overlay, roi.Bounds));
                            execution.OverlaysUpdated |= result.OverlaysUpdated || result.Overlays.Count > 0;

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
                        finally
                        {
                            pipelineTimer.Stop();
                            maxPipelineMs = Math.Max(maxPipelineMs, pipelineTimer.Elapsed.TotalMilliseconds);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _reportStatus($"Pipeline execution failed: {ex.Message}", true);
                }
                finally
                {
                    current.Dispose();
                }
            }

            _setInferenceMs(maxPipelineMs);
        }

        return execution;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DisposePipelines(_pipelines.Values);
            _pipelines.Clear();
        }
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
