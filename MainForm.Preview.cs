using System.Collections.Concurrent;
using System.Diagnostics;

namespace HshVisionLab;

public sealed partial class MainForm
{
    private readonly ConcurrentDictionary<string, DesktopPreviewBuffer> _previewBuffers = new();
    // Only the UI thread accesses displayed frames. PictureBoxes borrow their images.
    private readonly Dictionary<string, DesktopPreviewFrame> _displayedFrames = new();
    private System.Windows.Forms.Timer? _uiTimer;
    private volatile bool _closing;
    private bool _previewLayoutPending;

    private void StartCameraRuntime(CameraRuntime camera)
    {
        if (_previewBuffers.TryGetValue(camera.Settings.Id, out DesktopPreviewBuffer? buffer)) buffer.ResetMetrics();
        camera.Start();
    }

    private void Runtime_FrameReady(CameraRuntime camera, Bitmap image)
    {
        var frame = new DesktopPreviewFrame(image, camera.PreviewTiming);
        if (_closing || !_previewBuffers.TryGetValue(camera.Settings.Id, out DesktopPreviewBuffer? buffer))
        {
            frame.Dispose();
            return;
        }

        // Transfer ownership; replacing a pending frame disposes it immediately.
        if (!buffer.Offer(frame)) return;
        try
        {
            BeginInvoke(() => ApplyLatestFrame(camera, buffer));
        }
        catch (InvalidOperationException) { buffer.CancelPending(); }
    }

    private void ApplyLatestFrame(CameraRuntime camera, DesktopPreviewBuffer buffer)
    {
        DesktopPreviewFrame? next = buffer.Take();
        if (next is null) return;
        if (_closing || !_cameras.TryGetValue(camera.Settings.Id, out CameraRuntime? current) ||
            !ReferenceEquals(current, camera) || !buffer.Enabled || GetPreviewTarget(camera) is not PictureBox target)
        {
            next.Dispose();
            return;
        }

        ReleaseDisplayedFrame(camera.Settings.Id);
        _displayedFrames[camera.Settings.Id] = next;
        target.Image = next.Image;
        if (ReferenceEquals(target, _picView)) _lastFrameSize = camera.LastFrameSize;
    }

    private PictureBox? GetPreviewTarget(CameraRuntime camera)
    {
        if (_closing || !Visible || WindowState == FormWindowState.Minimized || !camera.Settings.Enabled)
            return null;
        PictureBox? target = _isCameraMaximized
            ? _maximizedCameraId == camera.Settings.Id ? _picView : null
            : _cameraTilePictures.GetValueOrDefault(camera.Settings.Id);
        return target is { IsDisposed: false, Visible: true, ClientSize.Width: > 0, ClientSize.Height: > 0 }
            ? target : null;
    }

    private void RequestPreviewLayout()
    {
        if (_closing || _previewLayoutPending || !IsHandleCreated) return;
        _previewLayoutPending = true;
        try
        {
            BeginInvoke(() =>
            {
                _previewLayoutPending = false;
                if (!_closing) UpdatePreviewDemand();
            });
        }
        catch (InvalidOperationException) { _previewLayoutPending = false; }
    }

    private void UpdatePreviewDemand()
    {
        foreach (CameraRuntime camera in _cameras.Values)
        {
            if (!_previewBuffers.TryGetValue(camera.Settings.Id, out DesktopPreviewBuffer? buffer)) continue;
            PictureBox? target = GetPreviewTarget(camera);
            bool enabled = target is not null;
            if (enabled && !buffer.Enabled) buffer.ResetMetrics();
            buffer.SetEnabled(enabled);
            camera.ConfigurePreview(enabled ? target!.ClientSize : Size.Empty, enabled);
            if (!enabled)
            {
                ReleaseDisplayedFrame(camera.Settings.Id);
            }
            else if (_displayedFrames.TryGetValue(camera.Settings.Id, out DesktopPreviewFrame? displayed))
            {
                target!.Image = displayed.Image;
            }
        }
    }

    private void RecordPreviewPaint(CameraRuntime camera, PictureBox picture)
    {
        if (_displayedFrames.TryGetValue(camera.Settings.Id, out DesktopPreviewFrame? frame) &&
            ReferenceEquals(picture.Image, frame.Image) &&
            _previewBuffers.TryGetValue(camera.Settings.Id, out DesktopPreviewBuffer? buffer))
            buffer.RecordPaint(frame, Stopwatch.GetTimestamp());
    }

    private void ReleaseDisplayedFrame(string cameraId)
    {
        if (!_displayedFrames.Remove(cameraId, out DesktopPreviewFrame? frame)) return;
        if (_cameraTilePictures.TryGetValue(cameraId, out PictureBox? tile) && ReferenceEquals(tile.Image, frame.Image))
            tile.Image = null;
        if (ReferenceEquals(_picView.Image, frame.Image)) _picView.Image = null;
        frame.Dispose();
    }
}
