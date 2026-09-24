using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using Microsoft.ML.OnnxRuntime;

namespace HshDetectionEngin.Plate;

/// <summary>
/// Reads plate characters from the end-to-end YOLO26 export used by
/// chars_best_v26. Its output is [1, 300, 6] rows of xyxy, confidence, class.
/// </summary>
internal sealed class Yolo26PlateRecognizer : IPlateTextRecognizer
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string[] _outputNames;
    private readonly string[] _labels;
    private readonly int _inputWidth;
    private readonly int _inputHeight;
    private readonly float _confidence;
    private readonly float[] _inputBuffer;
    private readonly OrtValue _inputTensor;
    private readonly RunOptions _runOptions = new();
    private readonly Dictionary<string, OrtValue> _inputs;
    private readonly Mat _letterbox;
    private readonly Mat _resized = new();
    private readonly Mat[] _planes = new Mat[3];
    private readonly Mat[] _floatPlanes = new Mat[3];
    private readonly VectorOfMat _sourcePlanes;
    private readonly VectorOfMat _destinationPlanes;
    private double _scale = 1;
    private int _padX;
    private int _padY;

    public double LastInferenceMs { get; private set; }

    public Yolo26PlateRecognizer(string modelPath, int intraOpThreads, float confidence)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("YOLO26 character model was not found.", modelPath);

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, intraOpThreads),
            EnableMemoryPattern = true
        };
        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputNames.First();
        _outputNames = _session.OutputNames.ToArray();
        var input = _session.InputMetadata.Values.FirstOrDefault()
            ?? throw new InvalidOperationException("YOLO26 character model has no input tensor.");
        int[] dimensions = input.Dimensions;
        _inputHeight = dimensions.Length >= 4 && dimensions[^2] > 0 ? dimensions[^2] : 416;
        _inputWidth = dimensions.Length >= 4 && dimensions[^1] > 0 ? dimensions[^1] : 416;
        _confidence = Math.Clamp(confidence, 0.05f, 0.95f);

        _labels = LoadLabels(modelPath);
        // MixChannels requires destination matrices to have the same depth
        // as the source. Allocate the RGB planes explicitly as 8-bit images;
        // they are normalized into the separate float planes afterwards.
        for (int channel = 0; channel < 3; channel++)
        {
            _planes[channel] = new Mat(_inputHeight, _inputWidth, DepthType.Cv8U, 1);
            _floatPlanes[channel] = new Mat(_inputHeight, _inputWidth, DepthType.Cv32F, 1);
        }
        // Allocate the source before VectorOfMat captures it. Constructing the
        // vector from an empty Mat leaves OpenCV with a stale one-channel
        // header and causes MixChannels to reject the source at runtime.
        _letterbox = new Mat(_inputHeight, _inputWidth, DepthType.Cv8U, 3);
        _inputBuffer = new float[3 * _inputWidth * _inputHeight];
        _inputTensor = OrtValue.CreateTensorValueFromMemory(_inputBuffer, [1, 3, _inputHeight, _inputWidth]);
        _inputs = new Dictionary<string, OrtValue>(1) { [_inputName] = _inputTensor };

        _sourcePlanes = new VectorOfMat(_letterbox);
        _destinationPlanes = new VectorOfMat(_planes[0], _planes[1], _planes[2]);
    }

    public PlateOcrResult Read(Mat crop)
    {
        if (crop.IsEmpty) return new PlateOcrResult(string.Empty, 0);

        PrepareInput(crop);
        var sw = Stopwatch.StartNew();
        using IDisposableReadOnlyCollection<OrtValue> outputs = _session.Run(_runOptions, _inputs, _outputNames);
        OrtValue output = outputs.First();
        ReadOnlySpan<float> values = output.GetTensorDataAsSpan<float>();
        List<CharacterBox> boxes = Decode(values, crop.Size);
        sw.Stop();
        LastInferenceMs = sw.Elapsed.TotalMilliseconds;

        if (boxes.Count < 8) return new PlateOcrResult(string.Empty, 0);
        if (boxes.Count > 8) boxes = boxes.OrderByDescending(box => box.Score).Take(8).OrderBy(box => box.CenterX).ToList();

        var characters = boxes
            .Select(box => new PlateOcrCharacter(
                box.Symbol,
                box.Score,
                new RectangleF(box.X1, box.Y1, box.X2 - box.X1, box.Y2 - box.Y1)))
            .ToArray();
        string text = string.Concat(characters.Select(character => character.Symbol));
        float confidence = characters.Length == 0 ? 0 : characters.Average(character => character.Confidence);
        return new PlateOcrResult(text, confidence, characters);
    }

    private void PrepareInput(Mat image)
    {
        _scale = Math.Min((double)_inputWidth / image.Width, (double)_inputHeight / image.Height);
        int width = Math.Max(1, (int)Math.Round(image.Width * _scale));
        int height = Math.Max(1, (int)Math.Round(image.Height * _scale));
        _padX = (_inputWidth - width) / 2;
        _padY = (_inputHeight - height) / 2;

        CvInvoke.Resize(image, _resized, new Size(width, height), 0, 0, Inter.Linear);
        CvInvoke.CopyMakeBorder(_resized, _letterbox, _padY, _inputHeight - height - _padY,
            _padX, _inputWidth - width - _padX, BorderType.Constant, new MCvScalar(114, 114, 114));
        CvInvoke.MixChannels(_sourcePlanes, _destinationPlanes, [2, 0, 1, 1, 0, 2]);

        int planeSize = _inputWidth * _inputHeight;
        for (int channel = 0; channel < 3; channel++)
        {
            _planes[channel].ConvertTo(_floatPlanes[channel], DepthType.Cv32F, 1.0 / 255.0, 0.0);
            Marshal.Copy(_floatPlanes[channel].DataPointer, _inputBuffer, channel * planeSize, planeSize);
        }
    }

    private List<CharacterBox> Decode(ReadOnlySpan<float> values, Size sourceSize)
    {
        const int rowSize = 6;
        if (_labels.Length == 0 || values.Length < rowSize || values.Length % rowSize != 0) return [];

        var result = new List<CharacterBox>(16);
        int rows = values.Length / rowSize;
        float inverseScale = (float)(1.0 / _scale);
        for (int row = 0; row < rows; row++)
        {
            int offset = row * rowSize;
            float score = values[offset + 4];
            if (score < _confidence) continue;

            float x1 = Math.Clamp((values[offset] - _padX) * inverseScale, 0, sourceSize.Width - 1);
            float y1 = Math.Clamp((values[offset + 1] - _padY) * inverseScale, 0, sourceSize.Height - 1);
            float x2 = Math.Clamp((values[offset + 2] - _padX) * inverseScale, 1, sourceSize.Width);
            float y2 = Math.Clamp((values[offset + 3] - _padY) * inverseScale, 1, sourceSize.Height);
            if (x2 <= x1 || y2 <= y1) continue;

            int classId = Math.Clamp((int)MathF.Round(values[offset + 5]), 0, _labels.Length - 1);
            string symbol = classId < _labels.Length
                ? PersianPlate.CharOf(_labels[classId], classId)
                : string.Empty;
            if (string.IsNullOrWhiteSpace(symbol)) continue;

            var box = new CharacterBox(x1, y1, x2, y2, score, symbol);
            if (result.Any(other => IoU(other, box) > 0.50f)) continue;
            result.Add(box);
        }
        return result.OrderBy(box => box.CenterX).ToList();
    }

    private static float IoU(CharacterBox a, CharacterBox b)
    {
        float x1 = Math.Max(a.X1, b.X1), y1 = Math.Max(a.Y1, b.Y1);
        float x2 = Math.Min(a.X2, b.X2), y2 = Math.Min(a.Y2, b.Y2);
        float intersection = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        float union = a.Area + b.Area - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static string[] LoadLabels(string modelPath)
    {
        string sidecar = Path.ChangeExtension(modelPath, ".labels.json");
        if (!File.Exists(sidecar) && Path.GetFileNameWithoutExtension(modelPath).EndsWith("_int8", StringComparison.OrdinalIgnoreCase))
        {
            string baseName = Path.GetFileNameWithoutExtension(modelPath)[..^5];
            sidecar = Path.Combine(Path.GetDirectoryName(modelPath) ?? string.Empty, baseName + ".labels.json");
        }
        if (!File.Exists(sidecar)) return [];
        try
        {
            string[]? labels = JsonSerializer.Deserialize<string[]>(File.ReadAllText(sidecar));
            return labels is { Length: > 0 } ? labels : [];
        }
        catch { return []; }
    }

    public void Dispose()
    {
        _inputs.Clear();
        _inputTensor.Dispose();
        _runOptions.Dispose();
        _session.Dispose();
        _sourcePlanes.Dispose();
        _destinationPlanes.Dispose();
        _letterbox.Dispose();
        _resized.Dispose();
        foreach (Mat plane in _planes) plane.Dispose();
        foreach (Mat plane in _floatPlanes) plane.Dispose();
    }

    private sealed record CharacterBox(float X1, float Y1, float X2, float Y2, float Score, string Symbol)
    {
        public float CenterX => (X1 + X2) / 2;
        public float Area => Math.Max(0, X2 - X1) * Math.Max(0, Y2 - Y1);
    }
}
