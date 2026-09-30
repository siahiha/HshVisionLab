using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using HshDetectionEngin.Licensing;
using Microsoft.ML.OnnxRuntime;

namespace HshDetectionEngin.Palm;

public sealed class PalmPipelineOptions
{
    public string DetectorKind { get; init; } = "BlazePalm";
    public int DetectorInputSize { get; init; } = 192;
    public float DetectionConfidence { get; init; } = 0.55f;
    public float NmsIoU { get; init; } = 0.30f;
    public int MaxHands { get; init; } = 2;
    public int RecognitionInputSize { get; init; } = 128;
    public bool RecognitionEnabled { get; init; } = true;
    public float RecognitionThreshold { get; init; } = 0.55f;
    public float UnknownMatchThreshold { get; init; } = 0.35f;
    public float MatchIou { get; init; } = 0.25f;
    public int TrackMaxMisses { get; init; } = 10;
}

public sealed record PalmEnrollment(byte[] PalmImage, float[] Embedding, float DetectionConfidence);

/// <summary>ONNX palm localization (BlazePalm or RTMDet-nano) plus CCNet/PPNet embedding matching.</summary>
public sealed class PalmPipeline : IProcessingPipeline
{
    private sealed class Candidate { public Rectangle Bounds; public float Confidence; public PointF[] Keypoints = []; }
    private sealed class Track { public int Id; public Rectangle Bounds; public int Misses; }
    private readonly PalmPipelineOptions _options;
    private readonly InferenceSession _detector;
    private readonly InferenceSession? _recognizer;
    private readonly PalmDatabase? _database;
    private readonly string _detectorKind;
    private readonly string _detectorInput;
    private readonly bool _detectorUsesNhwc;
    private readonly string? _recognizerInput;
    private readonly List<Track> _tracks = [];
    private readonly Dictionary<int, PalmMatch> _detectionOnlyUnknowns = [];
    private readonly Dictionary<int, PalmMatch> _recognitionTrackMatches = [];
    private int _nextTrackId = 1;

    public string Name => $"{_detectorKind} Palm Detection + {(_recognizer is null ? "Detection" : "Palmprint Recognition")}";

    public PalmPipeline(string detectorPath, PalmPipelineOptions options, string? recognitionPath = null,
        PalmDatabase? database = null, LicenseValidationResult? license = null)
    {
        LicenseValidator.Require(license, LicensedFeature.Palm);
        _options = options;
        _detectorKind = options.DetectorKind.Equals("RTMDet", StringComparison.OrdinalIgnoreCase) ? "RTMDet-nano" : "BlazePalm";
        _detector = CreateSession(detectorPath, options, out _detectorInput, out _detectorUsesNhwc);
        _database = database;
        if (options.RecognitionEnabled && database is not null && !string.IsNullOrWhiteSpace(recognitionPath))
        {
            _recognizer = CreateSession(recognitionPath, options, out string recognizerInput, out _);
            _recognizerInput = recognizerInput;
        }
    }

    public PipelineResult Process(ProcessingContext context)
    {
        if (context.Image.IsEmpty) return new PipelineResult();
        List<Candidate> candidates = _detectorKind == "BlazePalm"
            ? DetectBlazePalm(context.Image)
            : DetectRtmDet(context.Image);
        var accepted = new List<(Rectangle Bounds, float Confidence, string Label, IReadOnlyDictionary<string, object?> Metadata)>();
        foreach (Candidate candidate in candidates.Take(Math.Max(1, _options.MaxHands)))
        {
            using Mat palm = ExtractPalm(context.Image, candidate);
            float[]? embedding = _recognizer is null ? null : RunEmbedding(palm);
            PalmMatch? match = embedding is not null && _database is not null
                ? _database.IdentifyKnown(embedding, _options.RecognitionThreshold)
                : null;
            string label = match?.Name ?? (_recognizer is null ? "Palm" : "Unknown palm");
            byte[] crop = EncodeJpeg(palm);
            var metadata = new Dictionary<string, object?>
            {
                ["Accepted"] = true, ["Recognized"] = match is not null, ["IdentityId"] = match?.Id,
                ["Similarity"] = match?.Similarity ?? 0f, ["PersonNumber"] = match?.PersonNumber ?? 0,
                ["IsUnknown"] = match?.IsUnknown ?? false, ["MatchedSampleId"] = match?.MatchedSampleId,
                ["PalmImageJpeg"] = crop, ["PalmEmbedding"] = embedding,
                ["PalmRecordConfidence"] = candidate.Confidence, ["PalmRecognitionThreshold"] = _options.RecognitionThreshold
            };
            accepted.Add((candidate.Bounds, candidate.Confidence, label, metadata));
        }
        List<AnalysisDetection> detections = UpdateTracks(accepted);
        if (_recognizer is not null && _database is not null)
            detections = ResolveRecognition(detections);
        if (_recognizer is null && _database is not null)
            detections = RegisterDetectionOnlyUnknowns(detections);
        return new PipelineResult { Detections = detections };
    }

    private List<AnalysisDetection> ResolveRecognition(List<AnalysisDetection> detections)
    {
        PalmDatabase database = _database!;
        for (int index = 0; index < detections.Count; index++)
        {
            AnalysisDetection detection = detections[index];
            if (detection.Metadata?.TryGetValue("Recognized", out object? recognizedValue) == true &&
                recognizedValue is bool recognized && recognized)
            {
                if (detection.TrackId is int knownTrackId &&
                    detection.Metadata.TryGetValue("IdentityId", out object? knownId) &&
                    knownId is string knownIdentityId && !string.IsNullOrWhiteSpace(knownIdentityId))
                {
                    _recognitionTrackMatches[knownTrackId] = new PalmMatch(
                        knownIdentityId,
                        detection.Label,
                        detection.Metadata.TryGetValue("Similarity", out object? knownSimilarity) && knownSimilarity is not null
                            ? Convert.ToSingle(knownSimilarity, System.Globalization.CultureInfo.InvariantCulture)
                            : 0f,
                        detection.Metadata.TryGetValue("PersonNumber", out object? knownNumber) && knownNumber is not null
                            ? Convert.ToInt32(knownNumber, System.Globalization.CultureInfo.InvariantCulture)
                            : 0,
                        detection.Metadata.TryGetValue("MatchedSampleId", out object? knownSample) ? knownSample as string : null,
                        detection.Metadata.TryGetValue("IsUnknown", out object? knownUnknown) && knownUnknown is bool isUnknown && isUnknown);
                }
                continue;
            }

            if (detection.TrackId is not int trackId ||
                detection.Metadata?.TryGetValue("PalmEmbedding", out object? embeddingValue) != true ||
                embeddingValue is not float[] embedding || embedding.Length == 0 ||
                detection.Metadata.TryGetValue("PalmImageJpeg", out object? imageValue) != true ||
                imageValue is not byte[] image || image.Length == 0)
                continue;

            if (!_recognitionTrackMatches.TryGetValue(trackId, out PalmMatch? match))
            {
                match = database.IdentifyOrCreateUnknown(embedding, _options.RecognitionThreshold,
                    _options.UnknownMatchThreshold, image, "runtime-palm.jpg", detection.Confidence);
                _recognitionTrackMatches[trackId] = match;
            }

            var metadata = new Dictionary<string, object?>(detection.Metadata)
            {
                ["Recognized"] = true,
                ["IdentityId"] = match.Id,
                ["Similarity"] = match.Similarity,
                ["PersonNumber"] = match.PersonNumber,
                ["IsUnknown"] = match.IsUnknown,
                ["MatchedSampleId"] = match.MatchedSampleId
            };
            detections[index] = detection with { Label = match.Name, Metadata = metadata };
        }
        return detections;
    }

    private List<AnalysisDetection> RegisterDetectionOnlyUnknowns(List<AnalysisDetection> detections)
    {
        PalmDatabase database = _database!;
        for (int index = 0; index < detections.Count; index++)
        {
            AnalysisDetection detection = detections[index];
            if (detection.TrackId is not int trackId ||
                detection.Metadata?.TryGetValue("PalmImageJpeg", out object? imageValue) != true ||
                imageValue is not byte[] image || image.Length == 0)
                continue;

            if (!_detectionOnlyUnknowns.TryGetValue(trackId, out PalmMatch? match))
            {
                match = database.RegisterUnknownObservation(image, "runtime-palm-detection.jpg", detection.Confidence);
                _detectionOnlyUnknowns[trackId] = match;
            }

            var metadata = new Dictionary<string, object?>(detection.Metadata ?? new Dictionary<string, object?>())
            {
                ["Recognized"] = true,
                ["RecognitionAvailable"] = false,
                ["IdentityId"] = match.Id,
                ["Similarity"] = match.Similarity,
                ["PersonNumber"] = match.PersonNumber,
                ["IsUnknown"] = true,
                ["MatchedSampleId"] = match.MatchedSampleId
            };
            detections[index] = detection with { Label = match.Name, Metadata = metadata };
        }
        return detections;
    }

    public PalmEnrollment CreateEnrollment(Mat sourceImage)
    {
        if (_recognizer is null) throw new InvalidOperationException("Palm recognition is not enabled or its model is unavailable.");
        List<Candidate> candidates = _detectorKind == "BlazePalm" ? DetectBlazePalm(sourceImage) : DetectRtmDet(sourceImage);
        Candidate? candidate = candidates.OrderByDescending(x => x.Bounds.Width * x.Bounds.Height).FirstOrDefault();
        if (candidate is null) throw new InvalidDataException("No palm was detected in the image.");
        using Mat crop = ExtractPalm(sourceImage, candidate);
        return new PalmEnrollment(EncodeJpeg(crop), RunEmbedding(crop), candidate.Confidence);
    }

    public void RegisterIdentity(string name, Mat sourceImage, string fileName)
    {
        if (_database is null) throw new InvalidOperationException("Palm database is not available.");
        PalmEnrollment enrollment = CreateEnrollment(sourceImage);
        _database.RegisterSample(name, enrollment.Embedding, enrollment.PalmImage, fileName, detectionConfidence: enrollment.DetectionConfidence);
    }

    private List<Candidate> DetectBlazePalm(Mat source)
    {
        int size = Math.Clamp(_options.DetectorInputSize, 64, 1024);
        using Mat prepared = PrepareBlazeImage(source, size, out float ratio, out int padX, out int padY);
        float[] input = _detectorUsesNhwc
            ? CopyImageToNhwc(prepared, [0, 0, 0], [255, 255, 255])
            : CopyImageToNchw(prepared, [0, 0, 0], [255, 255, 255]);
        long[] shape = _detectorUsesNhwc ? [1, size, size, 3] : [1, 3, size, size];
        using OrtValue tensor = OrtValue.CreateTensorValueFromMemory(input, shape);
        using RunOptions runOptions = new();
        using var outputs = _detector.Run(runOptions, new Dictionary<string, OrtValue> { [_detectorInput] = tensor }, _detector.OutputNames);
        float[]? scores = null; float[]? regressors = null;
        foreach (OrtValue output in outputs)
        {
            float[] values = output.GetTensorDataAsSpan<float>().ToArray();
            if (values.Length == 2016) scores = values;
            else if (values.Length >= 2016 * 18) regressors = values;
        }
        if (scores is null || regressors is null) return [];
        var candidates = new List<Candidate>();
        List<(float X, float Y)> anchors = BlazeAnchors();
        for (int i = 0; i < 2016; i++)
        {
            float confidence = Sigmoid(scores[i]);
            if (confidence < _options.DetectionConfidence) continue;
            int offset = i * 18;
            float cx = (anchors[i].X * size + regressors[offset] - padX) / ratio;
            float cy = (anchors[i].Y * size + regressors[offset + 1] - padY) / ratio;
            float width = MathF.Abs(regressors[offset + 2]) / ratio;
            float height = MathF.Abs(regressors[offset + 3]) / ratio;
            Candidate candidate = new()
            {
                Confidence = confidence,
                Bounds = Rectangle.FromLTRB(
                    Math.Clamp((int)(cx - width / 2), 0, source.Width - 1),
                    Math.Clamp((int)(cy - height / 2), 0, source.Height - 1),
                    Math.Clamp((int)(cx + width / 2), 1, source.Width),
                    Math.Clamp((int)(cy + height / 2), 1, source.Height))
            };
            candidate.Keypoints = Enumerable.Range(0, 7).Select(k => new PointF(
                Math.Clamp((anchors[i].X * size + regressors[offset + 4 + k * 2] - padX) / ratio, 0, source.Width - 1),
                Math.Clamp((anchors[i].Y * size + regressors[offset + 5 + k * 2] - padY) / ratio, 0, source.Height - 1))).ToArray();
            candidates.Add(candidate);
        }
        return Nms(candidates);
    }

    private List<Candidate> DetectRtmDet(Mat source)
    {
        const int size = 320;
        using Mat prepared = new(size, size, DepthType.Cv8U, 3); prepared.SetTo(new MCvScalar(114, 114, 114));
        float ratio = Math.Min(size / (float)source.Width, size / (float)source.Height);
        int width = Math.Max(1, (int)(source.Width * ratio)); int height = Math.Max(1, (int)(source.Height * ratio));
        using var resized = new Mat(); CvInvoke.Resize(source, resized, new Size(width, height));
        using Mat region = new(prepared, new Rectangle(0, 0, width, height)); resized.CopyTo(region);
        float[] input = CopyImageToNchw(prepared, [103.53f, 116.28f, 123.675f], [57.375f, 57.12f, 58.395f], rgb: false);
        using OrtValue tensor = OrtValue.CreateTensorValueFromMemory(input, [1, 3, size, size]);
        using RunOptions runOptions = new(); using var outputs = _detector.Run(runOptions, new Dictionary<string, OrtValue> { [_detectorInput] = tensor }, _detector.OutputNames);
        float[] values = outputs.First().GetTensorDataAsSpan<float>().ToArray();
        int last = values.Length % 5 == 0 && values.Length / 5 <= 300 ? 5 : 6;
        int count = values.Length / last; var candidates = new List<Candidate>();
        for (int i = 0; i < count; i++)
        {
            int o = i * last; float confidence = last == 5 ? values[o + 4] : values[o + 4] * values[o + 5];
            if (confidence < _options.DetectionConfidence) continue;
            float x1, y1, x2, y2;
            if (last == 5) { x1 = values[o] / ratio; y1 = values[o + 1] / ratio; x2 = values[o + 2] / ratio; y2 = values[o + 3] / ratio; }
            else { float stride = i < 1600 ? 8 : i < 2000 ? 16 : 32; int gridWidth = size / (int)stride; int index = i < 1600 ? i : i < 2000 ? i - 1600 : i - 2000; int gx = index % gridWidth; int gy = index / gridWidth; float cx = (values[o] + gx) * stride; float cy = (values[o + 1] + gy) * stride; float w = MathF.Exp(values[o + 2]) * stride; float h = MathF.Exp(values[o + 3]) * stride; x1 = (cx - w / 2) / ratio; y1 = (cy - h / 2) / ratio; x2 = (cx + w / 2) / ratio; y2 = (cy + h / 2) / ratio; }
            candidates.Add(new Candidate { Confidence = confidence, Bounds = Rectangle.FromLTRB(Math.Clamp((int)x1, 0, source.Width - 1), Math.Clamp((int)y1, 0, source.Height - 1), Math.Clamp((int)x2, 1, source.Width), Math.Clamp((int)y2, 1, source.Height)) });
        }
        return Nms(candidates);
    }

    private float[] RunEmbedding(Mat palm)
    {
        if (_recognizer is null || _recognizerInput is null) throw new InvalidOperationException("Palm recognizer is unavailable.");
        using var gray = new Mat(); CvInvoke.CvtColor(palm, gray, ColorConversion.Bgr2Gray);
        using var resized = new Mat(); CvInvoke.Resize(gray, resized, new Size(_options.RecognitionInputSize, _options.RecognitionInputSize));
        float[] input = new float[_options.RecognitionInputSize * _options.RecognitionInputSize];
        using var floatMat = new Mat(); resized.ConvertTo(floatMat, DepthType.Cv32F, 1.0 / 255.0);
        Marshal.Copy(floatMat.DataPointer, input, 0, input.Length);
        using OrtValue tensor = OrtValue.CreateTensorValueFromMemory(input, [1, 1, _options.RecognitionInputSize, _options.RecognitionInputSize]);
        using RunOptions runOptions = new(); using var outputs = _recognizer.Run(runOptions, new Dictionary<string, OrtValue> { [_recognizerInput] = tensor }, _recognizer.OutputNames);
        float[] embedding = outputs.First().GetTensorDataAsSpan<float>().ToArray(); double norm = Math.Sqrt(embedding.Sum(x => x * x));
        if (norm > 0) for (int i = 0; i < embedding.Length; i++) embedding[i] = (float)(embedding[i] / norm);
        return embedding;
    }

    private static Mat ExtractPalm(Mat source, Candidate candidate)
    {
        Rectangle bounds = candidate.Bounds; int padX = Math.Max(2, bounds.Width / 8); int padY = Math.Max(2, bounds.Height / 8);
        Rectangle crop = Rectangle.Intersect(new Rectangle(bounds.X - padX, bounds.Y - padY, bounds.Width + padX * 2, bounds.Height + padY * 2), new Rectangle(Point.Empty, source.Size));
        using Mat view = new(source, crop); var result = new Mat(); CvInvoke.Resize(view, result, new Size(128, 128)); return result;
    }

    private List<AnalysisDetection> UpdateTracks(List<(Rectangle Bounds, float Confidence, string Label, IReadOnlyDictionary<string, object?> Metadata)> detections)
    {
        var results = new List<AnalysisDetection>(); var used = new HashSet<int>();
        foreach (var detection in detections)
        {
            Track? best = null; float bestIou = _options.MatchIou;
            foreach (Track track in _tracks.Where(x => !used.Contains(x.Id))) { float iou = IoU(track.Bounds, detection.Bounds); if (iou > bestIou) { bestIou = iou; best = track; } }
            best ??= new Track { Id = _nextTrackId++ }; if (!_tracks.Contains(best)) _tracks.Add(best); best.Bounds = detection.Bounds; best.Misses = 0; used.Add(best.Id);
            results.Add(new AnalysisDetection(AnalysisKind.Palm, detection.Label, detection.Confidence, detection.Bounds, best.Id, detection.Metadata));
        }
        foreach (Track track in _tracks) if (!used.Contains(track.Id)) track.Misses++;
        _tracks.RemoveAll(x => x.Misses > _options.TrackMaxMisses);
        foreach (int key in _detectionOnlyUnknowns.Keys.Where(key => _tracks.All(track => track.Id != key)).ToList())
            _detectionOnlyUnknowns.Remove(key);
        foreach (int key in _recognitionTrackMatches.Keys.Where(key => _tracks.All(track => track.Id != key)).ToList())
            _recognitionTrackMatches.Remove(key);
        return results;
    }

    private List<Candidate> Nms(List<Candidate> candidates)
    {
        var kept = new List<Candidate>(); foreach (Candidate candidate in candidates.OrderByDescending(x => x.Confidence)) if (kept.All(x => IoU(x.Bounds, candidate.Bounds) < _options.NmsIoU)) kept.Add(candidate); return kept.Take(Math.Max(1, _options.MaxHands)).ToList();
    }
    private static Mat PrepareBlazeImage(Mat source, int size, out float ratio, out int padX, out int padY)
    {
        ratio = Math.Min(size / (float)source.Width, size / (float)source.Height);
        int width = Math.Max(1, (int)MathF.Round(source.Width * ratio));
        int height = Math.Max(1, (int)MathF.Round(source.Height * ratio));
        padX = (size - width) / 2;
        padY = (size - height) / 2;
        using var resized = new Mat();
        CvInvoke.Resize(source, resized, new Size(width, height));
        var prepared = new Mat(size, size, DepthType.Cv8U, 3);
        prepared.SetTo(new MCvScalar(0, 0, 0));
        using Mat region = new(prepared, new Rectangle(padX, padY, width, height));
        resized.CopyTo(region);
        return prepared;
    }

    private static float[] CopyImageToNchw(Mat source, float[] mean, float[] std, int? targetSize = null, bool rgb = true)
    {
        using var image = new Mat(); if (targetSize is int size) CvInvoke.Resize(source, image, new Size(size, size)); else source.CopyTo(image);
        using var floatImage = new Mat(); image.ConvertTo(floatImage, DepthType.Cv32F); int width = image.Width, height = image.Height, plane = width * height; float[] result = new float[plane * 3]; var row = new float[width * 3];
        for (int y = 0; y < height; y++) { Marshal.Copy(IntPtr.Add(floatImage.DataPointer, checked((int)(y * floatImage.Step))), row, 0, row.Length); for (int x = 0; x < width; x++) { int p = y * width + x, s = x * 3; float c0 = rgb ? row[s + 2] : row[s]; float c2 = rgb ? row[s] : row[s + 2]; result[p] = (c0 - mean[0]) / std[0]; result[plane + p] = (row[s + 1] - mean[1]) / std[1]; result[plane * 2 + p] = (c2 - mean[2]) / std[2]; } } return result;
    }
    private static float[] CopyImageToNhwc(Mat source, float[] mean, float[] std, bool rgb = true)
    {
        using var floatImage = new Mat(); source.ConvertTo(floatImage, DepthType.Cv32F);
        int width = source.Width, height = source.Height;
        float[] result = new float[width * height * 3];
        var row = new float[width * 3];
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(IntPtr.Add(floatImage.DataPointer, checked((int)(y * floatImage.Step))), row, 0, row.Length);
            for (int x = 0; x < width; x++)
            {
                int p = (y * width + x) * 3, s = x * 3;
                float c0 = rgb ? row[s + 2] : row[s];
                float c2 = rgb ? row[s] : row[s + 2];
                result[p] = (c0 - mean[0]) / std[0];
                result[p + 1] = (row[s + 1] - mean[1]) / std[1];
                result[p + 2] = (c2 - mean[2]) / std[2];
            }
        }
        return result;
    }
    private static List<(float X, float Y)> BlazeAnchors() { var result = new List<(float, float)>(2016); Add(24, 2); Add(12, 6); return result; void Add(int grid, int repeats) { for (int y = 0; y < grid; y++) for (int x = 0; x < grid; x++) for (int r = 0; r < repeats; r++) result.Add(((x + .5f) / grid, (y + .5f) / grid)); } }
    private static float Sigmoid(float value) => value >= 0 ? 1f / (1f + MathF.Exp(-value)) : MathF.Exp(value) / (1f + MathF.Exp(value));
    private static Rectangle NormalizeRect(float x, float y, float w, float h, Size size) => Rectangle.FromLTRB(Math.Clamp((int)(x * size.Width), 0, size.Width - 1), Math.Clamp((int)(y * size.Height), 0, size.Height - 1), Math.Clamp((int)((x + w) * size.Width), 1, size.Width), Math.Clamp((int)((y + h) * size.Height), 1, size.Height));
    private static float IoU(Rectangle a, Rectangle b) { Rectangle i = Rectangle.Intersect(a, b); if (i.IsEmpty) return 0; float area = i.Width * i.Height; return area / (a.Width * a.Height + b.Width * b.Height - area); }
    private static byte[] EncodeJpeg(Mat image) { using Image<Bgr, byte> bgr = image.ToImage<Bgr, byte>(); using Bitmap bitmap = bgr.ToBitmap(); using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Jpeg); return stream.ToArray(); }
    private static InferenceSession CreateSession(string path, PalmPipelineOptions options, out string inputName, out bool usesNhwc)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Palm model not found.", path);
        using var settings = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1
        };
        var session = new InferenceSession(path, settings);
        inputName = session.InputNames.First();
        int[] dimensions = session.InputMetadata[inputName].Dimensions.ToArray();
        usesNhwc = dimensions.Length == 4 && dimensions[3] == 3 && dimensions[1] != 3;
        return session;
    }
    public void Dispose() { _detector.Dispose(); _recognizer?.Dispose(); GC.SuppressFinalize(this); }
}
