using System.Diagnostics;
using HshDetectionService;

namespace WebRtcStreaming.Tests;

internal static class ProtocolTests
{
    public static void PacketizerPreservesFrame()
    {
        foreach (int size in new[] { 1, 1199, 1200, 2401, 10000 })
        {
            byte[] encoded = Enumerable.Range(0, size).Select(index => (byte)(index % 251)).ToArray();
            IReadOnlyList<byte[]> packets = Vp8RtpPacketizer.CreatePackets(encoded);
            TestAssert.That(packets.Count > 0, "A nonempty encoded frame produced no RTP payloads.");
            TestAssert.That(packets.All(packet => packet.Length > 1 && packet.Length <= 1200),
                "A VP8 packet exceeded the payload bound or omitted frame data.");
            TestAssert.Equal((byte)0x10, packets[0][0], "The first VP8 fragment omitted its start bit.");
            TestAssert.That(packets.Skip(1).All(packet => packet[0] == 0),
                "A continuation VP8 fragment incorrectly started a new partition.");
            byte[] reconstructed = packets.SelectMany(packet => packet.Skip(1)).ToArray();
            TestAssert.That(encoded.SequenceEqual(reconstructed), "VP8 fragmentation lost, duplicated or reordered bytes.");

            var shared = new EncodedVideoFrame(64, 48, encoded, true);
            TestAssert.That(ReferenceEquals(shared.GetRtpPayloads(), shared.GetRtpPayloads()),
                "Viewers of the same encoded frame allocated separate RTP fragment collections.");
        }
    }

    public static void TimestampsFollowSourceTime()
    {
        const uint initial = 12345;
        long origin = Stopwatch.Frequency * 5;
        var clock = new RtpVideoTimestamp(initial);
        uint first = clock.GetTimestamp(origin);
        uint next = clock.GetTimestamp(origin + Stopwatch.Frequency / 10);
        uint afterDroppedFrames = clock.GetTimestamp(origin + Stopwatch.Frequency / 2);
        TestAssert.Equal(initial, first, "The RTP clock did not use its initial timestamp.");
        TestAssert.That(Math.Abs((long)unchecked(next - first) - 9000) <= 1,
            "A 100 ms frame interval did not advance the 90 kHz clock by 9000 ticks.");
        TestAssert.That(Math.Abs((long)unchecked(afterDroppedFrames - first) - 45000) <= 1,
            "Skipped frames shortened RTP time instead of preserving source elapsed time.");

        var wrapping = new RtpVideoTimestamp(uint.MaxValue - 4000);
        uint beforeWrap = wrapping.GetTimestamp(origin);
        uint afterWrap = wrapping.GetTimestamp(origin + Stopwatch.Frequency / 10);
        TestAssert.That(afterWrap < beforeWrap && Math.Abs((long)unchecked(afterWrap - beforeWrap) - 9000) <= 1,
            "RTP timestamp wraparound did not preserve the source time delta.");

        var longRunning = new RtpVideoTimestamp(initial);
        longRunning.GetTimestamp(origin);
        uint afterFourteenHours = longRunning.GetTimestamp(origin + Stopwatch.Frequency * (14L * 3600));
        TestAssert.Equal(unchecked(initial + (uint)(14L * 3600 * 90000)), afterFourteenHours,
            "An RTP clock running beyond one complete 32-bit cycle lost elapsed source time.");
        uint nextLongRunningFrame = longRunning.GetTimestamp(origin + Stopwatch.Frequency * (14L * 3600) + Stopwatch.Frequency / 10);
        TestAssert.That(Math.Abs((long)unchecked(nextLongRunningFrame - afterFourteenHours) - 9000) <= 1,
            "RTP frame cadence broke after a full timestamp cycle.");

        var largeGap = new RtpVideoTimestamp(initial);
        largeGap.GetTimestamp(origin);
        uint afterEightHours = largeGap.GetTimestamp(origin + Stopwatch.Frequency * (8L * 3600));
        TestAssert.Equal(unchecked(initial + (uint)(8L * 3600 * 90000)), afterEightHours,
            "A source time jump beyond the signed 32-bit range was mistaken for backward time.");
    }

    public static void SessionLeases()
    {
        long now = 0;
        var pending = new VideoSessionLease(leased: true, () => now);
        now = Stopwatch.Frequency * 29;
        TestAssert.That(!pending.IsExpired, "A pending signaling session expired before its 30 second allowance.");
        now = Stopwatch.Frequency * 31;
        TestAssert.That(pending.IsExpired, "An abandoned pending signaling session did not expire.");

        now = 0;
        var active = new VideoSessionLease(leased: true, () => now);
        active.MarkConnected();
        now = Stopwatch.Frequency * 44;
        TestAssert.That(!active.IsExpired, "A connected viewport session expired before 45 seconds.");
        active.Renew();
        now = Stopwatch.Frequency * 88;
        TestAssert.That(!active.IsExpired, "A heartbeat did not renew the connected session lease.");
        now = Stopwatch.Frequency * 90;
        TestAssert.That(active.IsExpired, "A stale connected viewport session remained alive.");

        now = 0;
        var legacy = new VideoSessionLease(leased: false, () => now);
        legacy.MarkConnected();
        now = Stopwatch.Frequency * 3600;
        TestAssert.That(!legacy.IsExpired, "A legacy connected session unexpectedly required viewport heartbeats.");
    }
}
