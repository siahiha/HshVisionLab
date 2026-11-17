using System.Collections.Concurrent;
using System.Drawing;
using HshDetectionService;

namespace WebRtcStreaming.Tests;

internal static class PumpTests
{
    private static Bitmap Image(byte marker)
    {
        var image = new Bitmap(64, 48);
        using Graphics graphics = Graphics.FromImage(image);
        graphics.Clear(Color.FromArgb(marker, marker, marker));
        return image;
    }

    private static void Feed(CameraVideoPump pump, long sequence, byte marker)
    {
        using Bitmap image = Image(marker);
        TestAssert.That(pump.PushFrame(image, sequence, sequence * 1000), "An active camera rejected a new frame.");
    }

    private sealed class FakeEncoder(bool blockFirst = false) : IVideoFrameEncoder
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new(!blockFirst);
        public readonly ConcurrentQueue<(byte Marker, bool KeyFrame, int Thread)> Calls = new();
        public int DisposeCount;
        public int DisposeThread;

        public EncodedVideoFrame Encode(Bitmap source, Size maxBounds, bool forceKeyFrame)
        {
            if (Calls.IsEmpty)
            {
                Entered.Set();
                if (!Release.Wait(5000)) throw new TimeoutException("The synthetic encoder was not released.");
            }
            byte marker = source.GetPixel(0, 0).R;
            Calls.Enqueue((marker, forceKeyFrame, Environment.CurrentManagedThreadId));
            Size output = VideoFrameGeometry.Fit(source.Size, maxBounds);
            return new EncodedVideoFrame(output.Width, output.Height, [marker], forceKeyFrame);
        }

        public void Dispose()
        {
            DisposeThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref DisposeCount);
        }
    }

    private sealed class FakePeer(string id, bool ready = true, Size? bounds = null) : IVideoPeer
    {
        public string Id => id;
        public bool IsReady { get; set; } = ready;
        public Size MaxFrameSize { get; set; } = bounds ?? new Size(640, 360);
        public bool NeedsKeyFrame { get; private set; } = true;
        public readonly ConcurrentQueue<(EncodedVideoFrame Frame, long Sequence, long Timestamp)> Frames = new();

        public bool TrySend(EncodedVideoFrame frame, long sequence, long receivedTimestamp)
        {
            if (!IsReady || NeedsKeyFrame && !frame.IsKeyFrame) return false;
            if (frame.IsKeyFrame) NeedsKeyFrame = false;
            Frames.Enqueue((frame, sequence, receivedTimestamp));
            return true;
        }
    }

    public static void SharedEncoderAndFanout()
    {
        var encoder = new FakeEncoder();
        int created = 0;
        using var pump = new CameraVideoPump("shared", () => { Interlocked.Increment(ref created); return encoder; });
        var first = new FakePeer("first");
        var second = new FakePeer("second", bounds: new Size(320, 180));
        pump.AddViewer(first);
        pump.AddViewer(second);
        Feed(pump, 10, 10);
        TestAssert.Eventually(() => first.Frames.Count == 1 && second.Frames.Count == 1,
            "Both viewers did not receive the shared encoded frame.");
        TestAssert.Equal(1, created, "Viewers created separate camera encoders.");
        TestAssert.Equal(1, encoder.Calls.Count, "One source frame was encoded more than once.");
        TestAssert.That(ReferenceEquals(first.Frames.Single().Frame.Sample, second.Frames.Single().Frame.Sample),
            "Fanout copied the encoded payload for each viewer.");
        using (Bitmap duplicate = Image(99))
        {
            TestAssert.That(!pump.PushFrame(duplicate, 10, 11000), "A duplicate sequence was accepted.");
            TestAssert.That(!pump.PushFrame(duplicate, 9, 12000), "An older sequence was accepted.");
        }
        TestAssert.Equal(1, encoder.Calls.Count, "Duplicate input caused an additional encode.");
        pump.RemoveViewer(first);
        Feed(pump, 11, 11);
        TestAssert.Eventually(() => second.Frames.Count == 2, "The remaining viewer stopped after another viewer left.");
        TestAssert.Equal(1, first.Frames.Count, "A removed viewer continued receiving frames.");
        TestAssert.Equal(1, created, "Removing one viewer recreated the shared encoder.");
    }

    public static void NewestOnlyBound()
    {
        var encoder = new FakeEncoder(blockFirst: true);
        using var pump = new CameraVideoPump("newest", () => encoder);
        var peer = new FakePeer("viewer");
        pump.AddViewer(peer);
        try
        {
            Feed(pump, 1, 1);
            TestAssert.That(encoder.Entered.Wait(3000), "The first encode did not start.");
            for (int sequence = 2; sequence <= 200; sequence++)
            {
                Feed(pump, sequence, (byte)sequence);
                var stats = pump.GetDiagnostics();
                TestAssert.That(stats.InFlightFrames <= 1 && stats.PendingFrames <= 1,
                    "The slow camera accumulated frames beyond one active and one waiting image.");
            }
            var blocked = pump.GetDiagnostics();
            TestAssert.Equal(1, blocked.InFlightFrames, "The blocked encode was not tracked as active.");
            TestAssert.Equal(1, blocked.PendingFrames, "The newest waiting frame was not retained.");
            encoder.Release.Set();
            TestAssert.Eventually(() => pump.GetDiagnostics().LastEncodedSequence == 200 && peer.Frames.Count == 2,
                "The encoder did not skip directly to the newest waiting frame.");
            TestAssert.That(peer.Frames.Select(frame => frame.Sequence).SequenceEqual(new long[] { 1, 200 }),
                "Intermediate queued frames escaped the newest-only policy.");
            TestAssert.That(encoder.Calls.Select(call => call.Marker).SequenceEqual(new byte[] { 1, 200 }),
                "The pump retained caller-owned images or encoded discarded frames.");
        }
        finally { encoder.Release.Set(); }
    }

    public static void CameraIndependence()
    {
        var blockedEncoder = new FakeEncoder(blockFirst: true);
        var healthyEncoder = new FakeEncoder();
        using var blocked = new CameraVideoPump("blocked", () => blockedEncoder);
        using var healthy = new CameraVideoPump("healthy", () => healthyEncoder);
        var blockedPeer = new FakePeer("blocked-viewer");
        var healthyPeer = new FakePeer("healthy-viewer");
        blocked.AddViewer(blockedPeer);
        healthy.AddViewer(healthyPeer);
        try
        {
            Feed(blocked, 1, 1);
            TestAssert.That(blockedEncoder.Entered.Wait(3000), "The blocked camera did not start.");
            Feed(healthy, 1, 2);
            TestAssert.Eventually(() => healthyPeer.Frames.Count == 1,
                "A slow camera prevented another camera from sending its frame.", 2000);
            Feed(healthy, 2, 3);
            TestAssert.Eventually(() => healthyPeer.Frames.Count == 2,
                "The healthy camera did not continue on its independent worker.", 2000);
            TestAssert.Equal(0, blockedPeer.Frames.Count, "The blocked camera unexpectedly finished.");
            TestAssert.Equal(1, healthyEncoder.Calls.Select(call => call.Thread).Distinct().Count(),
                "The camera created a new encoding task/thread for each frame.");
        }
        finally { blockedEncoder.Release.Set(); }
    }

    public static void ReadyViewerDemand()
    {
        int created = 0;
        var encoder = new FakeEncoder();
        using var pump = new CameraVideoPump("demand", () => { Interlocked.Increment(ref created); return encoder; });
        var peer = new FakePeer("pending", ready: false);
        pump.AddViewer(peer);
        TestAssert.That(pump.GetDemand() is null, "A pending viewer requested preview pixels.");
        using (Bitmap unused = Image(10))
            TestAssert.That(!pump.PushFrame(unused, 1, 1000), "A camera without ready viewers retained an image.");
        TestAssert.Equal(0, created, "A pending viewer allocated an encoder.");
        peer.IsReady = true;
        pump.NotifyViewerReady();
        TestAssert.That(pump.GetDemand() is not null, "A connected viewer did not request preview pixels.");
        Feed(pump, 2, 20);
        TestAssert.Eventually(() => peer.Frames.Count == 1, "The newly ready viewer did not receive video.");
        pump.RemoveViewer(peer);
        TestAssert.That(pump.GetDemand() is null, "The last viewer left stale preview demand.");
        using (Bitmap unused = Image(30))
            TestAssert.That(!pump.PushFrame(unused, 3, 3000), "A camera without viewers retained an image.");
    }

    public static void JoinRequestsKeyframe()
    {
        var encoder = new FakeEncoder();
        using var pump = new CameraVideoPump("join", () => encoder);
        var original = new FakePeer("original");
        pump.AddViewer(original);
        Feed(pump, 1, 1);
        TestAssert.Eventually(() => original.Frames.Count == 1, "The first viewer did not receive video.");
        Feed(pump, 2, 2);
        TestAssert.Eventually(() => original.Frames.Count == 2, "The initial camera sequence stalled.");
        TestAssert.That(!encoder.Calls.Last().KeyFrame, "An ordinary frame unnecessarily forced a keyframe.");
        var joining = new FakePeer("joining");
        pump.AddViewer(joining);
        Feed(pump, 3, 3);
        TestAssert.Eventually(() => original.Frames.Count == 3 && joining.Frames.Count == 1,
            "A joining viewer did not receive a decodable first frame.");
        TestAssert.That(joining.Frames.Single().Frame.IsKeyFrame && encoder.Calls.Last().KeyFrame,
            "A new viewer received an inter frame before a forced keyframe.");
        TestAssert.Equal(3, encoder.Calls.Count, "Joining created a second encoding pass for the same source frame.");
    }

    public static void ShutdownDrainsFrames()
    {
        var encoder = new FakeEncoder(blockFirst: true);
        var pump = new CameraVideoPump("shutdown", () => encoder);
        pump.AddViewer(new FakePeer("viewer"));
        Task? shutdown = null;
        try
        {
            Feed(pump, 1, 1);
            TestAssert.That(encoder.Entered.Wait(3000), "The shutdown check never entered its encoder.");
            Feed(pump, 2, 2);
            shutdown = Task.Run(pump.Dispose);
            TestAssert.Equal(0, Volatile.Read(ref encoder.DisposeCount),
                "Shutdown disposed the codec while Encode still owned it.");
        }
        finally
        {
            encoder.Release.Set();
            if (shutdown is null) pump.Dispose();
            else TestAssert.That(shutdown.Wait(5000), "Gateway shutdown did not join its camera worker.");
        }
        TestAssert.That(pump.Completion.Wait(5000), "The camera worker remained alive after shutdown.");
        TestAssert.Equal(1, encoder.DisposeCount, "The camera encoder was leaked or disposed twice.");
        TestAssert.Equal(encoder.Calls.First().Thread, encoder.DisposeThread,
            "The native encoder was released from a different owner thread.");
        var stopped = pump.GetDiagnostics();
        TestAssert.That(stopped.InFlightFrames == 0 && stopped.PendingFrames == 0,
            "Shutdown retained active or pending preview images.");
        using Bitmap afterShutdown = Image(3);
        TestAssert.That(!pump.PushFrame(afterShutdown, 3, 3000), "A stopped camera accepted another image.");
    }
}
