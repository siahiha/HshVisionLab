using System.Drawing;
using HshDetectionService;

namespace WebRtcStreaming.Tests;

internal static class GeometryTests
{
    public static void FitsAlignedBounds()
    {
        (Size Source, Size Bounds)[] cases =
        [
            (new(1920, 1080), new(1280, 720)),
            (new(3840, 2160), new(512, 288)),
            (new(1024, 768), new(640, 480)),
            (new(1080, 1920), new(360, 640)),
            (new(1920, 1080), new(301, 201)),
            (new(321, 241), new(1280, 720)),
            (new(1920, 1080), new(2000, 1500))
        ];

        foreach ((Size source, Size bounds) in cases)
        {
            Size output = VideoFrameGeometry.Fit(source, bounds);
            TestAssert.That(output.Width >= 16 && output.Height >= 16,
                $"Encoder geometry became empty for {source} inside {bounds}.");
            TestAssert.That(output.Width % 16 == 0 && output.Height % 16 == 0,
                $"Encoder geometry {output} is not aligned to 16 pixels.");
            TestAssert.That(output.Width <= bounds.Width && output.Height <= bounds.Height,
                $"Encoder geometry {output} exceeded viewport bounds {bounds}.");
            TestAssert.That(output.Width <= 1280 && output.Height <= 720,
                $"Encoder geometry {output} exceeded the service ceiling.");
            TestAssert.That(output.Width <= source.Width && output.Height <= source.Height,
                $"Encoder geometry {output} enlarged source {source}.");

            double idealAspect = source.Width / (double)source.Height;
            double actualAspect = output.Width / (double)output.Height;
            double roundingTolerance = 16.0 / Math.Min(output.Width, output.Height);
            TestAssert.That(Math.Abs(actualAspect / idealAspect - 1) <= roundingTolerance,
                $"Encoder geometry {output} changed source aspect ratio beyond alignment rounding.");
        }
    }

    public static void RetainsExactAlignedAspect()
    {
        TestAssert.Equal(new Size(1280, 720), VideoFrameGeometry.Fit(new Size(1920, 1080), new Size(1280, 720)),
            "The default 16:9 preview was unnecessarily shrunk or distorted.");
        TestAssert.Equal(new Size(640, 480), VideoFrameGeometry.Fit(new Size(1024, 768), new Size(640, 480)),
            "A 4:3 viewport lost its aligned source aspect ratio.");
        TestAssert.Equal(new Size(320, 240), VideoFrameGeometry.Fit(new Size(320, 240), new Size(1280, 720)),
            "Small source images should retain their aligned dimensions.");
    }
}
