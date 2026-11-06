using System.Drawing;

namespace HshDetectionEngin;

/// <summary>One instance per pipeline binding: IDs from different tasks never share state.</summary>
internal sealed class TrackMotionAnalyzer
{
    private sealed class State
    {
        public readonly Queue<(DateTime Time, PointF Center)> Samples = new();
        public DateTime LastSeen;
        public int Observations;
        public PointF? StableLinePoint;
        public int StableSide;
        public string Crossing = "None";
        public DateTime? CrossingAt;
        public int Crossings;
    }
    private readonly object _gate = new();
    private readonly Dictionary<(AnalysisKind Kind, int Id), State> _tracks = new();
    private Size _size;
    private DateTime _lastPrune;
    private long _generation;

    public long Generation { get { lock (_gate) return _generation; } }
    public void Reset() { lock (_gate) { _generation++; _tracks.Clear(); _lastPrune = default; } }

    public IReadOnlyList<AnalysisDetection> Apply(IReadOnlyList<AnalysisDetection> detections,
        MotionDirectionSettings settings, Size size, DateTime now, long? expectedGeneration = null)
    {
        if (!settings.Enabled) return detections;
        lock (_gate)
        {
            if (expectedGeneration.HasValue && expectedGeneration != _generation) return detections;
            if (size != _size) { _tracks.Clear(); _size = size; }
            if ((now - _lastPrune).TotalMilliseconds >= 1000)
            {
                foreach (var key in _tracks.Where(p => (now - p.Value.LastSeen).TotalMilliseconds > settings.TrackTimeoutMs)
                    .Select(p => p.Key).ToArray()) _tracks.Remove(key);
                _lastPrune = now;
            }
            return detections.Select(d => ApplyOne(d, settings, size, now)).ToArray();
        }
    }

    private AnalysisDetection ApplyOne(AnalysisDetection detection, MotionDirectionSettings settings, Size size, DateTime now)
    {
        var metadata = detection.Metadata is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(detection.Metadata);
        // Unknown is deliberately different from a measured stationary object (direction 0).
        metadata["MotionState"] = "Unknown";
        metadata["MotionDirection"] = null;
        metadata["MotionDx"] = null;
        metadata["MotionDy"] = null;
        metadata["MotionAngleDegrees"] = null;
        if (detection.TrackId is not int id || detection.Bounds.Width <= 0 || detection.Bounds.Height <= 0)
            return detection with { Metadata = metadata };
        var key = (detection.Kind, id);
        if (!_tracks.TryGetValue(key, out var state) || now < state.LastSeen ||
            (now - state.LastSeen).TotalMilliseconds > settings.TrackTimeoutMs)
        {
            if (_tracks.Count >= 4096 && !_tracks.ContainsKey(key)) return detection with { Metadata = metadata };
            _tracks[key] = state = new State();
        }
        var center = new PointF(detection.Bounds.X + detection.Bounds.Width * .5f, detection.Bounds.Y + detection.Bounds.Height * .5f);
        // Some modules can emit several results for the same track in one pass.
        if (state.LastSeen != now)
        {
            state.LastSeen = now;
            state.Observations = Math.Min(100, state.Observations + 1);
            state.Samples.Enqueue((now, center));
            while (state.Samples.Count > 1 && (now - state.Samples.ElementAt(1).Time).TotalMilliseconds >= settings.WindowMs)
                state.Samples.Dequeue();
            while (state.Samples.Count > 256) state.Samples.Dequeue();
            if (settings.LineEnabled) UpdateCrossing(state, center, settings, size, now);
        }
        var first = state.Samples.Peek();
        if (state.Observations >= settings.MinimumObservations && (now - first.Time).TotalMilliseconds >= settings.MinimumObservationMs)
        {
            float dx = center.X - first.Center.X, dy = center.Y - first.Center.Y;
            bool stationary = dx * dx + dy * dy < settings.MinimumDisplacementPixels * settings.MinimumDisplacementPixels;
            double angle = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
            metadata["MotionState"] = stationary ? "Stationary" : "Moving";
            // 0 stationary; 1 right, 2 down-right, 3 down, ... 8 up-right.
            metadata["MotionDirection"] = stationary ? 0 : (int)Math.Floor((angle + 22.5) / 45) % 8 + 1;
            metadata["MotionDx"] = stationary ? 0f : dx;
            metadata["MotionDy"] = stationary ? 0f : dy;
            metadata["MotionAngleDegrees"] = stationary ? null : angle;
        }
        if (settings.LineEnabled)
        {
            metadata["LineCrossing"] = state.Crossing;
            metadata["LineCrossingAtUtc"] = state.CrossingAt;
            metadata["LineCrossingCount"] = state.Crossings;
        }
        return detection with { Metadata = metadata };
    }

    private static void UpdateCrossing(State state, PointF point, MotionDirectionSettings s, Size size, DateTime now)
    {
        var a = new PointF(s.LineStartX * size.Width, s.LineStartY * size.Height);
        var b = new PointF(s.LineEndX * size.Width, s.LineEndY * size.Height);
        float lx = b.X - a.X, ly = b.Y - a.Y;
        float length = MathF.Sqrt(lx * lx + ly * ly);
        if (length < .001f) return;
        float distance = (lx * (point.Y - a.Y) - ly * (point.X - a.X)) / length;
        int side = Math.Abs(distance) < s.MinimumDisplacementPixels ? 0 : Math.Sign(distance);
        if (side == 0) return; // Dead band prevents jitter on the line from becoming repeated crossings.
        if (state.StableSide != 0 && side != state.StableSide && state.StableLinePoint is PointF previous)
        {
            float previousDistance = (lx * (previous.Y - a.Y) - ly * (previous.X - a.X)) / length;
            float t = previousDistance / (previousDistance - distance);
            float ix = previous.X + t * (point.X - previous.X), iy = previous.Y + t * (point.Y - previous.Y);
            float along = ((ix - a.X) * lx + (iy - a.Y) * ly) / (length * length);
            if (along >= 0 && along <= 1)
            {
                state.Crossing = (side > 0) == s.PositiveCrossingIsEntry ? "Entry" : "Exit";
                state.CrossingAt = now;
                state.Crossings++;
            }
        }
        state.StableSide = side;
        state.StableLinePoint = point;
    }
}
