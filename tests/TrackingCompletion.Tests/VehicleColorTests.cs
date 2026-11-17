using System.Diagnostics;
using System.Reflection;
using Emgu.CV;
using Emgu.CV.Structure;
using HshDetectionEngin;
using HshVisionLab;

internal static partial class Program
{
    private static readonly Rectangle ColorPlate = new(50, 100, 40, 10);
    private static readonly Rectangle ColorRegion = new(50, 70, 40, 30);
    private static readonly Color BodyRed = Color.FromArgb(190, 35, 40);
    private static readonly Color BodyBlue = Color.FromArgb(35, 85, 170);
    private static void Paint(Mat frame, Rectangle bounds, Color color)
    { using var region = new Mat(frame, bounds); region.SetTo(new MCvScalar(color.B, color.G, color.R)); }
    private static AnalysisDetection ColorDetection() => Detection(AnalysisKind.Plate, 1) with { Bounds = ColorPlate };

    private static void VehicleColorGeometry()
    {
        var settings = new VehicleColorSettings(); settings.Validate();
        var geometry = PlateVehicleColorAnalyzer.SampleBounds(ColorPlate, new Size(128, 128), settings);
        Check(geometry.Requested == ColorRegion && geometry.Clipped == ColorRegion && geometry.Coverage == 1,
            "Body region is not exactly one plate width by three plate heights above the plate.");
        settings.WidthMultiplier = 2; settings.GapHeightMultiplier = .5f;
        Check(PlateVehicleColorAnalyzer.SampleBounds(ColorPlate, new Size(128, 128), settings).Requested == new Rectangle(30, 65, 80, 30),
            "Configurable body region offsets are incorrect.");
        settings = new();
        using var frame = Frame(10, 128); Paint(frame, ColorRegion, Color.FromArgb(120,70,30));
        var tensor = OnnxVehicleColorModel.BuildInput(frame, ColorRegion);
        Check(tensor[0,0,0,0] == 30 && tensor[0,1,0,0] == 70 && tensor[0,2,0,0] == 120,
            "Model input is not unnormalized BGR.");
        string[] names = ["White", "SilverGray", "Yellow", "Red", "Green", "Blue", "Black"];
        for (int i = 0; i < 7; i++)
        {
            float[] probabilities = new float[7]; probabilities[i] = 1;
            Check(OnnxVehicleColorModel.Decode(probabilities, settings).Color == names[i], "Model color order mismatch.");
        }
        Check(OnnxVehicleColorModel.Decode([.51f,.49f,0,0,0,0,0], settings).Color is null, "Low confidence accepted.");
        settings.ModelConfidence = .4f;
        Check(OnnxVehicleColorModel.Decode([.51f,.49f,0,0,0,0,0], settings).Reason == "AmbiguousColor", "Margin ignored.");
        Check(OnnxVehicleColorModel.Decode([float.NaN,0,0,0,0,0,0], settings).Color is null, "Nonfinite output accepted.");
        Check(OnnxVehicleColorModel.Decode([1,1,1,1,1,1,1], settings).Color is null, "Invalid probabilities accepted.");
        settings.ModelConfidence = .01f; settings.ModelMinimumMargin = 0;
        using var model = new OnnxVehicleColorModel();
        var first = model.Predict(frame, ColorRegion, settings);
        Check(first.Color is not null && first.Score > 0 && first.Score <= 1, "Real protected model did not run.");
        Check(model.Predict(frame, ColorRegion, settings) == first, "Model output is not deterministic.");
        // Reproduce the real model's high-confidence white prediction on mostly
        // dark input with a bright strip. The guard rejects, it does not invent black.
        Paint(frame, ColorRegion, Color.FromArgb(15,15,15));
        Paint(frame, new Rectangle(50, 91, 40, 9), Color.FromArgb(235,235,235));
        var inconsistent = model.Predict(frame, ColorRegion, settings);
        Check(inconsistent.Color is null && inconsistent.Reason == "WhiteContradictsDarkRegion",
            $"Mostly dark crop with a bright reflection was not rejected: {inconsistent}.");
        Paint(frame, ColorRegion, Color.FromArgb(230,230,230));
        Check(model.Predict(frame, ColorRegion, settings).Color == "White", "White bodywork was rejected by the dark-region guard.");
        Paint(frame, ColorRegion, Color.FromArgb(8,8,8));
        Check(model.Predict(frame, ColorRegion, settings).Color == "Black", "Very dark input lost its valid model prediction.");
        var forcedWhite = new PlateVehicleColorAnalyzer.Estimate("White", .99f, "PretrainedModel");
        Check(OnnxVehicleColorModel.CheckImageConsistency(forcedWhite, OnnxVehicleColorModel.BuildInput(frame, ColorRegion)).Color is null,
            "A contradictory prediction was accepted at a high confidence.");
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) model.Predict(frame, ColorRegion, settings);
        Console.WriteLine($"Pretrained model synthetic crop: {timer.Elapsed.TotalMilliseconds / 20:0.00} ms/crop. Not an accuracy benchmark.");
    }

    private static void ColorCropFilter()
    {
        using var frame = Frame(10, 128);
        var crop = new Rectangle(20, 20, 72, 72);
        Paint(frame, crop, Color.FromArgb(100,60,20));
        Paint(frame, new Rectangle(55,55,1,1), Color.White);
        var raw = OnnxVehicleColorModel.BuildInput(frame, crop, false);
        var filtered = OnnxVehicleColorModel.BuildInput(frame, crop, true);
        Check(raw[0,0,35,35] == 255 && filtered[0,0,35,35] == 20 &&
            filtered[0,1,35,35] == 60 && filtered[0,2,35,35] == 100,
            "Median filtering did not remove an isolated highlight or changed channel order.");
        Check(filtered[0,0,10,10] == raw[0,0,10,10] && filtered[0,2,10,10] == raw[0,2,10,10],
            "Filtering changed the flat body color.");
        var originalAfterFilter = OnnxVehicleColorModel.BuildInput(frame, crop, false);
        Check(originalAfterFilter[0,0,35,35] == 255,
            "Filtering mutated the source frame shared with OCR and other ROIs.");
    }

    private static void VehicleColorTracking()
    {
        using var frame = Frame(10, 128); Paint(frame, ColorRegion, BodyRed);
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var settings = new VehicleColorSettings { SampleIntervalMs = 0, MaximumAttempts = 4 };
        int calls = 0;
        using var analyzer = new PlateVehicleColorAnalyzer((_, _, _) => { calls++; return new(calls == 1 ? "Red" : "Blue", .9f, "TestModel"); });
        AnalysisDetection Observe(int ms, AnalysisDetection? detection = null) => analyzer.Apply(frame, detection ?? ColorDetection(), settings,
            now.AddMilliseconds(ms), analyzer.Generation);
        var initial = Observe(0);
        Check((string)initial.Metadata!["VehicleColorStatus"]! == "Accepted" && (string)initial.Metadata["VehicleColor"]! == "Red",
            "Single-frame dominant color did not complete immediately.");
        Paint(frame, ColorRegion, BodyBlue);
        Check((string)Observe(1).Metadata!["VehicleColor"]! == "Red" && (int)Observe(2).Metadata!["VehicleColorAttempts"]! == 1,
            "A later frame changed or resampled the same track.");
        Check((string)Observe(4000).Metadata!["VehicleColor"]! == "Blue", "Stale track retained cached color.");
        long old = analyzer.Generation; analyzer.Reset();
        Check(analyzer.Apply(frame, ColorDetection(), settings, now, old).Metadata?.ContainsKey("VehicleColorStatus") != true,
            "Old-generation color sample survived a reset.");
        var clipped = ColorDetection() with { Bounds = new Rectangle(50, 10, 40, 10) };
        Check((string)Observe(5000, clipped).Metadata!["VehicleColorStatus"]! == "Unknown", "Invalid region was not immediately unknown.");
        Check((string)Observe(5001).Metadata!["VehicleColorStatus"]! == "Unknown" && (int)Observe(5001).Metadata!["VehicleColorAttempts"]! == 1,
            "Unknown result retried on another frame.");
        Check((string)Observe(5003, ColorDetection() with { TrackId = null }).Metadata!["VehicleColor"]! == "Blue",
            "An untracked single-frame detection did not receive its own estimate.");
        settings.Enabled = false;
        Check(Observe(6000).Metadata?.ContainsKey("VehicleColorStatus") != true, "Disabled color sampling still ran.");
    }

    private sealed class ColorAnchorPipeline(Rectangle plate) : IProcessingPipeline
    {
        public int Calls;
        public Point OffsetPerCall;
        public string Name => "Body sample anchor";
        public PipelineResult Process(ProcessingContext context)
        {
            int call = Interlocked.Increment(ref Calls) - 1;
            var currentPlate = new Rectangle(plate.X + OffsetPerCall.X * call, plate.Y + OffsetPerCall.Y * call, plate.Width, plate.Height);
            context.Image.SetTo(new MCvScalar(0)); // Sampling must use the original source, not this modified input.
            var expanded = new Rectangle(currentPlate.X - 2, currentPlate.Y - 2, currentPlate.Width + 4, currentPlate.Height + 4);
            return new PipelineResult { Detections = [new AnalysisDetection(AnalysisKind.Plate, "12ب34567", .9f, expanded, 1,
                new Dictionary<string, object?> { ["Accepted"] = true, ["PlateText"] = "12ب34567", ["PlateDetectorBounds"] = currentPlate })] };
        }
        public void Dispose() { }
    }

    private static void MovingColorOverlay()
    {
        foreach (string mode in new[] { RoiProcessingModes.Sequential, RoiProcessingModes.Parallel })
        {
            var settings = Settings(); var roi = settings.Rois[0]; roi.ProcessingMode = mode;
            roi.Processing.RemoveAll(t => t.Kind != ProcessingType.Plate);
            var pipeline = new ColorAnchorPipeline(ColorPlate) { OffsetPerCall = new Point(8, 7) };
            var registry = new ProcessingRegistry();
            registry.Register(new ProcessingModuleRegistration(ProcessingType.Plate, "Plate", AnalysisKind.Plate, (_, _) => [pipeline]));
            using var coordinator = new CameraPipelineCoordinator(settings, registry, (_, _) => { }, _ => { }); coordinator.Rebuild();
            using var frame = Frame(10, 128); Paint(frame, ColorRegion, BodyRed);
            var runtimeRoi = new CameraRuntime.RuntimeRoi(roi.Id, roi.Name, [new(0,0),new(128,0),new(128,128),new(0,128)],
                new Rectangle(0,0,128,128), true, mode);
            var initial = coordinator.Run(frame, [runtimeRoi]).Detections.Single();
            var moved = coordinator.Run(frame, [runtimeRoi]); var detection = moved.Detections.Single();
            var currentBounds = new Rectangle(58, 77, 40, 30);
            Check(moved.Overlays.Any(o => o.Color == Color.DeepSkyBlue && o.Bounds == currentBounds),
                "Live sample guide stayed at the old track position.");
            Check(currentBounds.Bottom == ((Rectangle)detection.Metadata!["PlateDetectorBounds"]!).Top,
                "Default live color guide has a gap above the original plate.");
            var sampled = (Dictionary<string, object?>)detection.Metadata["VehicleColorSampleBounds"]!;
            Check((int)sampled["x"]! == ColorRegion.X && (int)sampled["y"]! == ColorRegion.Y &&
                (int)detection.Metadata["VehicleColorAttempts"]! == 1 &&
                Equals(initial.Metadata!["VehicleColor"], detection.Metadata["VehicleColor"]),
                "Moving the guide changed the sampled geometry, color, or repeated inference.");
        }
    }

    private static void VehicleColorIntegration()
    {
        foreach (string mode in new[] { RoiProcessingModes.Sequential, RoiProcessingModes.Parallel })
        foreach (string completion in new[] { "Inline", "TrackAndQueue" })
        {
            var settings = Settings(); var roi = settings.Rois[0]; roi.ProcessingMode = mode;
            roi.Processing.RemoveAll(t => t.Kind != ProcessingType.Plate);
            roi.Processing[0].ExecutionMode = completion;
            roi.Processing[0].VehicleColor.ModelConfidence = .01f; roi.Processing[0].VehicleColor.ModelMinimumMargin = 0;
            var registry = new ProcessingRegistry();
            registry.Register(new ProcessingModuleRegistration(ProcessingType.Plate, "Plate", AnalysisKind.Plate, (_, _) => [new ColorAnchorPipeline(new Rectangle(20, 10, 40, 10))]));
            var errors = new List<string>();
            using var coordinator = new CameraPipelineCoordinator(settings, registry, (s, _) => errors.Add(s), _ => { }); coordinator.Rebuild();
            using var frame = Frame(10, 128); Paint(frame, ColorRegion, BodyRed);
            var bounds = new Rectangle(30, 90, 80, 38);
            var runtimeRoi = new CameraRuntime.RuntimeRoi(roi.Id, roi.Name, [new(30,90),new(110,90),new(110,128),new(30,128)], bounds, true, mode);
            var first = coordinator.Run(frame, [runtimeRoi]).Detections.Single();
            coordinator.Run(frame, [runtimeRoi]);
            var output = coordinator.Run(frame, [runtimeRoi]); var colored = output.Detections.Single();
            Check(errors.Count == 0, string.Join(";", errors));
            using var model = new OnnxVehicleColorModel();
            string expected = model.Predict(frame, ColorRegion, roi.Processing[0].VehicleColor).Color!;
            Check((string)colored.Metadata!["VehicleColor"]! == expected, "Sampling used a masked/transformed image or wrong ROI coordinates.");
            Check((Rectangle)colored.Metadata["PlateDetectorBounds"]! == ColorPlate, "Original detector box was not mapped to the source frame.");
            Check(output.Overlays.Any(o => o.Bounds == ColorRegion), "Body sample rectangle is missing or includes OCR padding.");
            using var fixture = new Fixture(); fixture.Plate.Release.Set();
            fixture.Offer(frame, first);
            Until(() => fixture.Results.Count == 1);
            Check((string)fixture.Results.Single().Detection.Metadata!["VehicleColorStatus"]! == "Accepted",
                "First-frame color did not accompany OCR completion.");
            using var readyFixture = new Fixture(); readyFixture.Plate.Release.Set();
            readyFixture.Offer(frame, colored); Until(() => readyFixture.Results.Count == 1);
            Check((string)readyFixture.Results.Single().Detection.Metadata!["VehicleColor"]! == expected, "Completion lost available color metadata.");
        }
    }

    private static void VehicleColorPersistence()
    {
        var settings = Settings(); var task = settings.Rois[0].Processing[0];
        task.VehicleColor.HeightMultiplier = 4; var clone = task.Clone(); clone.VehicleColor.HeightMultiplier = 5;
        task.VehicleColor.MedianFilterEnabled = false; clone.VehicleColor.MedianFilterEnabled = true;
        Check(task.VehicleColor.HeightMultiplier == 4, "Cloned task shares color settings.");
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<CameraSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        Check(reloaded.Rois[0].Processing[0].VehicleColor.HeightMultiplier == 4, "Color settings were not persisted.");
        Check(!reloaded.Rois[0].Processing[0].VehicleColor.MedianFilterEnabled, "Color filter flag was not independently persisted.");
        bool rejected = false;
        try { new VehicleColorSettings { ModelConfidence = 2 }.Validate(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Invalid dominant area threshold was accepted.");
        var presentation = typeof(MainForm).Assembly.GetType("HshVisionLab.VehicleColorPresentation")!;
        var method = presentation.GetMethod("FromPayload", BindingFlags.Public | BindingFlags.Static)!;
        string payload = System.Text.Json.JsonSerializer.Serialize(new { metadata = new { VehicleColorStatus = "Accepted", VehicleColor = "Red", VehicleColorScore = .9f } });
        string text = (string)method.Invoke(null, [payload])!;
        Check(text.Length > 0, "Saved event does not display its color.");
        string association = System.Text.Json.JsonSerializer.Serialize(new { plate = payload });
        Check((string)method.Invoke(null, [association])! == text, "Associated plate lost its color details.");
        Check((string)method.Invoke(null, ["{}"])! == "", "Old events display invented colors.");
    }

    private static void VehicleColorRuntime()
    {
        foreach (string mode in new[] { "Inline", "TrackAndQueue" })
        foreach (string colorState in new[] { "Accepted", "Unknown", "Disabled" })
            VehicleColorRuntimeCase(mode, colorState);
    }

    private static void VehicleColorRuntimeCase(string mode, string colorState)
    {
        var settings = Settings(); settings.MotionGateEnabled = false; settings.ActiveDetectionFps = 60;
        var roi = settings.Rois[0]; roi.Points = [new(0,0),new(1,0),new(1,1),new(0,1)];
        roi.Processing.RemoveAll(t => t.Kind != ProcessingType.Plate);
        roi.Processing[0].ExecutionMode = mode; roi.Processing[0].VehicleColor.ModelConfidence = .01f; roi.Processing[0].VehicleColor.ModelMinimumMargin = 0;
        roi.Processing[0].VehicleColor.Enabled = colorState != "Disabled";
        if (colorState == "Unknown")
        { roi.Processing[0].VehicleColor.MinimumSamples = 1; roi.Processing[0].VehicleColor.MaximumAttempts = 1; }
        var source = new TestFrameSource();
        var pipeline = new ColorAnchorPipeline(colorState == "Unknown" ? new Rectangle(50, 10, 40, 10) : ColorPlate);
        var recognizer = new Fake(); recognizer.Release.Set();
        var registry = new ProcessingRegistry(); registry.Register(new ProcessingModuleRegistration(ProcessingType.Plate, "Plate", AnalysisKind.Plate,
            (_, _) => [pipeline], recognizerFactory: _ => recognizer));
        using var camera = new CameraRuntime(settings, source, registry, enableTrackingCompletion: true);
        var events = new System.Collections.Concurrent.ConcurrentQueue<AnalysisDetection>();
        camera.PlateDetected += (_, item) => events.Enqueue(item.Detection!);
        camera.Start();
        try
        {
            // Only one observed frame: neither color nor direction can require another frame to allow recognition.
            var frame = Frame(10, 128); Paint(frame, ColorRegion, BodyRed); source.Feed(frame);
            Until(() => events.Count == 1);
            var metadata = events.Single().Metadata!;
            Check(metadata["MotionDirection"] is null && (string)metadata["MotionState"]! == "Unknown",
                "First-frame unknown motion was changed to force recognition.");
            Check(colorState == "Disabled" ? !metadata.ContainsKey("VehicleColorStatus") : (string)metadata["VehicleColorStatus"]! == colorState,
                $"{mode} recognition did not preserve optional {colorState} color metadata.");
        }
        finally { camera.Stop(); if (mode == "Inline") recognizer.Dispose(); }
    }

    private static void InvalidSupplementSettings()
    {
        foreach (string mode in new[] { RoiProcessingModes.Sequential, RoiProcessingModes.Parallel })
        {
            var settings = Settings(); var roi = settings.Rois[0]; roi.ProcessingMode = mode;
            roi.Processing.RemoveAll(t => t.Kind != ProcessingType.Plate);
            roi.Processing[0].MotionDirection.MinimumObservations = 0;
            roi.Processing[0].VehicleColor.ModelConfidence = 99;
            var registry = new ProcessingRegistry(); registry.Register(new ProcessingModuleRegistration(ProcessingType.Plate, "Plate", AnalysisKind.Plate,
                (_, _) => [new ColorAnchorPipeline(ColorPlate)]));
            var errors = new List<string>();
            using var coordinator = new CameraPipelineCoordinator(settings, registry, (s, _) => errors.Add(s), _ => { });
            coordinator.Rebuild();
            using var frame = Frame(10, 128);
            var runtimeRoi = new CameraRuntime.RuntimeRoi(roi.Id, roi.Name, [new(0,0),new(128,0),new(128,128),new(0,128)],
                new Rectangle(0,0,128,128), true, mode);
            var detections = coordinator.Run(frame, [runtimeRoi]).Detections;
            Check(errors.Count == 2 && coordinator.ActivePipelineCount == 1 && detections.Count == 1 && detections[0].Metadata!["Accepted"] is true,
                "Invalid optional settings prevented the plate pipeline from running.");
        }
    }

    private static void MissingColorModel()
    {
        // Modify only the package copied into this isolated test executable's output.
        string path = Path.Combine(AppContext.BaseDirectory, "Models", "VehicleColor", OnnxVehicleColorModel.ModelId + ".hshmodel");
        string backup = path + ".test-backup-" + Guid.NewGuid().ToString("N");
        File.Move(path, backup);
        try
        {
            var settings = Settings(); var roi = settings.Rois[0];
            roi.Processing.RemoveAll(t => t.Kind != ProcessingType.Plate);
            var registry = new ProcessingRegistry();
            registry.Register(new ProcessingModuleRegistration(ProcessingType.Plate, "Plate", AnalysisKind.Plate,
                (_, _) => [new ColorAnchorPipeline(ColorPlate)]));
            var errors = new List<string>();
            using var coordinator = new CameraPipelineCoordinator(settings, registry, (s, _) => errors.Add(s), _ => { });
            coordinator.Rebuild();
            using var frame = Frame(10, 128);
            var runtimeRoi = new CameraRuntime.RuntimeRoi(roi.Id, roi.Name, [new(0,0),new(128,0),new(128,128),new(0,128)],
                new Rectangle(0,0,128,128), true, RoiProcessingModes.Sequential);
            for (int i = 0; i < 2; i++)
            {
                var result = coordinator.Run(frame, [runtimeRoi]).Detections.Single();
                Check(result.Metadata!["Accepted"] is true && (string)result.Metadata["VehicleColorStatus"]! == "Failed",
                    "Missing model blocked the plate or silently fell back to a color heuristic.");
            }
            Check(errors.Count == 1, "Missing model was not reported once at initialization.");
        }
        finally { File.Move(backup, path); }
    }
}
