namespace HshVisionLab;

public sealed partial class MainForm
{
    private void AddRoi()
    {
        if (_active is null)
        {
            PushStatus("Select a camera before adding an ROI.", true);
            return;
        }

        string defaultName = $"ROI {_active.Settings.Rois.Count + 1}";
        string? name = PromptText("New ROI", defaultName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string normalizedName = name.Trim();
        if (_active.Settings.Rois.Any(item =>
            string.Equals(item.Name?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "ROI name must be unique for this camera.", "ROI manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var roi = new NamedRoi
        {
            Name = normalizedName,
            Enabled = true
        };
        _active.Settings.Rois.Add(roi);
        _active.UpdateFromSettings();

        RefreshRoiList();
        _lstRois.SelectedIndex = _active.Settings.Rois.Count - 1;
        BeginRoiEditForSelected();
        SaveAll();
    }

    private void RenameSelectedRoi()
    {
        NamedRoi? roi = SelectedRoi();
        if (roi is null)
        {
            return;
        }

        string? name = PromptText("Rename ROI", roi.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (_active is not null && _active.Settings.Rois.Any(item =>
            !ReferenceEquals(item, roi) && item.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "ROI name must be unique for this camera.", "ROI manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        roi.Name = name.Trim();
        _active?.UpdateFromSettings();
        RefreshRoiList();
        SaveAll();
    }

    private void DeleteSelectedRoi()
    {
        if (_active is null)
        {
            return;
        }

        NamedRoi? roi = SelectedRoi();
        if (roi is null)
        {
            return;
        }

        _active.Settings.Rois.Remove(roi);
        _active.UpdateFromSettings();
        ExitRoiEditMode();
        RefreshRoiList();
        SaveAll();
        _picView.Invalidate();
    }

    private void ToggleRoiEditMode()
    {
        if (_active is null)
        {
            return;
        }

        if (_roiEditMode)
        {
            FinishRoiEdit();
            return;
        }

        if (!_isCameraMaximized)
        {
            ShowMaximizedCamera(_active);
        }

        BeginRoiEditForSelected();
    }

    private void BeginRoiEditForSelected()
    {
        NamedRoi? roi = SelectedRoi();
        if (roi is null)
        {
            PushStatus("Select an ROI first.", true);
            return;
        }

        _editingRoiName = roi.Name;
        // Editing defines a replacement polygon. Do not seed the working list
        // with the existing points, otherwise new clicks are appended to the
        // old polygon and the saved ROI contains both versions.
        _roiEditingPoints.Clear();
        _roiEditMode = true;
        _picView.Cursor = Cursors.Cross;
        UpdateRoiActions();
        _picView.Invalidate();
    }

    private void FinishRoiEdit()
    {
        if (_active is null || _roiEditingPoints.Count < 3)
        {
            PushStatus("ROI needs at least 3 points.", true);
            return;
        }

        NamedRoi? roi = SelectedRoi();
        if (roi is null)
        {
            return;
        }

        roi.Points = _roiEditingPoints
            .Select(point => new RoiPoint(
                Math.Clamp(point.X, 0, 1),
                Math.Clamp(point.Y, 0, 1)))
            .ToList();
        roi.Enabled = true;
        _active.Settings.RoiPolygon.Clear();
        _active.Settings.RoiEnabled = true;

        ExitRoiEditMode();
        RefreshRoiList();
        SaveAll();
        _picView.Invalidate();
        PushStatus($"ROI '{roi.Name}' saved.");
    }

    private void ClearRoi()
    {
        if (_active is null)
        {
            return;
        }

        _active.Settings.Rois.Clear();
        _active.Settings.RoiPolygon.Clear();
        _active.Settings.RoiEnabled = false;
        _active.UpdateFromSettings();
        ExitRoiEditMode();
        RefreshRoiList();
        SaveAll();
        _picView.Invalidate();
        PushStatus("All ROIs cleared. No processing target is active.");
    }

    private void ExitRoiEditMode()
    {
        _roiEditMode = false;
        _editingRoiName = null;
        _roiEditingPoints.Clear();

        UpdateRoiActions();

        if (_picView is not null)
        {
            _picView.Cursor = Cursors.Hand;
            _picView.Invalidate();
        }
    }

    private NamedRoi? SelectedRoi()
    {
        if (_active is null)
        {
            return null;
        }

        int index = _lstRois.SelectedIndex;
        return index >= 0 && index < _active.Settings.Rois.Count
            ? _active.Settings.Rois[index]
            : null;
    }

    private void RefreshRoiList()
    {
        string? selectedName = SelectedRoi()?.Name;
        _lstRois.Items.Clear();
        if (_active is null)
        {
            UpdateRoiActions();
            return;
        }

        foreach (NamedRoi roi in _active.Settings.Rois)
        {
            string processing = string.Join(" + ", roi.Processing
                .Where(item => item.Enabled)
                .Select(item => _active.ProcessingModules.Descriptors
                    .FirstOrDefault(module => module.Type == item.Kind)?.DisplayName ?? item.Type));
            _lstRois.Items.Add($"{(roi.Enabled ? "✓" : "×")}  {roi.Name}  [{(string.IsNullOrWhiteSpace(processing) ? "No detection" : processing)}]");
        }

        if (_lstRois.Items.Count > 0)
        {
            int selectedIndex = selectedName is null
                ? 0
                : _active.Settings.Rois.FindIndex(item =>
                    item.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
            _lstRois.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
        }

        UpdateRoiActions();
    }

    private void UpdateRoiActions()
    {
        bool hasCamera = _active is not null;
        bool hasRoi = SelectedRoi() is not null;
        bool editing = _roiEditMode;

        _btnAddRoi.Enabled = hasCamera && !editing;
        _btnEditRoi.Enabled = hasRoi;
        _btnEditRoi.Text = string.Empty;
        SetRoiActionImage(_btnEditRoi, RoiActionIcon.Edit);
        _btnEditRoi.Invalidate();
        _roiToolTip.SetToolTip(_btnEditRoi, editing ? "Finish ROI editing" : "Edit ROI points");
        _btnRenameRoi.Enabled = hasRoi && !editing;
        _btnDeleteRoi.Enabled = hasRoi && !editing;
        _btnClearRoi.Enabled = hasCamera && !editing;
    }

    private void PicView_MouseClick(object? sender, MouseEventArgs e)
    {
        if (!_roiEditMode || _lastFrameSize.IsEmpty)
        {
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            if (_roiEditingPoints.Count > 0)
            {
                _roiEditingPoints.RemoveAt(_roiEditingPoints.Count - 1);
            }

            _picView.Invalidate();
            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (TryPictureBoxPointToNormalized(e.Location, out PointF point))
        {
            _roiEditingPoints.Add(point);
            _picView.Invalidate();
        }
    }

    private void PicView_Paint(object? sender, PaintEventArgs e)
    {
        if (!_roiEditMode || _roiEditingPoints.Count == 0)
        {
            return;
        }

        Point[] points = _roiEditingPoints
            .Select(NormalizedToPictureBoxPoint)
            .ToArray();
        using var pen = new Pen(Color.Orange, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        using var brush = new SolidBrush(Color.Orange);

        if (points.Length >= 2)
        {
            e.Graphics.DrawLines(pen, points);
        }

        if (points.Length >= 3)
        {
            e.Graphics.DrawLine(pen, points[^1], points[0]);
        }

        for (int index = 0; index < points.Length; index++)
        {
            e.Graphics.FillEllipse(brush, points[index].X - 4, points[index].Y - 4, 8, 8);
            e.Graphics.DrawString(
                (index + 1).ToString(),
                Font,
                Brushes.White,
                points[index].X + 6,
                points[index].Y + 4);
        }
    }

    private bool TryPictureBoxPointToNormalized(Point client, out PointF normalized)
    {
        normalized = default;
        Rectangle imageRect = GetDisplayedImageRect(_lastFrameSize, _picView.ClientSize);
        if (imageRect.IsEmpty || !imageRect.Contains(client))
        {
            return false;
        }

        normalized = new PointF(
            (client.X - imageRect.X) / (float)imageRect.Width,
            (client.Y - imageRect.Y) / (float)imageRect.Height);
        normalized.X = Math.Clamp(normalized.X, 0, 1);
        normalized.Y = Math.Clamp(normalized.Y, 0, 1);
        return true;
    }

    private Point NormalizedToPictureBoxPoint(PointF point)
    {
        Rectangle imageRect = GetDisplayedImageRect(_lastFrameSize, _picView.ClientSize);
        return new Point(
            imageRect.X + (int)(Math.Clamp(point.X, 0, 1) * imageRect.Width),
            imageRect.Y + (int)(Math.Clamp(point.Y, 0, 1) * imageRect.Height));
    }

    private static Rectangle GetDisplayedImageRect(Size image, Size client)
    {
        if (image.Width <= 0 || image.Height <= 0 || client.Width <= 0 || client.Height <= 0)
        {
            return Rectangle.Empty;
        }

        double scale = Math.Min(
            (double)client.Width / image.Width,
            (double)client.Height / image.Height);
        int width = Math.Max(1, (int)Math.Round(image.Width * scale));
        int height = Math.Max(1, (int)Math.Round(image.Height * scale));
        return new Rectangle(
            (client.Width - width) / 2,
            (client.Height - height) / 2,
            width,
            height);
    }
}
