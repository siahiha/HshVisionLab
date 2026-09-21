using Emgu.CV;
using System.Drawing;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace HshDetectionEngin.Plate;

internal enum PlatePreprocessingMode
{
    None,
    Standard,
    Advanced
}

/// <summary>
/// Lightweight preprocessing for the plate crop before the second (character)
/// inference. It intentionally does not touch the full camera frame.
/// </summary>
internal static class PlatePreprocessor
{
    public static PlatePreprocessingMode Parse(string? value)
    {
        return Enum.TryParse<PlatePreprocessingMode>(value, true, out var mode)
            ? mode
            : PlatePreprocessingMode.Standard;
    }

    public static Mat Apply(Mat source, PlatePreprocessingMode mode)
    {
        if (mode == PlatePreprocessingMode.None)
            return source.Clone();

        double factor = mode == PlatePreprocessingMode.Advanced ? 2.5 : 2.0;
        int targetWidth = Math.Clamp((int)Math.Round(source.Width * factor), 160, mode == PlatePreprocessingMode.Advanced ? 800 : 640);
        int targetHeight = Math.Max(12, (int)Math.Round(source.Height * (targetWidth / (double)Math.Max(1, source.Width))));

        var resized = new Mat();
        CvInvoke.Resize(source, resized, new Size(targetWidth, targetHeight), 0, 0, Inter.Cubic);

        if (mode == PlatePreprocessingMode.Standard)
        {
            var standard = new Mat();
            // Mild contrast/brightness correction only; preserve the original color information.
            resized.ConvertTo(standard, DepthType.Cv8U, 1.08, 3.0);
            resized.Dispose();
            return standard;
        }

        // Advanced: moderate contrast correction + unsharp masking.
        // No aggressive thresholding is used because it can destroy Persian glyph details.
        var contrast = new Mat();
        resized.ConvertTo(contrast, DepthType.Cv8U, 1.15, 5.0);
        resized.Dispose();

        var blurred = new Mat();
        var sharpened = new Mat();
        CvInvoke.GaussianBlur(contrast, blurred, new Size(0, 0), 1.1);
        CvInvoke.AddWeighted(contrast, 1.35, blurred, -0.35, 0.0, sharpened);
        contrast.Dispose();
        blurred.Dispose();
        return sharpened;
    }
}
