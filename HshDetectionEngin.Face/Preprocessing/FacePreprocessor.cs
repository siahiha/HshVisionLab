using Emgu.CV;
using Emgu.CV.CvEnum;

namespace HshDetectionEngin.Face;

public enum FacePreprocessingMode { None, Standard, Advanced }

/// <summary>Optional CPU-friendly image preparation before YuNet inference.</summary>
public static class FacePreprocessor
{
    public static FacePreprocessingMode Parse(string? value) =>
        Enum.TryParse<FacePreprocessingMode>(value, true, out var mode) ? mode : FacePreprocessingMode.None;

    public static Mat Apply(Mat source, FacePreprocessingMode mode)
    {
        if (mode == FacePreprocessingMode.None) return source.Clone();

        var result = new Mat();
        if (mode == FacePreprocessingMode.Standard)
        {
            CvInvoke.ConvertScaleAbs(source, result, 1.12, 4);
            return result;
        }

        using var gray = new Mat();
        using var equalized = new Mat();
        CvInvoke.CvtColor(source, gray, ColorConversion.Bgr2Gray);
        CvInvoke.EqualizeHist(gray, equalized);
        CvInvoke.CvtColor(equalized, result, ColorConversion.Gray2Bgr);
        return result;
    }
}
