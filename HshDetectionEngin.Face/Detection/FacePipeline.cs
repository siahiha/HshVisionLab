using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using HshDetectionEngin.Licensing;
using Microsoft.ML.OnnxRuntime;

namespace HshDetectionEngin.Face;

public sealed class FacePipelineOptions
{
    public int InputWidth { get; init; } = 320;
    public int InputHeight { get; init; } = 320;
    public float ConfidenceThreshold { get; set; } = 0.8f;
    public float MatchIouThreshold { get; set; } = 0.25f;
    public int MaxMisses { get; set; } = 10;
    public FacePreprocessingMode Preprocessing { get; set; } = FacePreprocessingMode.None;
    public int MaxFps { get; set; } = 8;
    /// <summary>ONNX Runtime CPU intra-op thread count for YuNet and SFace sessions.</summary>
    public int Threads { get; set; } = 1;
    public float NmsThreshold { get; set; } = 0.3f;
    public int TopK { get; set; } = 5000;
    public float UnknownMatchThreshold { get; set; } = 0.35f;
}

public sealed record FaceEnrollment(byte[] FaceImage, float[] Embedding, float DetectionConfidence);

/// <summary>YuNet face detection, temporal tracking and optional SFace identity matching.</summary>
public sealed class FacePipeline : IProcessingPipeline
{
    private const string DevelopmentLicense = "HSH-DETECTION-DEVELOPMENT-LICENSE-V1";
    private const int YuNetModelInputSize = 640;
    private const int SFaceModelInputSize = 112;
    private static readonly int[] YuNetStrides = [8, 16, 32];

    private readonly FacePipelineOptions _options;
    private readonly InferenceSession _detectorSession;
    private readonly string _detectorInputName;
    private readonly string[] _detectorOutputNames;
    private readonly int[] _clsOutputIndices = new int[3];
    private readonly int[] _objOutputIndices = new int[3];
    private readonly int[] _bboxOutputIndices = new int[3];
    private readonly int[] _kpsOutputIndices = new int[3];
    private readonly RunOptions _detectorRunOptions = new();
    private readonly OrtValue _detectorInputTensor;
    private readonly Dictionary<string, OrtValue> _detectorInputs;
    private readonly float[] _detectorInputBuffer = new float[3 * YuNetModelInputSize * YuNetModelInputSize];

    private readonly InferenceSession? _recognitionSession;
    private readonly string? _recognitionInputName;
    private readonly string[] _recognitionOutputNames = [];

    private readonly FaceDatabase? _database;
    private readonly float _recognitionThreshold;
    private readonly List<Track> _tracks = [];
    private int _nextTrackId = 1;
    private long _nextProcessTicks;

    private sealed class Track
    {
        public int Id;
        public Rectangle Bounds;
        public int Misses;
    }

    private sealed class FaceCandidate
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;
        public float Confidence;
        public PointF[] Landmarks = [];
        public Rectangle NmsBounds;
    }

    public string Name => _recognitionSession is null
        ? "YuNet Face Detection + Tracking"
        : "YuNet Face Detection + SFace Recognition";

    public FacePipeline(string modelPath, float confidence = 0.8f, FacePipelineOptions? options = null,
        string? recognitionModelPath = null, FaceDatabase? database = null, float recognitionThreshold = 0.40f,
        LicenseValidationResult? license = null)
    {
        LicenseValidator.Require(license, LicensedFeature.Face);
        _options = options ?? new FacePipelineOptions { ConfidenceThreshold = confidence };
        _database = database;
        _recognitionThreshold = recognitionThreshold;

        _detectorSession = CreateSession(modelPath, _options.Threads);
        _detectorInputName = _detectorSession.InputNames.First();
        _detectorOutputNames = _detectorSession.OutputNames.ToArray();
        for (int i = 0; i < YuNetStrides.Length; i++)
        {
            _clsOutputIndices[i] = FindOutputIndex(_detectorOutputNames, $"cls_{YuNetStrides[i]}");
            _objOutputIndices[i] = FindOutputIndex(_detectorOutputNames, $"obj_{YuNetStrides[i]}");
            _bboxOutputIndices[i] = FindOutputIndex(_detectorOutputNames, $"bbox_{YuNetStrides[i]}");
            _kpsOutputIndices[i] = FindOutputIndex(_detectorOutputNames, $"kps_{YuNetStrides[i]}");
        }
        _detectorInputTensor = OrtValue.CreateTensorValueFromMemory(
            _detectorInputBuffer,
            [1, 3, YuNetModelInputSize, YuNetModelInputSize]);
        _detectorInputs = new Dictionary<string, OrtValue>(1)
        {
            [_detectorInputName] = _detectorInputTensor
        };

        if (_database is not null && !string.IsNullOrWhiteSpace(recognitionModelPath))
        {
            _recognitionSession = CreateSession(recognitionModelPath, _options.Threads);
            _recognitionInputName = _recognitionSession.InputNames.First();
            _recognitionOutputNames = _recognitionSession.OutputNames.ToArray();
        }
    }

    public PipelineResult Process(ProcessingContext context)
    {
        if (context.Image.IsEmpty) return new PipelineResult();
        if (_options.MaxFps > 0)
        {
            long now = Stopwatch.GetTimestamp();
            if (now < _nextProcessTicks) return new PipelineResult();
            _nextProcessTicks = now + Math.Max(1, Stopwatch.Frequency / _options.MaxFps);
        }

        // Keep the configured FaceInputSize semantics before feeding the fixed
        // 640x640 YuNet graph. The package models are exported at 640x640;
        // detections are mapped back to this configured intermediate image.
        using var preparedImage = FacePreprocessor.Apply(context.Image, _options.Preprocessing);
        int inputWidth = Math.Max(32, _options.InputWidth);
        int inputHeight = Math.Max(32, _options.InputHeight);
        using var detectorImage = new Mat();
        CvInvoke.Resize(preparedImage, detectorImage, new Size(inputWidth, inputHeight));
        using var networkImage = new Mat();
        CvInvoke.Resize(detectorImage, networkImage, new Size(YuNetModelInputSize, YuNetModelInputSize));
        CopyImageToNchw(networkImage, _detectorInputBuffer, swapRedBlue: false);

        using var outputs = _detectorSession.Run(_detectorRunOptions, _detectorInputs, _detectorOutputNames);
        OrtValue[] outputValues = outputs.ToArray();
        List<FaceCandidate> faces = DecodeFaces(outputValues, detectorImage.Size);
        if (faces.Count == 0) return new PipelineResult { Detections = UpdateTracks([]) };

        var acceptedDetections = new List<(Rectangle Bounds, float Confidence, string Label, IReadOnlyDictionary<string, object?> Metadata)>(faces.Count);
        var rejectedDetections = new List<AnalysisDetection>();
        float scaleX = context.Image.Width / (float)detectorImage.Width;
        float scaleY = context.Image.Height / (float)detectorImage.Height;
        foreach (FaceCandidate face in faces)
        {
            int x = Math.Clamp((int)(face.X * scaleX), 0, context.Image.Width - 1);
            int y = Math.Clamp((int)(face.Y * scaleY), 0, context.Image.Height - 1);
            int w = Math.Clamp((int)(face.Width * scaleX), 1, context.Image.Width - x);
            int h = Math.Clamp((int)(face.Height * scaleY), 1, context.Image.Height - y);

            bool accepted = face.Confidence >= _options.ConfidenceThreshold;
            if (!accepted)
            {
                rejectedDetections.Add(new AnalysisDetection(
                    AnalysisKind.Face,
                    "Face",
                    face.Confidence,
                    new Rectangle(x, y, w, h),
                    null,
                    new Dictionary<string, object?>
                    {
                        ["Accepted"] = false,
                        ["OverlayThreshold"] = _options.ConfidenceThreshold,
                        ["Recognized"] = false
                    }));
                continue;
            }

            string label = "Unknown";
            string? identityId = null;
            float similarity = 0;
            int personNumber = 0;
            bool isUnknown = true;
            string? matchedSampleId = null;
            byte[]? alignedFaceJpeg = null;
            if (_recognitionSession is not null && _database is not null)
            {
                using var aligned = AlignCrop(detectorImage, face.Landmarks);
                float[] embedding = RunRecognition(aligned);
                byte[] faceImage = EncodeJpeg(aligned);
                FaceMatch match = _database.IdentifyOrCreateUnknown(embedding, _recognitionThreshold,
                    _options.UnknownMatchThreshold, faceImage, "runtime-face.jpg", face.Confidence);
                identityId = match.Id;
                similarity = match.Similarity;
                personNumber = match.PersonNumber;
                isUnknown = match.IsUnknown;
                matchedSampleId = match.MatchedSampleId;
                alignedFaceJpeg = faceImage;
                label = match.Name == "Unknown" ? $"Unknown ({ShortId(match.Id)})" : match.Name;
            }

            acceptedDetections.Add((new Rectangle(x, y, w, h), face.Confidence, label,
                new Dictionary<string, object?>
                {
                    ["Accepted"] = true,
                    ["OverlayThreshold"] = _options.ConfidenceThreshold,
                    ["Recognized"] = identityId is not null,
                    ["IdentityId"] = identityId,
                    ["Similarity"] = similarity,
                    ["PersonNumber"] = personNumber,
                    ["IsUnknown"] = isUnknown,
                    ["MatchedSampleId"] = matchedSampleId,
                    ["AlignedFaceJpeg"] = alignedFaceJpeg
                }));
        }

        List<AnalysisDetection> trackedAccepted = UpdateTracks(acceptedDetections);
        rejectedDetections.AddRange(trackedAccepted);
        return new PipelineResult { Detections = rejectedDetections };
    }

    private List<FaceCandidate> DecodeFaces(IReadOnlyList<OrtValue> outputs, Size targetSize)
    {
        var candidates = new List<FaceCandidate>();
        float minimumVisualConfidence = Math.Clamp(_options.ConfidenceThreshold * 0.90f, 0.01f, 1f);
        float scaleX = targetSize.Width / (float)YuNetModelInputSize;
        float scaleY = targetSize.Height / (float)YuNetModelInputSize;

        for (int level = 0; level < YuNetStrides.Length; level++)
        {
            int stride = YuNetStrides[level];
            ReadOnlySpan<float> cls = outputs[_clsOutputIndices[level]].GetTensorDataAsSpan<float>();
            ReadOnlySpan<float> obj = outputs[_objOutputIndices[level]].GetTensorDataAsSpan<float>();
            ReadOnlySpan<float> bbox = outputs[_bboxOutputIndices[level]].GetTensorDataAsSpan<float>();
            ReadOnlySpan<float> kps = outputs[_kpsOutputIndices[level]].GetTensorDataAsSpan<float>();
            int cols = YuNetModelInputSize / stride;
            int rows = YuNetModelInputSize / stride;
            int count = Math.Min(cls.Length, obj.Length);
            count = Math.Min(count, bbox.Length / 4);
            count = Math.Min(count, kps.Length / 10);
            count = Math.Min(count, rows * cols);

            for (int index = 0; index < count; index++)
            {
                float clsScore = Math.Clamp(cls[index], 0, 1);
                float objectness = Math.Clamp(obj[index], 0, 1);
                float score = MathF.Sqrt(clsScore * objectness);
                // Keep only candidates within 10% below the configured
                // threshold for red visual feedback. We intentionally do not
                // expose very weak candidates as detections.
                if (score < minimumVisualConfidence) continue;

                int row = index / cols;
                int col = index % cols;
                float centerX = (col + bbox[index * 4]) * stride;
                float centerY = (row + bbox[index * 4 + 1]) * stride;
                float width = MathF.Exp(bbox[index * 4 + 2]) * stride;
                float height = MathF.Exp(bbox[index * 4 + 3]) * stride;
                float x = (centerX - width / 2) * scaleX;
                float y = (centerY - height / 2) * scaleY;
                float mappedWidth = width * scaleX;
                float mappedHeight = height * scaleY;
                var landmarks = new PointF[5];
                for (int point = 0; point < 5; point++)
                {
                    landmarks[point] = new PointF(
                        (kps[index * 10 + point * 2] + col) * stride * scaleX,
                        (kps[index * 10 + point * 2 + 1] + row) * stride * scaleY);
                }

                candidates.Add(new FaceCandidate
                {
                    X = x,
                    Y = y,
                    Width = mappedWidth,
                    Height = mappedHeight,
                    Confidence = score,
                    Landmarks = landmarks,
                    NmsBounds = new Rectangle((int)x, (int)y, Math.Max(1, (int)mappedWidth), Math.Max(1, (int)mappedHeight))
                });
            }
        }

        int topK = Math.Max(1, _options.TopK);
        float nmsThreshold = Math.Clamp(_options.NmsThreshold, 0, 1);
        var kept = new List<FaceCandidate>(Math.Min(candidates.Count, topK));
        foreach (FaceCandidate candidate in candidates.OrderByDescending(x => x.Confidence).Take(topK))
        {
            if (kept.All(previous => IoU(previous.NmsBounds, candidate.NmsBounds) < nmsThreshold))
                kept.Add(candidate);
        }
        return kept;
    }

    private List<AnalysisDetection> UpdateTracks(List<(Rectangle Bounds, float Confidence, string Label, IReadOnlyDictionary<string, object?> Metadata)> detections)
    {
        var results = new List<AnalysisDetection>(detections.Count);
        var used = new HashSet<int>();
        foreach (var detection in detections)
        {
            Track? best = null;
            float bestIou = _options.MatchIouThreshold;
            foreach (Track track in _tracks)
            {
                if (used.Contains(track.Id)) continue;
                float iou = IoU(track.Bounds, detection.Bounds);
                if (iou > bestIou) { bestIou = iou; best = track; }
            }

            best ??= new Track { Id = _nextTrackId++ };
            if (!_tracks.Contains(best)) _tracks.Add(best);
            best.Bounds = detection.Bounds;
            best.Misses = 0;
            used.Add(best.Id);
            results.Add(new AnalysisDetection(AnalysisKind.Face, detection.Label, detection.Confidence,
                detection.Bounds, best.Id, detection.Metadata));
        }

        foreach (Track track in _tracks)
            if (!used.Contains(track.Id)) track.Misses++;
        _tracks.RemoveAll(t => t.Misses > _options.MaxMisses);
        return results;
    }

    /// <summary>Creates an SFace embedding from an aligned face image for database enrollment.</summary>
    public float[] CreateEmbedding(Mat faceImage)
    {
        if (_recognitionSession is null) throw new InvalidOperationException("Face recognition is not enabled for this pipeline.");
        if (faceImage.IsEmpty) throw new ArgumentException("Face image is empty.", nameof(faceImage));
        using var aligned = new Mat();
        CvInvoke.Resize(faceImage, aligned, new Size(SFaceModelInputSize, SFaceModelInputSize));
        return RunRecognition(aligned);
    }

    /// <summary>Detects, aligns and encodes the best face in a source image for enrollment.</summary>
    public FaceEnrollment CreateEnrollment(Mat sourceImage)
    {
        if (_recognitionSession is null) throw new InvalidOperationException("Face recognition is not enabled for this pipeline.");
        if (sourceImage.IsEmpty) throw new ArgumentException("Face image is empty.", nameof(sourceImage));

        using var preparedImage = FacePreprocessor.Apply(sourceImage, _options.Preprocessing);
        int inputWidth = Math.Max(32, _options.InputWidth);
        int inputHeight = Math.Max(32, _options.InputHeight);
        using var detectorImage = new Mat();
        CvInvoke.Resize(preparedImage, detectorImage, new Size(inputWidth, inputHeight));
        using var networkImage = new Mat();
        CvInvoke.Resize(detectorImage, networkImage, new Size(YuNetModelInputSize, YuNetModelInputSize));
        CopyImageToNchw(networkImage, _detectorInputBuffer, swapRedBlue: false);

        using var outputs = _detectorSession.Run(_detectorRunOptions, _detectorInputs, _detectorOutputNames);
        List<FaceCandidate> faces = DecodeFaces(outputs.ToArray(), detectorImage.Size);
        if (faces.Count > 1) throw new InvalidDataException("The enrollment image must contain exactly one face.");
        FaceCandidate? face = faces.OrderByDescending(x => x.Width * x.Height).FirstOrDefault();
        if (face is null) throw new InvalidDataException("No face was detected in the image.");

        using Mat aligned = AlignCrop(detectorImage, face.Landmarks);
        float[] embedding = RunRecognition(aligned);
        return new FaceEnrollment(EncodeJpeg(aligned), embedding, face.Confidence);
    }

    /// <summary>Detects and registers one cropped/aligned face sample in the SQLite database.</summary>
    public void RegisterIdentity(string name, Mat faceImage, string? databasePath = null, string? sourceFileName = null)
    {
        if (_database is null) throw new InvalidOperationException("Face database is not enabled for this pipeline.");
        FaceEnrollment enrollment = CreateEnrollment(faceImage);
        _database.RegisterSample(name, enrollment.Embedding, enrollment.FaceImage,
            sourceFileName ?? "face.jpg", detectionConfidence: enrollment.DetectionConfidence);
        _database.Save(databasePath);
    }

    private float[] RunRecognition(Mat alignedFace)
    {
        if (_recognitionSession is null || _recognitionInputName is null)
            throw new InvalidOperationException("Face recognition is not enabled for this pipeline.");

        using var rgb = new Mat();
        CvInvoke.CvtColor(alignedFace, rgb, ColorConversion.Bgr2Rgb);
        float[] inputBuffer = new float[3 * SFaceModelInputSize * SFaceModelInputSize];
        CopyImageToNchw(rgb, inputBuffer, swapRedBlue: false);
        using var inputTensor = OrtValue.CreateTensorValueFromMemory(
            inputBuffer,
            [1, 3, SFaceModelInputSize, SFaceModelInputSize]);
        using var runOptions = new RunOptions();
        var inputs = new Dictionary<string, OrtValue>(1) { [_recognitionInputName] = inputTensor };
        using var outputs = _recognitionSession.Run(runOptions, inputs, _recognitionOutputNames);
        foreach (OrtValue output in outputs)
            return output.GetTensorDataAsSpan<float>().ToArray();
        throw new InvalidOperationException("SFace returned no output.");
    }

    private static byte[] EncodeJpeg(Mat aligned)
    {
        using Image<Bgr, byte> alignedImage = aligned.ToImage<Bgr, byte>();
        using Bitmap bitmap = alignedImage.ToBitmap();
        using var imageStream = new MemoryStream();
        bitmap.Save(imageStream, ImageFormat.Jpeg);
        return imageStream.ToArray();
    }

    private static Mat AlignCrop(Mat source, IReadOnlyList<PointF> sourcePoints)
    {
        if (sourcePoints.Count != 5) throw new ArgumentException("Exactly five face landmarks are required.", nameof(sourcePoints));

        // Same five-point template and similarity transform used by OpenCV's
        // FaceRecognizerSF, kept here so SFace can run through ONNX Runtime.
        PointF[] targetPoints =
        [
            new(38.2946f, 51.6963f), new(73.5318f, 51.5014f), new(56.0252f, 71.7366f),
            new(41.5493f, 92.3655f), new(70.7299f, 92.2041f)
        ];
        const double targetMeanX = 56.0262;
        const double targetMeanY = 71.9008;
        double sourceMeanX = sourcePoints.Average(p => p.X);
        double sourceMeanY = sourcePoints.Average(p => p.Y);
        double numeratorA = 0;
        double numeratorB = 0;
        double denominator = 0;
        for (int i = 0; i < 5; i++)
        {
            double sx = sourcePoints[i].X - sourceMeanX;
            double sy = sourcePoints[i].Y - sourceMeanY;
            double dx = targetPoints[i].X - targetMeanX;
            double dy = targetPoints[i].Y - targetMeanY;
            numeratorA += sx * dx + sy * dy;
            numeratorB += sx * dy - sy * dx;
            denominator += sx * sx + sy * sy;
        }

        if (denominator <= double.Epsilon) throw new InvalidDataException("Face landmarks are degenerate.");
        double a = numeratorA / denominator;
        double b = numeratorB / denominator;
        double tx = targetMeanX - (a * sourceMeanX - b * sourceMeanY);
        double ty = targetMeanY - (b * sourceMeanX + a * sourceMeanY);
        double[] matrixValues = [a, -b, tx, b, a, ty];

        using var transform = new Mat(2, 3, DepthType.Cv64F, 1);
        Marshal.Copy(matrixValues, 0, transform.DataPointer, matrixValues.Length);
        var aligned = new Mat();
        CvInvoke.WarpAffine(source, aligned, transform, new Size(SFaceModelInputSize, SFaceModelInputSize),
            Inter.Linear, Warp.Default, BorderType.Constant, new MCvScalar());
        return aligned;
    }

    private static void CopyImageToNchw(Mat source, float[] destination, bool swapRedBlue)
    {
        using var floatImage = new Mat();
        source.ConvertTo(floatImage, DepthType.Cv32F);
        int width = source.Width;
        int height = source.Height;
        int planeSize = checked(width * height);
        int rowLength = checked(width * 3);
        var row = new float[rowLength];
        for (int y = 0; y < height; y++)
        {
            IntPtr rowPointer = IntPtr.Add(floatImage.DataPointer, checked((int)(y * floatImage.Step)));
            Marshal.Copy(rowPointer, row, 0, rowLength);
            for (int x = 0; x < width; x++)
            {
                int sourceIndex = x * 3;
                int pixelIndex = y * width + x;
                int redIndex = swapRedBlue ? 2 : 0;
                int blueIndex = swapRedBlue ? 0 : 2;
                destination[pixelIndex] = row[sourceIndex + redIndex];
                destination[planeSize + pixelIndex] = row[sourceIndex + 1];
                destination[2 * planeSize + pixelIndex] = row[sourceIndex + blueIndex];
            }
        }
    }

    private static InferenceSession CreateSession(string modelPath, int threads)
    {
        string runtimePath = modelPath.EndsWith(".hshmodel", StringComparison.OrdinalIgnoreCase)
            ? Materialize(modelPath)
            : modelPath;
        try
        {
            if (!File.Exists(runtimePath)) throw new FileNotFoundException("Face model not found.", runtimePath);
            using var sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = Math.Max(1, threads),
                EnableMemoryPattern = true
            };
            return new InferenceSession(runtimePath, sessionOptions);
        }
        finally
        {
            if (!string.Equals(runtimePath, modelPath, StringComparison.OrdinalIgnoreCase)) TryDelete(runtimePath);
        }
    }

    private static int FindOutputIndex(IReadOnlyList<string> names, string expected)
    {
        for (int i = 0; i < names.Count; i++)
            if (names[i].Equals(expected, StringComparison.Ordinal)) return i;
        throw new InvalidDataException($"YuNet output '{expected}' was not found.");
    }

    private static float IoU(Rectangle a, Rectangle b)
    {
        Rectangle intersection = Rectangle.Intersect(a, b);
        if (intersection.IsEmpty) return 0;
        float area = intersection.Width * intersection.Height;
        return area / (a.Width * a.Height + b.Width * b.Height - area);
    }

    private static float Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count != b.Count || a.Count == 0) return 0;
        double dot = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            aa += a[i] * a[i];
            bb += b[i] * b[i];
        }
        return aa <= 0 || bb <= 0 ? 0 : (float)(dot / Math.Sqrt(aa * bb));
    }

    private static string ShortId(string id) => id.Length > 14 ? id[^8..] : id;

    private static string Materialize(string packagePath)
    {
        byte[] package = File.ReadAllBytes(packagePath);
        if (package.Length <= 24 || Encoding.ASCII.GetString(package, 0, 8) != "HSHM0001")
            throw new InvalidDataException("Invalid protected face model package.");
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(
            Environment.GetEnvironmentVariable("HSH_DETECTION_LICENSE") ?? DevelopmentLicense));
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = package[8..24];
        byte[] model = aes.CreateDecryptor().TransformFinalBlock(package, 24, package.Length - 24);
        string path = Path.Combine(Path.GetTempPath(), $"hsh-face-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(path, model);
        CryptographicOperations.ZeroMemory(model);
        return path;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    public void Dispose()
    {
        _detectorInputs.Clear();
        _detectorInputTensor.Dispose();
        _detectorRunOptions.Dispose();
        _detectorSession.Dispose();
        _recognitionSession?.Dispose();
        GC.SuppressFinalize(this);
    }
}
