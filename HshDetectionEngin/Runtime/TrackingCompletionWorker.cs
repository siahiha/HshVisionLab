using System.Diagnostics;
using System.Drawing;
using Emgu.CV;

namespace HshDetectionEngin;

/// <summary>Bounded per-kind camera buffer. Owns all queued images and recognizers.</summary>
internal sealed class TrackingCompletionWorker : IDisposable
{
    private sealed class TrackState
    {
        public int Observations, Attempts;
        public DateTime LastSeen, LastEnqueued, LastAttempt, CompletedAt;
        public bool InFlight;
        public AnalysisDetection? DisplayResult;
    }
    private sealed class Bucket
    {
        public required SampleBufferSettings Settings;
        public readonly LinkedList<Work> Pending = new();
        public readonly Dictionary<string, TrackState> Tracks = new();
        public long Memory, Replaced, Dropped, Expired, Retried, Completed;
        public int InFlight;
        public double LastMs;
    }
    private sealed class Work : IDisposable
    {
        public required string Key, DefinitionId, SampleId;
        public required AnalysisDetection Detection;
        public required SharedFrame Snapshot;
        public required Mat Crop;
        public Mat Frame => Snapshot.Frame;
        public required DateTime ObservedAt;
        public required long Sequence, Epoch, Bytes;
        public void Dispose() { Snapshot.Release(); Crop.Dispose(); }
    }
    private sealed class SharedFrame
    {
        private int _references = 1;
        public Mat Frame { get; }
        public SharedFrame(Mat frame) => Frame = frame;
        public SharedFrame Retain() { Interlocked.Increment(ref _references); return this; }
        public void Release() { if (Interlocked.Decrement(ref _references) == 0) Frame.Dispose(); }
    }
    private readonly object _gate = new();
    private readonly object _publishGate = new();
    private readonly AutoResetEvent _ready = new(false);
    private readonly Dictionary<AnalysisKind, Bucket> _buckets;
    private readonly Dictionary<string, IRecognitionProcessor> _recognizers = new();
    private readonly Dictionary<string, string> _definitionNames = new();
    private readonly Dictionary<string, long> _definitionRevisions = new();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private Work? _activeWork;
    private readonly Action<Mat, AnalysisDetection, DateTime, long> _publish;
    private readonly Action<string> _error;
    private readonly Thread _thread;
    private bool _stopped;
    private long _epoch;
    private int _nextKind;

    public TrackingCompletionWorker(CameraSettings camera, ProcessingRegistry registry,
        Action<Mat, AnalysisDetection, DateTime, long> publish, Action<string> error)
    {
        TrackingCompletionSettings.Validate(camera);
        _publish = publish;
        _error = error;
        _buckets = camera.TrackingCompletion.SampleTypes.ToDictionary(s => s.Kind, s => new Bucket { Settings = s });
        try
        {
            var destinations = camera.Rois.Where(r => r.Enabled).SelectMany(r => r.Processing)
                .Where(t => t.Enabled && t.ExecutionMode == "TrackAndQueue").Select(t => t.RecognitionDefinitionId).ToHashSet();
            foreach (var definition in camera.RecognitionDefinitions.Where(d => d.Enabled && destinations.Contains(d.Id)))
            {
                var type = definition.Kind == AnalysisKind.Face ? ProcessingType.Face : ProcessingType.Plate;
                if (!registry.TryGet(type, out var module) || module is null) throw new InvalidOperationException($"Recognizer module is not registered: {type}");
                _recognizers.Add(definition.Id, module.CreateRecognizer(definition));
                _definitionNames.Add(definition.Id, definition.Name);
                _definitionRevisions.Add(definition.Id, definition.Revision);
            }
        }
        catch { foreach (var recognizer in _recognizers.Values) recognizer.Dispose(); _ready.Dispose(); throw; }
        _thread = new Thread(Run) { IsBackground = true, Name = $"Completion-{camera.Name}", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public IReadOnlyList<SampleBufferStatus> Status
    {
        get { lock (_gate) return _buckets.Select(p => new SampleBufferStatus(p.Key, p.Value.Pending.Count,
            p.Value.Settings.Capacity, p.Value.InFlight, p.Value.Memory, p.Value.Replaced, p.Value.Dropped,
            p.Value.Expired, p.Value.Retried, p.Value.Completed, p.Value.LastMs)).ToArray(); }
    }
    public long Generation { get { lock (_gate) return _epoch; } }

    public IReadOnlyList<SampleBufferEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Values.SelectMany(b => b.Pending).Concat(_activeWork is null ? [] : new[] { _activeWork })
                    .Select(w => new SampleBufferEntry(w.Detection.Kind, w.Detection.TrackId,
                        w.Detection.Metadata?.GetValueOrDefault("RoiName")?.ToString() ?? string.Empty,
                        _definitionNames.GetValueOrDefault(w.DefinitionId, w.DefinitionId),
                        ReferenceEquals(w, _activeWork) ? "Processing" : "Pending",
                        Math.Max(0, (DateTime.UtcNow - w.ObservedAt).TotalMilliseconds),
                        _buckets[w.Detection.Kind].Tracks.TryGetValue(w.Key, out var state) ? state.Attempts : 0)).ToArray();
            }
        }
    }

    public void Offer(Mat frame, IReadOnlyList<AnalysisDetection> detections, DateTime observedAt, long sequence, long? expectedGeneration = null)
    {
        DateTime now = DateTime.UtcNow;
        SharedFrame? sharedFrame = null;
        try
        {
        lock (_gate)
        {
            if (_stopped || expectedGeneration.HasValue && expectedGeneration.Value != _epoch) return;
            foreach (var bucket in _buckets.Values)
            {
                Expire(bucket, now);
                foreach (string key in bucket.Tracks.Where(p => !p.Value.InFlight &&
                    (now - p.Value.LastSeen).TotalMilliseconds > Math.Max(60000, bucket.Settings.MaxSampleAgeMs * 2L) &&
                    !bucket.Pending.Any(w => w.Key == p.Key)).Select(p => p.Key).ToArray()) bucket.Tracks.Remove(key);
            }
            foreach (var detection in detections)
            {
                if (!_buckets.TryGetValue(detection.Kind, out var bucket) || detection.TrackId is null ||
                    detection.Metadata?.TryGetValue("RecognitionDefinitionId", out var destination) != true || destination is not string definitionId ||
                    !_recognizers.ContainsKey(definitionId)) continue;
                var metadata = new Dictionary<string, object?>(detection.Metadata);
                if (metadata.TryGetValue("Accepted", out var accepted) && accepted is false) continue;
                if (detection.Kind == AnalysisKind.Face && metadata.TryGetValue("FaceRecordConfidence", out var threshold) &&
                    detection.Confidence < Convert.ToSingle(threshold, System.Globalization.CultureInfo.InvariantCulture)) continue;
                string key = $"{_epoch}:{metadata.GetValueOrDefault("RoiId")}:{metadata.GetValueOrDefault("ProcessingItemId")}:{detection.Kind}:{detection.TrackId}:{definitionId}";
                if (!bucket.Tracks.TryGetValue(key, out var state))
                {
                    // Keep bookkeeping bounded too; never erase a live completion cache to make room.
                    if (bucket.Tracks.Count >= Math.Max(128, bucket.Settings.Capacity * 16)) { bucket.Dropped++; continue; }
                    bucket.Tracks[key] = state = new TrackState();
                }
                state.LastSeen = now;
                state.Observations++;
                var settings = bucket.Settings;
                if (state.CompletedAt != default)
                {
                    if (settings.RefreshIntervalMs == 0 || (now - state.CompletedAt).TotalMilliseconds < settings.RefreshIntervalMs) continue;
                    state.CompletedAt = default;
                    state.Attempts = 0;
                }
                if (state.Observations < settings.MinTrackObservations || state.Attempts >= settings.MaxAttemptsPerTrack ||
                    (now - state.LastEnqueued).TotalMilliseconds < settings.MinEnqueueIntervalMs ||
                    (now - state.LastAttempt).TotalMilliseconds < settings.RetryDelayMs) continue;
                Rectangle bounds = Rectangle.Intersect(detection.Bounds, new Rectangle(Point.Empty, frame.Size));
                if (bounds.Width < 1 || bounds.Height < 1 || (now - observedAt).TotalMilliseconds >= settings.MaxSampleAgeMs) continue;
                long bytes = (long)frame.Step * frame.Rows + (long)bounds.Width * bounds.Height * frame.ElementSize;
                if (bytes > settings.MaxMemoryBytes) { bucket.Dropped++; continue; }
                var existing = bucket.Pending.First;
                while (existing is not null && existing.Value.Key != key) existing = existing.Next;
                if (existing is not null && existing.Value.Detection.Confidence > detection.Confidence) continue;
                if (existing is null)
                {
                    while (bucket.Pending.Count >= settings.Capacity) Drop(bucket, bucket.Pending.First!, false);
                }
                while (bucket.Memory - (existing?.Value.Bytes ?? 0) + bytes > settings.MaxMemoryBytes)
                {
                    var victim = bucket.Pending.First;
                    if (victim is not null && victim == existing) victim = victim.Next;
                    if (victim is null) break;
                    Drop(bucket, victim, false);
                }
                if (bucket.Memory - (existing?.Value.Bytes ?? 0) + bytes > settings.MaxMemoryBytes)
                {
                    bucket.Dropped++;
                    continue;
                }
                if (metadata.TryGetValue("TrackingLandmarks", out object? points) && points is PointF[] landmarks)
                    metadata["TrackingLandmarks"] = landmarks.Select(p => new PointF(p.X - bounds.X, p.Y - bounds.Y)).ToArray();
                Mat? crop = null;
                try
                {
                    sharedFrame ??= new SharedFrame(frame.Clone());
                    using var view = new Mat(frame, bounds);
                    crop = view.Clone();
                    var work = new Work { Key = key, DefinitionId = definitionId, SampleId = Guid.NewGuid().ToString("N"),
                        Detection = detection with { Bounds = bounds, Metadata = metadata }, Snapshot = sharedFrame.Retain(), Crop = crop,
                        ObservedAt = observedAt, Sequence = sequence, Epoch = _epoch, Bytes = bytes };
                    if (existing is not null)
                    {
                        bucket.Memory -= existing.Value.Bytes;
                        existing.Value.Dispose();
                        bucket.Replaced++;
                        existing.Value = work;
                    }
                    else bucket.Pending.AddLast(work);
                    bucket.Memory += bytes;
                    state.LastEnqueued = now;
                    crop = null;
                }
                finally { crop?.Dispose(); }
            }
        }
        _ready.Set();
        }
        finally { sharedFrame?.Release(); }
    }

    private static void Drop(Bucket bucket, LinkedListNode<Work> node, bool expired)
    {
        bucket.Pending.Remove(node);
        bucket.Memory -= node.Value.Bytes;
        node.Value.Dispose();
        if (expired) bucket.Expired++; else bucket.Dropped++;
    }

    public AnalysisDetection Display(AnalysisDetection detection)
    {
        lock (_gate)
        {
            if (!_buckets.TryGetValue(detection.Kind, out var bucket) || detection.Metadata is null) return detection;
            var m = detection.Metadata;
            string key = $"{_epoch}:{m.GetValueOrDefault("RoiId")}:{m.GetValueOrDefault("ProcessingItemId")}:{detection.Kind}:{detection.TrackId}:{m.GetValueOrDefault("RecognitionDefinitionId")}";
            if (!bucket.Tracks.TryGetValue(key, out var state) || state.CompletedAt == default || state.DisplayResult is null) return detection;
            var metadata = new Dictionary<string, object?>(m);
            metadata["TrackingCompleted"] = true;
            return detection with { Label = state.DisplayResult.Label, Metadata = metadata };
        }
    }
    private static void Expire(Bucket bucket, DateTime now)
    {
        for (var node = bucket.Pending.First; node is not null;)
        {
            var next = node.Next;
            if ((now - node.Value.ObservedAt).TotalMilliseconds >= bucket.Settings.MaxSampleAgeMs) Drop(bucket, node, true);
            node = next;
        }
    }

    private void Run()
    {
        while (true)
        {
            Work? work = null;
            Bucket? chosen = null;
            lock (_gate)
            {
                if (_stopped) return;
                var buckets = _buckets.Values.ToArray();
                for (int offset = 0; offset < buckets.Length; offset++)
                {
                    int index = (_nextKind + offset) % buckets.Length;
                    var bucket = buckets[index];
                    Expire(bucket, DateTime.UtcNow);
                    var node = bucket.Pending.First;
                    for (var candidate = node; candidate is not null;)
                    {
                        var next = candidate.Next;
                        if (bucket.Tracks[candidate.Value.Key].Attempts >= bucket.Settings.MaxAttemptsPerTrack) Drop(bucket, candidate, false);
                        candidate = next;
                    }
                    node = bucket.Pending.First;
                    while (node is not null && (DateTime.UtcNow - bucket.Tracks[node.Value.Key].LastAttempt).TotalMilliseconds < bucket.Settings.RetryDelayMs) node = node.Next;
                    if (node is null) continue;
                    work = node.Value;
                    _activeWork = work;
                    chosen = bucket;
                    bucket.Pending.Remove(node);
                    bucket.InFlight++;
                    var state = bucket.Tracks[work.Key];
                    state.InFlight = true;
                    if (state.Attempts++ > 0) bucket.Retried++;
                    state.LastAttempt = DateTime.UtcNow;
                    _nextKind = (index + 1) % buckets.Length;
                    break;
                }
            }
            if (work is null || chosen is null) { _ready.WaitOne(100); continue; }
            var timer = Stopwatch.StartNew();
            try
            {
                var result = _recognizers[work.DefinitionId].Complete(work.Crop, work.Detection);
                // Publication and epoch invalidation serialize; callbacks run without the queue lock.
                lock (_publishGate)
                {
                    bool eligible;
                    lock (_gate) eligible = !_stopped && work.Epoch == _epoch && result is not null &&
                        (DateTime.UtcNow - work.ObservedAt).TotalMilliseconds < chosen.Settings.MaxSampleAgeMs;
                    if (eligible) result = _recognizers[work.DefinitionId].FinalizeResult(result!);
                    bool publish = false;
                    lock (_gate)
                    {
                        bool current = !_stopped && work.Epoch == _epoch;
                        bool fresh = (DateTime.UtcNow - work.ObservedAt).TotalMilliseconds < chosen.Settings.MaxSampleAgeMs;
                        if (current && !fresh) chosen.Expired++;
                        if (current && fresh && result is not null)
                        {
                            var state = chosen.Tracks[work.Key];
                            state.CompletedAt = DateTime.UtcNow;
                            state.DisplayResult = result with { Metadata = null };
                            for (var node = chosen.Pending.First; node is not null;)
                            {
                                var next = node.Next;
                                if (node.Value.Key == work.Key) { chosen.Memory -= node.Value.Bytes; node.Value.Dispose(); chosen.Pending.Remove(node); }
                                node = next;
                            }
                            chosen.Completed++;
                            publish = true;
                        }
                    }
                    if (publish)
                    {
                        var metadata = result!.Metadata is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(result.Metadata);
                        metadata["TrackPending"] = false;
                        metadata["SampleId"] = work.SampleId;
                        metadata["SourceFrameSequence"] = work.Sequence;
                        metadata["SourceFrameCapturedAtUtc"] = work.ObservedAt;
                        metadata["CompletedAtUtc"] = DateTime.UtcNow;
                        metadata["RuntimeGeneration"] = work.Epoch;
                        metadata["RuntimeInstanceId"] = _instanceId;
                        metadata["RecognitionDefinitionId"] = work.DefinitionId;
                        metadata["RecognitionDefinitionName"] = _definitionNames[work.DefinitionId];
                        metadata["RecognitionDefinitionRevision"] = _definitionRevisions[work.DefinitionId];
                        _publish(work.Frame, result with { Metadata = metadata }, work.ObservedAt, work.Sequence);
                    }
                }
            }
            catch (Exception ex) { _error($"Tracked recognition: {ex.Message}"); }
            finally
            {
                lock (_gate)
                {
                    chosen.InFlight--;
                    _activeWork = null;
                    chosen.Memory -= work.Bytes;
                    chosen.LastMs = timer.Elapsed.TotalMilliseconds;
                    if (chosen.Tracks.TryGetValue(work.Key, out var state)) state.InFlight = false;
                }
                work.Dispose();
            }
        }
    }

    public void Invalidate()
    {
        lock (_publishGate)
        lock (_gate)
        {
            _epoch++;
            foreach (var bucket in _buckets.Values)
            {
                while (bucket.Pending.First is { } node) Drop(bucket, node, true);
                bucket.Tracks.Clear();
            }
        }
    }

    public void Dispose()
    {
        lock (_publishGate)
        lock (_gate) { _stopped = true; }
        _ready.Set();
        _thread.Join();
        Invalidate();
        foreach (var recognizer in _recognizers.Values) recognizer.Dispose();
        _ready.Dispose();
    }
}
