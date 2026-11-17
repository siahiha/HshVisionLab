using System.Drawing;
using Emgu.CV;

namespace HshDetectionEngin;

/// <summary>Small, bounded color samples from the original frame; never from the OCR crop or a motion mask.</summary>
internal sealed class PlateVehicleColorAnalyzer : IDisposable
{
    internal sealed record Estimate(string? Color, float Score, string Reason);
    private readonly Func<Mat, Rectangle, VehicleColorSettings, Estimate>? _estimateOverride;
    private OnnxVehicleColorModel? _model;
    internal PlateVehicleColorAnalyzer(Func<Mat, Rectangle, VehicleColorSettings, Estimate>? estimateOverride = null) => _estimateOverride = estimateOverride;
    internal void Prepare() { lock (_gate) { if (_estimateOverride is null) _model ??= new OnnxVehicleColorModel(); } }
    public void Dispose() { lock (_gate) { _model?.Dispose(); _model = null; _tracks.Clear(); } }
    private Estimate EstimateRegion(Mat frame, Rectangle bounds, VehicleColorSettings settings) =>
        _estimateOverride is not null ? _estimateOverride(frame, bounds, settings) : (_model ??= new OnnxVehicleColorModel()).Predict(frame, bounds, settings);
    private sealed class State
    {
        public DateTime LastSeen;
        public Rectangle Bounds;
        public float Coverage;
        public int Attempts, Samples;
        public string Status = "Pending", Reason = "NotEvaluated";
        public string? Color;
        public float Score;
    }
    private readonly object _gate = new();
    private readonly Dictionary<int, State> _tracks = new();
    private DateTime _lastPrune;
    private Size _size;
    private long _generation;
    public long Generation { get { lock (_gate) return _generation; } }
    public void Reset() { lock (_gate) { _generation++; _tracks.Clear(); _lastPrune = default; } }

    public AnalysisDetection Apply(Mat frame, AnalysisDetection detection, VehicleColorSettings settings, DateTime now, long expectedGeneration)
    {
        if (!settings.Enabled || detection.Kind != AnalysisKind.Plate) return detection;
        lock (_gate)
        {
            if (expectedGeneration != _generation) return detection;
            if (_size != frame.Size) { _tracks.Clear(); _size = frame.Size; }
            if ((now - _lastPrune).TotalMilliseconds > 1000)
            {
                foreach (int key in _tracks.Where(p => (now - p.Value.LastSeen).TotalMilliseconds > settings.TrackTimeoutMs).Select(p => p.Key).ToArray())
                    _tracks.Remove(key);
                _lastPrune = now;
            }
            var metadata = detection.Metadata is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(detection.Metadata);
            Rectangle plate = metadata.GetValueOrDefault("PlateDetectorBounds") is Rectangle original ? original : detection.Bounds;
            var (requested, bounds, coverage) = SampleBounds(plate, frame.Size, settings);
            // Live geometry follows the current plate; the actual sampled geometry below
            // remains attached to the one frame that produced the cached result.
            metadata["VehicleColorDisplayBounds"] = new Dictionary<string, object?> { ["x"] = bounds.X, ["y"] = bounds.Y, ["width"] = bounds.Width, ["height"] = bounds.Height };
            metadata["VehicleColorMethod"] = "AbovePlatePretrainedModel";
            metadata["VehicleColorModel"] = OnnxVehicleColorModel.ModelId;
            metadata["VehicleColorFilter"] = settings.MedianFilterEnabled ? "Median3x3" : "None";
            metadata["VehicleColorDrawRegion"] = settings.DrawSampleRegion;
            State state;
            if (detection.TrackId is not int id)
                state = new State();
            else
            {
                if (!_tracks.TryGetValue(id, out state!) || now < state.LastSeen || (now - state.LastSeen).TotalMilliseconds > settings.TrackTimeoutMs)
                {
                    if (_tracks.Count >= 4096 && !_tracks.ContainsKey(id)) state = new State { Status = "Unknown", Reason = "TrackLimit" };
                    else _tracks[id] = state = new State();
                }
            }
            state.LastSeen = now;
            if (state.Status == "Pending")
            {
                state.Attempts = 1;
                state.Bounds = bounds; state.Coverage = coverage;
                Estimate result = requested.Width < 4 || requested.Height < 4 || bounds.Width < 4 || bounds.Height < 4 || coverage < settings.MinimumRegionCoverage
                    ? new(null, 0, "InsufficientRegion") : EstimateRegion(frame, bounds, settings);
                state.Color = result.Color; state.Score = result.Score; state.Reason = result.Reason;
                state.Samples = result.Color is null ? 0 : 1;
                state.Status = result.Color is null ? "Unknown" : "Accepted";
            }
            // Cached attributes retain their original sampling geometry, not a later frame's crop.
            metadata["VehicleColorSampleBounds"] = new Dictionary<string, object?> { ["x"] = state.Bounds.X, ["y"] = state.Bounds.Y, ["width"] = state.Bounds.Width, ["height"] = state.Bounds.Height };
            metadata["VehicleColorRegionCoverage"] = state.Coverage;
            metadata["VehicleColor"] = state.Color ?? "Unknown";
            metadata["VehicleColorStatus"] = state.Status;
            metadata["VehicleColorScore"] = state.Score;
            metadata["VehicleColorSamples"] = state.Samples;
            metadata["VehicleColorAttempts"] = state.Attempts;
            metadata["VehicleColorReason"] = state.Reason;
            metadata.Remove("VehicleColorCandidate");
            metadata.Remove("VehicleColorCandidateScore");
            return detection with { Metadata = metadata };
        }
    }

    internal static (Rectangle Requested, Rectangle Clipped, float Coverage) SampleBounds(Rectangle plate, Size size, VehicleColorSettings settings)
    {
        if (plate.Width <= 0 || plate.Height <= 0) return (Rectangle.Empty, Rectangle.Empty, 0);
        int width = Math.Max(1, (int)Math.Round(plate.Width * settings.WidthMultiplier));
        int height = Math.Max(1, (int)Math.Round(plate.Height * settings.HeightMultiplier));
        int gap = (int)Math.Round(plate.Height * settings.GapHeightMultiplier);
        var requested = new Rectangle(plate.X + (plate.Width - width) / 2, plate.Y - gap - height, width, height);
        var clipped = Rectangle.Intersect(requested, new Rectangle(Point.Empty, size));
        float coverage = clipped.IsEmpty ? 0 : (float)((double)clipped.Width * clipped.Height / ((double)width * height));
        return (requested, clipped, coverage);
    }

}
