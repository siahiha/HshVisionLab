using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using HshDetectionEngin;
using HshVisionLab;

internal static partial class Program
{
    private static DesktopPreviewFrame PreviewFrame(Bitmap? image = null)
        => new(image ?? new Bitmap(2, 2), new CameraRuntime.PreviewFrameTiming(0, 0, 0));

    private static void CheckPreviewDisposed(DesktopPreviewFrame frame, Bitmap image)
    {
        bool frameDisposed = false, bitmapDisposed = false;
        try { _ = frame.Image; }
        catch (ObjectDisposedException) { frameDisposed = true; }
        try { _ = image.GetPixel(0, 0); }
        catch (Exception error) when (error is ArgumentException or ExternalException or ObjectDisposedException)
        { bitmapDisposed = true; }
        Check(frameDisposed && bitmapDisposed, "A released preview retained its bitmap.");
    }

    private static void PreviewOwnership()
    {
        using var buffer = new DesktopPreviewBuffer();
        var firstImage = new Bitmap(2, 2);
        var first = PreviewFrame(firstImage);
        Check(buffer.Offer(first), "The first frame did not request a UI callback.");
        using var second = PreviewFrame();
        Check(!buffer.Offer(second), "A newer waiting frame queued an extra callback.");
        CheckPreviewDisposed(first, firstImage);
        Check(buffer.ReplacedFrames == 1 && ReferenceEquals(buffer.Take(), second),
            "The UI did not receive ownership of the newest original frame.");
        Check(second.Image.Width == 2 && buffer.Take() is null, "Taking a frame disposed it or retained another copy.");

        var hiddenImage = new Bitmap(2, 2);
        var hidden = PreviewFrame(hiddenImage);
        Check(buffer.Offer(hidden), "A consumed callback prevented the next update.");
        buffer.SetEnabled(false);
        CheckPreviewDisposed(hidden, hiddenImage);
        var rejectedImage = new Bitmap(2, 2);
        var rejected = PreviewFrame(rejectedImage);
        Check(!buffer.Offer(rejected), "A hidden preview queued a callback.");
        CheckPreviewDisposed(rejected, rejectedImage);
        buffer.SetEnabled(true);
        using var resumed = PreviewFrame();
        Check(!buffer.Offer(resumed) && ReferenceEquals(buffer.Take(), resumed),
            "Rapid hide/show queued a duplicate callback or discarded the resumed image.");

        var cancelledImage = new Bitmap(2, 2);
        var cancelled = PreviewFrame(cancelledImage);
        Check(buffer.Offer(cancelled), "The next frame did not request a callback.");
        buffer.CancelPending();
        CheckPreviewDisposed(cancelled, cancelledImage);
        using var recovered = PreviewFrame();
        Check(buffer.Offer(recovered) && ReferenceEquals(buffer.Take(), recovered),
            "A failed UI post permanently stalled future previews.");

        var shutdownImage = new Bitmap(2, 2);
        var shutdown = PreviewFrame(shutdownImage);
        buffer.Offer(shutdown);
        buffer.Dispose();
        CheckPreviewDisposed(shutdown, shutdownImage);
        Check(buffer.Take() is null && !buffer.Enabled, "A queued callback retained an image after shutdown.");
        buffer.SetEnabled(true);
        var lateImage = new Bitmap(2, 2);
        var late = PreviewFrame(lateImage);
        Check(!buffer.Offer(late) && !buffer.Enabled, "A late camera frame revived a disposed preview.");
        CheckPreviewDisposed(late, lateImage);
        late.Dispose();

        // Simultaneous producers must still retain one frame and one callback.
        using var concurrent = new DesktopPreviewBuffer();
        var offered = new DesktopPreviewFrame[256];
        var images = new Bitmap[offered.Length];
        int callbacks = 0;
        Parallel.For(0, offered.Length, index =>
        {
            images[index] = new Bitmap(2, 2);
            offered[index] = PreviewFrame(images[index]);
            if (concurrent.Offer(offered[index])) Interlocked.Increment(ref callbacks);
        });
        using var winner = concurrent.Take();
        Check(winner is not null && callbacks == 1 && concurrent.ReplacedFrames == offered.Length - 1,
            "Concurrent production grew the pending frame or UI callback count.");
        for (int index = 0; index < offered.Length; index++)
            if (!ReferenceEquals(offered[index], winner)) CheckPreviewDisposed(offered[index], images[index]);
    }

    private static void PreviewMetrics()
    {
        using var buffer = new DesktopPreviewBuffer();
        long origin = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        static long Milliseconds(int value) => (long)(value * (double)Stopwatch.Frequency / 1000);
        for (int index = 1; index <= 150; index++)
        {
            using var frame = new DesktopPreviewFrame(new Bitmap(1, 1),
                new CameraRuntime.PreviewFrameTiming(origin, origin + Milliseconds(1), index));
            buffer.RecordPaint(frame, origin + Milliseconds(index));
            buffer.RecordPaint(frame, origin + Milliseconds(index + 500));
        }
        PreviewDisplayMetrics metrics = buffer.Metrics;
        Check(metrics.SampleCount == 120, "The preview latency sample history was not bounded.");
        Check(Math.Abs(metrics.LastRenderMs - 1) < .01 && Math.Abs(metrics.LastDisplayMs - 150) < .01,
            "Preview timestamps did not measure receive-to-ready and receive-to-paint latency.");
        Check(Math.Abs(metrics.MeanDisplayMs - 90.5) < .01 && Math.Abs(metrics.P95DisplayMs - 144) < .01,
            "Preview latency statistics used evicted or repeatedly painted frames.");
        Check(metrics.DisplayFps >= 0 && double.IsFinite(metrics.DisplayFps), "Display FPS was invalid.");

        buffer.ResetMetrics();
        Check(buffer.Metrics.SampleCount == 0, "Restarting retained the prior latency measurements.");
        using var rejectedMetrics = new DesktopPreviewBuffer();
        using var invalid = new DesktopPreviewFrame(new Bitmap(1, 1), new CameraRuntime.PreviewFrameTiming(10, 5, 151));
        rejectedMetrics.RecordPaint(invalid, 20);
        rejectedMetrics.SetEnabled(false);
        using var hidden = new DesktopPreviewFrame(new Bitmap(1, 1), new CameraRuntime.PreviewFrameTiming(origin, origin + 1, 152));
        rejectedMetrics.RecordPaint(hidden, origin + Milliseconds(1));
        Check(rejectedMetrics.Metrics.SampleCount == 0, "Invalid or hidden previews entered latency statistics.");
    }

    private static Mat RectangularPreviewFrame(byte pixel)
    {
        var frame = new Mat(360, 640, DepthType.Cv8U, 3);
        frame.SetTo(new MCvScalar(pixel, pixel, pixel));
        return frame;
    }

    private static CameraSettings PreviewSettings() => new()
    {
        Name = "Preview test", SourceUrl = "test", Enabled = true,
        DrawBoxes = false, MotionGateEnabled = false, RoiEnabled = false
    };

    private static void PreviewRuntimeDemand()
    {
        var source = new TestFrameSource();
        using var camera = new CameraRuntime(PreviewSettings(), source, new ProcessingRegistry());
        var received = new ConcurrentQueue<(Size Size, byte Pixel, CameraRuntime.PreviewFrameTiming Timing)>();
        camera.FrameReady += (_, image) =>
        {
            using (image) received.Enqueue((image.Size, image.GetPixel(0, 0).R, camera.PreviewTiming));
        };
        camera.ConfigurePreview(new Size(160, 90), enabled: false);
        camera.Start();
        try
        {
            source.Feed(RectangularPreviewFrame(10));
            Until(() => source.Dequeued >= 1);
            Check(received.IsEmpty, "A hidden preview rendered a bitmap.");
            Check(camera.LastFrameSize == new Size(640, 360), "Hiding preview stopped source-resolution updates.");

            camera.ConfigurePreview(new Size(160, 90));
            long beforeReceive = Stopwatch.GetTimestamp();
            source.Feed(RectangularPreviewFrame(30));
            Until(() => received.Count == 1);
            var initial = received.First();
            Check(initial.Size == new Size(160, 90) && initial.Pixel == 30,
                "Resuming did not render the next fresh frame at the requested preview size.");
            Check(initial.Timing.Sequence == 2 && initial.Timing.ReceivedTimestamp >= beforeReceive &&
                initial.Timing.ReadyTimestamp >= initial.Timing.ReceivedTimestamp,
                "Preview timing lost the receive sequence or monotonic timestamps.");
            Check(camera.LastFrameSize == new Size(640, 360), "Resizing preview changed detection/ROI source coordinates.");

            camera.ConfigurePreview(new Size(80, 45));
            Thread.Sleep(80); // Allow the bounded 15 FPS preview deadline to pass.
            source.Feed(RectangularPreviewFrame(50));
            Until(() => received.Count == 2);
            Check(received.Last().Size == new Size(80, 45), "Changing viewport size retained the old rendering allocation.");

            camera.ConfigurePreview(new Size(80, 45), enabled: false);
            source.Feed(RectangularPreviewFrame(90));
            Until(() => source.Dequeued >= 4);
            Check(received.Count == 2, "Hidden preview continued producing display frames.");
        }
        finally { camera.Stop(); }
    }

    private static void PreviewLatestFrame()
    {
        var source = new TestFrameSource();
        using var camera = new CameraRuntime(PreviewSettings(), source, new ProcessingRegistry());
        using var blocked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var received = new ConcurrentQueue<(byte Pixel, long Sequence)>();
        camera.FrameReady += (_, image) =>
        {
            using (image)
            {
                received.Enqueue((image.GetPixel(0, 0).R, camera.PreviewTiming.Sequence));
                if (received.Count == 1)
                {
                    blocked.Set();
                    if (!release.Wait(5000)) throw new TimeoutException("Preview consumer was not released.");
                }
            }
        };
        camera.ConfigurePreview(new Size(160, 90));
        camera.Start();
        try
        {
            source.Feed(RectangularPreviewFrame(10));
            Check(blocked.Wait(3000), "The first preview did not reach the consumer.");
            Thread.Sleep(80);
            source.Feed(RectangularPreviewFrame(50));
            Thread.Sleep(80);
            source.Feed(RectangularPreviewFrame(90));
            release.Set();
            Until(() => received.Count == 2);
            Check(received.Last() == ((byte)90, 3L), "A slow preview consumer displayed an older waiting frame.");
        }
        finally { release.Set(); camera.Stop(); }
    }
}
