using System.Drawing;
using Emgu.CV;

namespace HshDetectionEngin;

/// <summary>Bounded whole-car enrichment on the existing source frame, independent of OCR acceptance.</summary>
internal sealed class PlateVehicleColorAnalyzer : IDisposable
{
    internal sealed record Estimate(string? Color, float Score, string Reason);
    private OnnxVehicleColorModel? _model;
    private OnnxVehicleDetector? _detector;
    private readonly object _gate = new();
    private readonly Dictionary<int, State> _tracks = new();
    private DateTime _lastPrune, _lastInference;
    private Size _size;
    private long _generation;
    private sealed class State
    {
        public DateTime LastSeen, SampledAt;
        public Rectangle Bounds;
        public int Attempts, Samples;
        public string Status = "Pending", Reason = "RateLimited";
        public string? Color;
        public float Score, VehicleScore;
    }
    public long Generation { get { lock (_gate) return _generation; } }
    public void Reset() { lock (_gate) { _generation++; _tracks.Clear(); _lastPrune = _lastInference = default; } }
    internal void Prepare(VehicleColorSettings settings)
    {
        lock (_gate)
        {
            try
            {
                _model ??= new OnnxVehicleColorModel(settings.ModelFile);
                _detector ??= new OnnxVehicleDetector(settings.VehicleDetectorModelFile);
            }
            catch { _model?.Dispose(); _model = null; _detector?.Dispose(); _detector = null; throw; }
        }
    }
    public void Dispose() { lock (_gate) { _model?.Dispose(); _model = null; _detector?.Dispose(); _detector = null; _tracks.Clear(); } }

    public IReadOnlyList<AnalysisDetection> Apply(Mat frame, IReadOnlyList<AnalysisDetection> detections,
        VehicleColorSettings settings, float plateThreshold, DateTime now, long expectedGeneration)
    {
        if (!settings.Enabled || !detections.Any(d => Eligible(d, plateThreshold))) return detections;
        lock (_gate)
        {
            if (expectedGeneration != _generation) return detections;
            if (_size != frame.Size) { _tracks.Clear(); _size = frame.Size; _lastInference = default; }
            if ((now - _lastPrune).TotalMilliseconds > 1000 || now < _lastPrune)
            {
                foreach (int key in _tracks.Where(p => now < p.Value.LastSeen ||
                    (now - p.Value.LastSeen).TotalMilliseconds > settings.TrackTimeoutMs).Select(p => p.Key).ToArray()) _tracks.Remove(key);
                _lastPrune = now;
            }
            bool evaluate = now < _lastInference || (now - _lastInference).TotalMilliseconds >= 1000d / settings.MaxFps;
            IReadOnlyList<OnnxVehicleDetector.Vehicle> vehicles = [];
            if (evaluate)
            {
                Prepare(settings);
                vehicles = _detector!.Detect(frame, settings);
                _lastInference = now;
            }
            var output = new List<AnalysisDetection>(detections.Count);
            // Reuse a color prediction when two plate results refer to the same car.
            var predictions = new Dictionary<Rectangle, Estimate>();
            foreach (var detection in detections)
            {
                if (!Eligible(detection, plateThreshold)) { output.Add(detection); continue; }
                var metadata = detection.Metadata is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(detection.Metadata);
                var plate = metadata.GetValueOrDefault("PlateDetectorBounds") is Rectangle original ? original : detection.Bounds;
                var state = new State();
                if (detection.TrackId is int id)
                {
                    if (!_tracks.TryGetValue(id, out state!))
                    {
                        state = new State();
                        if (_tracks.Count < 4096) _tracks.Add(id, state);
                    }
                }
                state.LastSeen = now;
                if (evaluate)
                {
                    state.Attempts++; state.SampledAt = now;
                    var vehicle = OnnxVehicleDetector.Match(plate, vehicles);
                    state.Bounds = vehicle?.Bounds ?? Rectangle.Empty;
                    state.VehicleScore = vehicle?.Score ?? 0;
                    Estimate result;
                    if (vehicle is null) result = new(null, 0, "VehicleNotFoundOrAmbiguous");
                    else if (!predictions.TryGetValue(vehicle.Bounds, out result!))
                    {
                        result = _model!.Predict(frame, vehicle.Bounds, settings);
                        predictions.Add(vehicle.Bounds, result);
                    }
                    state.Color = result.Color; state.Score = result.Score; state.Reason = result.Reason;
                    if (result.Color is not null) state.Samples++;
                    state.Status = result.Color is null ? "Unknown" : "Accepted";
                    // Draw only boxes localized on this frame; cached geometry can be stale.
                    if (vehicle is not null) metadata["VehicleColorDisplayBounds"] = Box(vehicle.Bounds);
                }
                metadata["VehicleColorMethod"] = "DetectedWholeVehicle";
                metadata["VehicleColorModel"] = settings.ModelFile;
                metadata["VehicleColorVehicleDetectorModel"] = settings.VehicleDetectorModelFile;
                metadata["VehicleDetectionScore"] = state.VehicleScore;
                metadata["VehicleColorFilter"] = settings.MedianFilterEnabled ? "Median3x3" : "None";
                metadata["VehicleColorDrawRegion"] = settings.DrawSampleRegion;
                metadata["VehicleColorSampleBounds"] = Box(state.Bounds);
                metadata["VehicleColorSampledAt"] = state.SampledAt == default ? null : state.SampledAt;
                metadata["VehicleColorCached"] = !evaluate;
                metadata["VehicleColor"] = state.Color ?? "Unknown";
                metadata["VehicleColorStatus"] = state.Status;
                metadata["VehicleColorScore"] = state.Score;
                metadata["VehicleColorSamples"] = state.Samples;
                metadata["VehicleColorAttempts"] = state.Attempts;
                metadata["VehicleColorReason"] = state.Reason;
                output.Add(detection with { Metadata = metadata });
            }
            return output;
        }
    }
    private static bool Eligible(AnalysisDetection detection, float threshold) => detection.Kind == AnalysisKind.Plate &&
        float.IsFinite(detection.Confidence) && detection.Confidence >= threshold;
    private static Dictionary<string, object?> Box(Rectangle bounds) => new()
        { ["x"] = bounds.X, ["y"] = bounds.Y, ["width"] = bounds.Width, ["height"] = bounds.Height };
}
