namespace HshDetectionEngin.Plate;

/// <summary>
/// Multi-object tracker for license-plate boxes. Since the current model does
/// not expose a vehicle class, each detected plate is treated as the vehicle's
/// visual anchor. Tracks are matched by IoU + center distance and extrapolate
/// their last velocity between detector passes.
/// </summary>
internal sealed class PlateTracker
{
    private sealed class Track
    {
        public int Id;
        public PlateDetection Detection = null!;
        public float Vx;
        public float Vy;
        public int Misses;
        public int Age;
    }

    private readonly List<Track> _tracks = new();
    private int _nextId = 1;

    public IReadOnlyList<PlateDetection> Current { get; private set; } = Array.Empty<PlateDetection>();
    public IReadOnlyList<(int Id, PlateDetection Detection)> CurrentTracks
        => _tracks.Select(t => (t.Id, t.Detection)).ToArray();

    public void Update(IReadOnlyList<PlateDetection> detections, float minIoU = 0.08f, float maxCenterDistance = 0.55f, int maxMisses = 5)
    {
        var plates = detections.Where(d => PersianPlate.IsPlate(d.ClassId)).ToList();
        var usedTracks = new bool[_tracks.Count];
        var usedDetections = new bool[plates.Count];

        // Greedy highest-quality matching. The number of plates in a camera
        // frame is normally small, so this is considerably cheaper than a full
        // assignment algorithm and avoids extra allocations.
        var candidates = new List<(int ti, int di, float score)>(_tracks.Count * Math.Max(1, plates.Count));
        for (int ti = 0; ti < _tracks.Count; ti++)
        {
            var predicted = Predict(_tracks[ti].Detection, _tracks[ti].Vx, _tracks[ti].Vy);
            for (int di = 0; di < plates.Count; di++)
            {
                var d = plates[di];
                float iou = IoU(predicted, d);
                float dist = CenterDistance(predicted, d);
                float diag = MathF.Sqrt(predicted.Width * predicted.Width + predicted.Height * predicted.Height);
                float normalized = diag <= 1 ? float.MaxValue : dist / diag;
                if (iou >= minIoU || normalized <= maxCenterDistance)
                {
                    float score = iou * 2f + MathF.Max(0, 1f - normalized);
                    candidates.Add((ti, di, score));
                }
            }
        }
        candidates.Sort((a, b) => b.score.CompareTo(a.score));

        foreach (var c in candidates)
        {
            if (usedTracks[c.ti] || usedDetections[c.di]) continue;
            usedTracks[c.ti] = true;
            usedDetections[c.di] = true;

            var t = _tracks[c.ti];
            var old = t.Detection;
            var d = plates[c.di];
            t.Vx = (d.X + d.Width * .5f) - (old.X + old.Width * .5f);
            t.Vy = (d.Y + d.Height * .5f) - (old.Y + old.Height * .5f);
            t.Detection = d;
            t.Misses = 0;
            t.Age++;
        }

        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            if (usedTracks[i]) continue;
            var t = _tracks[i];
            t.Detection = Predict(t.Detection, t.Vx, t.Vy);
            t.Vx *= 0.92f;
            t.Vy *= 0.92f;
            t.Misses++;
            if (t.Misses > Math.Max(1, maxMisses)) _tracks.RemoveAt(i);
        }

        for (int di = 0; di < plates.Count; di++)
        {
            if (usedDetections[di]) continue;
            _tracks.Add(new Track
            {
                Id = _nextId++,
                Detection = plates[di],
                Age = 1,
                Misses = 0
            });
        }

        Current = _tracks.Select(t => t.Detection).ToArray();
    }

    public void Clear()
    {
        _tracks.Clear();
        Current = Array.Empty<PlateDetection>();
        _nextId = 1;
    }

    private static PlateDetection Predict(PlateDetection d, float vx, float vy) =>
        d with { X = d.X + vx, Y = d.Y + vy };

    private static float CenterDistance(PlateDetection a, PlateDetection b)
    {
        float ax = a.X + a.Width * .5f, ay = a.Y + a.Height * .5f;
        float bx = b.X + b.Width * .5f, by = b.Y + b.Height * .5f;
        return MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
    }

    private static float IoU(PlateDetection a, PlateDetection b)
    {
        float ax2 = a.X + a.Width, ay2 = a.Y + a.Height;
        float bx2 = b.X + b.Width, by2 = b.Y + b.Height;
        float ix1 = Math.Max(a.X, b.X), iy1 = Math.Max(a.Y, b.Y);
        float ix2 = Math.Min(ax2, bx2), iy2 = Math.Min(ay2, by2);
        float iw = Math.Max(0, ix2 - ix1), ih = Math.Max(0, iy2 - iy1);
        float inter = iw * ih;
        float union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }
}

