using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using HshDetectionEngin;

namespace HshDetectionService;

public sealed partial class DetectionRuntimeHost
{
    private readonly ConcurrentDictionary<string, long> _snapshotDemandUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _previewDemandTimer;
    private int _servicePreviewFps = 15;
    private static readonly long SnapshotLeaseTicks = Stopwatch.Frequency * 3;

    private bool HasSnapshotDemand(string cameraId) =>
        _snapshotDemandUntil.TryGetValue(cameraId, out long until) && until > Stopwatch.GetTimestamp();

    private void RequestSnapshotPreview(string cameraId)
    {
        if (_shutdown.IsCancellationRequested) return;
        _snapshotDemandUntil[cameraId] = Stopwatch.GetTimestamp() + SnapshotLeaseTicks;
        UpdatePreviewDemand(cameraId);
    }

    private void UpdatePreviewDemand(string cameraId)
    {
        if (_shutdown.IsCancellationRequested || !TryGetCamera(cameraId, out Camera? camera) || camera is null) return;
        Size? streamSize = _webrtc.GetPreviewDemand(cameraId);
        bool snapshot = HasSnapshotDemand(cameraId);
        Size size = snapshot ? new Size(1280, 720) : streamSize ?? Size.Empty;
        int fps = streamSize.HasValue ? _servicePreviewFps : Math.Min(5, _servicePreviewFps);
        camera.ConfigurePreview(size, enabled: snapshot || streamSize.HasValue, maxFps: fps);
    }

    private void RefreshPreviewDemand()
    {
        if (_shutdown.IsCancellationRequested) return;
        try
        {
            Camera[] cameras;
            lock (_gate) cameras = _cameras.Values.ToArray();
            foreach (Camera camera in cameras) UpdatePreviewDemand(camera.Settings.Id);
            long now = Stopwatch.GetTimestamp();
            foreach ((string id, long until) in _snapshotDemandUntil)
                if (until <= now)
                    ((ICollection<KeyValuePair<string, long>>)_snapshotDemandUntil).Remove(new(id, until));
            foreach (string id in _latestFrames.Keys)
                if (!HasSnapshotDemand(id) && _latestFrames.TryRemove(id, out LatestFrameSlot? slot)) slot.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not refresh preview demand.");
        }
    }

    private void ClearPreviewDemand(string cameraId)
    {
        _snapshotDemandUntil.TryRemove(cameraId, out _);
        _webrtc.RemoveCamera(cameraId);
        if (_latestFrames.TryRemove(cameraId, out LatestFrameSlot? slot)) slot.Dispose();
        UpdatePreviewDemand(cameraId);
    }

    private bool StoreSnapshot(string cameraId, Bitmap frame, long sequence)
    {
        while (HasSnapshotDemand(cameraId))
        {
            if (_latestFrames.TryGetValue(cameraId, out LatestFrameSlot? slot))
            {
                if (slot.TryReplace(frame, sequence)) return true;
                ((ICollection<KeyValuePair<string, LatestFrameSlot>>)_latestFrames).Remove(new(cameraId, slot));
                continue;
            }
            if (_latestFrames.TryAdd(cameraId, new LatestFrameSlot(frame, sequence))) return true;
        }
        return false;
    }

    public async Task<Bitmap?> GetLatestFrameAsync(string cameraId, CancellationToken cancellationToken)
    {
        // A snapshot request wakes preview rendering, without starting capture
        // or inference. Wait briefly for its first frame after an idle period.
        long started = Stopwatch.GetTimestamp();
        do
        {
            Bitmap? frame = GetLatestFrame(cameraId);
            if (frame is not null) return frame;
            if (!TryGetCamera(cameraId, out Camera? camera) || camera is null || !camera.IsRunning) return null;
            await Task.Delay(25, cancellationToken);
        }
        while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < 750);
        return null;
    }
}
