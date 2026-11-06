using HshDetectionEngin;

internal static partial class Program
{
    private static AnalysisDetection MotionDetection(int x, int y, int? track = 1, AnalysisKind kind = AnalysisKind.Plate)
        => new(kind, "Test", .9f, new Rectangle(x - 5, y - 5, 10, 10), track);

    private static void MotionDirections()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var size = new Size(100, 100);
        var settings = new MotionDirectionSettings();
        settings.Validate();
        foreach (var (dx, dy, expected) in new[] { (10, 0, 1), (10, 10, 2), (0, 10, 3), (-10, 10, 4),
            (-10, 0, 5), (-10, -10, 6), (0, -10, 7), (10, -10, 8), (0, 0, 0) })
        {
            var analyzer = new TrackMotionAnalyzer();
            for (int i = 0; i < 3; i++)
            {
                var result = analyzer.Apply([MotionDetection(50 + i * dx, 50 + i * dy)], settings, size, now.AddMilliseconds(i * 100)).Single();
                if (i < 2) Check(result.Metadata!["MotionDirection"] is null, "Unobserved direction was reported as stationary.");
                else Check((int)result.Metadata!["MotionDirection"]! == expected, "Incorrect compass direction.");
            }
        }
        var jitter = new TrackMotionAnalyzer();
        jitter.Apply([MotionDetection(50, 50)], settings, size, now);
        jitter.Apply([MotionDetection(51, 49)], settings, size, now.AddMilliseconds(100));
        var stationary = jitter.Apply([MotionDetection(49, 51)], settings, size, now.AddMilliseconds(200)).Single();
        Check((int)stationary.Metadata!["MotionDirection"]! == 0 && (float)stationary.Metadata["MotionDx"]! == 0 &&
            (float)stationary.Metadata["MotionDy"]! == 0 && stationary.Metadata["MotionAngleDegrees"] is null, "Jitter did not produce a zero vector.");
        jitter.Apply([MotionDetection(70, 50)], settings, size, now.AddMilliseconds(300));
        var stopped = jitter.Apply([MotionDetection(70, 50)], settings, size, now.AddMilliseconds(1000)).Single();
        Check((int)stopped.Metadata!["MotionDirection"]! == 0, "Old movement persisted after stopping.");
        var gap = jitter.Apply([MotionDetection(80, 50)], settings, size, now.AddMilliseconds(4000)).Single();
        Check(gap.Metadata!["MotionDirection"] is null, "Reappearing track retained old motion.");
        long oldGeneration = jitter.Generation;
        jitter.Reset();
        Check(jitter.Apply([MotionDetection(1, 1)], settings, size, now.AddMilliseconds(4050), oldGeneration).Single().Metadata is null,
            "An observation from before reset was accepted.");
        Check(jitter.Apply([MotionDetection(90, 50)], settings, size, now.AddMilliseconds(4100)).Single().Metadata!["MotionDirection"] is null,
            "Reset retained old motion.");
        Check(jitter.Apply([MotionDetection(50, 50, null)], settings, size, now).Single().Metadata!["MotionDirection"] is null, "Missing track ID became stationary.");
        settings.Enabled = false;
        Check(jitter.Apply([MotionDetection(50, 50)], settings, size, now).Single().Metadata is null, "Disabled analysis still produced metadata.");
        settings.Enabled = true;
        // Low detector FPS must still reach the minimum observation count.
        var slow = new TrackMotionAnalyzer();
        slow.Apply([MotionDetection(20, 20)], settings, size, now);
        slow.Apply([MotionDetection(30, 20)], settings, size, now.AddMilliseconds(1000));
        Check((int)slow.Apply([MotionDetection(40, 20)], settings, size, now.AddMilliseconds(2000)).Single().Metadata!["MotionDirection"]! == 1,
            "Low FPS tracks never acquired a direction.");

        settings.LineEnabled = true;
        var line = new TrackMotionAnalyzer();
        AnalysisDetection At(int x, int y, int ms) => line.Apply([MotionDetection(x, y)], settings, size, now.AddMilliseconds(ms)).Single();
        At(50, 30, 0); At(50, 49, 100); At(50, 51, 200);
        var entry = At(50, 60, 300);
        Check((string)entry.Metadata!["LineCrossing"]! == "Entry" && (int)entry.Metadata["LineCrossingCount"]! == 1, "Entry crossing was missed.");
        At(50, 51, 400); At(50, 49, 500);
        Check((int)At(50, 60, 600).Metadata!["LineCrossingCount"]! == 1, "Line jitter duplicated an event.");
        Check((string)At(50, 30, 700).Metadata!["LineCrossing"]! == "Exit", "Reverse crossing was missed.");
        line.Reset(); At(95, 30, 800);
        Check((string)At(95, 70, 900).Metadata!["LineCrossing"]! == "None", "Crossing outside the line segment was counted.");
        line.Reset(); settings.PositiveCrossingIsEntry = false; At(50, 30, 1000);
        Check((string)At(50, 70, 1100).Metadata!["LineCrossing"]! == "Exit", "Entry direction setting was ignored.");
    }

    private static void MotionPersistence()
    {
        var settings = Settings();
        var task = settings.Rois[0].Processing[0];
        task.MotionDirection.MinimumDisplacementPixels = 7;
        task.MotionDirection.LineEnabled = true;
        task.MotionDirection.PositiveCrossingIsEntry = false;
        var cloned = task.Clone(); cloned.MotionDirection.MinimumDisplacementPixels = 9;
        Check(task.MotionDirection.MinimumDisplacementPixels == 7, "Task clone shares motion settings.");
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<CameraSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        Check(reloaded.Rois[0].Processing[0].MotionDirection.LineEnabled &&
            !reloaded.Rois[0].Processing[0].MotionDirection.PositiveCrossingIsEntry, "Motion configuration did not round-trip.");
        bool rejected = false;
        try { new MotionDirectionSettings { LineEnabled = true, LineEndX = .1f }.Validate(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Degenerate crossing line was accepted.");
    }

    private sealed class MovingPipeline(AnalysisKind kind) : IProcessingPipeline
    {
        private int _step;
        public string Name => "Moving test";
        public PipelineResult Process(ProcessingContext context) => new() { Detections = [MotionDetection(30 + 10 * _step++, 30, 1, kind)] };
        public void Dispose() { }
    }

    private static void MotionIntegration()
    {
        foreach (var mode in new[] { RoiProcessingModes.Sequential, RoiProcessingModes.Parallel })
        {
            var settings = Settings();
            var roi = settings.Rois[0]; roi.ProcessingMode = mode;
            roi.Processing.Add(new CameraProcessingSettings { Type = "Palm" });
            roi.Processing.Add(new CameraProcessingSettings { Type = "Object" });
            foreach (var task in roi.Processing) { task.MotionDirection.MinimumObservationMs = 0; task.MotionDirection.LineEnabled = true; }
            var registry = new ProcessingRegistry();
            foreach (var (type, kind) in new[] { (ProcessingType.Face, AnalysisKind.Face), (ProcessingType.Plate, AnalysisKind.Plate),
                (ProcessingType.Palm, AnalysisKind.Palm), (ProcessingType.Parse("Object"), AnalysisKind.Object) })
                registry.Register(new ProcessingModuleRegistration(type, type.ToString(), kind, (_, _) => [new MovingPipeline(kind)]));
            var errors = new List<string>();
            using var coordinator = new CameraPipelineCoordinator(settings, registry, (s, _) => errors.Add(s), _ => { });
            coordinator.Rebuild();
            using var frame = Frame(10, 128);
            var runtimeRoi = new CameraRuntime.RuntimeRoi(roi.Id, roi.Name,
                [new(10, 10), new(110, 10), new(110, 110), new(10, 110)], new Rectangle(10, 10, 100, 100), true, mode);
            coordinator.Run(frame, [runtimeRoi]); coordinator.Run(frame, [runtimeRoi]);
            var output = coordinator.Run(frame, [runtimeRoi]);
            Check(errors.Count == 0, string.Join(";", errors));
            Check(output.Detections.Count == 4 && output.Detections.All(d => (int)d.Metadata!["MotionDirection"]! == 1),
                "Shared direction was not attached to every modality.");
            Check(output.Detections.All(d => d.Bounds.X == 55), "Motion changed ROI-to-frame coordinates.");
            Check(output.Overlays.Any(o => o.Color == Color.Cyan), "Crossing line was not drawn.");
            coordinator.ResetMotion();
            Check(coordinator.Run(frame, [runtimeRoi]).Detections.All(d => d.Metadata!["MotionDirection"] is null), "Coordinator did not reset motion.");
        }
        using var fixture = new Fixture();
        fixture.Plate.Release.Set();
        using var sample = Frame();
        var tracked = Detection(AnalysisKind.Plate, 1) with { Metadata = new Dictionary<string, object?>(Detection(AnalysisKind.Plate, 1).Metadata!)
            { ["MotionDirection"] = 0, ["MotionState"] = "Stationary", ["MotionDx"] = 0f, ["MotionDy"] = 0f } };
        fixture.Offer(sample, tracked);
        Until(() => fixture.Results.Count == 1);
        Check((int)fixture.Results.Single().Detection.Metadata!["MotionDirection"]! == 0, "Completion lost the sampled motion metadata.");
    }
}
