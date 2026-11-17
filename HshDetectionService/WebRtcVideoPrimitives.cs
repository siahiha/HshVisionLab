using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WebRtcStreaming.Tests")]

namespace HshDetectionService;

internal sealed record EncodedVideoFrame(int Width, int Height, byte[] Sample, bool IsKeyFrame)
{
    private IReadOnlyList<byte[]>? _payloads;
    public IReadOnlyList<byte[]> GetRtpPayloads() => _payloads ??= Vp8RtpPacketizer.CreatePackets(Sample);
}

internal interface IVideoFrameEncoder : IDisposable
{
    EncodedVideoFrame Encode(Bitmap source, Size maxBounds, bool forceKeyFrame);
}

internal interface IVideoPeer
{
    string Id { get; }
    bool IsReady { get; }
    Size MaxFrameSize { get; }
    bool NeedsKeyFrame { get; }
    bool TrySend(EncodedVideoFrame frame, long sequence, long receivedTimestamp);
}

internal static class VideoFrameGeometry
{
    public static Size NormalizeBounds(int width, int height)
        => new(Math.Clamp(width, 16, 1280), Math.Clamp(height, 16, 720));

    public static Size Fit(Size source, Size maxBounds)
    {
        if (source.Width < 16 || source.Height < 16)
            throw new ArgumentOutOfRangeException(nameof(source), "Video dimensions must be at least 16 pixels.");
        Size bounds = NormalizeBounds(maxBounds.Width, maxBounds.Height);
        double scale = Math.Min(1, Math.Min((double)bounds.Width / source.Width, (double)bounds.Height / source.Height));
        // VP8 requires both dimensions to be multiples of 16. Resize the entire
        // image, without cropping or padding, so normalised ROI coordinates stay valid.
        return new Size(Math.Max(16, (int)(source.Width * scale) / 16 * 16),
            Math.Max(16, (int)(source.Height * scale) / 16 * 16));
    }
}

internal sealed class VideoSessionLease
{
    private readonly Func<long> _clock;
    private readonly long _createdAt;
    private long _lastRenewedAt;
    private int _leased;
    private int _connected;

    public VideoSessionLease(bool leased, Func<long>? clock = null)
    {
        _clock = clock ?? Stopwatch.GetTimestamp;
        _createdAt = _lastRenewedAt = _clock();
        _leased = leased ? 1 : 0;
    }

    public void MarkConnected() => Volatile.Write(ref _connected, 1);
    public void Renew()
    {
        Interlocked.Exchange(ref _lastRenewedAt, _clock());
        Volatile.Write(ref _leased, 1);
    }

    public bool IsExpired
    {
        get
        {
            long now = _clock();
            return Volatile.Read(ref _connected) == 0 && now - _createdAt >= 30 * Stopwatch.Frequency
                || Volatile.Read(ref _leased) != 0 && now - Interlocked.Read(ref _lastRenewedAt) >= 45 * Stopwatch.Frequency;
        }
    }
}

internal sealed class RtpVideoTimestamp
{
    private readonly uint _initialTimestamp;
    private long _origin;
    private long _previousOffset;
    private bool _started;

    public RtpVideoTimestamp(uint initialTimestamp = 0) => _initialTimestamp = initialTimestamp;

    public uint GetTimestamp(long receivedTimestamp)
    {
        if (!_started)
        {
            _started = true;
            _origin = receivedTimestamp;
            return _initialTimestamp;
        }
        long elapsed = Math.Max(0, receivedTimestamp - _origin);
        long offset = (long)Math.Round(elapsed * (90000.0 / Stopwatch.Frequency));
        if (offset <= _previousOffset) offset = _previousOffset + 1;
        _previousOffset = offset;
        // Convert through an integer before wrapping: a direct floating-point
        // to uint conversion is out of range after roughly 13 hours of video.
        return unchecked(_initialTimestamp + (uint)offset);
    }
}

internal static class Vp8RtpPacketizer
{
    // Same minimal VP8 descriptor as SIPSorcery's SendVp8Frame. Leave room for
    // the descriptor inside the 1200-byte RTP payload bound.
    public const int MaxPayloadLength = 1200;

    public static IReadOnlyList<byte[]> CreatePackets(byte[] sample)
    {
        const int fragmentLength = MaxPayloadLength - 1;
        if (sample.Length == 0) return Array.Empty<byte[]>();
        var payloads = new byte[(sample.Length + fragmentLength - 1) / fragmentLength][];
        for (int index = 0, offset = 0; index < payloads.Length; index++, offset += fragmentLength)
        {
            int length = Math.Min(fragmentLength, sample.Length - offset);
            var payload = new byte[length + 1];
            payload[0] = index == 0 ? (byte)0x10 : (byte)0;
            Buffer.BlockCopy(sample, offset, payload, 1, length);
            payloads[index] = payload;
        }
        return payloads;
    }
}

public sealed record WebRtcCameraDiagnostics(
    string CameraId,
    int ViewerCount,
    int ConnectedViewerCount,
    int Width,
    int Height,
    long ReceivedFrames,
    long EncodedFrames,
    long ReplacedFrames,
    long DuplicateFrames,
    double LastEncodeMs,
    double LastFrameAgeMs,
    double EncodedFps,
    long EncodedBytes,
    long SentFrames,
    long SendErrors,
    string? LastError,
    int InFlightFrames,
    int PendingFrames,
    long LastEncodedSequence);
