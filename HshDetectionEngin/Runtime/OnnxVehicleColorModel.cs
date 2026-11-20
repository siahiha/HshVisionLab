using System.Drawing;
using System.Runtime.InteropServices;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HshDetectionEngin;

/// <summary>NCHW float color classifiers with model-specific preprocessing.</summary>
internal sealed class OnnxVehicleColorModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly VehicleColorModelDescriptor _contract;
    public string ModelId { get; }
    public OnnxVehicleColorModel(string modelFile)
    {
        var model = VehicleModelCatalog.Resolve(modelFile, "VehicleColor");
        _contract = model.ColorContract!;
        ModelId = model.Name;
        _session = Open(model.Path);
        try
        {
            if (_session.InputMetadata.Count != 1 ||
                !_session.InputMetadata.TryGetValue(_contract.InputName, out var input) || input.ElementType != typeof(float) ||
                !input.Dimensions.SequenceEqual(new[] { 1, 3, _contract.InputHeight, _contract.InputWidth }) ||
                !_session.OutputMetadata.TryGetValue(_contract.OutputName, out var output) || output.ElementType != typeof(float) ||
                output.Dimensions.Length == 0 || output.Dimensions.Any(d => d <= 0) || output.Dimensions[0] != 1 ||
                output.Dimensions.Aggregate(1L, (a, b) => a * b) < _contract.OutputOffset + _contract.Labels.Length ||
                output.Dimensions.Aggregate(1L, (a, b) => a * b) > 4096)
                throw new InvalidDataException("Unexpected vehicle color model contract.");
        }
        catch { _session.Dispose(); throw; }
    }

    internal static InferenceSession Open(string package)
    {
        string temporary = ProtectedModelPackage.Materialize(package, "hsh-vehicle");
        try
        {
            using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            return new InferenceSession(temporary, options);
        }
        finally { File.Delete(temporary); }
    }

    internal static DenseTensor<float> BuildInput(Mat frame, Rectangle bounds, VehicleColorModelDescriptor contract, bool median)
    {
        using var crop = new Mat(frame, bounds);
        using var filtered = new Mat();
        if (median) CvInvoke.MedianBlur(crop, filtered, 3);
        using var resized = new Mat();
        CvInvoke.Resize(median ? filtered : crop, resized, new Size(contract.InputWidth, contract.InputHeight), 0, 0, Inter.Linear);
        int step = (int)resized.Step;
        var bytes = new byte[step * resized.Rows]; Marshal.Copy(resized.DataPointer, bytes, 0, bytes.Length);
        var tensor = new DenseTensor<float>(new[] { 1, 3, resized.Rows, resized.Cols });
        for (int y = 0; y < resized.Rows; y++)
        for (int x = 0; x < resized.Cols; x++)
        for (int c = 0; c < 3; c++)
            tensor[0, c, y, x] = (bytes[y * step + x * 3 + (contract.ColorOrder == "RGB" ? 2 - c : c)] * contract.Scale - contract.Mean[c]) / contract.Std[c];
        return tensor;
    }

    public PlateVehicleColorAnalyzer.Estimate Predict(Mat frame, Rectangle bounds, VehicleColorSettings settings)
    {
        if (frame.Depth != DepthType.Cv8U || frame.NumberOfChannels != 3) return new(null, 0, "UnsupportedImage");
        var input = BuildInput(frame, bounds, _contract, settings.MedianFilterEnabled);
        using var output = _session.Run([NamedOnnxValue.CreateFromTensor(_contract.InputName, input)], [_contract.OutputName]);
        return Decode(output.First().AsTensor<float>().ToArray(), _contract);
    }

    internal static PlateVehicleColorAnalyzer.Estimate Decode(float[] output, VehicleColorModelDescriptor contract)
    {
        if (output.Length < contract.OutputOffset + contract.Labels.Length) return new(null, 0, "InvalidModelOutput");
        float[] scores = output.Skip(contract.OutputOffset).Take(contract.Labels.Length).ToArray();
        if (scores.Any(v => !float.IsFinite(v))) return new(null, 0, "InvalidModelOutput");
        if (contract.OutputKind == "Logits")
        {
            float max = scores.Max();
            scores = scores.Select(v => MathF.Exp(v - max)).ToArray();
            float sum = scores.Sum();
            scores = scores.Select(v => v / sum).ToArray();
        }
        else if (scores.Any(v => v < 0 || v > 1) ||
            (contract.OutputKind == "Probabilities" && Math.Abs(scores.Sum() - 1) > .02f))
            return new(null, 0, "InvalidModelOutput");
        int top = Array.IndexOf(scores, scores.Max());
        // The score is descriptive only. Color never gates the plate or its text.
        return new(contract.Labels[top], scores[top], "WholeVehicleModel");
    }
    public void Dispose() => _session.Dispose();
}
