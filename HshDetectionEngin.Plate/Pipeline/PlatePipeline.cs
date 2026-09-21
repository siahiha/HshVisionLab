using System.Diagnostics;
using System.Drawing;
using Emgu.CV;

namespace HshDetectionEngin.Plate;

/// <summary>Complete Iranian plate detection, OCR and tracking pipeline.</summary>
internal sealed class PlatePipeline : IProcessingPipeline
{
    private readonly PlateProcessingOptions _options;
    private readonly int _maxFps;
    private readonly YoloDetector _detector;
    private readonly PlateTracker _tracker = new();
    private long _nextProcessTicks;

    public string Name => "Iranian Plate Detection";
    public double LastInferenceMs => _detector.LastInferenceMs;

    public PlatePipeline(PlateProcessingOptions options, int maxFps, int threads)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _maxFps = maxFps;
        string modelPath = PlateModelPaths.Find(options.ModelFile)
            ?? throw new FileNotFoundException($"Plate model was not found: {options.ModelFile}");
        string? temporaryModel = null;
        try
        {
            bool encrypted = modelPath.EndsWith(".hshmodel", StringComparison.OrdinalIgnoreCase);
            temporaryModel = encrypted ? SecureModelLoader.Materialize(modelPath) : null;
            _detector = new YoloDetector(new YoloOptions
            {
                ModelPath = temporaryModel ?? modelPath,
                InputWidth = options.InputSize,
                InputHeight = options.InputSize,
                ConfThreshold = options.Confidence,
                NmsIoUThreshold = options.NmsIoU,
                IntraOpThreads = threads,
                AutoOptimizeModel = !encrypted
            });
        }
        finally
        {
            if (temporaryModel is not null) TryDelete(temporaryModel);
        }
    }

    public PipelineResult Process(ProcessingContext context)
    {
        if (context.Image.IsEmpty) return new PipelineResult();
        int fps = Math.Max(1, _maxFps);
        long now = Stopwatch.GetTimestamp();
        if (now < _nextProcessTicks) return new PipelineResult();
        _nextProcessTicks = now + Math.Max(1, Stopwatch.Frequency / fps);

        _detector.UpdateThresholds(_options.Confidence, _options.NmsIoU);
        List<PlateDetection> detections = Deduplicate(_detector.Detect(context.Image));
        _tracker.Update(detections, maxMisses: Math.Clamp(_options.TrackMaxMisses, 1, 60));

        List<PlateDetection> plates = detections.Where(d => PersianPlate.IsPlate(d.ClassId)).ToList();
        List<PlateDetection> characters = detections.Where(d => !PersianPlate.IsPlate(d.ClassId)).ToList();
        var results = new List<AnalysisDetection>(plates.Count);
        foreach (PlateDetection plate in plates)
        {
            Rectangle cropBounds = ExpandBounds(plate, context.Image.Size);
            if (cropBounds.Width < 12 || cropBounds.Height < 6) continue;
            List<PlateDetection> inside = ReadCharacters(context.Image, cropBounds, characters);
            string text = BuildPlateText(inside);
            bool accepted = plate.Score >= _options.Confidence && PersianPlate.IsValidIranianPlate(text);
            int? trackId = FindTrackId(plate);
            int characterOffsetX = context.SourceBounds.X;
            int characterOffsetY = context.SourceBounds.Y;
            var characterDetails = inside.Select((character, index) => new Dictionary<string, object?>
            {
                ["index"] = index,
                ["classId"] = character.ClassId,
                ["symbol"] = PersianPlate.CharOf(character.ClassId),
                ["confidence"] = character.Score,
                ["bounds"] = new Dictionary<string, object?>
                {
                    ["x"] = character.X + characterOffsetX,
                    ["y"] = character.Y + characterOffsetY,
                    ["width"] = character.Width,
                    ["height"] = character.Height
                }
            }).ToList();
            results.Add(new AnalysisDetection(
                AnalysisKind.Plate,
                string.IsNullOrWhiteSpace(text) ? "Plate" : text,
                plate.Score,
                cropBounds,
                trackId,
                new Dictionary<string, object?>
                {
                    ["Accepted"] = accepted,
                    ["PlateText"] = text,
                    ["Threshold"] = _options.Confidence,
                    ["OverlayThreshold"] = _options.Confidence,
                    ["HasCharacterDetails"] = characterDetails.Count > 0,
                    ["Characters"] = characterDetails
                }));
        }

        return new PipelineResult { Detections = results };
    }

    private List<PlateDetection> ReadCharacters(Mat image, Rectangle bounds, List<PlateDetection> characters)
    {
        PlatePreprocessingMode preprocessing = PlatePreprocessor.Parse(_options.Preprocessing);
        if (preprocessing == PlatePreprocessingMode.None)
            return GetCharactersInside(characters, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);

        using var crop = new Mat(image, bounds);
        using var prepared = PlatePreprocessor.Apply(crop, preprocessing);
        return _detector.Detect(prepared).Where(d => !PersianPlate.IsPlate(d.ClassId)).ToList();
    }

    private int? FindTrackId(PlateDetection detection)
    {
        int id = -1; float best = .05f;
        foreach (var track in _tracker.CurrentTracks)
        {
            float iou = IoU(track.Detection, detection);
            if (iou > best) { best = iou; id = track.Id; }
        }
        return id >= 0 ? id : null;
    }

    private static Rectangle ExpandBounds(PlateDetection plate, Size imageSize)
    {
        int mx = Math.Max(2, (int)(plate.Width * .05f));
        int my = Math.Max(2, (int)(plate.Height * .10f));
        int x0 = Math.Clamp((int)plate.X - mx, 0, imageSize.Width - 1);
        int y0 = Math.Clamp((int)plate.Y - my, 0, imageSize.Height - 1);
        int x1 = Math.Clamp((int)(plate.X + plate.Width) + mx, 1, imageSize.Width);
        int y1 = Math.Clamp((int)(plate.Y + plate.Height) + my, 1, imageSize.Height);
        return Rectangle.FromLTRB(x0, y0, x1, y1);
    }

    private static List<PlateDetection> Deduplicate(List<PlateDetection> detections)
    {
        var result = new List<PlateDetection>(detections.Count);
        foreach (PlateDetection detection in detections.OrderByDescending(x => x.Score))
            if (!result.Any(other => other.ClassId == detection.ClassId && IoU(other, detection) >= .55f)) result.Add(detection);
        return result;
    }

    private static List<PlateDetection> GetCharactersInside(List<PlateDetection> chars, int x0, int y0, int x1, int y1) =>
        chars.Where(c => { float x = c.X + c.Width / 2f, y = c.Y + c.Height / 2f; return x >= x0 && x <= x1 && y >= y0 && y <= y1; }).ToList();

    private static string BuildPlateText(List<PlateDetection> characters)
    {
        if (characters.Count == 0) return string.Empty;
        characters.Sort((a, b) => (b.X + b.Width / 2f).CompareTo(a.X + a.Width / 2f));
        string raw = string.Concat(characters.Select(c => PersianPlate.CharOf(c.ClassId)));
        return string.IsNullOrWhiteSpace(raw) ? string.Empty : new string(raw.Reverse().ToArray());
    }

    private static float IoU(PlateDetection a, PlateDetection b)
    {
        float ax2 = a.X + a.Width, ay2 = a.Y + a.Height, bx2 = b.X + b.Width, by2 = b.Y + b.Height;
        float x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y), x2 = Math.Min(ax2, bx2), y2 = Math.Min(ay2, by2);
        float width = Math.Max(0, x2 - x1), height = Math.Max(0, y2 - y1), intersection = width * height;
        float union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    public void Dispose() => _detector.Dispose();
}
