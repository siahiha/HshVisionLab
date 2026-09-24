using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Microsoft.ML.OnnxRuntime;

namespace HshDetectionEngin.Plate;

/// <summary>
/// Segments a plate crop using a vertical projection profile and classifies
/// each glyph with the lightweight Platrix CNN model.
/// </summary>
internal sealed class CnnPlateRecognizer : IPlateTextRecognizer
{
    private static readonly string[] FallbackLabels =
    [
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        "ا", "ب", "ت", "ج", "د", "س", "ص", "ط", "ع", "ق",
        "ل", "م", "ن", "ه", "و", "پ", "ژ", "ی"
    ];

    private const int InputSize = 32;
    private const int NormalizedHeight = 96;
    private readonly InferenceSession _session;
    private readonly string[] _outputNames;
    private readonly string[] _labels;
    private readonly float[] _inputBuffer = new float[InputSize * InputSize];
    private readonly OrtValue _inputTensor;
    private readonly RunOptions _runOptions = new();
    private readonly Dictionary<string, OrtValue> _inputs;
    private readonly Mat _gray = new();
    private readonly Mat _enhancedGray = new();
    private readonly Mat _binary = new();
    private readonly Mat _normalized = new();
    private readonly Mat _resized = new();
    private readonly Mat _float = new();

    public double LastInferenceMs { get; private set; }

    public CnnPlateRecognizer(string modelPath, int intraOpThreads)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Plate CNN OCR model was not found: {modelPath}");

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, intraOpThreads),
            EnableMemoryPattern = true
        };
        _session = new InferenceSession(modelPath, sessionOptions);
        string inputName = _session.InputNames.First();
        _outputNames = _session.OutputNames.ToArray();
        _labels = LoadLabels(modelPath);
        _inputTensor = OrtValue.CreateTensorValueFromMemory(_inputBuffer, [1, 1, InputSize, InputSize]);
        _inputs = new Dictionary<string, OrtValue>(1) { [inputName] = _inputTensor };
    }

    public PlateOcrResult Read(Mat crop)
    {
        if (crop.IsEmpty) return new PlateOcrResult(string.Empty, 0);
        List<Mat> glyphs = Segment(crop);
        if (glyphs.Count == 0) return new PlateOcrResult(string.Empty, 0);

        var probabilities = new List<float[]>(_labels.Length);
        var sw = Stopwatch.StartNew();
        foreach (Mat glyph in glyphs)
        {
            try
            {
                PrepareInput(glyph);
                using var outputs = _session.Run(_runOptions, _inputs, _outputNames);
                OrtValue output = null!;
                foreach (var value in outputs) { output = value; break; }
                ReadOnlySpan<float> logits = output.GetTensorDataAsSpan<float>();
                probabilities.Add(Softmax(logits));
            }
            finally { glyph.Dispose(); }
        }
        sw.Stop();
        LastInferenceMs = sw.Elapsed.TotalMilliseconds;
        return Decode(probabilities);
    }

    private List<Mat> Segment(Mat crop)
    {
        if (crop.NumberOfChannels > 1)
            CvInvoke.CvtColor(crop, _gray, ColorConversion.Bgr2Gray);
        else
            crop.CopyTo(_gray);

        using Mat enhanced = PlatePreprocessor.Apply(crop, PlatePreprocessingMode.Advanced);
        if (enhanced.NumberOfChannels > 1)
            CvInvoke.CvtColor(enhanced, _enhancedGray, ColorConversion.Bgr2Gray);
        else
            enhanced.CopyTo(_enhancedGray);

        CvInvoke.Threshold(_enhancedGray, _binary, 0, 255, ThresholdType.BinaryInv | ThresholdType.Otsu);
        using Image<Gray, byte> thresholdImage = _binary.ToImage<Gray, byte>();
        byte[,,] thresholdData = thresholdImage.Data;
        double mean = 0;
        for (int y = 0; y < _binary.Rows; y++)
            for (int x = 0; x < _binary.Cols; x++) mean += thresholdData[y, x, 0];
        if (mean > 127 * (double)_binary.Rows * _binary.Cols)
            CvInvoke.BitwiseNot(_binary, _binary);

        int height = _binary.Rows;
        if (height <= 0 || _binary.Cols <= 0) return [];
        double scale = NormalizedHeight / (double)height;
        int width = Math.Max(1, (int)Math.Round(_binary.Cols * scale));
        CvInvoke.Resize(_binary, _normalized, new Size(width, NormalizedHeight), 0, 0, Inter.Area);

        using Image<Gray, byte> normalizedImage = _normalized.ToImage<Gray, byte>();
        byte[,,] data = normalizedImage.Data;
        var bands = new List<(int Start, int End)>();
        bool active = false;
        int start = 0;
        int minimumInk = Math.Max(1, (int)(NormalizedHeight * .03));
        for (int x = 0; x < width; x++)
        {
            int ink = 0;
            for (int y = 0; y < NormalizedHeight; y++) if (data[y, x, 0] > 0) ink++;
            if (ink > minimumInk && !active) { start = x; active = true; }
            if (ink <= minimumInk && active)
            {
                bands.Add((start, x));
                active = false;
            }
        }
        if (active) bands.Add((start, width));

        int minWidth = Math.Max(2, (int)(width * .008));
        var glyphs = new List<Mat>(Math.Min(10, bands.Count));
        foreach ((int startBand, int endBand) in bands)
        {
            if (endBand - startBand < minWidth) continue;
            int top = NormalizedHeight, bottom = -1;
            for (int y = 0; y < NormalizedHeight; y++)
            {
                bool hasInk = false;
                for (int x = startBand; x < endBand; x++)
                    if (data[y, x, 0] > 0) { hasInk = true; break; }
                if (hasInk) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
            }
            if (bottom < top || bottom - top + 1 < NormalizedHeight * .18) continue;
            int glyphWidth = endBand - startBand;
            int glyphHeight = bottom - top + 1;
            using var glyph = new Mat(_normalized, new Rectangle(startBand, top, glyphWidth, glyphHeight));
            int side = Math.Max(glyphWidth, glyphHeight);
            using var square = new Mat(side, side, DepthType.Cv8U, 1);
            square.SetTo(new MCvScalar(0));
            int xOffset = (side - glyphWidth) / 2;
            int yOffset = (side - glyphHeight) / 2;
            using var target = new Mat(square, new Rectangle(xOffset, yOffset, glyphWidth, glyphHeight));
            glyph.CopyTo(target);
            var resized = new Mat();
            CvInvoke.Resize(square, resized, new Size(InputSize, InputSize), 0, 0, Inter.Area);
            glyphs.Add(resized);
            if (glyphs.Count >= 10) break;
        }
        return glyphs;
    }

    private void PrepareInput(Mat glyph)
    {
        glyph.ConvertTo(_float, DepthType.Cv32F, 1.0 / 255.0, 0.0);
        Marshal.Copy(_float.DataPointer, _inputBuffer, 0, _inputBuffer.Length);
    }

    private float[] Softmax(ReadOnlySpan<float> logits)
    {
        int count = Math.Min(_labels.Length, logits.Length);
        var result = new float[_labels.Length];
        float max = float.NegativeInfinity;
        for (int i = 0; i < count; i++) max = Math.Max(max, logits[i]);
        float sum = 0;
        for (int i = 0; i < count; i++) sum += MathF.Exp(logits[i] - max);
        if (sum <= 0) return result;
        for (int i = 0; i < count; i++) result[i] = MathF.Exp(logits[i] - max) / sum;
        return result;
    }

    private PlateOcrResult Decode(List<float[]> probabilities)
    {
        var digits = Enumerable.Range(0, _labels.Length).Where(i => _labels[i].Length == 1 && char.IsDigit(_labels[i][0])).ToArray();
        var letters = Enumerable.Range(0, _labels.Length).Where(i => !digits.Contains(i)).ToArray();
        if (digits.Length == 0 || letters.Length == 0) return new PlateOcrResult(string.Empty, 0);

        int letterPosition = 0;
        float bestMargin = float.NegativeInfinity;
        for (int i = 0; i < probabilities.Count; i++)
        {
            float digitBest = digits.Max(index => probabilities[i][index]);
            float letterBest = letters.Max(index => probabilities[i][index]);
            if (letterBest - digitBest > bestMargin) { bestMargin = letterBest - digitBest; letterPosition = i; }
        }

        var chars = new List<string>(probabilities.Count);
        var confidences = new List<float>(probabilities.Count);
        for (int i = 0; i < probabilities.Count; i++)
        {
            int[] pool = i == letterPosition ? letters : digits;
            int best = pool.MaxBy(index => probabilities[i][index]);
            chars.Add(_labels[best]);
            confidences.Add(probabilities[i][best]);
        }
        return new PlateOcrResult(string.Concat(chars), confidences.Count == 0 ? 0 : confidences.Average());
    }

    private static string[] LoadLabels(string modelPath)
    {
        string sidecar = Path.ChangeExtension(modelPath, ".labels.json");
        if (!File.Exists(sidecar)) return FallbackLabels;
        try
        {
            string[]? labels = JsonSerializer.Deserialize<string[]>(File.ReadAllText(sidecar));
            return labels is { Length: > 0 } ? labels : FallbackLabels;
        }
        catch { return FallbackLabels; }
    }

    public void Dispose()
    {
        _inputs.Clear();
        _inputTensor.Dispose();
        _runOptions.Dispose();
        _session.Dispose();
        _gray.Dispose();
        _enhancedGray.Dispose();
        _binary.Dispose();
        _normalized.Dispose();
        _resized.Dispose();
        _float.Dispose();
    }
}
