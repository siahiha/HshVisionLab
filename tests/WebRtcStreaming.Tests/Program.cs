namespace WebRtcStreaming.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Contains("--serve", StringComparer.OrdinalIgnoreCase))
        {
            SyntheticBrowserProbe.RunAsync().GetAwaiter().GetResult();
            return 0;
        }

        (string Name, Action Run)[] tests =
        [
            ("viewport geometry is bounded and aligned", GeometryTests.FitsAlignedBounds),
            ("aligned source aspect ratios are preserved", GeometryTests.RetainsExactAlignedAspect),
            ("camera encoder is shared and duplicate sequences are dropped", PumpTests.SharedEncoderAndFanout),
            ("slow encoding retains only the newest waiting frame", PumpTests.NewestOnlyBound),
            ("a blocked camera does not stall another camera", PumpTests.CameraIndependence),
            ("pending viewers do not allocate encoding resources", PumpTests.ReadyViewerDemand),
            ("new viewers receive a keyframe before dependent frames", PumpTests.JoinRequestsKeyframe),
            ("shutdown drains bounded frames and releases the encoder", PumpTests.ShutdownDrainsFrames),
            ("I420 storage is reused at stable geometry", PixelBufferTests.ReusesStableStorage),
            ("I420 conversion preserves source coverage without borders", PixelBufferTests.PreservesImageCoverage),
            ("I420 planes retain their expected color layout", PixelBufferTests.HasCorrectPlaneLayout),
            ("native VP8 encoding survives viewport downsize and upsize", PixelBufferTests.NativeEncoderResizes),
            ("VP8 payload fragments preserve frame bytes", ProtocolTests.PacketizerPreservesFrame),
            ("RTP timestamps follow capture time and wrap correctly", ProtocolTests.TimestampsFollowSourceTime),
            ("pending and active session leases expire and renew", ProtocolTests.SessionLeases)
        ];

        int failed = 0;
        foreach ((string name, Action run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception error)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {error}");
            }
        }

        Console.WriteLine($"{tests.Length - failed}/{tests.Length} streaming checks passed.");
        return failed == 0 ? 0 : 1;
    }
}
