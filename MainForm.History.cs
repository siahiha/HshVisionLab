namespace HshVisionLab;

public sealed partial class MainForm
{
    private void RebuildAllHistoryCards()
    {
        foreach (Control control in _flowPlates.Controls.OfType<Control>().ToArray())
        {
            DisposePlateCard(control);
        }

        var items = _cameras.Values
            .SelectMany(camera => camera.History.Select(item => new
            {
                Camera = camera.Settings.Name,
                Label = item.Text,
                Crop = item.Crop,
                Confidence = item.Confidence,
                Timestamp = item.Timestamp,
                Archived = false
            }))
            .Concat(_cameras.Values.SelectMany(camera => camera.ArchivedHistory.Select(item => new
            {
                Camera = camera.Settings.Name,
                Label = item.Text,
                Crop = item.Crop,
                Confidence = item.Confidence,
                Timestamp = item.Timestamp,
                Archived = true
            })) )
            .Concat(_cameras.Values.SelectMany(camera => camera.AnalysisHistory.Select(item => new
            {
                Camera = camera.Settings.Name,
                Label = item.Detection.TrackId is int trackId ? $"Face #{trackId}" : "Face",
                Crop = item.Crop,
                Confidence = item.Detection.Confidence,
                Timestamp = item.Timestamp,
                Archived = false
            })))
            .Concat(_cameras.Values.SelectMany(camera => camera.ArchivedAnalysisHistory.Select(item => new
            {
                Camera = camera.Settings.Name,
                Label = item.Detection.TrackId is int trackId ? $"Face #{trackId}" : "Face",
                Crop = item.Crop,
                Confidence = item.Detection.Confidence,
                Timestamp = item.Timestamp,
                Archived = true
            })))
            .OrderByDescending(entry => entry.Timestamp)
            .Take(100)
            .ToArray();

        foreach (var entry in items.Reverse())
        {
            try
            {
                AddPlateCard(
                    new Bitmap(entry.Crop),
                    entry.Camera,
                    entry.Label,
                    entry.Confidence,
                    entry.Timestamp);
            }
            finally
            {
                if (entry.Archived) entry.Crop.Dispose();
            }
        }
    }

    private void AddPlateCard(
        Bitmap crop,
        string cameraName,
        string plate,
        float confidence,
        DateTime timestamp)
    {
        const int maxCards = 100;
        int width = Math.Max(250, _flowPlates.ClientSize.Width - 28);

        var card = new Panel
        {
            Width = width,
            Height = 116,
            BackColor = Color.FromArgb(29, 32, 39),
            Margin = new Padding(2, 2, 2, 8),
            Padding = new Padding(6)
        };

        var picture = new PictureBox
        {
            Image = crop,
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Left,
            Width = 105,
            BackColor = Color.Black
        };

        var cameraLabel = new Label
        {
            Text = cameraName,
            Dock = DockStyle.Top,
            Height = 25,
            ForeColor = Color.LightSkyBlue,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var plateLabel = new Label
        {
            Text = plate,
            Dock = DockStyle.Top,
            Height = 32,
            ForeColor = Color.White,
            Font = new Font("Tahoma", 12F, FontStyle.Bold),
            RightToLeft = RightToLeft.Yes,
            TextAlign = ContentAlignment.MiddleCenter
        };

        var detailLabel = new Label
        {
            Text = $"Confidence: {confidence:P0}\r\n{timestamp:yyyy-MM-dd HH:mm:ss}",
            Dock = DockStyle.Fill,
            ForeColor = Color.Silver,
            TextAlign = ContentAlignment.MiddleCenter
        };

        var textPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(6, 0, 0, 0)
        };
        textPanel.Controls.Add(detailLabel);
        textPanel.Controls.Add(plateLabel);
        textPanel.Controls.Add(cameraLabel);

        card.Controls.Add(textPanel);
        card.Controls.Add(picture);
        _flowPlates.Controls.Add(card);
        card.BringToFront();

        while (_flowPlates.Controls.Count > maxCards)
        {
            DisposePlateCard(_flowPlates.Controls[^1]);
        }
    }

    private static void DisposePlateCard(Control control)
    {
        if (control is Panel panel)
        {
            foreach (PictureBox picture in panel.Controls.OfType<PictureBox>())
            {
                picture.Image?.Dispose();
                picture.Image = null;
            }
        }

        control.Parent?.Controls.Remove(control);
        control.Dispose();
    }

    private void SaveSnapshot()
    {
        if (_picView.Image is not Bitmap bitmap || _active is null)
        {
            return;
        }

        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "snapshots");
            Directory.CreateDirectory(directory);
            string fileName = Path.Combine(
                directory,
                $"{_active.Settings.Name}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            bitmap.Save(fileName, System.Drawing.Imaging.ImageFormat.Png);
            PushStatus($"Snapshot saved: {Path.GetFileName(fileName)}");
        }
        catch (Exception ex)
        {
            PushStatus(ex.Message, true);
        }
    }

    private void UpdateStatsUi()
    {
        if (_active is null)
        {
            _stFps.Text = "FPS: --";
            _stInfer.Text = "| infer: -- ms";
            _stRes.Text = "| res: --";
            _stDropped.Text = "| dropped: 0";
            return;
        }

        _stFps.Text = $"FPS: {_active.ProcessingFps:0.0}";
        _stInfer.Text = $"| infer: {_active.LastInferenceMs:0.#} ms";
        _stRes.Text = _active.LastFrameSize.IsEmpty
            ? "| res: --"
            : $"| res: {_active.LastFrameSize.Width}x{_active.LastFrameSize.Height}";
        _stDropped.Text = $"| dropped: {_active.DroppedFrames}";
    }

    private void OnSaveSettingsClick()
    {
        SaveAll();
        PushStatus("All camera settings saved.");
    }

    private static string? PromptText(string title, string initial)
    {
        using var form = new Form
        {
            Width = 380,
            Height = 155,
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };

        var textBox = new TextBox
        {
            Left = 14,
            Top = 16,
            Width = 340,
            Text = initial
        };
        var ok = new Button
        {
            Text = "OK",
            Left = 190,
            Top = 55,
            Width = 78,
            DialogResult = DialogResult.OK
        };
        var cancel = new Button
        {
            Text = "Cancel",
            Left = 276,
            Top = 55,
            Width = 78,
            DialogResult = DialogResult.Cancel
        };

        form.Controls.AddRange([textBox, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK
            ? textBox.Text.Trim()
            : null;
    }
}
