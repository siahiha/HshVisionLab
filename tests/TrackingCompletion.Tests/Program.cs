using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using HshDetectionEngin;
using HshVisionLab;
using HshDetectionEngin.Face;
using HshDetectionEngin.Plate;
using HshDetectionEngin.Licensing;

internal static partial class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Run("Independent capacity, replacement, fairness, snapshot and completion cache", CapacityAndCache);
        Run("Generation invalidates in-flight results", Generation);
        Run("Expired queued and in-flight samples never publish", Expiry);
        Run("Memory exhausted by in-flight sample rejects input safely", MemoryLimit);
        Run("Retry attempts are bounded", Retry);
        Run("Configuration validation and backwards-compatible persistence", Configuration);
        Run("Windows settings controls persist routing in English and Persian", WindowsSettingsBothLanguages);
        Run("Camera recognitions remain listed after saving, reopening and editing", RecognitionRoundTrip);
        Run("Camera runtime emits only completed results with their source frame", RuntimeIntegration);
        Run("Shared motion directions, stationary zero, jitter, gaps and crossing lines", MotionDirections);
        Run("Motion settings persist and clone independently", MotionPersistence);
        Run("Motion metadata passes through sequential/parallel tasks and completion", MotionIntegration);
        if (args.Contains("--models")) Run("Protected Face and Plate recognizer inference", ModelIntegration);
        Console.WriteLine("All tracked-completion checks passed.");
    }
    private static void Run(string name, Action test) { test(); Console.WriteLine($"PASS {name}"); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.ElapsedMilliseconds > 5000) throw new TimeoutException("Test condition did not complete."); Thread.Sleep(10); }
    }
    private static Mat Frame(byte pixel = 10, int size = 8)
    {
        var frame = new Mat(size, size, DepthType.Cv8U, 3);
        frame.SetTo(new MCvScalar(pixel, pixel, pixel));
        return frame;
    }
    private static AnalysisDetection Detection(AnalysisKind kind, int track, float confidence = .9f) => new(kind, "Tracking", confidence,
        new Rectangle(2, 2, 4, 4), track, new Dictionary<string, object?>
        { ["RoiId"] = "roi", ["ProcessingItemId"] = kind.ToString(), ["RecognitionDefinitionId"] = kind.ToString(), ["TrackPending"] = true });
    private sealed class Fake : IRecognitionProcessor
    {
        public readonly ManualResetEventSlim Ready = new(false), Release = new(false);
        public int Calls;
        public int Finalized;
        public bool Reject;
        public AnalysisDetection? Complete(Mat crop, AnalysisDetection detection)
        {
            Interlocked.Increment(ref Calls);
            Ready.Set();
            if (!Release.Wait(5000)) throw new TimeoutException("Recognizer was not released by test.");
            return Reject ? null : detection with { Label = "Completed" };
        }
        public void Dispose() { Ready.Dispose(); Release.Dispose(); }
        public AnalysisDetection FinalizeResult(AnalysisDetection result) { Interlocked.Increment(ref Finalized); return result; }
    }
    private sealed class Fixture : IDisposable
    {
        public CameraSettings Camera { get; } = Settings();
        public Fake Face { get; } = new();
        public Fake Plate { get; } = new();
        public ConcurrentQueue<(AnalysisDetection Detection, byte Pixel)> Results { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();
        public TrackingCompletionWorker Worker { get; }
        public Fixture(Action<CameraSettings>? configure = null)
        {
            configure?.Invoke(Camera);
            var registry = new ProcessingRegistry();
            registry.Register(new ProcessingModuleRegistration(ProcessingType.Face, "Face", AnalysisKind.Face, (_, _) => [], recognizerFactory: _ => Face));
            registry.Register(new ProcessingModuleRegistration(ProcessingType.Plate, "Plate", AnalysisKind.Plate, (_, _) => [], recognizerFactory: _ => Plate));
            Worker = new TrackingCompletionWorker(Camera, registry, (frame, detection, _, _) => Results.Enqueue((detection, Marshal.ReadByte(frame.DataPointer))), Errors.Enqueue);
        }
        public void Offer(Mat frame, params AnalysisDetection[] detections) => Worker.Offer(frame, detections, DateTime.UtcNow, 42);
        public void Dispose()
        {
            Face.Release.Set(); Plate.Release.Set(); Worker.Dispose();
            Check(Errors.IsEmpty, string.Join("; ", Errors));
        }
    }
    private static CameraSettings Settings()
    {
        var camera = new CameraSettings();
        camera.RecognitionDefinitions = [new() { Id = "Face", Name = "Face recognition", Kind = AnalysisKind.Face }, new() { Id = "Plate", Name = "Plate recognition", Kind = AnalysisKind.Plate, ModelFile = "ocr_crnn.onnx" }];
        camera.Rois = [new() { Id = "roi", Processing = [new() { Name = "Face tracker", Type = "Face", Id = "Face", ExecutionMode = "TrackAndQueue", RecognitionDefinitionId = "Face" },
            new() { Name = "Plate tracker", Type = "Plate", Id = "Plate", ExecutionMode = "TrackAndQueue", RecognitionDefinitionId = "Plate" }] }];
        foreach (var settings in camera.TrackingCompletion.SampleTypes) { settings.Capacity = 2; settings.MinEnqueueIntervalMs = 0; settings.RetryDelayMs = 0; settings.MaxAttemptsPerTrack = 2; }
        return camera;
    }
    private static void CapacityAndCache()
    {
        using var fixture = new Fixture();
        using var frame = Frame();
        fixture.Offer(frame, Detection(AnalysisKind.Face, 1));
        Check(fixture.Face.Ready.Wait(3000), "First face did not start.");
        fixture.Offer(frame, Detection(AnalysisKind.Plate, 1), Detection(AnalysisKind.Plate, 2));
        fixture.Offer(frame, Detection(AnalysisKind.Face, 2), Detection(AnalysisKind.Face, 3), Detection(AnalysisKind.Face, 4));
        fixture.Offer(frame, Detection(AnalysisKind.Face, 3, .95f));
        var status = fixture.Worker.Status;
        Check(status.Single(s => s.Kind == AnalysisKind.Face) is { Pending: 2, Dropped: 1, Replaced: 1 }, "Face capacity/replacement was incorrect.");
        Check(status.Single(s => s.Kind == AnalysisKind.Plate).Pending == 2, "Face evicted Plate samples.");
        frame.SetTo(new MCvScalar(99, 99, 99));
        fixture.Plate.Release.Set(); fixture.Face.Release.Set();
        Until(() => fixture.Results.Count == 5 && fixture.Worker.Status.All(s => s.InFlight == 0));
        var results = fixture.Results.ToArray();
        Check(results[0].Pixel == 10 && results[0].Detection.Metadata!["SourceFrameSequence"] is 42L, "Completion used latest frame instead of original snapshot.");
        Check(results[1].Detection.Kind == AnalysisKind.Plate, "Types were not scheduled fairly.");
        int calls = fixture.Face.Calls;
        fixture.Offer(frame, Detection(AnalysisKind.Face, 1));
        Thread.Sleep(150);
        Check(fixture.Face.Calls == calls, "Completed track was recognized again.");
        Check(fixture.Worker.Status.All(s => s.MemoryBytes == 0), "Completed work leaked image memory.");
    }
    private static void Generation()
    {
        using var fixture = new Fixture(); using var frame = Frame();
        fixture.Offer(frame, Detection(AnalysisKind.Face, 1)); Check(fixture.Face.Ready.Wait(3000), "Recognition did not start.");
        fixture.Worker.Invalidate(); fixture.Face.Release.Set();
        Until(() => fixture.Worker.Status.All(s => s.InFlight == 0));
        Check(fixture.Results.IsEmpty, "Old generation published.");
        Check(fixture.Face.Finalized == 0, "Cancelled recognition changed identity state.");
        fixture.Worker.Offer(frame, [Detection(AnalysisKind.Face, 1)], DateTime.UtcNow, 42, expectedGeneration: 0);
        Thread.Sleep(100);
        Check(fixture.Face.Calls == 1, "A frame from an old capture generation was queued.");
        fixture.Offer(frame, Detection(AnalysisKind.Face, 1)); Until(() => fixture.Results.Count == 1);
        Check(fixture.Results.First().Detection.Metadata!["RuntimeGeneration"] is 1L, "Track cache leaked across generations.");
    }
    private static void Expiry()
    {
        using var fixture = new Fixture(c => c.TrackingCompletion.SampleTypes.ForEach(s => s.MaxSampleAgeMs = 80));
        using var frame = Frame(); fixture.Offer(frame, Detection(AnalysisKind.Face, 1));
        Check(fixture.Face.Ready.Wait(3000), "Recognition did not start.");
        fixture.Offer(frame, Detection(AnalysisKind.Plate, 1)); Thread.Sleep(150);
        fixture.Face.Release.Set(); fixture.Plate.Release.Set();
        Until(() => fixture.Worker.Status.All(s => s.InFlight == 0 && s.Pending == 0));
        Check(fixture.Results.IsEmpty && fixture.Worker.Status.Sum(s => s.Expired) == 2, "Expired samples were published or not counted.");
    }
    private static void MemoryLimit()
    {
        using var fixture = new Fixture(c => c.TrackingCompletion.SampleTypes.Single(s => s.Kind == AnalysisKind.Face).MaxMemoryBytes = 240);
        using var frame = Frame(); fixture.Offer(frame, Detection(AnalysisKind.Face, 1));
        Check(fixture.Face.Ready.Wait(3000), "Recognition did not start.");
        fixture.Offer(frame, Detection(AnalysisKind.Face, 2));
        Check(fixture.Worker.Status.Single(s => s.Kind == AnalysisKind.Face) is { Dropped: 1, Pending: 0 }, "In-flight memory was not reserved.");
    }
    private static void Retry()
    {
        using var fixture = new Fixture(); using var frame = Frame();
        fixture.Face.Reject = true; fixture.Face.Release.Set();
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            fixture.Offer(frame, Detection(AnalysisKind.Face, 1));
            Until(() => fixture.Face.Calls == attempt && fixture.Worker.Status.All(s => s.InFlight == 0));
        }
        fixture.Offer(frame, Detection(AnalysisKind.Face, 1)); Thread.Sleep(150);
        Check(fixture.Face.Calls == 2 && fixture.Results.IsEmpty, "Retry cap or acceptance gate failed.");
    }
    private static void Configuration()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<CameraSettings>("{\"Name\":\"Old\"}")!;
        old.EnsureProcessingDefaults(); TrackingCompletionSettings.Validate(old);
        var configured = Settings(); var clone = configured.Clone(); TrackingCompletionSettings.Validate(clone);
        Check(clone.Rois[0].Processing[0].RecognitionDefinitionId == "Face", "Destination was lost during cloning.");
        configured.RecognitionDefinitions[0].Enabled = false;
        bool rejected = false; try { TrackingCompletionSettings.Validate(configured); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Disabled destination was accepted.");
    }
    private static void WindowsSettingsBothLanguages()
    {
        var localization = typeof(CameraSettingsForm).Assembly.GetType("HshVisionLab.UiLocalization")!;
        var current = localization.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)!;
        var previous = current.GetValue(null)!;
        try
        {
            current.SetValue(null, Enum.Parse(previous.GetType(), "English")); WindowsSettings("");
            current.SetValue(null, Enum.Parse(previous.GetType(), "Persian")); WindowsSettings("-fa");
        }
        finally { current.SetValue(null, previous); }
    }
    private static void WindowsSettings(string suffix)
    {
        using var form = new CameraSettingsForm(Settings(), false);
        var task = form.Result.Rois[0].Processing[0];
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(CameraSettingsForm).GetMethod("SelectProcessingItem", flags)!.Invoke(form, [task]);
        var mode = (ComboBox)typeof(CameraSettingsForm).GetField("_completionMode", flags)!.GetValue(form)!;
        Check(mode.Items.Count == 2, "Recognition execution modes are missing.");
        mode.SelectedIndex = 1;
        var destination = (ComboBox)typeof(CameraSettingsForm).GetField("_completionDestination", flags)!.GetValue(form)!;
        Check(destination.Items.Count == 1, "Destination list was not filtered by modality.");
        destination.SelectedIndex = 0;
        typeof(CameraSettingsForm).GetMethod("CommitSelectedProperties", flags)!.Invoke(form, null);
        Check(task.ExecutionMode == "TrackAndQueue" && task.RecognitionDefinitionId == "Face", "Windows settings did not persist routing.");
        Directory.CreateDirectory("TestResults");
        // Render the test form off-screen without showing an interactive window.
        form.Opacity = 0; form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.Show(); Application.DoEvents(); form.PerformLayout();
        var recognitionList = (ListBox)typeof(CameraSettingsForm).GetField("_recognitionList", flags)!.GetValue(form)!;
        var generalPanel = Descendants(form).OfType<Panel>().First(p => p.AutoScroll && p.Contains(recognitionList));
        generalPanel.AutoScrollPosition = new Point(0, generalPanel.VerticalScroll.Maximum);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, form.ClientRectangle);
        bitmap.Save(Path.GetFullPath($"TestResults/tracked-completion-general{suffix}.png"));
        var tabs = Descendants(form).OfType<TabControl>().First();
        tabs.SelectedIndex = 1; form.PerformLayout();
        typeof(CameraSettingsForm).GetMethod("ConfigureProcessingLayout", flags)!.Invoke(form, null);
        form.DrawToBitmap(bitmap, form.ClientRectangle);
        bitmap.Save(Path.GetFullPath($"TestResults/tracked-completion-roi{suffix}.png"));
        using var motionEditor = (Form)typeof(CameraSettingsForm).GetMethod("CreateMotionEditor", flags)!.Invoke(form, [task])!;
        var displacement = Descendants(motionEditor).OfType<NumericUpDown>().Single(n => n.Name == "Minimum displacement (ROI pixels)");
        displacement.Value = 6;
        motionEditor.Opacity = 0; motionEditor.ShowInTaskbar = false; motionEditor.StartPosition = FormStartPosition.Manual;
        motionEditor.Location = new Point(-30000, -30000); motionEditor.Show(); Application.DoEvents(); motionEditor.PerformLayout();
        using var motionBitmap = new Bitmap(motionEditor.Width, motionEditor.Height);
        motionEditor.DrawToBitmap(motionBitmap, motionEditor.ClientRectangle);
        motionBitmap.Save(Path.GetFullPath($"TestResults/motion-settings{suffix}.png"));
        ((Button)motionEditor.AcceptButton!).PerformClick();
        Check(task.MotionDirection.MinimumDisplacementPixels == 6, "Windows motion editor did not persist its settings.");
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        { yield return child; foreach (Control nested in Descendants(child)) yield return nested; }
    }

    private static void RecognitionRoundTrip()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        ListBox List(CameraSettingsForm form) => (ListBox)typeof(CameraSettingsForm).GetField("_recognitionList", flags)!.GetValue(form)!;
        void Save(CameraSettingsForm form)
        {
            typeof(CameraSettingsForm).GetMethod("SaveAndClose", flags)!.Invoke(form, null);
            Check(form.DialogResult == DialogResult.OK, "Camera settings were not saved.");
        }
        CameraSettings Reload(CameraSettings camera)
        {
            // Use a dedicated file; never touch the application's settings.json.
            Directory.CreateDirectory("TestResults");
            string path = Path.GetFullPath("TestResults/recognition-roundtrip.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new AppSettings { Cameras = [camera] }));
            var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))!.Cameras.Single();
            loaded.EnsureProcessingDefaults();
            return loaded;
        }
        var source = Settings();
        source.RecognitionDefinitions.Clear();
        foreach (var task in source.Rois.SelectMany(r => r.Processing))
        { task.ExecutionMode = "Inline"; task.RecognitionDefinitionId = string.Empty; }
        CameraSettings saved;
        using (var add = new CameraSettingsForm(source, false))
        {
            Check(List(add).Items.Count == 0, "New camera unexpectedly has recognitions.");
            add.Result.RecognitionDefinitions.Add(new RecognitionDefinition { Id = "saved-face", Name = "Saved recognition" });
            Save(add);
            saved = Reload(add.Result);
        }
        using (var edit = new CameraSettingsForm(saved, false))
        {
            Check(List(edit).Items.Count == 1 && List(edit).SelectedItem!.ToString()!.Contains("Saved recognition"),
                "Saved recognition was not listed when editing the camera.");
            edit.Result.RecognitionDefinitions[0].Name = "Renamed recognition";
            Save(edit);
            saved = Reload(edit.Result);
        }
        using var reopened = new CameraSettingsForm(saved, false);
        Check(List(reopened).Items.Count == 1 && List(reopened).SelectedItem!.ToString()!.Contains("Renamed recognition"),
            "Edited recognition disappeared after reopening.");
        // Returning to General must recover the view from canonical settings too.
        reopened.Opacity = 0; reopened.ShowInTaskbar = false; reopened.StartPosition = FormStartPosition.Manual;
        reopened.Location = new Point(-30000, -30000);
        reopened.Show(); Application.DoEvents();
        var tabs = Descendants(reopened).OfType<TabControl>().First();
        tabs.SelectedIndex = 1; Application.DoEvents();
        List(reopened).Items.Clear();
        tabs.SelectedIndex = 0; Application.DoEvents();
        Check(List(reopened).Items.Count == 1 && List(reopened).SelectedItem is not null,
            "General tab did not reload recognition definitions.");
    }
    private sealed class TestFrameSource : IFrameSource
    {
        private readonly ConcurrentQueue<Mat> _frames = new();
        public event Action<string>? StateChanged;
        public event Action<Exception>? ErrorOccurred { add { } remove { } }
        public event Action<Mat>? FrameAvailable;
        public bool IsRunning { get; private set; }
        public int QueueCount => _frames.Count;
        public long CapturedFrames { get; private set; }
        public long DroppedFrames => 0;
        public string Source => "test";
        public int Dequeued;
        public void Configure(string source, string transport, int bufferCount, int reconnectDelayMs) { }
        public void Start() { IsRunning = true; StateChanged?.Invoke("Connected"); }
        public void Feed(Mat frame) { CapturedFrames++; FrameAvailable?.Invoke(frame); _frames.Enqueue(frame); }
        public bool TryDequeueFrame(out Mat frame, int timeoutMs, CancellationToken cancellationToken)
        {
            if (_frames.TryDequeue(out frame!)) { Interlocked.Increment(ref Dequeued); return true; }
            Thread.Sleep(10); return false;
        }
        public void Stop() { IsRunning = false; while (_frames.TryDequeue(out var frame)) frame.Dispose(); }
        public void Dispose() => Stop();
    }
    private sealed class TrackingPipeline : IProcessingPipeline
    {
        public string Name => "Test tracker";
        public PipelineResult Process(ProcessingContext context) => new() { Detections = [Detection(AnalysisKind.Face, 1),
            new AnalysisDetection(AnalysisKind.Face, "Rejected", .3f, new Rectangle(1, 1, 4, 4), null, new Dictionary<string, object?> { ["Accepted"] = false })] };
        public void Dispose() { }
    }
    private static void RuntimeIntegration()
    {
        var settings = Settings();
        settings.MotionGateEnabled = false;
        settings.ActiveDetectionFps = 60;
        settings.Rois[0].Points = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        settings.Rois[0].Processing.RemoveAt(1);
        var source = new TestFrameSource();
        var recognizer = new Fake();
        var registry = new ProcessingRegistry();
        registry.Register(new ProcessingModuleRegistration(ProcessingType.Face, "Face", AnalysisKind.Face,
            (_, _) => [new TrackingPipeline()], recognizerFactory: _ => recognizer));
        using var camera = new CameraRuntime(settings, source, registry, enableTrackingCompletion: true);
        int rawEvents = 0;
        var completed = new ConcurrentQueue<(int Pixel, long Sequence)>();
        camera.PipelineResultsReady += (_, _) => Interlocked.Increment(ref rawEvents);
        camera.AnalysisDetected += (_, item) => completed.Enqueue((item.SourceFrame!.GetPixel(0, 0).R, (long)item.Detection.Metadata!["SourceFrameSequence"]!));
        camera.Start();
        try
        {
            source.Feed(Frame(10, 128));
            Check(recognizer.Ready.Wait(3000), "Camera never queued its tracked sample.");
            source.Feed(Frame(99, 128));
            Until(() => source.Dequeued >= 2);
            recognizer.Release.Set();
            Until(() => completed.Count == 1);
            Check(completed.First() == (10, 1L), "Runtime event did not retain the original frame context.");
            Check(rawEvents == 0, "TrackUpdate escaped to the completed-results event.");
        }
        finally { recognizer.Release.Set(); camera.Stop(); }
    }
    private static void ModelIntegration()
    {
        var license = LicenseValidator.Load(Path.GetFullPath("license.hshlic"));
        Check(license.Allows(LicensedFeature.Face) && license.Allows(LicensedFeature.Plate), "A valid local Face/Plate license is needed for this integration check.");
        string root = Path.GetFullPath(Path.Combine("TestResults", "tracked-models", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        using var database = FaceDatabase.Load(Path.Combine(root, "identity-database.db"));
        var faceModule = new FaceModule(database, license).CreateRegistration();
        using var face = faceModule.CreateRecognizer(new RecognitionDefinition { Kind = AnalysisKind.Face });
        using var image = Frame(50, 112);
        var faceDetection = new AnalysisDetection(AnalysisKind.Face, "Tracking", .95f, new Rectangle(0, 0, 112, 112), 1,
            new Dictionary<string, object?> { ["TrackingLandmarks"] = new PointF[] { new(38.2946f, 51.6963f), new(73.5318f, 51.5014f), new(56.0252f, 71.7366f), new(41.5493f, 92.3655f), new(70.7299f, 92.2041f) } });
        var pending = face.Complete(image, faceDetection)!;
        Check(database.Identities.Count == 0, "Inference mutated identities before generation validation.");
        var result = face.FinalizeResult(pending);
        Check(result.Metadata!["IsUnknown"] is true && database.Identities.Count == 1, "Unknown face completion failed.");
        using var plate = PlateModule.CreateRegistration(license).CreateRecognizer(new RecognitionDefinition { Kind = AnalysisKind.Plate, ModelFile = "ocr_crnn.onnx", Confidence = .35f });
        using var plateImage = new Mat(32, 128, DepthType.Cv8U, 3); plateImage.SetTo(new MCvScalar(50, 50, 50));
        var plateResult = plate.Complete(plateImage, Detection(AnalysisKind.Plate, 1));
        Check(plateResult is null || plateResult.Metadata!["Accepted"] is true && (float)plateResult.Metadata["RecognitionConfidence"]! >= .35f,
            "OCR emitted a result below the acceptance threshold.");
    }
}
