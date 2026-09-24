using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Microsoft.ML.OnnxRuntime;

namespace HshDetectionEngin.Plate;

internal interface IPlateTextRecognizer : IDisposable
{
    double LastInferenceMs { get; }
    PlateOcrResult Read(Mat crop);
}

/// <summary>
/// Whole-plate CRNN recognizer. It deliberately accepts one plate crop at a
/// time, keeping the detector and OCR stages independent and bounded in CPU
/// cost. The model contract is 1x1x32x128 grayscale with CTC output.
/// </summary>
internal sealed class CrnnPlateRecognizer : IPlateTextRecognizer
{
    private static readonly string[] FallbackLabels =
    [
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        "ا", "ب", "ت", "ث", "ج", "ح", "د", "ز", "س", "ش",
        "ص", "ط", "ع", "ق", "ل", "م", "ن", "ه", "و", "پ", "ژ", "ی"
    ];

    private const int ImageWidth = 128;
    private const int ImageHeight = 32;
    private readonly InferenceSession _session;
    private readonly string[] _outputNames;
    private readonly string[] _labels;
    private readonly int _blank;
    private readonly float[] _inputBuffer = new float[ImageWidth * ImageHeight];
    private readonly OrtValue _inputTensor;
    private readonly RunOptions _runOptions = new();
    private readonly Dictionary<string, OrtValue> _inputs;
    private readonly Mat _gray = new();
    private readonly Mat _resized = new();
    private readonly Mat _float = new();

    public double LastInferenceMs { get; private set; }

    public CrnnPlateRecognizer(string modelPath, int intraOpThreads)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Plate OCR model was not found: {modelPath}");

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
        _blank = _labels.Length;
        _inputTensor = OrtValue.CreateTensorValueFromMemory(_inputBuffer, [1, 1, ImageHeight, ImageWidth]);
        _inputs = new Dictionary<string, OrtValue>(1) { [inputName] = _inputTensor };
    }

    public PlateOcrResult Read(Mat crop)
    {
        if (crop.IsEmpty) return new PlateOcrResult(string.Empty, 0);

        PrepareInput(crop);
        var sw = Stopwatch.StartNew();
        using var outputs = _session.Run(_runOptions, _inputs, _outputNames);
        OrtValue output = null!;
        foreach (var value in outputs) { output = value; break; }
        ReadOnlySpan<float> logits = output.GetTensorDataAsSpan<float>();
        PlateOcrResult result = Decode(logits);
        sw.Stop();
        LastInferenceMs = sw.Elapsed.TotalMilliseconds;
        return result;
    }

    private void PrepareInput(Mat crop)
    {
        if (crop.NumberOfChannels > 1)
            CvInvoke.CvtColor(crop, _gray, ColorConversion.Bgr2Gray);
        else
            crop.CopyTo(_gray);

        CvInvoke.Resize(_gray, _resized, new Size(ImageWidth, ImageHeight), 0, 0, Inter.Area);
        _resized.ConvertTo(_float, DepthType.Cv32F, 1.0 / 255.0, 0.0);
        Marshal.Copy(_float.DataPointer, _inputBuffer, 0, _inputBuffer.Length);
    }

    private PlateOcrResult Decode(ReadOnlySpan<float> logits)
    {
        int classes = _labels.Length + 1;
        if (logits.Length < classes || logits.Length % classes != 0)
            return new PlateOcrResult(string.Empty, 0);

        int timesteps = logits.Length / classes;
        var characters = new List<PlateOcrCharacter>(10);
        int previous = -1;
        for (int t = 0; t < timesteps; t++)
        {
            int offset = t * classes;
            float max = float.NegativeInfinity;
            int index = 0;
            for (int c = 0; c < classes; c++)
            {
                float value = logits[offset + c];
                if (value > max) { max = value; index = c; }
            }

            float sum = 0;
            for (int c = 0; c < classes; c++) sum += MathF.Exp(logits[offset + c] - max);
            float probability = sum <= 0 ? 0 : MathF.Exp(logits[offset + index] - max) / sum;
            if (index != _blank && index != previous && index < _labels.Length)
            {
                characters.Add(new PlateOcrCharacter(_labels[index], probability));
            }
            previous = index;
        }

        return new PlateOcrResult(
            string.Concat(characters.Select(character => character.Symbol)),
            characters.Count == 0 ? 0 : characters.Average(character => character.Confidence),
            characters);
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
        _resized.Dispose();
        _float.Dispose();
    }
}
