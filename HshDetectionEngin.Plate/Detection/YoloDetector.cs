using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using Microsoft.ML.OnnxRuntime;

namespace HshDetectionEngin.Plate;

/// <summary>
/// YOLO ONNX detector (CPU / OnnxRuntime) optimized for continuous video.
/// All preprocessing buffers are preallocated and reused => minimal GC pressure per frame.
/// </summary>
internal sealed class YoloDetector : IDisposable
{
    private struct Candidate
    {
        public float Cx, Cy, W, H, Score;
        public int Cls;
    }

    private readonly YoloOptions _opt;
    private InferenceSession? _session;
    private string _inputName = "";
    private string[] _outputNames = [];
    private string[] _classNames = [];

    private Mat? _letterbox;
    private Mat? _resized;
    private readonly Mat[] _plane8 = new Mat[3];
    private readonly Mat[] _plane32 = new Mat[3];
    private VectorOfMat? _srcVec;
    private VectorOfMat? _dstVec;
    private static readonly int[] FromTo = { 2, 0, 1, 1, 0, 2 }; // BGR planes -> RGB CHW

    private float[]? _inputBuffer;
    private OrtValue? _inputTensor;
    private RunOptions? _runOptions;
    private Dictionary<string, OrtValue>? _inputs;

    private double _scale = 1;
    private int _padX, _padY;

    private readonly List<Candidate> _cands = new(512);
    private readonly List<int> _order = new(512);
    private bool[] _suppressed = new bool[1024];

    public double LastInferenceMs { get; private set; }
    public double LastPreprocessMs { get; private set; }
    public int InputWidth => _opt.InputWidth;
    public int InputHeight => _opt.InputHeight;
    public string ModelPath => _opt.ModelPath;
    public int ClassCount => _classNames.Length;
    public string ModelInfo { get; private set; } = "";

    public YoloDetector(YoloOptions opt)
    {
        _opt = opt;
        Load();
    }

    /// <summary>Live-update thresholds without rebuilding the session.</summary>
    public void UpdateThresholds(float confidence, float nmsIou)
    {
        _opt.ConfThreshold = confidence;
        _opt.NmsIoUThreshold = nmsIou;
    }

    private void Load()
    {
        if (!File.Exists(_opt.ModelPath))
            throw new FileNotFoundException($"Model not found: {_opt.ModelPath}");

        var so = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, _opt.IntraOpThreads),
            EnableMemoryPattern = true,
        };

        string runtimeModelPath = _opt.AutoOptimizeModel
            ? ModelOptimizer.EnsureOptimized(_opt.ModelPath)
            : _opt.ModelPath;

        _session = new InferenceSession(runtimeModelPath, so);
        _inputName = _session.InputNames.First();
        _outputNames = _session.OutputNames.ToArray();

        if (_session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var raw))
            _classNames = ParseClassNames(raw);

        int w = _opt.InputWidth;
        int h = _opt.InputHeight;
        int n = w * h;

        _letterbox = new Mat(h, w, DepthType.Cv8U, 3);
        _resized = new Mat();

        for (int c = 0; c < 3; c++)
        {
            _plane8[c] = new Mat(h, w, DepthType.Cv8U, 1);
            _plane32[c] = new Mat(h, w, DepthType.Cv32F, 1);
        }

        _srcVec = new VectorOfMat(_letterbox);
        _dstVec = new VectorOfMat(
            _plane8[0],
            _plane8[1],
            _plane8[2]);

        _inputBuffer = new float[3L * n];

        _inputTensor?.Dispose();
        _inputTensor = OrtValue.CreateTensorValueFromMemory(
            _inputBuffer,
            [1, 3, h, w]);

        _runOptions?.Dispose();
        _runOptions = new RunOptions();

        _inputs = new Dictionary<string, OrtValue>(1)
        {
            [_inputName] = _inputTensor
        };

        ModelInfo =
            $"{Path.GetFileName(runtimeModelPath)} | " +
            $"in {_opt.InputWidth}x{_opt.InputHeight} | " +
            $"CPU x{so.IntraOpNumThreads} | " +
            $"classes {_classNames.Length}";
    }

    private static string[] ParseClassNames(string metadataValue)
    {
        var matches = Regex.Matches(metadataValue, @"(\d+)\s*:\s*'([^']*)'");
        if (matches.Count == 0) return [];
        var arr = new string[matches.Max(m => int.Parse(m.Groups[1].Value)) + 1];
        foreach (Match m in matches)
            arr[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value;
        return arr;
    }

    public List<PlateDetection> Detect(Mat bgrFrame)
    {
        if (_session is null || _inputTensor is null || _letterbox is null || _resized is null ||
            _inputBuffer is null || _runOptions is null || _srcVec is null || _dstVec is null)
            throw new InvalidOperationException("Detector not initialized.");

        var swPre = Stopwatch.StartNew();
        int iw = _opt.InputWidth, ih = _opt.InputHeight, n = iw * ih;

        // ---- Letterbox: aspect-preserving resize + gray padding ----
        _scale = Math.Min((double)iw / bgrFrame.Width, (double)ih / bgrFrame.Height);
        int nw = Math.Max(1, (int)Math.Round(bgrFrame.Width * _scale));
        int nh = Math.Max(1, (int)Math.Round(bgrFrame.Height * _scale));
        _padX = (iw - nw) / 2;
        _padY = (ih - nh) / 2;

        CvInvoke.Resize(bgrFrame, _resized, new Size(nw, nh), 0, 0, Inter.Linear);
        CvInvoke.CopyMakeBorder(_resized, _letterbox, _padY, ih - nh - _padY, _padX, iw - nw - _padX,
            BorderType.Constant, new MCvScalar(114, 114, 114));

        // Split BGR -> three 8U planes reordered as RGB
        CvInvoke.MixChannels(_srcVec, _dstVec, FromTo);

        // Normalize (/255) and pack each plane into the contiguous CHW buffer
        for (int c = 0; c < 3; c++)
        {
            _plane8[c].ConvertTo(_plane32[c], DepthType.Cv32F, 1.0 / 255.0, 0.0);
            Marshal.Copy(_plane32[c].DataPointer, _inputBuffer, c * n, n);
        }
        swPre.Stop();
        LastPreprocessMs = swPre.Elapsed.TotalMilliseconds;

        // ---- Inference ----
        var swInf = Stopwatch.StartNew();
        using (var outputs = _session.Run(_runOptions, _inputs!, _outputNames))
        {
            OrtValue first = null!;
            foreach (var ov in outputs) { first = ov; break; }   // single output model
            var span = first.GetTensorDataAsSpan<float>();
            ExtractCandidates(span);
        }
        swInf.Stop();
        LastInferenceMs = swInf.Elapsed.TotalMilliseconds;

        return Postprocess(bgrFrame.Width, bgrFrame.Height);
    }

    private void ExtractCandidates(ReadOnlySpan<float> output)
    {
        _cands.Clear();

        // output layout: [1, 4 + numClasses, anchors] -> column-major
        int numClasses = Math.Max(1, _classNames.Length);
        int stride = output.Length / (4 + numClasses);
        if (stride <= 0) return;

        float confThresh = _opt.ConfThreshold;

        for (int i = 0; i < stride; i++)
        {
            float best = 0f;
            int bestCls = -1;
            for (int k = 0; k < numClasses; k++)
            {
                float s = output[i + (4 + k) * stride];
                if (s > best) { best = s; bestCls = k; }
            }
            if (best < confThresh || bestCls < 0) continue;

            _cands.Add(new Candidate
            {
                Cx = output[i],
                Cy = output[i + stride],
                W = output[i + 2 * stride],
                H = output[i + 3 * stride],
                Score = best,
                Cls = bestCls
            });
        }

        // NMS is O(n²). Keeping only the strongest candidates dramatically
        // reduces CPU cost on busy frames while retaining all useful boxes.
        const int MaxCandidatesForNms = 512;
        if (_cands.Count > MaxCandidatesForNms)
        {
            _cands.Sort((a, b) => b.Score.CompareTo(a.Score));
            _cands.RemoveRange(MaxCandidatesForNms, _cands.Count - MaxCandidatesForNms);
        }
    }

    private List<PlateDetection> Postprocess(int srcW, int srcH)
    {
        var result = new List<PlateDetection>(_cands.Count);
        if (_cands.Count == 0) return result;

        _order.Clear();
        for (int i = 0; i < _cands.Count; i++) _order.Add(i);
        _order.Sort((a, b) => _cands[b].Score.CompareTo(_cands[a].Score));

        if (_suppressed.Length < _cands.Count)
            _suppressed = new bool[_cands.Count * 2];
        else
            Array.Clear(_suppressed, 0, _cands.Count);

        float iouThr = _opt.NmsIoUThreshold;
        float invScale = (float)(1.0 / _scale);

        for (int oi = 0; oi < _order.Count; oi++)
        {
            int idx = _order[oi];
            if (_suppressed[idx]) continue;
            var a = _cands[idx];

            for (int oj = oi + 1; oj < _order.Count; oj++)
            {
                int jdx = _order[oj];
                if (_suppressed[jdx]) continue;
                var b = _cands[jdx];
                if (b.Cls != a.Cls) continue; // class-aware NMS
                if (IoU(a, b) > iouThr) _suppressed[jdx] = true;
            }

            // letterbox coords -> source frame coords (clamped)
            float x1 = (a.Cx - a.W * 0.5f - _padX) * invScale;
            float y1 = (a.Cy - a.H * 0.5f - _padY) * invScale;
            float x2 = (a.Cx + a.W * 0.5f - _padX) * invScale;
            float y2 = (a.Cy + a.H * 0.5f - _padY) * invScale;
            x1 = Math.Clamp(x1, 0, srcW - 1);
            y1 = Math.Clamp(y1, 0, srcH - 1);
            x2 = Math.Clamp(x2, 1, srcW);
            y2 = Math.Clamp(y2, 1, srcH);

            string label = a.Cls < _classNames.Length && !string.IsNullOrEmpty(_classNames[a.Cls])
                ? _classNames[a.Cls]
                : a.Cls.ToString();

            result.Add(new PlateDetection(x1, y1, x2 - x1, y2 - y1, a.Cls, label, a.Score));
        }
        return result;
    }

    private static float IoU(Candidate a, Candidate b)
    {
        float ax1 = a.Cx - a.W * 0.5f, ay1 = a.Cy - a.H * 0.5f;
        float ax2 = ax1 + a.W, ay2 = ay1 + a.H;
        float bx1 = b.Cx - b.W * 0.5f, by1 = b.Cy - b.H * 0.5f;
        float bx2 = bx1 + b.W, by2 = by1 + b.H;

        float ix1 = Math.Max(ax1, bx1), iy1 = Math.Max(ay1, by1);
        float ix2 = Math.Min(ax2, bx2), iy2 = Math.Min(ay2, by2);
        float iw = Math.Max(0f, ix2 - ix1), ih = Math.Max(0f, iy2 - iy1);
        float inter = iw * ih;
        float union = a.W * a.H + b.W * b.H - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public void Dispose()
    {
        _inputs?.Clear();
        _inputs = null;
        _inputTensor?.Dispose();
        _runOptions?.Dispose();
        _session?.Dispose();
        _srcVec?.Dispose();
        _dstVec?.Dispose();
        foreach (var m in _plane8) m?.Dispose();
        foreach (var m in _plane32) m?.Dispose();
        _resized?.Dispose();
        _letterbox?.Dispose();
    }
}
