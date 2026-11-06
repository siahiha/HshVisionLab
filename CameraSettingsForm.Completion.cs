using HshDetectionEngin.Face;
using HshDetectionEngin.Plate;

namespace HshVisionLab;

public sealed partial class CameraSettingsForm
{
    private readonly ComboBox _completionMode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _completionDestination = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly List<Control> _completionRoutingControls = [];
    private readonly ListBox _recognitionList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private sealed record ExecutionChoice(string Value, string Label) { public override string ToString() => UiLocalization.T(Label); }
    private sealed record DefinitionChoice(RecognitionDefinition Definition)
    {
        public override string ToString() => $"{Definition.Name} • {Definition.Kind} • {Definition.ModelFile}{(Definition.Enabled ? "" : " [off]")}";
    }

    private void BuildCompletionRouting(TableLayoutPanel table)
    {
        int startRow = table.RowCount;
        AddSection(table, "Recognition routing");
        _completionMode.Items.AddRange(new object[] { new ExecutionChoice("Inline", "Direct recognition"), new ExecutionChoice("TrackAndQueue", "Tracking and camera buffer") });
        AddRow(table, "Recognition execution", _completionMode);
        AddRow(table, "Recognition destination", _completionDestination);
        foreach (Control control in table.Controls)
            if (table.GetRow(control) >= startRow) _completionRoutingControls.Add(control);
        _completionMode.SelectedIndexChanged += (_, _) => UpdateCompletionRoutingAvailability();
    }

    private void LoadCompletionRouting(CameraProcessingSettings item)
    {
        _completionMode.SelectedItem = _completionMode.Items.Cast<ExecutionChoice>().FirstOrDefault(c => c.Value == item.ExecutionMode) ?? _completionMode.Items[0];
        ReloadCompletionDestinations(item.RecognitionDefinitionId);
        UpdateCompletionRoutingAvailability();
    }

    private void ReloadCompletionDestinations(string? selectedId = null)
    {
        selectedId ??= (_completionDestination.SelectedItem as DefinitionChoice)?.Definition.Id;
        var kind = _selectedProcessing?.Kind == ProcessingType.Face ? AnalysisKind.Face : AnalysisKind.Plate;
        _completionDestination.Items.Clear();
        foreach (var definition in _settings.RecognitionDefinitions.Where(d => d.Kind == kind)) _completionDestination.Items.Add(new DefinitionChoice(definition));
        _completionDestination.SelectedItem = _completionDestination.Items.Cast<DefinitionChoice>().FirstOrDefault(c => c.Definition.Id == selectedId);
    }

    private void CommitCompletionRouting(CameraProcessingSettings item)
    {
        if (item.Kind != ProcessingType.Face && item.Kind != ProcessingType.Plate) return;
        item.ExecutionMode = (_completionMode.SelectedItem as ExecutionChoice)?.Value ?? "Inline";
        item.RecognitionDefinitionId = (_completionDestination.SelectedItem as DefinitionChoice)?.Definition.Id ?? string.Empty;
    }

    private void UpdateCompletionRoutingAvailability()
    {
        bool inline = (_completionMode.SelectedItem as ExecutionChoice)?.Value != "TrackAndQueue";
        _completionDestination.Enabled = !inline;
        foreach (Control control in new Control[] { _chkCharacterRecognition, _cmbCharacterModel, _numCharacterConfidence, _numCharacterMaxFps,
            _chkFaceRecognition, _cmbFaceRecognitionModel, _numFaceRecognitionThreshold, _numFaceUnknownMatch }) control.Enabled = inline;
        _processingToolTip.SetToolTip(_completionDestination, UiLocalization.T("Add recognition destinations in the General tab."));
    }

    private void BuildCompletionGeneral(TableLayoutPanel general)
    {
        AddSection(general, "Camera recognitions");
        var panel = new TableLayoutPanel { Width = 650, Height = 200, ColumnCount = 1, RowCount = 2 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        foreach (string caption in new[] { "Add recognition", "Edit", "Delete" })
        {
            var button = MakeButton(caption, caption == "Delete" ? Color.Firebrick : Color.SteelBlue);
            actions.Controls.Add(button);
            button.Click += (_, _) =>
            {
                if (!CommitSelectedProperties()) return;
                var selected = (_recognitionList.SelectedItem as DefinitionChoice)?.Definition;
                if (caption == "Delete")
                {
                    if (selected is null) return;
                    if (RecognitionDependencies(selected.Id).Length > 0) { ShowRecognitionDependencies(selected.Id); return; }
                    _settings.RecognitionDefinitions.Remove(selected);
                }
                else
                {
                    if (caption == "Edit" && selected is null) return;
                    var definition = caption == "Edit" ? selected! : null;
                    if (!EditRecognitionDefinition(definition)) return;
                }
                RefreshRecognitionList();
                ReloadCompletionDestinations(_selectedProcessing?.RecognitionDefinitionId);
            };
        }
        _recognitionList.BackColor = Color.FromArgb(38, 42, 50);
        _recognitionList.ForeColor = Color.Gainsboro;
        panel.Controls.Add(actions, 0, 0);
        panel.Controls.Add(_recognitionList, 0, 1);
        AddRow(general, "Recognition destinations", panel);
        AddSection(general, "Sample buffers by type");
        foreach (var settings in _settings.TrackingCompletion.SampleTypes)
        {
            var button = MakeButton(settings.Kind == AnalysisKind.Face ? "Face buffer" : "Plate buffer", Color.SteelBlue);
            button.Click += (_, _) => EditSampleBuffer(settings);
            AddRow(general, settings.Kind == AnalysisKind.Face ? "Face buffer" : "Plate buffer", button);
        }
    }

    private void RefreshRecognitionList()
    {
        string? selectedId = (_recognitionList.SelectedItem as DefinitionChoice)?.Definition.Id;
        _recognitionList.BeginUpdate();
        try
        {
            _recognitionList.Items.Clear();
            foreach (var definition in _settings.RecognitionDefinitions)
                _recognitionList.Items.Add(new DefinitionChoice(definition));
            _recognitionList.SelectedItem = _recognitionList.Items.Cast<DefinitionChoice>()
                .FirstOrDefault(item => item.Definition.Id == selectedId);
            if (_recognitionList.SelectedIndex < 0 && _recognitionList.Items.Count > 0)
                _recognitionList.SelectedIndex = 0;
        }
        finally { _recognitionList.EndUpdate(); }
    }

    private string[] RecognitionDependencies(string id) => _settings.Rois.Where(r => r.Enabled)
        .SelectMany(r => r.Processing.Where(t => t.Enabled && t.ExecutionMode == "TrackAndQueue" && t.RecognitionDefinitionId == id)
            .Select(t => $"{r.Name} / {t.Name}")).ToArray();
    private void ShowRecognitionDependencies(string id) => MessageBox.Show(this,
        UiLocalization.T("Change or disable these ROI tasks before removing this recognition:") + Environment.NewLine + string.Join(Environment.NewLine, RecognitionDependencies(id)),
        UiLocalization.T("Camera settings"), MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private bool EditRecognitionDefinition(RecognitionDefinition? existing)
    {
        using var form = CreateCompletionEditor("Camera recognition", out var table, out var save);
        var name = new TextBox { Text = existing?.Name ?? UiLocalization.T("Recognition") };
        var kind = ConfigureCombo(new ComboBox(), "Face", "Plate");
        kind.SelectedItem = (existing?.Kind ?? AnalysisKind.Face).ToString();
        kind.Enabled = existing is null;
        var enabled = new CheckBox { Checked = existing?.Enabled ?? true };
        var model = ConfigureCombo(new ComboBox());
        var confidence = ConfigureNumber(new NumericUpDown(), .01m, 1m, .01m, 2);
        confidence.Value = (decimal)(existing?.Confidence ?? .40f);
        var unknown = ConfigureNumber(new NumericUpDown(), .01m, 1m, .01m, 2);
        unknown.Value = (decimal)(existing?.UnknownMatchThreshold ?? .35f);
        var threads = ConfigureNumber(new NumericUpDown(), 1, 16, 1, 0);
        threads.Value = existing?.Threads ?? 1;
        void ReloadModels()
        {
            string? selected = existing?.ModelFile;
            model.Items.Clear();
            var source = kind.SelectedItem?.ToString() == "Face" ? _cmbFaceRecognitionModel : _cmbCharacterModel;
            model.Items.AddRange(source.Items.Cast<object>().ToArray());
            model.SelectedItem = model.Items.Cast<object>().FirstOrDefault(m => m.ToString() == selected);
            if (model.SelectedIndex < 0 && model.Items.Count > 0) model.SelectedIndex = 0;
            unknown.Enabled = kind.SelectedItem?.ToString() == "Face";
        }
        kind.SelectedIndexChanged += (_, _) => ReloadModels();
        ReloadModels();
        AddEditorRow(table, "Name", name);
        AddEditorRow(table, "Sample type", kind);
        AddEditorRow(table, "Enabled", enabled);
        AddEditorRow(table, "Recognition model", model);
        AddEditorRow(table, "Acceptance threshold", confidence);
        AddEditorRow(table, "Unknown-person match threshold", unknown);
        AddEditorRow(table, "Threads (ONNX Runtime IntraOp)", threads);
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || model.SelectedItem is null)
            { MessageBox.Show(form, UiLocalization.T("Enter a name and select a recognition model.")); return; }
            if (existing is not null && !enabled.Checked && RecognitionDependencies(existing.Id).Length > 0)
            { ShowRecognitionDependencies(existing.Id); return; }
            var definition = existing ?? new RecognitionDefinition();
            if (existing is not null) definition.Revision++;
            definition.Name = name.Text.Trim();
            definition.Kind = kind.SelectedItem?.ToString() == "Face" ? AnalysisKind.Face : AnalysisKind.Plate;
            definition.Enabled = enabled.Checked;
            definition.ModelFile = model.SelectedItem.ToString()!;
            definition.Confidence = (float)confidence.Value;
            definition.UnknownMatchThreshold = (float)unknown.Value;
            definition.Threads = (int)threads.Value;
            if (existing is null) _settings.RecognitionDefinitions.Add(definition);
            form.DialogResult = DialogResult.OK;
        };
        UiLocalization.Apply(form);
        return form.ShowDialog(this) == DialogResult.OK;
    }

    private void EditSampleBuffer(SampleBufferSettings settings)
    {
        using var form = CreateCompletionEditor(settings.Kind == AnalysisKind.Face ? "Face buffer" : "Plate buffer", out var table, out var save);
        NumericUpDown Number(string label, decimal value, decimal min, decimal max)
        {
            var number = ConfigureNumber(new NumericUpDown(), min, max, 1, 0);
            number.Value = Math.Clamp(value, min, max);
            AddEditorRow(table, label, number);
            return number;
        }
        var capacity = Number("Pending sample capacity", settings.Capacity, 1, 4096);
        var memory = Number("Maximum memory (MB)", settings.MaxMemoryBytes / (1024 * 1024), 1, 8192);
        var age = Number("Maximum sample age (ms)", settings.MaxSampleAgeMs, 100, 60000);
        var observations = Number("Minimum track observations", settings.MinTrackObservations, 1, 100);
        var interval = Number("Minimum enqueue interval (ms)", settings.MinEnqueueIntervalMs, 0, 60000);
        var retry = Number("Retry delay (ms)", settings.RetryDelayMs, 0, 60000);
        var attempts = Number("Maximum attempts per track", settings.MaxAttemptsPerTrack, 1, 100);
        var refresh = Number("Recognition refresh (ms, 0 = once)", settings.RefreshIntervalMs, 0, 3600000);
        save.Click += (_, _) =>
        {
            if (retry.Value >= age.Value && attempts.Value > 1)
            { MessageBox.Show(form, UiLocalization.T("Retry delay must be shorter than maximum sample age.")); return; }
            settings.Capacity = (int)capacity.Value;
            settings.MaxMemoryBytes = (long)memory.Value * 1024 * 1024;
            settings.MaxSampleAgeMs = (int)age.Value;
            settings.MinTrackObservations = (int)observations.Value;
            settings.MinEnqueueIntervalMs = (int)interval.Value;
            settings.RetryDelayMs = (int)retry.Value;
            settings.MaxAttemptsPerTrack = (int)attempts.Value;
            settings.RefreshIntervalMs = (int)refresh.Value;
            _settings.TrackingCompletion.MaxMemoryBytes = _settings.TrackingCompletion.SampleTypes.Sum(s => s.MaxMemoryBytes);
            form.DialogResult = DialogResult.OK;
        };
        UiLocalization.Apply(form);
        form.ShowDialog(this);
    }

    private Form CreateCompletionEditor(string title, out TableLayoutPanel table, out Button save)
    {
        var form = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, Size = new Size(650, 570),
            MinimumSize = new Size(590, 480), BackColor = BackColor, ForeColor = ForeColor, ShowInTaskbar = false };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) };
        table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        scroll.Controls.Add(table);
        save = MakeButton("Save", Color.SteelBlue);
        var cancel = MakeButton("Cancel", Color.Gray);
        cancel.DialogResult = DialogResult.Cancel;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.Add(save); actions.Controls.Add(cancel);
        form.Controls.Add(scroll); form.Controls.Add(actions);
        form.AcceptButton = save; form.CancelButton = cancel;
        return form;
    }
    private static void AddEditorRow(TableLayoutPanel table, string label, Control value)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(4, 10, 8, 8) }, 0, row);
        value.Dock = DockStyle.Top; value.Margin = new Padding(4, 6, 4, 6);
        table.Controls.Add(value, 1, row);
    }
    private void ValidateCompletionModels()
    {
        foreach (var definition in _settings.RecognitionDefinitions.Where(d => d.Enabled))
        {
            string packageName = Path.ChangeExtension(Path.GetFileName(definition.ModelFile), ".hshmodel");
            var directories = definition.Kind == AnalysisKind.Face ? GetModelDirectories("Face", "HshDetectionEngin.Face") : GetModelDirectories("Plate", "HshDetectionEngin.Plate");
            string? path = directories.Select(directory => Path.Combine(directory, packageName)).FirstOrDefault(File.Exists);
            if (path is null || definition.Kind == AnalysisKind.Plate && !PlateOcrModelCatalog.IsOcrModel(path))
                throw new InvalidOperationException($"{definition.Name}: {UiLocalization.T("Recognition model is not available.")}");
        }
    }
}
