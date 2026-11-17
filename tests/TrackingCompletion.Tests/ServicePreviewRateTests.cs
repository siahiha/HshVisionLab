using System.Diagnostics;
using HshDetectionEngin;

internal static partial class Program
{
    private static void ServicePreviewDisposal()
    {
        var source = new TestFrameSource();
        var camera = new CameraRuntime(PreviewSettings(), source, new ProcessingRegistry());
        Task updates = Task.Run(() =>
        {
            for (int index = 0; index < 1000; index++)
                camera.ConfigurePreview(new Size(160, 90), enabled: index % 2 == 0, maxFps: 5);
        });
        camera.Dispose();
        updates.GetAwaiter().GetResult();
        camera.ConfigurePreview(new Size(320, 180));
        camera.Start();
        camera.Dispose();
        Check(!source.IsRunning && PreviewNativeSlots(camera).All(frame => frame is null),
            "Preview configuration revived a disposed runtime or retained native images.");
    }

    private static void ServicePreviewRate()
    {
        var source = new TestFrameSource();
        using var camera = new CameraRuntime(PreviewSettings(), source, new ProcessingRegistry());
        int delivered = 0;
        camera.FrameReady += (_, image) => { image.Dispose(); Interlocked.Increment(ref delivered); };
        camera.ConfigurePreview(new Size(160, 90), enabled: true, maxFps: 5);
        camera.Start();
        try
        {
            source.Feed(RectangularPreviewFrame(10));
            Until(() => Volatile.Read(ref delivered) == 1);
            long start = Stopwatch.GetTimestamp();
            int fed = 0;
            do
            {
                source.Feed(RectangularPreviewFrame((byte)(20 + fed++ % 100)));
                Thread.Sleep(20);
            }
            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 1050);
            Until(() => source.Dequeued >= fed + 1);
            Check(Volatile.Read(ref delivered) <= 8,
                "The service preview ignored its configured frame-rate limit.");
            Check(camera.LastFrameSize == new Size(640, 360),
                "Configuring preview rate or size changed the capture/inference frame dimensions.");
        }
        finally { camera.Stop(); }
    }
}
