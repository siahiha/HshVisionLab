using System.Diagnostics;
using System.Drawing;
using Emgu.CV;

namespace HshDetectionEngin.Plate;

/// <summary>Plate detection with an optional second-stage OCR recognizer.</summary>
internal sealed class PlatePipeline : IProcessingPipeline
{
    private sealed record CachedOcr(PlateOcrResult Result, long Timestamp);

    private readonly PlateProcessingOptions _options;
    private readonly int _maxFps;
    private readonly bool _trackOnly;
    private readonly YoloDetector _plateDetector;
    private readonly IPlateTextRecognizer? _ocr;
    private readonly PlateTracker _tracker = new();
    private readonly Dictionary<int, CachedOcr> _ocrCache = [];
    private long _nextProcessTicks;
    private double _lastOcrInferenceMs;

    public string Name => "Iranian Plate Detection";
    public double LastInferenceMs => _plateDetector.LastInferenceMs + _lastOcrInferenceMs;

    public PlatePipeline(PlateProcessingOptions options, int maxFps, int threads, bool trackOnly = false)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _maxFps = maxFps;
        _trackOnly = trackOnly;
        string platePath = PlateModelPaths.Find(options.ModelFile)
            ?? throw new FileNotFoundException($"Plate model was not found: {options.ModelFile}");
        string? temporaryModel = null;
        try
        {
            if (!platePath.EndsWith(".hshmodel", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Plate models must be protected .hshmodel packages.");
            temporaryModel = SecureModelLoader.Materialize(platePath);
            _plateDetector = new YoloDetector(new YoloOptions
            {
                ModelPath = temporaryModel,
                InputWidth = options.InputSize,
                InputHeight = options.InputSize,
                ConfThreshold = options.Confidence,
                NmsIoUThreshold = options.NmsIoU,
                IntraOpThreads = threads,
                AutoOptimizeModel = false
            });
        }
        finally
        {
            if (temporaryModel is not null) TryDelete(temporaryModel);
        }

        if (trackOnly || !options.CharacterRecognitionEnabled) return;
        string ocrPath = PlateModelPaths.Find(options.CharacterModelFile)
            ?? throw new FileNotFoundException($"Plate recognition model was not found: {options.CharacterModelFile}");
        if (!ocrPath.EndsWith(".hshmodel", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Plate recognition models must be protected .hshmodel packages.");
        string ocrRuntimePath = SecureModelLoader.Materialize(ocrPath);
        try
        {
            _ocr = PlateOcrRecognizerFactory.Create(ocrRuntimePath, ocrPath, threads, options.CharacterConfidence);
        }
        finally
        {
            TryDelete(ocrRuntimePath);
        }
    }

    public PipelineResult Process(ProcessingContext context)
    {
        if (context.Image.IsEmpty) return new PipelineResult();
        int fps = Math.Max(1, _maxFps);
        long now = Stopwatch.GetTimestamp();
        if (now < _nextProcessTicks) return new PipelineResult();
        _nextProcessTicks = now + Math.Max(1, Stopwatch.Frequency / fps);
        _lastOcrInferenceMs = 0;

        // A combined model emits box and glyph scores in the same tensor.
        // Filtering glyphs with the box threshold can remove a required digit
        // even though the box itself confidently passes detection.
        _plateDetector.UpdateThresholds(_options.Confidence, _options.NmsIoU,
            !_trackOnly ? _options.CharacterConfidence : null);
        List<PlateDetection> detections = Deduplicate(_plateDetector.Detect(context.Image));
        _tracker.Update(detections, maxMisses: Math.Clamp(_options.TrackMaxMisses, 1, 60));
        if (_ocrCache.Count > 0)
        {
            HashSet<int> activeTracks = _tracker.CurrentTracks.Select(track => track.Id).ToHashSet();
            foreach (int staleId in _ocrCache.Keys.Where(id => !activeTracks.Contains(id)).ToArray())
                _ocrCache.Remove(staleId);
        }
        List<PlateDetection> plates = detections.Where(d => PersianPlate.IsPlate(d.ClassId)).ToList();
        List<PlateDetection> detectedCharacters = detections.Where(d => !PersianPlate.IsPlate(d.ClassId)).ToList();

        // A materialized crop list keeps stage two independent from frame
        // traversal and guarantees one OCR call per plate crop.
        var plateCrops = new List<(PlateDetection Detection, Rectangle Bounds, int? TrackId)>(plates.Count);
        foreach (PlateDetection plate in plates)
        {
            Rectangle bounds = ExpandBounds(plate, context.Image.Size);
            if (bounds.Width >= 12 && bounds.Height >= 6)
                plateCrops.Add((plate, bounds, FindTrackId(plate)));
        }

        var results = new List<AnalysisDetection>(plateCrops.Count);
        foreach (var item in plateCrops)
        {
            Rectangle detectorBounds = Rectangle.Round(new RectangleF(item.Detection.X, item.Detection.Y, item.Detection.Width, item.Detection.Height));
            if (_trackOnly)
            {
                results.Add(new AnalysisDetection(AnalysisKind.Plate, "Tracking", item.Detection.Score, item.Bounds, item.TrackId,
                    new Dictionary<string, object?> { ["Accepted"] = true, ["TrackPending"] = true, ["OverlayThreshold"] = _options.Confidence,
                        ["PlateDetectorBounds"] = detectorBounds }));
                continue;
            }
            List<PlateDetection> inside = [];
            IReadOnlyList<PlateOcrCharacter> recognizedCharacters = Array.Empty<PlateOcrCharacter>();
            // Prefer the combined model's existing frame output. When OCR is
            // available, weak/incomplete text goes straight to OCR rather than
            // spending another detector inference on a preprocessed crop.
            if (_plateDetector.HasCharacterClasses)
                inside = _ocr is null
                    ? ReadCharacters(context.Image, item.Bounds, detectedCharacters)
                    : GetCharactersInside(detectedCharacters, item.Bounds.Left, item.Bounds.Top, item.Bounds.Right, item.Bounds.Bottom);
            string text = BuildPlateText(inside);
            bool reliableDetectorText = IsReliableDetectorText(inside, text, _options.CharacterConfidence);
            bool boxAccepted = item.Detection.Score >= _options.Confidence;
            bool usedOcr = _ocr is not null && boxAccepted && !reliableDetectorText;
            string? fallbackReason = usedOcr && _plateDetector.HasCharacterClasses
                ? (text.Length < 8 ? "IncompleteOrLowConfidenceCharacters" : "InvalidTextFormat")
                : null;
            float recognitionConfidence = inside.Count > 0 ? inside.Average(character => character.Score) : 0;
            bool recognitionFailed = false;
            if (usedOcr)
            {
                try
                {
                    PlateOcrResult recognition = ReadOcr(context.Image, item.Bounds, item.TrackId, now);
                    text = recognition.Text;
                    recognitionConfidence = recognition.Confidence;
                    recognizedCharacters = recognition.Characters;
                }
                catch
                {
                    // Preserve the detector box for supplementary analysis even
                    // when the independent recognizer cannot read this crop.
                    recognitionFailed = true; text = ""; recognitionConfidence = 0;
                }
            }

            bool validText = PersianPlate.IsValidIranianPlate(text);
            string? rejectionReason = !boxAccepted ? "LowDetectionConfidence"
                : recognitionFailed ? "RecognitionFailed"
                : usedOcr && !(recognitionConfidence >= _options.CharacterConfidence) ? "LowRecognitionConfidence"
                : !validText ? (text.Length < 8 ? "IncompleteText" : "InvalidTextFormat")
                : null;
            bool accepted = rejectionReason is null;
            List<Dictionary<string, object?>> characterDetails = usedOcr
                ? recognizedCharacters.Select((character, index) => new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["classId"] = null,
                    ["symbol"] = character.Symbol,
                    ["confidence"] = character.Confidence,
                    ["bounds"] = character.Bounds is RectangleF bounds
                        ? new Dictionary<string, object?>
                        {
                            ["x"] = item.Bounds.Left + bounds.X + context.SourceBounds.X,
                            ["y"] = item.Bounds.Top + bounds.Y + context.SourceBounds.Y,
                            ["width"] = bounds.Width,
                            ["height"] = bounds.Height
                        }
                        : null
                }).ToList()
                : inside.Select((character, index) => new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["classId"] = character.ClassId,
                    ["symbol"] = PersianPlate.CharOf(character.Label, character.ClassId),
                    ["confidence"] = character.Score,
                    ["bounds"] = new Dictionary<string, object?>
                    {
                        ["x"] = character.X + context.SourceBounds.X,
                        ["y"] = character.Y + context.SourceBounds.Y,
                        ["width"] = character.Width,
                        ["height"] = character.Height
                    }
                }).ToList();

            results.Add(new AnalysisDetection(
                AnalysisKind.Plate,
                string.IsNullOrWhiteSpace(text) ? "Plate" : text,
                item.Detection.Score,
                item.Bounds,
                item.TrackId,
                new Dictionary<string, object?>
                {
                    ["Accepted"] = accepted,
                    ["RejectionReason"] = rejectionReason,
                    ["TextValid"] = validText,
                    ["CharacterCount"] = text.Length,
                    ["CharacterThreshold"] = _options.CharacterConfidence,
                    ["PlateDetectorBounds"] = detectorBounds,
                    ["PlateText"] = text,
                    ["RecognitionConfidence"] = recognitionConfidence,
                    ["RecognitionModel"] = Path.GetFileName(usedOcr ? _options.CharacterModelFile : _options.ModelFile),
                    ["RecognitionSource"] = usedOcr ? (_plateDetector.HasCharacterClasses ? "OcrFallback" : "Ocr") : "Detector",
                    ["OcrFallbackReason"] = fallbackReason,
                    ["Threshold"] = _options.Confidence,
                    ["OverlayThreshold"] = _options.Confidence,
                    ["HasCharacterDetails"] = characterDetails.Count > 0,
                    ["Characters"] = characterDetails
                }));
        }
        return new PipelineResult { Detections = results };
    }

    private PlateOcrResult ReadOcr(Mat image, Rectangle bounds, int? trackId, long now)
    {
        if (trackId is int id && _ocrCache.TryGetValue(id, out CachedOcr? cached))
        {
            int maxFps = Math.Max(0, _options.CharacterMaxFps);
            long interval = maxFps == 0 ? 0 : Math.Max(1, Stopwatch.Frequency / maxFps);
            if (maxFps > 0 && now - cached.Timestamp < interval) return cached.Result;
        }
        using var crop = new Mat(image, bounds);
        PlateOcrResult result = _ocr!.Read(crop);
        _lastOcrInferenceMs += _ocr.LastInferenceMs;
        if (trackId is int track) _ocrCache[track] = new CachedOcr(result, now);
        return result;
    }

    private List<PlateDetection> ReadCharacters(Mat image, Rectangle bounds, List<PlateDetection> characters)
    {
        PlatePreprocessingMode preprocessing = PlatePreprocessor.Parse(_options.Preprocessing);
        if (preprocessing == PlatePreprocessingMode.None)
            return GetCharactersInside(characters, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        using var crop = new Mat(image, bounds);
        using var prepared = PlatePreprocessor.Apply(crop, preprocessing);
        float scaleX = bounds.Width / (float)prepared.Width;
        float scaleY = bounds.Height / (float)prepared.Height;
        return _plateDetector.Detect(prepared)
            .Where(d => !PersianPlate.IsPlate(d.ClassId))
            .Select(d => d with { X = bounds.X + d.X * scaleX, Y = bounds.Y + d.Y * scaleY,
                Width = d.Width * scaleX, Height = d.Height * scaleY })
            .ToList();
    }

    private static bool IsReliableDetectorText(List<PlateDetection> characters, string text, float threshold) =>
        PersianPlate.IsValidIranianPlate(text) && characters.Count == 8 &&
        characters.All(character => float.IsFinite(character.Score) && character.Score >= threshold &&
            !string.IsNullOrEmpty(PersianPlate.CharOf(character.Label, character.ClassId)));

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
        string raw = string.Concat(characters.Select(c => PersianPlate.CharOf(c.Label, c.ClassId)));
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

    public void Dispose()
    {
        _ocr?.Dispose();
        _plateDetector.Dispose();
        _ocrCache.Clear();
    }
}
