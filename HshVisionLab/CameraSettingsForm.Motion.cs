namespace HshVisionLab;

public sealed partial class CameraSettingsForm
{
    private TableLayoutPanel _motionSettingsTable = null!;

    private void BuildMotionSettings(TableLayoutPanel table)
    {
        _motionSettingsTable = table;
        var edit = MakeButton("Motion settings", Color.SteelBlue);
        edit.Click += (_, _) => EditMotionSettings();
        AddRow(table, "Motion direction", edit);
        _processingToolTip.SetToolTip(edit, UiLocalization.T("Requires a stable track ID. Stationary direction = 0."));
    }

    private void EditMotionSettings()
    {
        if (_selectedProcessing is null) return;
        using var form = CreateMotionEditor(_selectedProcessing);
        form.ShowDialog(this);
    }

    private Form CreateMotionEditor(CameraProcessingSettings item)
    {
        var settings = item.MotionDirection?.Clone() ?? new MotionDirectionSettings();
        var form = CreateCompletionEditor("Motion direction", out var table, out var save);
        var enabled = new CheckBox { Checked = settings.Enabled };
        AddEditorRow(table, "Enabled", enabled);
        NumericUpDown Number(string label, decimal value, decimal min, decimal max, int decimals = 0)
        {
            var number = ConfigureNumber(new NumericUpDown(), min, max, decimals == 0 ? 1 : .01m, decimals);
            number.Name = label;
            number.Value = Math.Clamp(value, min, max);
            AddEditorRow(table, label, number);
            return number;
        }
        var window = Number("Motion window (ms)", settings.WindowMs, 50, 10000);
        var observations = Number("Minimum track observations", settings.MinimumObservations, 2, 100);
        var duration = Number("Minimum observation time (ms)", settings.MinimumObservationMs, 0, 10000);
        var displacement = Number("Minimum displacement (ROI pixels)", (decimal)settings.MinimumDisplacementPixels, .01m, 1000, 2);
        var timeout = Number("Motion track timeout (ms)", settings.TrackTimeoutMs, 51, 60000);
        var line = new CheckBox { Checked = settings.LineEnabled };
        AddEditorRow(table, "Entry/exit crossing line", line);
        var x1 = Number("Line start X (0–1 in ROI)", (decimal)settings.LineStartX, 0, 1, 2);
        var y1 = Number("Line start Y (0–1 in ROI)", (decimal)settings.LineStartY, 0, 1, 2);
        var x2 = Number("Line end X (0–1 in ROI)", (decimal)settings.LineEndX, 0, 1, 2);
        var y2 = Number("Line end Y (0–1 in ROI)", (decimal)settings.LineEndY, 0, 1, 2);
        var positive = new CheckBox { Checked = settings.PositiveCrossingIsEntry };
        AddEditorRow(table, "Positive-side crossing is entry (default: down)", positive);
        AddEditorRow(table, "Image axes: X right, Y down. Default line: downward crossing is entry.", new Label { AutoSize = true, Text = "0 = stationary; 1…8 = moving" });
        save.Click += (_, _) =>
        {
            settings.Enabled = enabled.Checked;
            settings.WindowMs = (int)window.Value;
            settings.MinimumObservations = (int)observations.Value;
            settings.MinimumObservationMs = (int)duration.Value;
            settings.MinimumDisplacementPixels = (float)displacement.Value;
            settings.TrackTimeoutMs = (int)timeout.Value;
            settings.LineEnabled = line.Checked;
            settings.LineStartX = (float)x1.Value; settings.LineStartY = (float)y1.Value;
            settings.LineEndX = (float)x2.Value; settings.LineEndY = (float)y2.Value;
            settings.PositiveCrossingIsEntry = positive.Checked;
            try { settings.Validate(); }
            catch (InvalidOperationException ex) { MessageBox.Show(form, UiLocalization.T(ex.Message)); return; }
            item.MotionDirection = settings;
            form.DialogResult = DialogResult.OK;
        };
        UiLocalization.Apply(form);
        return form;
    }
}
