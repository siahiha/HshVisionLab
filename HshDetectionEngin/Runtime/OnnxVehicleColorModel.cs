using System.Drawing;
using System.Runtime.InteropServices;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HshDetectionEngin;

/// <summary>Intel OMZ 0039. BGR 0..255; preprocessing and softmax are already in the graph.</summary>
internal sealed class OnnxVehicleColorModel : IDisposable
{
    internal const string ModelId = "vehicle-attributes-recognition-barrier-0039";
    private static readonly string[] Colors = ["White", "SilverGray", "Yellow", "Red", "Green", "Blue", "Black"];
    private readonly InferenceSession _session;

    public OnnxVehicleColorModel()
    {
        string package = Path.Combine(AppContext.BaseDirectory, "Models", "VehicleColor", ModelId + ".hshmodel");
        string temporary = ProtectedModelPackage.Materialize(package, "hsh-vehicle-color");
        try
        {
            using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            _session = new InferenceSession(temporary, options);
            if (!_session.InputMetadata.TryGetValue("input", out var input) || !input.Dimensions.SequenceEqual(new[] { 1, 3, 72, 72 }) ||
                !_session.OutputMetadata.TryGetValue("color", out var output) || !output.Dimensions.SequenceEqual(new[] { 1, 7, 1, 1 }))
            { _session.Dispose(); throw new InvalidDataException("Unexpected vehicle color model contract."); }
            try
            {
                using var warmup = _session.Run([NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(new[] { 1, 3, 72, 72 }))], ["color"]);
            }
            catch { _session.Dispose(); throw; }
        }
        finally { File.Delete(temporary); }
    }

    internal static DenseTensor<float> BuildInput(Mat frame, Rectangle bounds, bool medianFilterEnabled = true)
    {
        using var crop = new Mat(frame, bounds);
        using var filtered = new Mat();
        if (medianFilterEnabled) CvInvoke.MedianBlur(crop, filtered, 3);
        using var resized = new Mat();
        CvInvoke.Resize(medianFilterEnabled ? filtered : crop, resized, new Size(72, 72), 0, 0, Inter.Linear);
        int step = (int)resized.Step;
        var bytes = new byte[step * 72]; Marshal.Copy(resized.DataPointer, bytes, 0, bytes.Length);
        var tensor = new DenseTensor<float>(new[] { 1, 3, 72, 72 });
        for (int y = 0; y < 72; y++)
        for (int x = 0; x < 72; x++)
        for (int c = 0; c < 3; c++) tensor[0, c, y, x] = bytes[y * step + x * 3 + c];
        return tensor;
    }

    public PlateVehicleColorAnalyzer.Estimate Predict(Mat frame, Rectangle bounds, VehicleColorSettings settings)
    {
        if (frame.Depth != DepthType.Cv8U || frame.NumberOfChannels != 3)
            return new(null, 0, "UnsupportedImage");
        var input = BuildInput(frame, bounds, settings.MedianFilterEnabled);
        using var output = _session.Run([NamedOnnxValue.CreateFromTensor("input", input)], ["color"]);
        return CheckImageConsistency(Decode(output.First().AsTensor<float>().ToArray(), settings), input);
    }

    // A rejection guard, not a second classifier: the vehicle-trained network can
    // mistake a reflection on a small dark crop for white bodywork. Never relabel
    // an inconsistent prediction as black or infer the actual paint from luminance.
    internal static PlateVehicleColorAnalyzer.Estimate CheckImageConsistency(
        PlateVehicleColorAnalyzer.Estimate prediction, DenseTensor<float> input)
    {
        if (prediction.Color != "White") return prediction;
        int dark = 0;
        for (int y = 0; y < 72; y++)
        for (int x = 0; x < 72; x++)
        {
            float luminance = .114f * input[0, 0, y, x] + .587f * input[0, 1, y, x] + .299f * input[0, 2, y, x];
            if (luminance < 80) dark++;
        }
        return dark >= 72 * 72 * .65f
            ? new(null, prediction.Score, "WhiteContradictsDarkRegion") : prediction;
    }

    internal static PlateVehicleColorAnalyzer.Estimate Decode(float[] scores, VehicleColorSettings settings)
    {
        if (scores.Length != Colors.Length || scores.Any(v => !float.IsFinite(v) || v < 0 || v > 1) || Math.Abs(scores.Sum() - 1) > .01f)
            return new(null, 0, "InvalidModelOutput");
        int[] order = Enumerable.Range(0, scores.Length).OrderByDescending(i => scores[i]).ToArray();
        float top = scores[order[0]];
        if (top < settings.ModelConfidence) return new(null, top, "LowConfidence");
        if (top - scores[order[1]] < settings.ModelMinimumMargin) return new(null, top, "AmbiguousColor");
        return new(Colors[order[0]], top, "PretrainedModel");
    }

    public void Dispose() => _session.Dispose();
}
