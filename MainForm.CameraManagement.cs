namespace HshVisionLab;

public sealed partial class MainForm
{
    private void ShowCameraManager()
    {
        if (_cameraManagerForm is not null && !_cameraManagerForm.IsDisposed)
        {
            _cameraManagerForm.RefreshCameras(_cameras.Values.ToArray(), _active?.Settings.Id);
            _cameraManagerForm.WindowState = FormWindowState.Normal;
            _cameraManagerForm.BringToFront();
            _cameraManagerForm.Activate();
            return;
        }

        _cameraManagerForm = new CameraManagerForm(
            () => _cameras.Values.ToArray(),
            AddCamera,
            ToggleCamera,
            EditCamera,
            RemoveCamera,
            camera => SelectCamera(camera));
        _cameraManagerForm.FormClosed += (_, _) => _cameraManagerForm = null;
        _cameraManagerForm.Show(this);
    }

    private void CameraGrid_SelectionChanged(object? sender, EventArgs e)
    {
        if (_updatingCameraGrid) return;
        if (_cameraGrid.CurrentRow?.Tag is CameraRuntime camera)
        {
            SelectCamera(camera, false);
        }
    }

    private void CameraGrid_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0)
        {
            return;
        }

        if (_cameraGrid.Rows[e.RowIndex].Tag is not CameraRuntime camera)
        {
            return;
        }

        switch (_cameraGrid.Columns[e.ColumnIndex].Name)
        {
            case "StartStop":
                ToggleCamera(camera);
                break;
            case "Edit":
                EditCamera(camera);
                break;
            case "Delete":
                RemoveCamera(camera);
                break;
        }
    }

    private void CameraGrid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _cameraGrid.Rows[e.RowIndex].Tag is not CameraRuntime camera)
        {
            return;
        }

        SelectCamera(camera, false);
        ShowMaximizedCamera(camera);
    }

    private void SelectCamera(CameraRuntime camera, bool updateGrid = true)
    {
        if (_active == camera)
        {
            RefreshRoiList();
            return;
        }

        ExitRoiEditMode();
        _active = camera;
        _appSettings.SelectedCameraId = camera.Settings.Id;

        if (updateGrid)
        {
            SelectCameraGridRow(camera);
        }

        RefreshRoiList();
        UpdateCameraGrid();
        UpdateMaximizedView();
        PushStatus($"Selected camera: {camera.Settings.Name}");
    }

    private void SelectCameraGridRow(CameraRuntime camera)
    {
        foreach (DataGridViewRow row in _cameraGrid.Rows)
        {
            if (!ReferenceEquals(row.Tag, camera))
            {
                continue;
            }

            row.Selected = true;
            if (ReferenceEquals(row.DataGridView, _cameraGrid) && row.Index >= 0 && row.Cells.Count > 0 &&
                ReferenceEquals(row.Cells[0].DataGridView, _cameraGrid))
            {
                _cameraGrid.CurrentCell = row.Cells[0];
            }
            break;
        }
    }

    private void ToggleCamera(CameraRuntime camera)
    {
        if (camera.IsRunning)
        {
            camera.Stop();
        }
        else
        {
            camera.Start();
        }

        SelectCamera(camera);
        UpdateCameraGrid();
        RebuildMultiCameraGrid();
    }

    private void StartAllCameras()
    {
        foreach (CameraRuntime camera in _cameras.Values.ToArray())
        {
            if (!camera.IsRunning)
            {
                camera.Start();
            }
        }

        SaveAll();
        UpdateCameraGrid();
        RebuildMultiCameraGrid();
        PushStatus("All cameras started.");
    }

    private void StopAllCameras()
    {
        foreach (CameraRuntime camera in _cameras.Values.ToArray())
        {
            if (camera.IsRunning)
            {
                camera.Stop();
            }
        }

        SaveAll();
        UpdateCameraGrid();
        RebuildMultiCameraGrid();
        PushStatus("All cameras stopped.");
    }

    private void AddCamera()
    {
        var settings = new CameraSettings
        {
            Name = GetNextCameraName(),
            SourceUrl = string.Empty
        };

        using var dialog = new CameraSettingsForm(settings, true, _processingCatalog.Descriptors);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        CameraSettings result = dialog.Result;
        AddRuntime(result);
        SelectCamera(_cameras[result.Id]);
        SaveAll();
        UpdateCameraGrid();
        RebuildMultiCameraGrid();
    }

    private void EditCamera(CameraRuntime camera)
    {
        bool wasRunning = camera.IsRunning;
        if (wasRunning)
        {
            camera.Stop();
        }

        using var dialog = new CameraSettingsForm(camera.Settings, false, _processingCatalog.Descriptors);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            ApplySettings(dialog.Result, camera.Settings);
            camera.UpdateFromSettings();

            if (wasRunning)
            {
                camera.Start();
            }

            SaveAll();
            SelectCamera(camera);
            UpdateCameraGrid();
            RebuildMultiCameraGrid();
        }
        else if (wasRunning)
        {
            camera.Start();
        }
    }

    private void RemoveCamera(CameraRuntime camera)
    {
        DialogResult result = MessageBox.Show(
            this,
            $"Delete camera '{camera.Settings.Name}'?\r\nIts local detection history will also be removed.",
            "Delete camera",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
        {
            return;
        }

        bool wasActive = _active == camera;
        camera.Stop();
        camera.FrameReady -= Runtime_FrameReady;
        camera.PlateDetected -= Runtime_PlateDetected;
        camera.AnalysisDetected -= Runtime_AnalysisDetected;
        camera.StatusChanged -= Runtime_StatusChanged;
        _cameras.Remove(camera.Settings.Id);

        lock (_frameGate)
        {
            if (_latestFrames.Remove(camera.Settings.Id, out Bitmap? bitmap))
            {
                bitmap.Dispose();
            }
        }

        camera.Dispose();

        if (wasActive)
        {
            _active = null;
            CameraRuntime? next = _cameras.Values.FirstOrDefault();
            if (next is not null)
            {
                SelectCamera(next);
            }
            else
            {
                ClearSelectedCameraView();
            }
        }

        SaveAll();
        UpdateCameraGrid();
        RebuildMultiCameraGrid();
        RebuildAllHistoryCards();
    }

    private void ApplySettings(CameraSettings source, CameraSettings target)
    {
        string id = target.Id;
        CameraSettings clone = source.Clone();
        clone.Id = id;

        target.Name = clone.Name;
        target.SourceUrl = clone.SourceUrl;
        target.ModelFile = clone.ModelFile;
        target.PlateEnabled = clone.PlateEnabled;
        target.Processing = clone.Processing;
        target.FaceModelFile = clone.FaceModelFile;
        target.FaceEnabled = clone.FaceEnabled;
        target.FaceInputSize = clone.FaceInputSize;
        target.FaceConfidence = clone.FaceConfidence;
        target.FaceRecordConfidence = clone.FaceRecordConfidence;
        target.FaceRecognitionEnabled = clone.FaceRecognitionEnabled;
        target.FaceRecognitionModelFile = clone.FaceRecognitionModelFile;
        target.FaceRecognitionThreshold = clone.FaceRecognitionThreshold;
        target.FaceMatchIou = clone.FaceMatchIou;
        target.FaceTrackMaxMisses = clone.FaceTrackMaxMisses;
        target.FacePreprocessing = clone.FacePreprocessing;
        target.FaceMaxFps = clone.FaceMaxFps;
        target.FaceNmsThreshold = clone.FaceNmsThreshold;
        target.FaceTopK = clone.FaceTopK;
        target.FaceUnknownMatchThreshold = clone.FaceUnknownMatchThreshold;
        target.FaceEventCooldownSeconds = clone.FaceEventCooldownSeconds;
        target.Transport = clone.Transport;
        target.CaptureBackend = clone.CaptureBackend;
        target.BufferCount = clone.BufferCount;
        target.ReconnectDelaySec = clone.ReconnectDelaySec;
        target.Confidence = clone.Confidence;
        target.NmsIoU = clone.NmsIoU;
        target.MaxFps = clone.MaxFps;
        target.InputSize = clone.InputSize;
        target.Threads = clone.Threads;
        target.DrawBoxes = clone.DrawBoxes;
        target.DetectionOverlayHoldMs = clone.DetectionOverlayHoldMs;
        target.DuplicateEventCooldownSeconds = clone.DuplicateEventCooldownSeconds;
        target.MotionGateEnabled = clone.MotionGateEnabled;
        target.MotionFps = clone.MotionFps;
        target.MotionThreshold = clone.MotionThreshold;
        target.MotionChangedPercent = clone.MotionChangedPercent;
        target.MotionRoiScalePercent = clone.MotionRoiScalePercent;
        target.MotionHoldMs = clone.MotionHoldMs;
        target.ActiveDetectionFps = clone.ActiveDetectionFps;
        target.IdleDetectionFps = clone.IdleDetectionFps;
        target.RoiLeft = clone.RoiLeft;
        target.RoiTop = clone.RoiTop;
        target.RoiRight = clone.RoiRight;
        target.RoiBottom = clone.RoiBottom;
        target.RoiEnabled = clone.RoiEnabled;
        target.RoiPolygon = clone.RoiPolygon;
        target.Rois = clone.Rois;
        target.ProcessingSchemaVersion = clone.ProcessingSchemaVersion;
        target.TrackMaxMisses = clone.TrackMaxMisses;
        target.PlatePreprocessing = clone.PlatePreprocessing;
    }

    private string GetNextCameraName()
    {
        int index = 1;
        while (_cameras.Values.Any(camera =>
            camera.Settings.Name.Equals($"Camera {index}", StringComparison.OrdinalIgnoreCase)))
        {
            index++;
        }

        return $"Camera {index}";
    }

    private void SaveAll()
    {
        _appSettings.Cameras = _cameras.Values
            .Select(camera => camera.Settings)
            .ToList();
        _appSettings.SelectedCameraId = _active?.Settings.Id;
        _appSettings.Save();
    }

    private void UpdateCameraGrid()
    {
        if (_cameraGrid is null || IsDisposed)
        {
            return;
        }

        string? selectedId = _active?.Settings.Id;
        CameraRuntime[] cameras = _cameras.Values.ToArray();
        bool structureChanged = _cameraGrid.Rows.Count != cameras.Length;

        if (!structureChanged)
        {
            for (int index = 0; index < cameras.Length; index++)
            {
                if (!ReferenceEquals(_cameraGrid.Rows[index].Tag, cameras[index]))
                {
                    structureChanged = true;
                    break;
                }
            }
        }

        _cameraGrid.SuspendLayout();
        _updatingCameraGrid = true;
        try
        {
            if (structureChanged)
            {
                _cameraGrid.Rows.Clear();

                foreach (CameraRuntime camera in cameras)
                {
                    int rowIndex = _cameraGrid.Rows.Add(
                        camera.Settings.Name,
                        camera.Settings.SourceUrl,
                        camera.IsRunning ? "RUNNING" : "STOPPED",
                        camera.IsRunning ? camera.ProcessingFps.ToString("0.0") : "--",
                        camera.IsRunning ? "Stop" : "Start",
                        "Edit",
                        "Delete");
                    _cameraGrid.Rows[rowIndex].Tag = camera;
                }
            }

            for (int index = 0; index < cameras.Length; index++)
            {
                CameraRuntime camera = cameras[index];
                DataGridViewRow row = _cameraGrid.Rows[index];
                row.Cells[0].Value = camera.Settings.Name;
                row.Cells[1].Value = camera.Settings.SourceUrl;
                row.Cells[2].Value = camera.IsRunning ? "RUNNING" : "STOPPED";
                row.Cells[3].Value = camera.IsRunning ? camera.ProcessingFps.ToString("0.0") : "--";
                row.Cells[4].Value = camera.IsRunning ? "Stop" : "Start";
                row.Cells[5].Value = "Edit";
                row.Cells[6].Value = "Delete";
                row.Cells[2].Style.ForeColor = camera.IsRunning ? Color.LightGreen : Color.Silver;
                row.Cells[4].Style.ForeColor = camera.IsRunning ? Color.Orange : Color.LightGreen;
                row.Cells[6].Style.ForeColor = Color.Salmon;
                row.Selected = camera.Settings.Id == selectedId;
            }
        }
        finally
        {
            _updatingCameraGrid = false;
            _cameraGrid.ResumeLayout();
        }

        if (_cameraGrid.Rows.Count == 0)
        {
            _cameraGrid.ClearSelection();
        }

        _cameraManagerForm?.RefreshCameras(cameras, selectedId);
    }
}
