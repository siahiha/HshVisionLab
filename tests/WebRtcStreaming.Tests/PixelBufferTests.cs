using System.Drawing;
using System.Drawing.Imaging;
using HshDetectionService;

namespace WebRtcStreaming.Tests;

internal static class PixelBufferTests
{
    private static Bitmap Solid(Color color, int width = 128, int height = 96)
    {
        var image = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using Graphics graphics = Graphics.FromImage(image);
        graphics.Clear(color);
        return image;
    }

    public static void ReusesStableStorage()
    {
        using var buffer = new ReusableVideoFrameBuffer();
        using Bitmap dark = Solid(Color.FromArgb(30, 30, 30));
        using Bitmap light = Solid(Color.FromArgb(200, 200, 200));
        byte[] initial = buffer.Prepare(dark, new Size(128, 96));
        byte firstLuma = initial[0];
        for (int frame = 0; frame < 100; frame++)
        {
            byte[] reused = buffer.Prepare(light, new Size(128, 96));
            TestAssert.That(ReferenceEquals(initial, reused),
                "Stable frame dimensions allocated a new managed I420 array.");
        }
        TestAssert.That(initial[0] > firstLuma + 100, "Reused storage did not receive the latest pixels.");
        TestAssert.Equal(128, buffer.Width, "Prepared I420 width changed unexpectedly.");
        TestAssert.Equal(96, buffer.Height, "Prepared I420 height changed unexpectedly.");
        TestAssert.Equal(128 * 96 * 3 / 2, initial.Length, "The I420 array has an unexpected layout/size.");

        byte[] smaller = buffer.Prepare(dark, new Size(64, 48));
        TestAssert.Equal(64, buffer.Width, "Viewport resizing did not update encoder width.");
        TestAssert.Equal(48, buffer.Height, "Viewport resizing did not update encoder height.");
        TestAssert.Equal(64 * 48 * 3 / 2, smaller.Length, "Viewport resizing left an oversized I420 payload.");
        TestAssert.That(ReferenceEquals(smaller, buffer.Prepare(light, new Size(64, 48))),
            "The new viewport geometry did not stabilize its I420 allocation.");
    }

    public static void PreservesImageCoverage()
    {
        using var source = new Bitmap(192, 128, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(source))
        {
            graphics.FillRectangle(Brushes.Red, 0, 0, 96, 64);
            graphics.FillRectangle(Brushes.Lime, 96, 0, 96, 64);
            graphics.FillRectangle(Brushes.Blue, 0, 64, 96, 64);
            graphics.FillRectangle(Brushes.White, 96, 64, 96, 64);
        }
        using var buffer = new ReusableVideoFrameBuffer();
        byte[] pixels = buffer.Prepare(source, new Size(96, 64));
        TestAssert.Equal(96, buffer.Width, "A 3:2 source did not fit its aligned 3:2 viewport.");
        TestAssert.Equal(64, buffer.Height, "A 3:2 source did not fit its aligned 3:2 viewport.");

        byte Luma(double x, double y) => pixels[(int)(y * buffer.Height) * buffer.Width + (int)(x * buffer.Width)];
        static bool Near(byte actual, int expected) => Math.Abs(actual - expected) <= 8;
        TestAssert.That(Near(Luma(.25, .25), 82) && Near(Luma(.75, .25), 145) &&
                        Near(Luma(.25, .75), 41) && Near(Luma(.75, .75), 235),
            "Viewport scaling cropped, padded or moved normalized source/ROI coordinates.");
        TestAssert.That(Near(pixels[0], 82) && Near(pixels[buffer.Width - 1], 145) &&
                        Near(pixels[(buffer.Height - 1) * buffer.Width], 41) &&
                        Near(pixels[buffer.Width * buffer.Height - 1], 235),
            "The encoded image introduced black borders or lost source corners.");
    }

    public static void HasCorrectPlaneLayout()
    {
        using Bitmap red = Solid(Color.Red, 64, 48);
        using var buffer = new ReusableVideoFrameBuffer();
        byte[] pixels = buffer.Prepare(red, new Size(64, 48));
        int area = buffer.Width * buffer.Height;
        TestAssert.That(Math.Abs(pixels[0] - 82) <= 8,
            "The Y plane does not contain red-source luma.");
        TestAssert.That(Math.Abs(pixels[area] - 90) <= 8,
            "The I420 U plane is missing, swapped or misaligned.");
        TestAssert.That(Math.Abs(pixels[area + area / 4] - 240) <= 8,
            "The I420 V plane is missing, swapped or misaligned.");
    }

    public static void NativeEncoderResizes()
    {
        using Bitmap source = Solid(Color.MidnightBlue, 1280, 720);
        using var encoder = new Vp8CameraFrameEncoder();
        Size[] transitions = [new(640, 360), new(320, 180), new(1280, 720), new(1280, 720), new(640, 360)];
        Size previous = Size.Empty;
        foreach (Size bounds in transitions)
        {
            using (Graphics graphics = Graphics.FromImage(source))
            {
                graphics.Clear(Color.MidnightBlue);
                graphics.FillRectangle(Brushes.White, bounds.Width / 4, 200, 160, 240);
            }
            Size expected = VideoFrameGeometry.Fit(source.Size, bounds);
            EncodedVideoFrame frame;
            try { frame = encoder.Encode(source, bounds, forceKeyFrame: false); }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Native VP8 resize from {previous} to {expected} failed.", error);
            }
            TestAssert.That(frame.Sample.Length >= 10, $"Native VP8 output stopped after changing bounds to {bounds}.");
            TestAssert.Equal(expected.Width, frame.Width, "Native encoder output width did not follow viewport resize.");
            TestAssert.Equal(expected.Height, frame.Height, "Native encoder output height did not follow viewport resize.");
            if (expected != previous)
                TestAssert.That(frame.IsKeyFrame, $"The changed native geometry {expected} did not produce a keyframe.");
            TestAssert.Equal((frame.Sample[0] & 1) == 0, frame.IsKeyFrame,
                "The encoded frame type disagrees with the VP8 bitstream.");
            if (frame.IsKeyFrame)
            {
                TestAssert.That(frame.Sample[3] == 0x9d && frame.Sample[4] == 0x01 && frame.Sample[5] == 0x2a,
                    "A native keyframe omitted the VP8 sync signature.");
                int headerWidth = (frame.Sample[6] | frame.Sample[7] << 8) & 0x3fff;
                int headerHeight = (frame.Sample[8] | frame.Sample[9] << 8) & 0x3fff;
                TestAssert.Equal(expected.Width, headerWidth, "VP8 keyframe header retained an old frame width.");
                TestAssert.Equal(expected.Height, headerHeight, "VP8 keyframe header retained an old frame height.");
            }
            previous = expected;
        }
    }
}
