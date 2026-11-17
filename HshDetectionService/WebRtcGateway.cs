using System.Diagnostics;
using System.Drawing;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace HshDetectionService;

/// <summary>One bounded encoder worker per camera; all its peers share encoded samples.</summary>
public sealed class WebRtcGateway : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PeerSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CameraVideoPump> _cameras = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _retiringCameras = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _maintenanceTimer;
    private bool _disposed;

    public WebRtcGateway()
        => _maintenanceTimer = new Timer(_ => ExpireSessions(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

    public event Action<string>? ViewerDemandChanged;

    public async Task<WebRtcOfferResult> AcceptOfferAsync(string cameraId, string sdp, CancellationToken cancellationToken,
        int maxWidth = 1280, int maxHeight = 720, bool leased = false)
    {
        if (string.IsNullOrWhiteSpace(cameraId)) throw new ArgumentException("Camera id is required.", nameof(cameraId));
        if (string.IsNullOrWhiteSpace(sdp)) throw new ArgumentException("SDP offer is required.", nameof(sdp));
        cancellationToken.ThrowIfCancellationRequested();
        string sessionId = Guid.NewGuid().ToString("N");
        (RTCPeerConnection peer, PeerSession session) = await CreateSessionAsync(sessionId, cameraId,
            VideoFrameGeometry.NormalizeBounds(maxWidth, maxHeight), leased, cancellationToken).ConfigureAwait(false);

        try
        {
            peer.OnVideoFormatsNegotiated += formats =>
            {
                if (formats.Count > 0) session.SetPayloadType(formats[0].FormatID);
            };
            peer.onconnectionstatechange += state =>
            {
                if (state == RTCPeerConnectionState.connected)
                {
                    session.MarkConnected();
                    NotifyDemandChanged(cameraId);
                }
                else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected or RTCPeerConnectionState.closed)
                    Close(sessionId);
            };
            peer.OnReceiveReport += (_, media, report) =>
            {
                if (media == SDPMediaTypesEnum.video && report.Feedback is RTCPFeedback feedback
                    && feedback.Header.PacketType == RTCPReportTypesEnum.PSFB
                    && feedback.Header.PayloadFeedbackMessageType == PSFBFeedbackTypesEnum.PLI)
                    session.RequestKeyFrame();
            };
            peer.addTrack(new MediaStreamTrack(new List<VideoFormat> { new(VideoCodecsEnum.VP8, 96, 90000) }, MediaStreamStatusEnum.SendOnly));
            NotifyDemandChanged(cameraId);
            SetDescriptionResultEnum result = peer.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });
            if (result != SetDescriptionResultEnum.OK)
                throw new InvalidOperationException($"WebRTC remote SDP was rejected: {result}.");
            RTCSessionDescriptionInit answer = peer.createAnswer(new RTCAnswerOptions());
            await peer.setLocalDescription(answer).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new WebRtcOfferResult(sessionId, peer.localDescription.type.ToString(), peer.localDescription.sdp.ToString());
        }
        catch
        {
            Close(sessionId);
            throw;
        }
    }

    private async Task<(RTCPeerConnection Peer, PeerSession Session)> CreateSessionAsync(string sessionId, string cameraId,
        Size bounds, bool leased, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? retiring;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _retiringCameras.TryGetValue(cameraId, out retiring);
            }
            if (retiring is not null)
                await retiring.WaitAsync(cancellationToken).ConfigureAwait(false);

            var peer = new RTCPeerConnection();
            bool registered = false;
            try
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    // A final viewer may have closed while the peer was being
                    // created. Do not overlap a replacement encoder with its
                    // predecessor's final in-flight frame.
                    if (!_retiringCameras.TryGetValue(cameraId, out retiring) || retiring.IsCompleted)
                    {
                        if (!_cameras.TryGetValue(cameraId, out CameraVideoPump? camera))
                        {
                            camera = new CameraVideoPump(cameraId);
                            _cameras.Add(cameraId, camera);
                        }
                        var session = new PeerSession(sessionId, cameraId, peer, camera, bounds, leased, ClosePeer);
                        _sessions.Add(sessionId, session);
                        camera.AddViewer(session);
                        registered = true;
                        return (peer, session);
                    }
                }
            }
            finally
            {
                if (!registered) peer.Dispose();
            }
        }
    }

    public bool HasViewers(string cameraId)
    {
        lock (_gate) return !_disposed && _cameras.ContainsKey(cameraId);
    }

    public Size? GetPreviewDemand(string cameraId)
    {
        CameraVideoPump? camera;
        lock (_gate) _cameras.TryGetValue(cameraId, out camera);
        return camera?.GetDemand();
    }

    /// <summary>The caller retains frame ownership; a viewed camera takes one pending clone.</summary>
    public void PushFrame(string cameraId, Bitmap frame, long sequence, long receivedTimestamp)
    {
        CameraVideoPump? camera;
        lock (_gate) _cameras.TryGetValue(cameraId, out camera);
        camera?.PushFrame(frame, sequence, receivedTimestamp);
    }

    public bool UpdateSessionDemand(string sessionId, int? maxWidth, int? maxHeight)
    {
        PeerSession? session;
        lock (_gate) _sessions.TryGetValue(sessionId, out session);
        if (session is null) return false;
        if (session.IsExpired)
        {
            Close(sessionId);
            return false;
        }
        bool resized = session.UpdateDemand(maxWidth, maxHeight);
        if (resized) NotifyDemandChanged(session.CameraId);
        return true;
    }

    public bool Close(string sessionId)
    {
        PeerSession? session;
        CameraVideoPump? retired = null;
        lock (_gate)
        {
            if (!_sessions.Remove(sessionId, out session)) return false;
            session.Camera.RemoveViewer(session);
            if (!session.Camera.HasViewers)
            {
                _cameras.Remove(session.CameraId);
                retired = session.Camera;
                _retiringCameras[session.CameraId] = retired.Completion;
            }
        }
        if (retired is not null) RetireCamera(retired);
        session.Dispose();
        NotifyDemandChanged(session.CameraId);
        return true;
    }

    public void RemoveCamera(string cameraId)
    {
        PeerSession[] sessions;
        CameraVideoPump? camera;
        lock (_gate)
        {
            sessions = _sessions.Values.Where(session => session.CameraId.Equals(cameraId, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (PeerSession session in sessions) _sessions.Remove(session.Id);
            _cameras.Remove(cameraId, out camera);
            if (camera is not null) _retiringCameras[cameraId] = camera.Completion;
        }
        if (camera is not null) RetireCamera(camera);
        foreach (PeerSession session in sessions) session.Dispose();
        if (camera is not null || sessions.Length > 0) NotifyDemandChanged(cameraId);
    }

    public int SessionCount { get { lock (_gate) return _sessions.Count; } }

    public IReadOnlyList<WebRtcCameraDiagnostics> GetDiagnostics()
    {
        CameraVideoPump[] cameras;
        lock (_gate) cameras = _cameras.Values.ToArray();
        return cameras.Select(camera => camera.GetDiagnostics()).ToArray();
    }

    private void ClosePeer(string sessionId) => Close(sessionId);

    private void RetireCamera(CameraVideoPump camera)
    {
        camera.Dispose();
        _ = camera.Completion.ContinueWith(completed =>
        {
            lock (_gate)
            {
                if (_retiringCameras.TryGetValue(camera.CameraId, out Task? retiring) && ReferenceEquals(retiring, completed))
                    _retiringCameras.Remove(camera.CameraId);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void ExpireSessions()
    {
        PeerSession[] sessions;
        lock (_gate)
        {
            if (_disposed) return;
            sessions = _sessions.Values.ToArray();
        }
        foreach (PeerSession session in sessions)
            if (session.IsExpired) Close(session.Id);
    }

    private void NotifyDemandChanged(string cameraId)
    {
        Delegate[] handlers = ViewerDemandChanged?.GetInvocationList() ?? [];
        foreach (Action<string> handler in handlers)
        {
            try { handler(cameraId); } catch { }
        }
    }

    public void Dispose()
    {
        PeerSession[] sessions;
        CameraVideoPump[] cameras;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = _sessions.Values.ToArray();
            cameras = _cameras.Values.ToArray();
            foreach (CameraVideoPump camera in cameras) _retiringCameras[camera.CameraId] = camera.Completion;
            _sessions.Clear();
            _cameras.Clear();
        }
        _maintenanceTimer.Dispose();
        foreach (CameraVideoPump camera in cameras) RetireCamera(camera);
        foreach (PeerSession session in sessions) session.Dispose();
        foreach (CameraVideoPump camera in cameras) NotifyDemandChanged(camera.CameraId);
    }

    private sealed class PeerSession : IVideoPeer, IDisposable
    {
        private sealed record Demand(Size Bounds);
        private readonly RTCPeerConnection _peer;
        private readonly VideoSessionLease _lease;
        private readonly Action<string> _close;
        private readonly object _sendGate = new();
        private Demand _demand;
        private RtpVideoTimestamp? _timestamps;
        private int _payloadType = 96;
        private int _ready, _disposed;
        private long _keyFrameRequested = 1;
        private long _keyFrameSatisfied;
        private long _lastKeyFrameRequest;

        public PeerSession(string id, string cameraId, RTCPeerConnection peer, CameraVideoPump camera,
            Size bounds, bool leased, Action<string> close)
        {
            Id = id; CameraId = cameraId; _peer = peer; Camera = camera;
            _demand = new Demand(bounds); _lease = new VideoSessionLease(leased); _close = close;
        }

        public string Id { get; }
        public string CameraId { get; }
        public CameraVideoPump Camera { get; }
        public bool IsReady => Volatile.Read(ref _ready) != 0 && Volatile.Read(ref _disposed) == 0;
        public bool IsExpired => _lease.IsExpired;
        public Size MaxFrameSize => Volatile.Read(ref _demand).Bounds;
        public bool NeedsKeyFrame => Interlocked.Read(ref _keyFrameRequested) != Interlocked.Read(ref _keyFrameSatisfied);

        public void MarkConnected()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (_lease.IsExpired) { _close(Id); return; }
            _lease.MarkConnected();
            Interlocked.Increment(ref _keyFrameRequested);
            Volatile.Write(ref _ready, 1);
            Camera.NotifyViewerReady();
        }

        public void SetPayloadType(int payloadType) => Volatile.Write(ref _payloadType, payloadType);

        public bool UpdateDemand(int? width, int? height)
        {
            Size previous = MaxFrameSize;
            Size next = VideoFrameGeometry.NormalizeBounds(width ?? previous.Width, height ?? previous.Height);
            Volatile.Write(ref _demand, new Demand(next));
            _lease.Renew();
            if (next != previous) Camera.UpdateDemand();
            return next != previous;
        }

        public void RequestKeyFrame()
        {
            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Read(ref _lastKeyFrameRequest);
            if (now - previous < Stopwatch.Frequency) return;
            Interlocked.Exchange(ref _lastKeyFrameRequest, now);
            Interlocked.Increment(ref _keyFrameRequested);
            Camera.NotifyViewerReady();
        }

        public bool TrySend(EncodedVideoFrame frame, long sequence, long receivedTimestamp)
        {
            if (!IsReady || frame.Sample.Length == 0) return false;
            try
            {
                lock (_sendGate)
                {
                    if (!IsReady || !_peer.IsSecureContextReady() || NeedsKeyFrame && !frame.IsKeyFrame) return false;
                    _timestamps ??= new RtpVideoTimestamp(_peer.VideoLocalTrack?.Timestamp ?? 0);
                    uint timestamp = _timestamps.GetTimestamp(receivedTimestamp);
                    long keyFrameRequest = Interlocked.Read(ref _keyFrameRequested);
                    IReadOnlyList<byte[]> payloads = frame.GetRtpPayloads();
                    for (int index = 0; index < payloads.Count; index++)
                    {
                        _peer.VideoStream?.SetRtpHeaderExtensionValue(TransportWideCCExtension.RTP_HEADER_EXTENSION_URI, null);
                        _peer.SendRtpRaw(SDPMediaTypesEnum.video, payloads[index], timestamp,
                            index == payloads.Count - 1 ? 1 : 0, Volatile.Read(ref _payloadType));
                    }
                    // Preserve a PLI that arrived while this keyframe was being
                    // transmitted; it may report loss of one of these packets.
                    if (frame.IsKeyFrame) Interlocked.Exchange(ref _keyFrameSatisfied, keyFrameRequest);
                    return true;
                }
            }
            catch
            {
                _close(Id);
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Volatile.Write(ref _ready, 0);
            lock (_sendGate)
            {
                try { _peer.close(); } catch { }
                try { _peer.Dispose(); } catch { }
            }
        }
    }
}

public sealed record WebRtcOfferResult(string SessionId, string Type, string Sdp, bool SupportsViewportUpdates = true);
