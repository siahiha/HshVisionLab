using System.Drawing;
using System.Runtime.InteropServices;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HshDetectionEngin;

/// <summary>Official YOLOX-Nano asset, raw BGR 0..255 with top-left letterbox.</summary>
internal sealed class OnnxVehicleDetector : IDisposable
{
    internal sealed record Vehicle(Rectangle Bounds, float Score, int ClassId);
    private readonly InferenceSession _session;
    private readonly string _input, _output;
    private const int InputSize = 416;
    public OnnxVehicleDetector(string modelFile)
    {
        _session = OnnxVehicleColorModel.Open(VehicleModelCatalog.Resolve(modelFile, "VehicleDetection").Path);
        try
        {
            var input = _session.InputMetadata.Single(); var output = _session.OutputMetadata.Single();
            _input = input.Key; _output = output.Key;
            if (input.Value.ElementType != typeof(float) || !input.Value.Dimensions.SequenceEqual(new[] { 1, 3, InputSize, InputSize }) ||
                output.Value.ElementType != typeof(float) || !output.Value.Dimensions.SequenceEqual(new[] { 1, 3549, 85 }))
                throw new InvalidDataException("Unexpected YOLOX-Nano vehicle detector contract.");
        }
        catch { _session.Dispose(); throw; }
    }

    public IReadOnlyList<Vehicle> Detect(Mat frame, VehicleColorSettings settings)
    {
        if (frame.Depth != DepthType.Cv8U || frame.NumberOfChannels != 3) return [];
        float ratio = Math.Min((float)InputSize / frame.Width, (float)InputSize / frame.Height);
        int width = Math.Clamp((int)(frame.Width * ratio), 1, InputSize), height = Math.Clamp((int)(frame.Height * ratio), 1, InputSize);
        using var resized = new Mat(); CvInvoke.Resize(frame, resized, new Size(width, height), 0, 0, Inter.Linear);
        int step = (int)resized.Step;
        var bytes = new byte[step * height]; Marshal.Copy(resized.DataPointer, bytes, 0, bytes.Length);
        var tensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        for (int c = 0; c < 3; c++)
        for (int y = 0; y < InputSize; y++)
        for (int x = 0; x < InputSize; x++)
            tensor[0, c, y, x] = y < height && x < width ? bytes[y * step + x * 3 + c] : 114;
        using var outputs = _session.Run([NamedOnnxValue.CreateFromTensor(_input, tensor)], [_output]);
        var data = outputs.First().AsTensor<float>();
        var candidates = new List<Vehicle>();
        int index = 0;
        foreach (int stride in new[] { 8, 16, 32 })
        for (int y = 0; y < InputSize / stride; y++)
        for (int x = 0; x < InputSize / stride; x++, index++)
        {
            int best = 0;
            for (int c = 1; c < 80; c++) if (data[0, index, 5 + c] > data[0, index, 5 + best]) best = c;
            if (best is not (2 or 5 or 7)) continue; // COCO car, bus, truck
            float score = data[0, index, 4] * data[0, index, 5 + best];
            if (!float.IsFinite(score) || score < settings.VehicleDetectionConfidence) continue;
            float cx = (data[0, index, 0] + x) * stride / ratio, cy = (data[0, index, 1] + y) * stride / ratio;
            float w = MathF.Exp(data[0, index, 2]) * stride / ratio, h = MathF.Exp(data[0, index, 3]) * stride / ratio;
            if (!float.IsFinite(cx + cy + w + h) || w <= 0 || h <= 0) continue;
            int left = (int)Math.Clamp(MathF.Floor(cx - w / 2), 0, frame.Width), top = (int)Math.Clamp(MathF.Floor(cy - h / 2), 0, frame.Height);
            int right = (int)Math.Clamp(MathF.Ceiling(cx + w / 2), 0, frame.Width), bottom = (int)Math.Clamp(MathF.Ceiling(cy + h / 2), 0, frame.Height);
            if (right - left >= 12 && bottom - top >= 12) candidates.Add(new(Rectangle.FromLTRB(left, top, right, bottom), score, best));
        }
        var kept = new List<Vehicle>();
        foreach (var candidate in candidates.OrderByDescending(v => v.Score).Take(512))
        {
            if (kept.Any(v => IoU(v.Bounds, candidate.Bounds) > settings.VehicleNmsIoU)) continue;
            kept.Add(candidate);
            if (kept.Count >= 128) break;
        }
        return kept;
    }

    internal static Vehicle? Match(Rectangle plate, IReadOnlyList<Vehicle> vehicles)
    {
        if (plate.Width <= 0 || plate.Height <= 0) return null;
        var center = new Point(plate.Left + plate.Width / 2, plate.Top + plate.Height / 2);
        var matches = vehicles.Where(v => v.Bounds.Contains(center) && Coverage(plate, v.Bounds) >= .9 &&
            (double)v.Bounds.Width * v.Bounds.Height >= (double)plate.Width * plate.Height * 2)
            .OrderBy(v => (double)v.Bounds.Width * v.Bounds.Height).ToArray();
        if (matches.Length == 0) return null;
        // Similar overlapping enclosing boxes cannot be assigned reliably.
        if (matches.Length > 1 && (double)matches[1].Bounds.Width * matches[1].Bounds.Height <
            (double)matches[0].Bounds.Width * matches[0].Bounds.Height * 1.2) return null;
        return matches[0];
    }
    private static double Coverage(Rectangle plate, Rectangle vehicle)
    {
        var overlap = Rectangle.Intersect(plate, vehicle);
        return Math.Max(0, (double)overlap.Width * overlap.Height) / ((double)plate.Width * plate.Height);
    }
    private static double IoU(Rectangle a, Rectangle b)
    {
        var intersection = Rectangle.Intersect(a, b);
        double area = Math.Max(0, (double)intersection.Width * intersection.Height);
        return area / ((double)a.Width * a.Height + (double)b.Width * b.Height - area);
    }
    public void Dispose() => _session.Dispose();
}
