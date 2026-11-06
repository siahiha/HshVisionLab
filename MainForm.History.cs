namespace HshVisionLab;

public sealed partial class MainForm
{
    private void RebuildAllHistoryCards()
    {
        foreach (Control control in _flowPlates.Controls.OfType<Control>().ToArray())
        {
            DisposeDetectionCard(control);
        }

        foreach (DesktopEventRecord entry in _eventStore.Query(limit: 25).Reverse())
        {
            AddEventCard(entry);
        }
    }

    private void AddEventCard(DesktopEventRecord entry)
    {
        Bitmap? mainCrop = LoadEventCrop(entry.CropPath);
        Bitmap? secondaryCrop = LoadEventCrop(entry.SecondaryCropPath);

        if (mainCrop is null)
        {
            mainCrop = secondaryCrop;
            secondaryCrop = null;
        }

        if (mainCrop is null)
        {
            secondaryCrop?.Dispose();
            return;
        }

        AddDetectionCard(
            mainCrop,
            secondaryCrop,
            entry.CameraName,
            entry.Label,
            entry.Confidence,
            entry.OccurredAtUtc.ToLocalTime(),
            entry.DisplayAxis);
    }

    private static Bitmap? LoadEventCrop(string? path)
    {
        string fullPath = DesktopEventStore.ResolvePath(path);
        if (!File.Exists(fullPath)) return null;
        try
        {
            using Image image = Image.FromFile(fullPath);
            return new Bitmap(image);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatAnalysisLabel(AnalysisDetection detection)
    {
        string kind = detection.Kind switch
        {
            AnalysisKind.Face => "Face",
            AnalysisKind.Palm => "Palm",
            _ => detection.Kind.ToString()
        };
        return detection.TrackId is int trackId ? $"{kind} #{trackId}" : kind;
    }

    private void AddDetectionCard(
        Bitmap mainCrop,
        Bitmap? secondaryCrop,
        string cameraName,
        string label,
        float confidence,
        DateTime timestamp,
        string displayAxis)
    {
        const int maxCards = 25;
        int width = Math.Max(250, _flowPlates.ClientSize.Width - 28);

        var card = new Panel
        {
            Width = width,
            Height = 116,
            BackColor = Color.FromArgb(29, 32, 39),
            Margin = new Padding(2, 2, 2, 8),
            Padding = new Padding(6)
        };

        var imagePanel = new Panel
        {
            Dock = DockStyle.Left,
            Width = 105,
            BackColor = Color.Black
        };

        var picture = new PictureBox
        {
            Image = mainCrop,
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Fill,
            BackColor = Color.Black
        };
        imagePanel.Controls.Add(picture);

        if (secondaryCrop is not null)
        {
            var secondaryPicture = new PictureBox
            {
                Image = secondaryCrop,
                SizeMode = PictureBoxSizeMode.Zoom,
                Width = 44,
                Height = 44,
                Left = 55,
                Top = 54,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                BackColor = Color.Black,
                BorderStyle = BorderStyle.FixedSingle
            };
            imagePanel.Controls.Add(secondaryPicture);
            secondaryPicture.BringToFront();

            var faceBadge = new Label
            {
                Text = "FACE",
                AutoSize = false,
                Width = 44,
                Height = 14,
                Left = 55,
                Top = 40,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                BackColor = Color.FromArgb(190, 0, 0, 0),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 6.5F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            };
            imagePanel.Controls.Add(faceBadge);
            faceBadge.BringToFront();
        }

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
            Text = label,
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
        card.Controls.Add(imagePanel);
        _flowPlates.Controls.Add(card);
        card.BringToFront();

        while (_flowPlates.Controls.Count > maxCards)
        {
            DisposeDetectionCard(_flowPlates.Controls[^1]);
        }
    }

    private static void DisposeDetectionCard(Control control)
    {
        foreach (PictureBox picture in GetPictureBoxes(control))
        {
            picture.Image?.Dispose();
            picture.Image = null;
        }

        control.Parent?.Controls.Remove(control);
        control.Dispose();
    }

    private static IEnumerable<PictureBox> GetPictureBoxes(Control control)
    {
        foreach (Control child in control.Controls)
        {
            if (child is PictureBox picture)
            {
                yield return picture;
            }

            foreach (PictureBox nested in GetPictureBoxes(child))
            {
                yield return nested;
            }
        }
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
