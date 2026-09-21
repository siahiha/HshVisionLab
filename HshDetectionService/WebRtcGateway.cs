using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Vpx.Net;

namespace HshDetectionService;

/// <summary>
/// Small WebRTC video gateway. The runtime supplies the latest composited frame;
/// this class publishes that frame to each peer independently. Detection latency
/// therefore never delays the preview and an old drawing is simply replaced by
/// the next drawing state, exactly like the desktop preview pipeline.
/// </summary>
public sealed class WebRtcGateway : IDisposable
{
    private readonly ConcurrentDictionary<string, PeerSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LatestFrameSlot> _latestFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _pumpTimer;
    private readonly SemaphoreSlim _pumpGate = new(1, 1);
    private bool _disposed;

    public WebRtcGateway()
    {
        _pumpTimer = new Timer(_ => Pump(), null, TimeSpan.FromMilliseconds(67), TimeSpan.FromMilliseconds(67));
    }

    public async Task<WebRtcOfferResult> AcceptOfferAsync(string cameraId, string sdp, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cameraId)) throw new ArgumentException("Camera id is required.", nameof(cameraId));
        if (string.IsNullOrWhiteSpace(sdp)) throw new ArgumentException("SDP offer is required.", nameof(sdp));
        ObjectDisposedException.ThrowIf(_disposed, this);

        string sessionId = Guid.NewGuid().ToString("N");
        var peer = new RTCPeerConnection();
        var encoder = new Vp8NetVideoEncoderEndPoint();
        var session = new PeerSession(sessionId, cameraId, peer, encoder);
        _sessions[sessionId] = session;

        peer.OnVideoFormatsNegotiated += formats =>
        {
            VideoFormat? selected = formats.FirstOrDefault();
            if (selected is not null)
            {
                try { encoder.SetVideoSourceFormat(selected.Value); } catch { }
            }
        };
        encoder.OnVideoSourceEncodedSample += (duration, sample) =>
        {
            try { peer.SendVideo(duration, sample); } catch { }
        };
        peer.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected or RTCPeerConnectionState.closed)
                Close(sessionId);
        };

        peer.addTrack(new MediaStreamTrack(encoder.GetVideoSourceFormats(), MediaStreamStatusEnum.SendOnly));
        SetDescriptionResultEnum remoteResult = peer.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = sdp
        });
        if (remoteResult != SetDescriptionResultEnum.OK)
        {
            Close(sessionId);
            throw new InvalidOperationException($"WebRTC remote SDP was rejected: {remoteResult}.");
        }

        RTCSessionDescriptionInit answer = peer.createAnswer(new RTCAnswerOptions());
        await peer.setLocalDescription(answer).WaitAsync(cancellationToken);
        return new WebRtcOfferResult(sessionId, peer.localDescription.type.ToString(), peer.localDescription.sdp.ToString());
    }

    public void PushFrame(string cameraId, Bitmap frame)
    {
        if (_disposed || frame.Width <= 0 || frame.Height <= 0) return;
        Bitmap clone = new(frame);
        _latestFrames.AddOrUpdate(cameraId,
            _ => new LatestFrameSlot(clone),
            (_, old) => { old.Replace(clone); return old; });
    }

    public bool Close(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out PeerSession? session)) return false;
        session.Dispose();
        return true;
    }

    public int SessionCount => _sessions.Count;

    private void Pump()
    {
        if (_disposed || !_pumpGate.Wait(0)) return;
        try
        {
            foreach (PeerSession session in _sessions.Values)
            {
                if (!_latestFrames.TryGetValue(session.CameraId, out LatestFrameSlot? slot)) continue;
                using Bitmap frame = slot.Clone();
                try { session.Push(frame); }
                catch { Close(session.Id); }
            }
        }
        finally { _pumpGate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pumpTimer.Dispose();
        foreach (string sessionId in _sessions.Keys.ToArray()) Close(sessionId);
        foreach (LatestFrameSlot slot in _latestFrames.Values) slot.Dispose();
        _latestFrames.Clear();
        _pumpGate.Dispose();
    }

    private sealed class PeerSession : IDisposable
    {
        private readonly RTCPeerConnection _peer;
        private readonly Vp8NetVideoEncoderEndPoint _encoder;
        private int _disposed;

        public PeerSession(string id, string cameraId, RTCPeerConnection peer, Vp8NetVideoEncoderEndPoint encoder)
        {
            Id = id; CameraId = cameraId; _peer = peer; _encoder = encoder;
        }

        public string Id { get; }
        public string CameraId { get; }

        public void Push(Bitmap source)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            using Bitmap bgr = ToBgr24(source);
            Rectangle area = new(0, 0, bgr.Width, bgr.Height);
            BitmapData data = bgr.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int rowBytes = bgr.Width * 3;
                byte[] packed = new byte[rowBytes * bgr.Height];
                int stride = data.Stride;
                for (int row = 0; row < bgr.Height; row++)
                {
                    int sourceRow = stride >= 0 ? row : bgr.Height - 1 - row;
                    Marshal.Copy(IntPtr.Add(data.Scan0, sourceRow * Math.Abs(stride)), packed, row * rowBytes, rowBytes);
                }
                _encoder.ExternalVideoSourceRawSample(67, bgr.Width, bgr.Height, packed, VideoPixelFormatsEnum.Bgr);
            }
            finally { bgr.UnlockBits(data); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _peer.close(); } catch { }
            try { _encoder.CloseVideo().GetAwaiter().GetResult(); } catch { }
            _encoder.Dispose();
            _peer.Dispose();
        }

        private static Bitmap ToBgr24(Bitmap source)
        {
            if (source.PixelFormat == PixelFormat.Format24bppRgb) return new Bitmap(source);
            var result = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
            using Graphics graphics = Graphics.FromImage(result);
            graphics.DrawImageUnscaled(source, 0, 0);
            return result;
        }
    }

    private sealed class LatestFrameSlot : IDisposable
    {
        private readonly object _gate = new();
        private Bitmap _frame;

        public LatestFrameSlot(Bitmap frame) => _frame = frame;
        public void Replace(Bitmap frame) { lock (_gate) { Bitmap old = _frame; _frame = frame; old.Dispose(); } }
        public Bitmap Clone() { lock (_gate) return new Bitmap(_frame); }
        public void Dispose() { lock (_gate) _frame.Dispose(); }
    }
}

public sealed record WebRtcOfferResult(string SessionId, string Type, string Sdp);
