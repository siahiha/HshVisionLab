namespace HshVisionLab;

public sealed partial class CameraSettingsForm
{
    private readonly List<Control> _vehicleColorControls = [];

    private void BuildVehicleColorSettings(TableLayoutPanel table)
    {
        int startRow = table.RowCount;
        var edit = MakeButton("Vehicle color settings", Color.SteelBlue);
        edit.Click += (_, _) =>
        {
            if (_selectedProcessing is null) return;
            using var form = CreateVehicleColorEditor(_selectedProcessing);
            form.ShowDialog(this);
        };
        AddRow(table, "Vehicle body color", edit);
        foreach (Control control in table.Controls)
            if (table.GetRow(control) >= startRow) _vehicleColorControls.Add(control);
    }

    private Form CreateVehicleColorEditor(CameraProcessingSettings item)
    {
        var settings = item.VehicleColor?.Clone() ?? new VehicleColorSettings();
        var form = CreateCompletionEditor("Vehicle color settings", out var table, out var save);
        var enabled = new CheckBox { Checked = settings.Enabled };
        AddEditorRow(table, "Enabled", enabled);
        var draw = new CheckBox { Checked = settings.DrawSampleRegion };
        AddEditorRow(table, "Draw detected vehicle box", draw);
        var median = new CheckBox { Name = "MedianFilterEnabled", Checked = settings.MedianFilterEnabled };
        AddEditorRow(table, "Reduce color crop noise (Median 3x3)", median);
        NumericUpDown Number(string label, decimal value, decimal min, decimal max, int decimals = 0)
        {
            var number = ConfigureNumber(new NumericUpDown(), min, max, decimals == 0 ? 1 : .01m, decimals);
            number.Name = label; number.Value = Math.Clamp(value, min, max);
            AddEditorRow(table, label, number); return number;
        }
        var models = VehicleModelCatalog.Enumerate();
        ComboBox ModelSelector(string label, string capability, string selected)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
            foreach (var model in models.Where(m => m.Capability == capability)) combo.Items.Add(model.Name);
            selected = Path.ChangeExtension(Path.GetFileName(selected), ".onnx");
            if (!combo.Items.Contains(selected)) combo.Items.Add(selected);
            combo.SelectedItem = selected;
            AddEditorRow(table, label, combo); return combo;
        }
        var colorModel = ModelSelector("Color model", "VehicleColor", settings.ModelFile);
        var contract = new Label { AutoSize = true, MaximumSize = new Size(480, 0) };
        void ShowContract()
        {
            var model = models.FirstOrDefault(m => m.Capability == "VehicleColor" && m.Name == colorModel.SelectedItem?.ToString());
            contract.Text = model?.ColorContract is { } c
                ? $"{c.InputWidth} x {c.InputHeight}, {c.ColorOrder}, scale {c.Scale}; {string.Join(", ", c.Labels)}"
                : "Selected model is unavailable";
        }
        colorModel.SelectedIndexChanged += (_, _) => ShowContract(); ShowContract();
        AddEditorRow(table, "Model input (automatic)", contract);
        var vehicleModel = ModelSelector("Vehicle detection model", "VehicleDetection", settings.VehicleDetectorModelFile);
        var vehicleConfidence = Number("Vehicle box confidence", (decimal)settings.VehicleDetectionConfidence, .01m, 1, 2);
        var vehicleNms = Number("Vehicle NMS IoU", (decimal)settings.VehicleNmsIoU, .01m, 1, 2);
        var fps = Number("Maximum color processing FPS", settings.MaxFps, 1, 10);
        var timeout = Number("Color track timeout (ms)", settings.TrackTimeoutMs, 1, 60000);
        AddEditorRow(table, "Color has no minimum score and does not affect plate acceptance.", new Label { AutoSize = true, Text = "" });
        AddEditorRow(table, "Body color is an estimate. Disable for infrared images.", new Label { AutoSize = true, Text = "" });
        save.Click += (_, _) =>
        {
            settings.Enabled = enabled.Checked; settings.DrawSampleRegion = draw.Checked;
            settings.MedianFilterEnabled = median.Checked;
            settings.ModelFile = colorModel.SelectedItem?.ToString() ?? settings.ModelFile;
            settings.VehicleDetectorModelFile = vehicleModel.SelectedItem?.ToString() ?? settings.VehicleDetectorModelFile;
            settings.VehicleDetectionConfidence = (float)vehicleConfidence.Value;
            settings.VehicleNmsIoU = (float)vehicleNms.Value;
            settings.MaxFps = (int)fps.Value; settings.TrackTimeoutMs = (int)timeout.Value;
            try { settings.Validate(); }
            catch (InvalidOperationException ex) { MessageBox.Show(form, UiLocalization.T(ex.Message)); return; }
            item.VehicleColor = settings; form.DialogResult = DialogResult.OK;
        };
        UiLocalization.Apply(form); return form;
    }
}
