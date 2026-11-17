using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using HshDetectionEngin;

internal static partial class Program
{
    private static Mat?[] PreviewNativeSlots(CameraRuntime camera)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object gate = typeof(CameraRuntime).GetField("_previewFrameGate", flags)!.GetValue(camera)!;
        lock (gate)
        {
            return new[] { "_latestPreviewFrame", "_sparePreviewFrame", "_secondSparePreviewFrame" }
                .Select(name => (Mat?)typeof(CameraRuntime).GetField(name, flags)!.GetValue(camera)).ToArray();
        }
    }

    private static void RuntimePreviewReuse()
    {
        var source = new TestFrameSource();
        using var camera = new CameraRuntime(PreviewSettings(), source, new ProcessingRegistry());
        using var blocked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int delivered = 0;
        camera.FrameReady += (_, image) =>
        {
            image.Dispose();
            if (Interlocked.Increment(ref delivered) == 1)
            {
                blocked.Set();
                release.Wait(5000);
            }
        };
        camera.Start();
        try
        {
            source.Feed(RectangularPreviewFrame(10));
            Check(blocked.Wait(3000), "The preview did not reach its consumer.");
            Thread.Sleep(80);
            source.Feed(RectangularPreviewFrame(30));
            Mat pending = PreviewNativeSlots(camera)[0]
                ?? throw new InvalidOperationException("The stalled preview did not retain a pending source frame.");
            IntPtr pendingPixels = pending.DataPointer;
            Thread.Sleep(80);
            source.Feed(RectangularPreviewFrame(50));
            Check(ReferenceEquals(PreviewNativeSlots(camera)[0], pending) && pending.DataPointer == pendingPixels,
                "Replacing a waiting preview allocated another native source image.");

            release.Set();
            Until(() => Volatile.Read(ref delivered) == 2 && PreviewNativeSlots(camera).Count(frame => frame is not null) == 2);
            Mat[] initial = PreviewNativeSlots(camera).OfType<Mat>().ToArray();
            Check(initial.Distinct().Count() == 2 && initial.Contains(pending), "The preview pool retained duplicate or unexpected buffers.");
            var pixels = initial.ToDictionary(frame => frame, frame => frame.DataPointer);
            Thread.Sleep(80);
            source.Feed(RectangularPreviewFrame(70));
            Until(() => Volatile.Read(ref delivered) == 3 && PreviewNativeSlots(camera).Count(frame => frame is not null) == 2);
            Mat[] reused = PreviewNativeSlots(camera).OfType<Mat>().ToArray();
            Check(reused.All(frame => pixels.TryGetValue(frame, out IntPtr address) && frame.DataPointer == address),
                "A stable-resolution preview reallocated its two native source buffers.");

            camera.ConfigurePreview(Size.Empty, enabled: false);
            Check(PreviewNativeSlots(camera).All(frame => frame is null), "Hiding preview retained native source buffers.");
        }
        finally { release.Set(); camera.Stop(); }
        Check(PreviewNativeSlots(camera).All(frame => frame is null), "Stopping preview retained native source buffers.");
    }

    private static void RuntimePreviewServiceDefaults()
    {
        var source = new TestFrameSource();
        using var camera = new CameraRuntime(PreviewSettings(), source, new ProcessingRegistry());
        var sizes = new ConcurrentQueue<Size>();
        camera.Start();
        try
        {
            source.Feed(RectangularPreviewFrame(10));
            Until(() => source.Dequeued >= 1);
            Check(camera.PreviewTiming.Sequence == 0 && PreviewNativeSlots(camera).All(frame => frame is null),
                "A camera without a preview subscriber allocated preview images.");
            camera.FrameReady += (_, image) => { using (image) sizes.Enqueue(image.Size); };
            var highResolution = new Mat(1080, 1920, DepthType.Cv8U, 3);
            highResolution.SetTo(new MCvScalar(40, 40, 40));
            source.Feed(highResolution);
            Until(() => !sizes.IsEmpty && source.Dequeued >= 2 && camera.LastFrameSize == new Size(1920, 1080));
            Check(sizes.Single() == new Size(1280, 720), "An unconfigured service preview lost its existing size ceiling.");
            Check(camera.LastFrameSize == new Size(1920, 1080), "The default preview resized the inference source.");
        }
        finally { camera.Stop(); }
    }

    private sealed class MutatingPreviewRenderer : IFrameRenderer
    {
        public Bitmap Render(Mat frame, IReadOnlyList<AnalysisDetection> detections, CameraSettings settings)
        {
            frame.SetTo(new MCvScalar(99, 99, 99));
            return frame.ToBitmap();
        }
    }

    private static void RuntimePreviewSourceIsolation()
    {
        var source = new TestFrameSource();
        using var camera = new CameraRuntime(PreviewSettings(), source, new ProcessingRegistry())
        { Renderer = new MutatingPreviewRenderer() };
        using var delivered = new ManualResetEventSlim();
        byte outputPixel = 0;
        camera.FrameReady += (_, image) =>
        {
            using (image) outputPixel = image.GetPixel(0, 0).R;
            delivered.Set();
        };
        // This runs before TestFrameSource enqueues/disposes the source Mat.
        source.FrameAvailable += frame =>
        {
            Check(delivered.Wait(3000), "The preview renderer did not complete.");
            Check(Marshal.ReadByte(frame.DataPointer) == 10, "Rendering modified the shared capture/inference source.");
        };
        camera.Start();
        try
        {
            source.Feed(RectangularPreviewFrame(10));
            Check(outputPixel == 99, "The source isolation check did not mutate the renderer's own Mat.");
        }
        finally { camera.Stop(); }
    }

    private static void RuntimePreviewRetainedBitmap()
    {
        var source = new TestFrameSource();
        using var camera = new CameraRuntime(PreviewSettings(), source, new ProcessingRegistry());
        var images = new ConcurrentQueue<Bitmap>();
        // Retain exactly the delivered objects, as the Windows preview now does.
        camera.FrameReady += (_, image) => images.Enqueue(image);
        camera.ConfigurePreview(new Size(160, 90));
        camera.Start();
        try
        {
            source.Feed(RectangularPreviewFrame(10));
            Until(() => images.Count == 1);
            Bitmap first = images.First();
            Check(first.Size == new Size(160, 90) && first.GetPixel(0, 0).R == 10,
                "The first retained preview was not rendered at the configured size.");

            foreach (byte pixel in new byte[] { 50, 90 })
            {
                Thread.Sleep(80);
                int expected = images.Count + 1;
                source.Feed(RectangularPreviewFrame(pixel));
                Until(() => images.Count == expected);
                Check(images.Last().GetPixel(0, 0).R == pixel && first.GetPixel(0, 0).R == 10,
                    "Reusing a native preview/resize Mat changed an earlier delivered bitmap.");
            }

            camera.ConfigurePreview(Size.Empty, enabled: false);
            Until(() => PreviewNativeSlots(camera).All(frame => frame is null));
            Check(first.GetPixel(0, 0).R == 10 && images.Last().GetPixel(0, 0).R == 90,
                "Hiding preview invalidated a bitmap still owned by its subscriber.");
            camera.Stop();
            Check(first.GetPixel(0, 0).R == 10 && images.Last().GetPixel(0, 0).R == 90,
                "Releasing preview native buffers invalidated delivered bitmap storage.");
        }
        finally
        {
            camera.Stop();
            while (images.TryDequeue(out Bitmap? image)) image.Dispose();
        }
    }
}
