using HshDetectionEngin.Face;
using HshDetectionEngin.Plate;

namespace HshVisionLab;

/// <summary>Edits one camera without changing the live runtime until the user presses OK.</summary>
public sealed class CameraSettingsForm : Form
{
    private readonly CameraSettings _settings;
    private readonly IReadOnlyList<ProcessingModuleDescriptor> _processingModules;
    private readonly TextBox _txtName = new();
    private readonly TextBox _txtSource = new();
    private readonly ComboBox _cmbTransport = new();
    private readonly ComboBox _cmbCaptureBackend = new();
    private readonly ComboBox _cmbModel = new();
    private readonly ComboBox _cmbInputSize = new();
    private readonly ComboBox _cmbPreprocessing = new();
    private readonly ComboBox _cmbFaceModel = new();
    private readonly ComboBox _cmbFacePreprocessing = new();
    private readonly ComboBox _cmbFaceRecognitionModel = new();
    private readonly ComboBox _cmbFaceInputSize = new();
    private readonly NumericUpDown _numFaceConfidence = new();
    private readonly NumericUpDown _numFaceRecordConfidence = new();
    private readonly NumericUpDown _numFaceRecognitionThreshold = new();
    private readonly NumericUpDown _numFaceIou = new();
    private readonly NumericUpDown _numFaceTrackMisses = new();
    private readonly NumericUpDown _numFaceMaxFps = new();
    private readonly NumericUpDown _numFaceThreads = new();
    private readonly NumericUpDown _numFaceNms = new();
    private readonly NumericUpDown _numFaceTopK = new();
    private readonly NumericUpDown _numFaceUnknownMatch = new();
    private readonly NumericUpDown _numFaceEventCooldown = new();
    private readonly CheckBox _chkPlateEnabled = new();
    private readonly CheckBox _chkFaceEnabled = new();
    private readonly CheckBox _chkFaceRecognition = new();
    private readonly TreeView _trvProcessing = new();
    private readonly ComboBox _cmbAddProcessing = new();
    private readonly Button _btnAddProcessing = new();
    private readonly Button _btnRemoveProcessing = new();
    private readonly ToolTip _processingToolTip = new();
    private readonly List<(Control Label, Control Value)> _plateProperties = [];
    private readonly List<(Control Label, Control Value)> _faceProperties = [];
    private readonly List<(Control Label, Control Value)> _genericProperties = [];
    private readonly List<(Control Label, Control Value)> _roiProperties = [];
    private readonly List<Control> _plateSectionLabels = [];
    private readonly List<Control> _faceSectionLabels = [];
    private readonly List<Control> _genericSectionLabels = [];
    private readonly List<Control> _roiSectionLabels = [];
    private readonly TextBox _txtSelectedRoiName = new();
    private readonly CheckBox _chkSelectedRoiEnabled = new();
    private string _currentSection = string.Empty;
    private TargetNode? _selectedTarget;
    private CameraProcessingSettings? _selectedProcessing;
    private readonly NumericUpDown _numConfidence = new();
    private readonly NumericUpDown _numIou = new();
    private readonly NumericUpDown _numMaxFps = new();
    private readonly NumericUpDown _numPlateBuffer = new();
    private readonly NumericUpDown _numFaceBuffer = new();
    private readonly NumericUpDown _numThreads = new();
    private readonly NumericUpDown _numGenericMaxFps = new();
    private readonly NumericUpDown _numGenericThreads = new();
    private readonly CheckBox _chkGenericEnabled = new();
    private readonly PropertyGrid _genericOptions = new();
    private readonly NumericUpDown _numReconnect = new();
    private readonly CheckBox _chkDrawBoxes = new();
    private readonly CheckBox _chkMotionGate = new();
    private readonly NumericUpDown _numMotionFps = new();
    private readonly NumericUpDown _numMotionThreshold = new();
    private readonly NumericUpDown _numMotionChanged = new();
    private readonly NumericUpDown _numMotionRoiScale = new();
    private readonly NumericUpDown _numMotionHold = new();
    private readonly NumericUpDown _numActiveDetectionFps = new();
    private readonly NumericUpDown _numIdleDetectionFps = new();
    private readonly NumericUpDown _numTrackMisses = new();
    private SplitContainer? _processingSplit;

    private sealed record PerformancePreset(
        string Name,
        string? PlateModelFile,
        string? FaceModelFile,
        int Threads,
        int BufferCount,
        int PlateInputSize,
        int FaceInputSize,
        int ProcessingFps,
        int ActiveFps,
        int IdleFps,
        int MotionFps,
        int FaceTopK,
        int MotionHoldMs);

    private sealed class TargetNode
    {
        public NamedRoi? Roi { get; init; }
    }

    private sealed record ProcessingNode(TargetNode Target, CameraProcessingSettings Item);
    private sealed record ProcessingOption(ProcessingType Type, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    public CameraSettingsForm(
        CameraSettings source,
        bool isNew,
        IEnumerable<ProcessingModuleDescriptor>? processingModules = null)
    {
        _settings = source.Clone();
        _processingModules = (processingModules ?? [])
            .Where(module => !string.IsNullOrWhiteSpace(module.Type.Value))
            .GroupBy(module => module.Type)
            .Select(group => group.First())
            .OrderBy(module => module.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (_processingModules.Count == 0)
        {
            _processingModules =
            [
                new ProcessingModuleDescriptor(
                    ProcessingType.Plate,
                    "Plate detection",
                    AnalysisKind.Plate,
                    typeof(PlateProcessingOptions),
                    "plate"),
                new ProcessingModuleDescriptor(
                    ProcessingType.Face,
                    "Face detection",
                    AnalysisKind.Face,
                    typeof(FaceProcessingOptions),
                    "face")
            ];
        }

        Text = isNew ? UiLocalization.T("Add Camera") : $"{UiLocalization.T("Edit Camera")} - {_settings.Name}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 680);
        Size = new Size(1020, 800);
        BackColor = Color.FromArgb(30, 33, 40);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9F);
        ShowInTaskbar = false;

        BuildUi();
        UiLocalization.Apply(this);
        LoadValues();
        Shown += (_, _) => ConfigureProcessingLayout();
    }

    public CameraSettings Result => _settings;

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(16),
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var generalTab = new TabPage("General") { BackColor = BackColor, ForeColor = ForeColor, Padding = new Padding(8) };
        var processingTab = new TabPage("Processing") { BackColor = BackColor, ForeColor = ForeColor, Padding = new Padding(8) };
        tabs.TabPages.Add(generalTab);
        tabs.TabPages.Add(processingTab);

        var generalScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var general = CreateSettingsTable();
        AddSection(general, "Camera connection");
        AddRow(general, "Camera name", _txtName);
        AddRow(general, "RTSP / camera source", _txtSource);
        AddRow(general, "RTSP transport", ConfigureCombo(_cmbTransport, "TCP", "UDP"));
        AddRow(general, "RTSP receiver", ConfigureCombo(_cmbCaptureBackend, "FFmpeg", "LibVLC", "MediaMTX"));
        AddRow(general, "Reconnect delay (seconds)", ConfigureNumber(_numReconnect, 1, 120, 1, 0));
        AddSection(general, "Performance");
        AddRow(general, "Draw boxes and labels", ConfigureCheckBox(_chkDrawBoxes));
        AddSection(general, "Motion gate");
        AddRow(general, "Enable motion gate", ConfigureCheckBox(_chkMotionGate));
        AddRow(general, "Motion sampling FPS", ConfigureNumber(_numMotionFps, 1, 60, 1, 0));
        AddRow(general, "Motion threshold", ConfigureNumber(_numMotionThreshold, 1, 255, 1, 0));
        AddRow(general, "Changed percent", ConfigureNumber(_numMotionChanged, 0.01m, 1.0m, 0.01m, 2));
        AddRow(general, "Motion ROI scale (%)", ConfigureNumber(_numMotionRoiScale, 25, 300, 5, 0));
        AddRow(general, "Motion hold (ms)", ConfigureNumber(_numMotionHold, 100, 10000, 100, 0));
        AddSection(general, "Scheduling");
        AddRow(general, "Active detection FPS", ConfigureNumber(_numActiveDetectionFps, 1, 60, 1, 0));
        AddRow(general, "Idle detection FPS", ConfigureNumber(_numIdleDetectionFps, 0, 30, 1, 0));
        generalScroll.Controls.Add(general);
        generalTab.Controls.Add(generalScroll);

        var processingSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            IsSplitterFixed = false,
            FixedPanel = FixedPanel.None
        };
        _processingSplit = processingSplit;
        var processingListPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(4)
        };
        processingListPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        processingListPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        processingListPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var processingTitle = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Text = "ROI processing targets  •  execution order is the tree order",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(95, 175, 255)
        };
        var processingTreePanel = ConfigureProcessingList();
        var processingAddPanel = ConfigureProcessingAdd();
        processingListPanel.Controls.Add(processingAddPanel, 0, 0);
        processingListPanel.Controls.Add(processingTitle, 0, 1);
        processingListPanel.Controls.Add(processingTreePanel, 0, 2);
        processingListPanel.Controls.SetChildIndex(processingAddPanel, 0);
        processingListPanel.Controls.SetChildIndex(processingTitle, 1);
        processingListPanel.Controls.SetChildIndex(processingTreePanel, 2);
        processingSplit.Panel1.Controls.Add(processingListPanel);
        processingSplit.Panel1.Controls.SetChildIndex(processingListPanel, 0);

        var propertyScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var properties = CreateSettingsTable();
        AddSection(properties, "ROI properties");
        _txtSelectedRoiName.Width = 220;
        _chkSelectedRoiEnabled.Text = "Enabled";
        AddRow(properties, "ROI name", _txtSelectedRoiName);
        AddRow(properties, "Status", ConfigureCheckBox(_chkSelectedRoiEnabled));
        AddSection(properties, "Detection properties");
        AddRow(properties, "Enabled", ConfigureCheckBox(_chkPlateEnabled));
        _cmbModel.SelectedIndexChanged += (_, _) => RefreshInputSizes();
        _cmbFaceModel.SelectedIndexChanged += (_, _) => RefreshFaceInputSizes();
        AddRow(properties, "Model", ConfigureCombo(_cmbModel));
        AddRow(properties, "Input size", ConfigureCombo(_cmbInputSize));
        AddRow(properties, "Preprocessing", ConfigureCombo(_cmbPreprocessing, "None", "Standard", "Advanced"));
        AddRow(properties, "Confidence", ConfigureNumber(_numConfidence, 0.05m, 0.95m, 0.01m, 2));
        AddRow(properties, "NMS IoU", ConfigureNumber(_numIou, 0.05m, 0.90m, 0.05m, 2));
        AddRow(properties, "Max processing FPS", ConfigureNumber(_numMaxFps, 1, 30, 1, 0));
        AddRow(properties, "Threads (ONNX Runtime IntraOp)", ConfigureNumber(_numThreads, 1, Math.Max(1, Math.Min(16, Environment.ProcessorCount)), 1, 0));
        AddRow(properties, "Buffer count (0 = newest only)", ConfigureNumber(_numPlateBuffer, 0, 10, 1, 0));
        AddRow(properties, "Track max misses", ConfigureNumber(_numTrackMisses, 1, 30, 1, 0));
        AddSection(properties, "FACE DETECTION  •  YuNet finds face boxes");
        AddRow(properties, "Enabled", ConfigureCheckBox(_chkFaceEnabled));
        AddRow(properties, "Detection model", ConfigureCombo(_cmbFaceModel));
        AddRow(properties, "Input size", ConfigureCombo(_cmbFaceInputSize));
        AddRow(properties, "Preprocessing", ConfigureCombo(_cmbFacePreprocessing, "None", "Standard", "Advanced"));
        AddRow(properties, "Detection confidence", ConfigureNumber(_numFaceConfidence, 0.05m, 0.99m, 0.01m, 2));
        AddRow(properties, "NMS IoU", ConfigureNumber(_numFaceNms, 0.05m, 0.90m, 0.05m, 2));
        AddRow(properties, "Max candidate faces", ConfigureNumber(_numFaceTopK, 1, 10000, 100, 0));

        AddSection(properties, "FACE IDENTIFICATION  •  SFace compares identity");
        AddRow(properties, "Enable identification", ConfigureCheckBox(_chkFaceRecognition));
        AddRow(properties, "Recognition model (SFace)", ConfigureCombo(_cmbFaceRecognitionModel));
        AddRow(properties, "Known-person threshold", ConfigureNumber(_numFaceRecognitionThreshold, 0.05m, 0.99m, 0.01m, 2));
        AddRow(properties, "Unknown-person match threshold", ConfigureNumber(_numFaceUnknownMatch, 0.05m, 0.99m, 0.01m, 2));

        AddSection(properties, "FACE TRACKING AND RECORDING");
        AddRow(properties, "Max processing FPS", ConfigureNumber(_numFaceMaxFps, 1, 30, 1, 0));
        AddRow(properties, "Threads (ONNX Runtime IntraOp)", ConfigureNumber(_numFaceThreads, 1, Math.Max(1, Math.Min(16, Environment.ProcessorCount)), 1, 0));
        AddRow(properties, "Buffer count (0 = newest only)", ConfigureNumber(_numFaceBuffer, 0, 10, 1, 0));
        AddRow(properties, "History record confidence", ConfigureNumber(_numFaceRecordConfidence, 0.05m, 0.99m, 0.01m, 2));
        AddRow(properties, "History event cooldown (seconds)", ConfigureNumber(_numFaceEventCooldown, 1, 3600, 1, 0));
        AddRow(properties, "Tracking IoU", ConfigureNumber(_numFaceIou, 0.05m, 0.90m, 0.05m, 2));
        AddRow(properties, "Track max misses", ConfigureNumber(_numFaceTrackMisses, 1, 60, 1, 0));
        AddSection(properties, "MODULE OPTIONS  •  custom processing");
        AddRow(properties, "Enabled", ConfigureCheckBox(_chkGenericEnabled));
        AddRow(properties, "Max processing FPS", ConfigureNumber(_numGenericMaxFps, 1, 30, 1, 0));
        AddRow(properties, "Threads (ONNX Runtime IntraOp)", ConfigureNumber(
            _numGenericThreads,
            1,
            Math.Max(1, Math.Min(16, Environment.ProcessorCount)),
            1,
            0));
        _genericOptions.ToolbarVisible = false;
        _genericOptions.HelpVisible = true;
        _genericOptions.PropertySort = PropertySort.Alphabetical;
        _genericOptions.Height = 360;
        AddRow(properties, "Module settings", _genericOptions);
        _numThreads.ValueChanged += (_, _) =>
        {
            if (_numFaceThreads.Value != _numThreads.Value) _numFaceThreads.Value = _numThreads.Value;
        };
        _numFaceThreads.ValueChanged += (_, _) =>
        {
            if (_numThreads.Value != _numFaceThreads.Value) _numThreads.Value = _numFaceThreads.Value;
        };
        _numPlateBuffer.ValueChanged += (_, _) =>
        {
            if (_numFaceBuffer.Value != _numPlateBuffer.Value) _numFaceBuffer.Value = _numPlateBuffer.Value;
        };
        _numFaceBuffer.ValueChanged += (_, _) =>
        {
            if (_numPlateBuffer.Value != _numFaceBuffer.Value) _numPlateBuffer.Value = _numFaceBuffer.Value;
        };
        propertyScroll.Controls.Add(properties);
        processingSplit.Panel2.Controls.Add(propertyScroll);
        processingSplit.Panel2.Controls.SetChildIndex(propertyScroll, 0);
        processingTab.Controls.Add(processingSplit);
        root.Controls.Add(tabs, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 12, 0, 0)
        };
        var btnOk = MakeButton("Save", Color.FromArgb(0, 122, 204));
        var btnCancel = MakeButton("Cancel", Color.FromArgb(70, 74, 84));
        var btnRestoreDefaults = MakeButton("Restore Defaults", Color.FromArgb(112, 82, 170));
        btnOk.Click += (_, _) => SaveAndClose();
        btnCancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        btnRestoreDefaults.Click += (_, _) => ShowPerformancePresets(btnRestoreDefaults);
        btnRestoreDefaults.AccessibleName = "Restore performance defaults";
        buttons.Controls.Add(btnOk);
        buttons.Controls.Add(btnCancel);
        buttons.Controls.Add(btnRestoreDefaults);
        root.Controls.Add(buttons, 0, 1);

        Controls.Add(root);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private void ConfigureProcessingLayout()
    {
        SplitContainer? split = _processingSplit;
        if (split is null || split.IsDisposed || split.Width <= 0) return;

        int width = split.Width;
        int leftMinimum = Math.Min(165, Math.Max(120, width - 460));
        int rightMinimum = Math.Min(420, Math.Max(260, width - leftMinimum));
        int maximumDistance = width - rightMinimum;
        if (maximumDistance < leftMinimum) return;

        // Set a valid distance while the default minimum sizes are still active.
        int desiredDistance = Math.Clamp(250, split.Panel1MinSize, maximumDistance);
        split.SplitterDistance = desiredDistance;

        // Apply the responsive minimums only after the form has its real width.
        split.Panel1MinSize = leftMinimum;
        split.Panel2MinSize = rightMinimum;
        split.SplitterDistance = Math.Clamp(desiredDistance, leftMinimum, width - rightMinimum);
    }

    private static TableLayoutPanel CreateSettingsTable()
    {
        var table = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(4) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private void AddSection(TableLayoutPanel table, string title)
    {
        _currentSection = title;
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(95, 175, 255),
            Margin = new Padding(0, 16, 0, 8)
        };
        table.Controls.Add(label, 0, row);
        table.SetColumnSpan(label, 2);
        if (title.StartsWith("Plate", StringComparison.OrdinalIgnoreCase) ||
            title.StartsWith("Detection", StringComparison.OrdinalIgnoreCase)) _plateSectionLabels.Add(label);
        if (title.StartsWith("Face", StringComparison.OrdinalIgnoreCase)) _faceSectionLabels.Add(label);
        if (title.StartsWith("MODULE", StringComparison.OrdinalIgnoreCase)) _genericSectionLabels.Add(label);
        if (title.StartsWith("ROI", StringComparison.OrdinalIgnoreCase)) _roiSectionLabels.Add(label);
    }

    private void AddRow(TableLayoutPanel table, string labelText, Control control)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 12, 8)
        };
        control.Margin = new Padding(0, 4, 0, 4);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        table.Controls.Add(label, 0, row);
        table.Controls.Add(control, 1, row);
        if (_currentSection.StartsWith("Plate", StringComparison.OrdinalIgnoreCase) ||
            _currentSection.StartsWith("Detection", StringComparison.OrdinalIgnoreCase)) _plateProperties.Add((label, control));
        if (_currentSection.StartsWith("Face", StringComparison.OrdinalIgnoreCase)) _faceProperties.Add((label, control));
        if (_currentSection.StartsWith("MODULE", StringComparison.OrdinalIgnoreCase)) _genericProperties.Add((label, control));
        if (_currentSection.StartsWith("ROI", StringComparison.OrdinalIgnoreCase)) _roiProperties.Add((label, control));
    }

    private static ComboBox ConfigureCombo(ComboBox combo, params string[] items)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Items.AddRange(items);
        combo.Width = 220;
        return combo;
    }

    private static NumericUpDown ConfigureNumber(NumericUpDown control, decimal min, decimal max, decimal increment, int decimals)
    {
        control.Minimum = min;
        control.Maximum = max;
        control.Increment = increment;
        control.DecimalPlaces = decimals;
        control.Width = 180;
        return control;
    }

    private static CheckBox ConfigureCheckBox(CheckBox checkBox)
    {
        checkBox.AutoSize = true;
        return checkBox;
    }

    private Control ConfigureProcessingList()
    {
        _trvProcessing.Dock = DockStyle.Fill;
        _trvProcessing.HideSelection = false;
        _trvProcessing.FullRowSelect = true;
        _trvProcessing.BackColor = Color.FromArgb(42, 45, 54);
        _trvProcessing.ForeColor = Color.Gainsboro;
        _trvProcessing.BorderStyle = BorderStyle.FixedSingle;
        _trvProcessing.AfterSelect += (_, _) => ShowSelectedProcessingProperties();
        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(_trvProcessing);
        panel.Controls.SetChildIndex(_trvProcessing, 0);
        return panel;
    }

    private Control ConfigureProcessingAdd()
    {
        _cmbAddProcessing.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbAddProcessing.Items.AddRange(_processingModules
            .Select(module => new ProcessingOption(module.Type, module.DisplayName))
            .Cast<object>()
            .ToArray());
        _cmbAddProcessing.SelectedIndex = 0;
        _btnAddProcessing.Text = "+";
        _btnRemoveProcessing.Text = "−";
        _btnAddProcessing.AutoSize = false;
        _btnRemoveProcessing.AutoSize = false;
        _cmbAddProcessing.Width = 130;
        _btnAddProcessing.Size = new Size(35, 35);
        _btnRemoveProcessing.Size = new Size(35, 35);
        _btnAddProcessing.MinimumSize = _btnAddProcessing.Size;
        _btnRemoveProcessing.MinimumSize = _btnRemoveProcessing.Size;
        _btnAddProcessing.Font = new Font("Segoe UI Symbol", 8F, FontStyle.Bold);
        _btnRemoveProcessing.Font = new Font("Segoe UI Symbol", 8F, FontStyle.Bold);
        _btnAddProcessing.Padding = new Padding(0);
        _btnRemoveProcessing.Padding = new Padding(0);
        _btnAddProcessing.TextAlign = ContentAlignment.MiddleCenter;
        _btnRemoveProcessing.TextAlign = ContentAlignment.MiddleCenter;
        _btnAddProcessing.UseVisualStyleBackColor = false;
        _btnRemoveProcessing.UseVisualStyleBackColor = false;
        _btnAddProcessing.FlatStyle = FlatStyle.Flat;
        _btnRemoveProcessing.FlatStyle = FlatStyle.Flat;
        _btnAddProcessing.FlatAppearance.BorderSize = 1;
        _btnRemoveProcessing.FlatAppearance.BorderSize = 1;
        _btnAddProcessing.FlatAppearance.BorderColor = Color.FromArgb(0, 160, 220);
        _btnRemoveProcessing.FlatAppearance.BorderColor = Color.FromArgb(210, 85, 85);
        _btnAddProcessing.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 145, 205);
        _btnRemoveProcessing.FlatAppearance.MouseOverBackColor = Color.FromArgb(185, 65, 65);
        _btnAddProcessing.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 105, 165);
        _btnRemoveProcessing.FlatAppearance.MouseDownBackColor = Color.FromArgb(145, 45, 45);
        _btnAddProcessing.BackColor = Color.FromArgb(0, 122, 204);
        _btnRemoveProcessing.BackColor = Color.FromArgb(165, 55, 55);
        _btnAddProcessing.ForeColor = Color.White;
        _btnRemoveProcessing.ForeColor = Color.White;
        _btnAddProcessing.Cursor = Cursors.Hand;
        _btnRemoveProcessing.Cursor = Cursors.Hand;
        _btnAddProcessing.AccessibleName = "Add processing";
        _btnRemoveProcessing.AccessibleName = "Remove selected processing";
        _processingToolTip.SetToolTip(_cmbAddProcessing, "Select processing type");
        _processingToolTip.SetToolTip(_btnAddProcessing, "Add selected processing to the selected ROI");
        _processingToolTip.SetToolTip(_btnRemoveProcessing, "Remove selected processing from the selected ROI");
        _btnAddProcessing.Click += (_, _) => AddProcessing();
        _btnRemoveProcessing.Click += (_, _) => RemoveProcessing();
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            WrapContents = true,
            AutoScroll = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 4, 0, 0)
        };
        panel.Controls.Add(_cmbAddProcessing); panel.Controls.Add(_btnAddProcessing); panel.Controls.Add(_btnRemoveProcessing);
        panel.Controls.SetChildIndex(_cmbAddProcessing, 0);
        panel.Controls.SetChildIndex(_btnAddProcessing, 1);
        panel.Controls.SetChildIndex(_btnRemoveProcessing, 2);
        return panel;
    }

    private void RefreshProcessingList()
    {
        object? previousTag = _trvProcessing.SelectedNode?.Tag;
        _trvProcessing.BeginUpdate();
        try
        {
            _trvProcessing.Nodes.Clear();
            foreach (NamedRoi roi in _settings.Rois)
            {
                AddTargetNode(new TargetNode { Roi = roi },
                    $"{(roi.Enabled ? "✓" : "×")}  {roi.Name}", previousTag);
            }

            if (_trvProcessing.Nodes.Count == 0)
            {
                _trvProcessing.Nodes.Add(new TreeNode("No ROI defined. Create an ROI in the main view first.")
                {
                    ForeColor = Color.FromArgb(180, 185, 195)
                });
            }

            _trvProcessing.ExpandAll();
        }
        finally
        {
            _trvProcessing.EndUpdate();
        }

        if (_trvProcessing.SelectedNode is null && _trvProcessing.Nodes.Count > 0 && _trvProcessing.Nodes[0].Tag is not null)
        {
            _trvProcessing.SelectedNode = _trvProcessing.Nodes[0];
        }

        if (_trvProcessing.SelectedNode is null)
        {
            ShowSelectedProcessingProperties();
        }
    }

    private void AddProcessing()
    {
        if (!CommitSelectedProperties()) return;
        TargetNode? target = GetSelectedTarget();
        if (target is null) return;

        ProcessingType type = (_cmbAddProcessing.SelectedItem as ProcessingOption)?.Type ?? _processingModules[0].Type;
        IList<CameraProcessingSettings> items = GetItems(target);
        int suffix = 1;
        string baseName = _processingModules.FirstOrDefault(module =>
            module.Type == type)?.DisplayName ?? type.Value;
        string name = baseName;
        while (items.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} {++suffix}";
        }

        items.Add(_settings.CreateProcessingItem(type, name));
        RefreshProcessingList();
        SelectProcessingItem(items[^1]);
    }

    private void RemoveProcessing()
    {
        if (!CommitSelectedProperties()) return;
        if (_trvProcessing.SelectedNode?.Tag is not ProcessingNode node) return;
        GetItems(node.Target).Remove(node.Item);
        RefreshProcessingList();
    }

    private void ShowSelectedProcessingProperties()
    {
        CommitSelectedProperties();
        _selectedTarget = GetSelectedTarget();
        _selectedProcessing = (_trvProcessing.SelectedNode?.Tag as ProcessingNode)?.Item;

        string editorKey = _selectedProcessing is null ? string.Empty : GetEditorKey(_selectedProcessing.Kind);
        bool plate = editorKey.Equals("plate", StringComparison.OrdinalIgnoreCase);
        bool face = editorKey.Equals("face", StringComparison.OrdinalIgnoreCase);
        bool genericProcessing = _selectedProcessing is not null && !plate && !face;
        bool roi = _selectedProcessing is null && _selectedTarget?.Roi is not null;
        foreach ((Control label, Control value) in _plateProperties) { label.Visible = plate; value.Visible = plate; }
        foreach ((Control label, Control value) in _faceProperties) { label.Visible = face; value.Visible = face; }
        foreach ((Control label, Control value) in _genericProperties) { label.Visible = genericProcessing; value.Visible = genericProcessing; }
        foreach ((Control label, Control value) in _roiProperties) { label.Visible = roi; value.Visible = roi; }
        foreach (Control label in _plateSectionLabels) label.Visible = plate;
        foreach (Control label in _faceSectionLabels) label.Visible = face;
        foreach (Control label in _genericSectionLabels) label.Visible = genericProcessing;
        foreach (Control label in _roiSectionLabels) label.Visible = roi;

        if (_selectedProcessing is not null)
        {
            LoadProcessingProperties(_selectedProcessing);
        }
        else if (_selectedTarget?.Roi is not null)
        {
            _txtSelectedRoiName.Text = _selectedTarget.Roi.Name;
            _chkSelectedRoiEnabled.Checked = _selectedTarget.Roi.Enabled;
        }

        _btnAddProcessing.Enabled = _selectedTarget is not null;
        _btnRemoveProcessing.Enabled = _selectedProcessing is not null;
    }

    private string GetEditorKey(ProcessingType type) =>
        _processingModules.FirstOrDefault(module => module.Type == type)?.EditorKey ?? "options";

    private ProcessingModuleDescriptor? FindModule(ProcessingType type) =>
        _processingModules.FirstOrDefault(module => module.Type == type);

    private void AddTargetNode(TargetNode target, string text, object? previousTag)
    {
        TreeNode node = new(text) { Tag = target };
        _trvProcessing.Nodes.Add(node);
        foreach (CameraProcessingSettings item in GetItems(target))
        {
            string displayName = _processingModules.FirstOrDefault(module =>
                module.Type == item.Kind)?.DisplayName ?? item.Type;
            node.Nodes.Add(new TreeNode($"{(item.Enabled ? "✓" : "×")}  {item.Name} ({displayName})")
            {
                Tag = new ProcessingNode(target, item)
            });
        }

        if (ReferenceEquals(target, previousTag)) _trvProcessing.SelectedNode = node;
        foreach (TreeNode child in node.Nodes)
        {
            if (previousTag is ProcessingNode previous && child.Tag is ProcessingNode current &&
                ReferenceEquals(previous.Item, current.Item))
            {
                _trvProcessing.SelectedNode = child;
            }
        }
    }

    private TargetNode? GetSelectedTarget()
    {
        if (_trvProcessing.SelectedNode?.Tag is TargetNode target) return target;
        if (_trvProcessing.SelectedNode?.Tag is ProcessingNode processing) return processing.Target;
        return null;
    }

    private IList<CameraProcessingSettings> GetItems(TargetNode target) => target.Roi?.Processing ?? [];

    private void SelectProcessingItem(CameraProcessingSettings item)
    {
        foreach (TreeNode targetNode in _trvProcessing.Nodes)
        {
            foreach (TreeNode child in targetNode.Nodes)
            {
                if (child.Tag is ProcessingNode node && ReferenceEquals(node.Item, item))
                {
                    _trvProcessing.SelectedNode = child;
                    return;
                }
            }
        }
    }

    private void LoadProcessingProperties(CameraProcessingSettings item)
    {
        PlateProcessingOptions plate = item.GetOptions<PlateProcessingOptions>();
        FaceProcessingOptions face = item.GetOptions<FaceProcessingOptions>();
        _chkPlateEnabled.Checked = item.Enabled;
        _chkFaceEnabled.Checked = item.Enabled;
        _cmbModel.SelectedItem = _cmbModel.Items.Contains(plate.ModelFile) ? plate.ModelFile : _cmbModel.Items.Cast<string>().FirstOrDefault();
        RefreshInputSizes();
        _cmbInputSize.SelectedItem = _cmbInputSize.Items.Contains(plate.InputSize.ToString()) ? plate.InputSize.ToString() : _cmbInputSize.Items.Cast<string>().FirstOrDefault();
        _cmbPreprocessing.SelectedItem = plate.Preprocessing;
        _numConfidence.Value = (decimal)Math.Clamp(plate.Confidence, 0.05f, 0.95f);
        _numIou.Value = (decimal)Math.Clamp(plate.NmsIoU, 0.05f, 0.90f);
        _numMaxFps.Value = Math.Clamp(item.MaxFps, 1, 30);
        _numThreads.Value = Math.Clamp(item.Threads, 1, (int)_numThreads.Maximum);
        _numTrackMisses.Value = Math.Clamp(plate.TrackMaxMisses, 1, 30);

        _cmbFaceModel.SelectedItem = _cmbFaceModel.Items.Contains(face.ModelFile) ? face.ModelFile : _cmbFaceModel.Items.Cast<string>().FirstOrDefault();
        _cmbFacePreprocessing.SelectedItem = face.Preprocessing;
        RefreshFaceInputSizes();
        _cmbFaceInputSize.SelectedItem = _cmbFaceInputSize.Items.Contains(face.InputSize.ToString()) ? face.InputSize.ToString() : _cmbFaceInputSize.Items.Cast<string>().FirstOrDefault();
        _numFaceConfidence.Value = (decimal)Math.Clamp(face.Confidence, 0.05f, 0.99f);
        _numFaceMaxFps.Value = Math.Clamp(item.MaxFps, 1, 30);
        _numFaceThreads.Value = Math.Clamp(item.Threads, 1, (int)_numFaceThreads.Maximum);
        _numFaceNms.Value = (decimal)Math.Clamp(face.NmsThreshold, 0.05f, 0.90f);
        _numFaceTopK.Value = Math.Clamp(face.TopK, 1, 10000);
        _numFaceRecordConfidence.Value = (decimal)Math.Clamp(face.RecordConfidence, 0.05f, 0.99f);
        _numFaceEventCooldown.Value = Math.Clamp(face.EventCooldownSeconds, 1, 3600);
        _numFaceRecognitionThreshold.Value = (decimal)Math.Clamp(face.RecognitionThreshold, 0.05f, 0.99f);
        _numFaceUnknownMatch.Value = (decimal)Math.Clamp(face.UnknownMatchThreshold, 0.05f, 0.99f);
        _cmbFaceRecognitionModel.SelectedItem = _cmbFaceRecognitionModel.Items.Contains(face.RecognitionModelFile) ? face.RecognitionModelFile : _cmbFaceRecognitionModel.Items.Cast<string>().FirstOrDefault();
        _numFaceIou.Value = (decimal)Math.Clamp(face.MatchIou, 0.05f, 0.90f);
        _numFaceTrackMisses.Value = Math.Clamp(face.TrackMaxMisses, 1, 60);
        _chkFaceRecognition.Checked = face.RecognitionEnabled;

        _chkGenericEnabled.Checked = item.Enabled;
        _numGenericMaxFps.Value = Math.Clamp(item.MaxFps, 1, 30);
        _numGenericThreads.Value = Math.Clamp(item.Threads, 1, (int)_numGenericThreads.Maximum);
        ProcessingModuleDescriptor? module = FindModule(item.Kind);
        _genericOptions.SelectedObject = module?.OptionsType is Type optionsType
            ? item.GetOptions(optionsType)
            : null;
    }

    private bool CommitSelectedProperties()
    {
        if (_selectedProcessing is CameraProcessingSettings item)
        {
            string editorKey = GetEditorKey(item.Kind);
            bool isPlateEditor = editorKey.Equals("plate", StringComparison.OrdinalIgnoreCase);
            bool isFaceEditor = editorKey.Equals("face", StringComparison.OrdinalIgnoreCase);
            PlateProcessingOptions plateOptions = item.GetOptions<PlateProcessingOptions>();
            FaceProcessingOptions faceOptions = item.GetOptions<FaceProcessingOptions>();
            if (!isPlateEditor && !isFaceEditor)
            {
                item.Enabled = _chkGenericEnabled.Checked;
                item.MaxFps = (int)_numGenericMaxFps.Value;
                item.Threads = (int)_numGenericThreads.Value;
                if (_genericOptions.SelectedObject is not null)
                    item.SetOptions(_genericOptions.SelectedObject);
            }
            else if (isFaceEditor)
            {
                item.Enabled = _chkFaceEnabled.Checked;
                item.MaxFps = (int)_numFaceMaxFps.Value;
                item.Threads = (int)_numFaceThreads.Value;
                faceOptions.ModelFile = _cmbFaceModel.SelectedItem?.ToString() ?? faceOptions.ModelFile;
                faceOptions.InputSize = int.TryParse(_cmbFaceInputSize.SelectedItem?.ToString(), out int faceInputSize) ? faceInputSize : faceOptions.InputSize;
                faceOptions.Preprocessing = _cmbFacePreprocessing.SelectedItem?.ToString() ?? faceOptions.Preprocessing;
                faceOptions.Confidence = (float)_numFaceConfidence.Value;
                faceOptions.RecordConfidence = (float)_numFaceRecordConfidence.Value;
                faceOptions.EventCooldownSeconds = (int)_numFaceEventCooldown.Value;
                faceOptions.RecognitionEnabled = _chkFaceRecognition.Checked;
                faceOptions.RecognitionModelFile = _cmbFaceRecognitionModel.SelectedItem?.ToString() ?? faceOptions.RecognitionModelFile;
                faceOptions.RecognitionThreshold = (float)_numFaceRecognitionThreshold.Value;
                faceOptions.UnknownMatchThreshold = (float)_numFaceUnknownMatch.Value;
                faceOptions.MatchIou = (float)_numFaceIou.Value;
                faceOptions.TrackMaxMisses = (int)_numFaceTrackMisses.Value;
                faceOptions.NmsThreshold = (float)_numFaceNms.Value;
                faceOptions.TopK = (int)_numFaceTopK.Value;
                item.SetOptions(faceOptions);
            }
            else
            {
                item.Enabled = _chkPlateEnabled.Checked;
                item.MaxFps = (int)_numMaxFps.Value;
                item.Threads = (int)_numThreads.Value;
                plateOptions.ModelFile = _cmbModel.SelectedItem?.ToString() ?? plateOptions.ModelFile;
                plateOptions.InputSize = int.TryParse(_cmbInputSize.SelectedItem?.ToString(), out int inputSize) ? inputSize : plateOptions.InputSize;
                plateOptions.Preprocessing = _cmbPreprocessing.SelectedItem?.ToString() ?? plateOptions.Preprocessing;
                plateOptions.Confidence = (float)_numConfidence.Value;
                plateOptions.NmsIoU = (float)_numIou.Value;
                plateOptions.TrackMaxMisses = (int)_numTrackMisses.Value;
                item.SetOptions(plateOptions);
            }
            return true;
        }
        else if (_selectedTarget?.Roi is NamedRoi roi)
        {
            string name = _txtSelectedRoiName.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(this, "ROI name is required.", "Camera settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtSelectedRoiName.Focus();
                return false;
            }

            if (_settings.Rois.Any(item =>
                !ReferenceEquals(item, roi) &&
                string.Equals(item.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, "ROI name must be unique for this camera.", "Camera settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtSelectedRoiName.Focus();
                _txtSelectedRoiName.SelectAll();
                return false;
            }

            roi.Name = name;
            roi.Enabled = _chkSelectedRoiEnabled.Checked;
        }

        return true;
    }

    private static Button MakeButton(string text, Color backColor)
    {
        return new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(100, 34),
            BackColor = backColor,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Padding = new Padding(14, 4, 14, 4),
            UseVisualStyleBackColor = false,
            Image = MaterialIconRenderer.CreateForAction(text),
            ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText
        };
    }

    private void LoadValues()
    {
        _txtName.Text = _settings.Name;
        _txtSource.Text = _settings.SourceUrl;
        _cmbTransport.SelectedItem = _settings.Transport;
        _cmbCaptureBackend.SelectedItem = string.Equals(_settings.CaptureBackend, "LibVLC", StringComparison.OrdinalIgnoreCase)
            ? "LibVLC"
            : string.Equals(_settings.CaptureBackend, "MediaMTX", StringComparison.OrdinalIgnoreCase)
                ? "MediaMTX"
                : "FFmpeg";
        LoadModels();
        LoadFaceModels();
        LoadFaceRecognitionModels();
        _settings.EnsureProcessingDefaults();
        _numPlateBuffer.Value = Math.Clamp(_settings.BufferCount, 0, 10);
        _numFaceBuffer.Value = Math.Clamp(_settings.BufferCount, 0, 10);
        _numThreads.Value = Math.Clamp(_settings.Threads, 1, (int)_numThreads.Maximum);
        RefreshProcessingList();
        _numReconnect.Value = Math.Clamp(_settings.ReconnectDelaySec, 1, 120);
        _chkDrawBoxes.Checked = _settings.DrawBoxes;
        _chkMotionGate.Checked = _settings.MotionGateEnabled;
        _numMotionFps.Value = Math.Clamp(_settings.MotionFps, 1, 60);
        _numMotionThreshold.Value = (decimal)Math.Clamp(_settings.MotionThreshold, 1, 255);
        _numMotionChanged.Value = (decimal)Math.Clamp(_settings.MotionChangedPercent, 0.01, 1.0);
        _numMotionRoiScale.Value = (decimal)Math.Clamp(_settings.MotionRoiScalePercent, 25, 300);
        _numMotionHold.Value = Math.Clamp(_settings.MotionHoldMs, 100, 10000);
        _numActiveDetectionFps.Value = Math.Clamp(_settings.ActiveDetectionFps, 1, 60);
        _numIdleDetectionFps.Value = Math.Clamp(_settings.IdleDetectionFps, 0, 30);
        _numTrackMisses.Value = Math.Clamp(_settings.TrackMaxMisses, 1, 30);
    }

    private void SaveAndClose()
    {
        if (string.IsNullOrWhiteSpace(_txtName.Text))
        {
            MessageBox.Show(this, "Camera name is required.", "Camera settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtName.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_txtSource.Text))
        {
            MessageBox.Show(this, "Camera source is required.", "Camera settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtSource.Focus();
            return;
        }

        _settings.Name = _txtName.Text.Trim();
        _settings.SourceUrl = _txtSource.Text.Trim();
        _settings.Transport = _cmbTransport.SelectedItem?.ToString() ?? "TCP";
        _settings.CaptureBackend = _cmbCaptureBackend.SelectedItem?.ToString() ?? "FFmpeg";
        if (!CommitSelectedProperties() || !ValidateRoiNames()) return;
        UpdateProcessingFlags();
        _settings.BufferCount = (int)_numPlateBuffer.Value;
        _settings.ReconnectDelaySec = (int)_numReconnect.Value;
        _settings.DrawBoxes = _chkDrawBoxes.Checked;
        _settings.MotionGateEnabled = _chkMotionGate.Checked;
        _settings.MotionFps = (int)_numMotionFps.Value;
        _settings.MotionThreshold = (double)_numMotionThreshold.Value;
        _settings.MotionChangedPercent = (double)_numMotionChanged.Value;
        _settings.MotionRoiScalePercent = (double)_numMotionRoiScale.Value;
        _settings.MotionHoldMs = (int)_numMotionHold.Value;
        _settings.ActiveDetectionFps = (int)_numActiveDetectionFps.Value;
        _settings.IdleDetectionFps = (int)_numIdleDetectionFps.Value;
        _settings.TrackMaxMisses = (int)_numTrackMisses.Value;
        _settings.ProcessingSchemaVersion = CameraSettings.ProcessingSchemaVersionCurrent;

        DialogResult = DialogResult.OK;
    }

    private void ShowPerformancePresets(Control owner)
    {
        var menu = new ContextMenuStrip();
        AddPresetMenuItem(menu, CreateWeakPreset());
        AddPresetMenuItem(menu, CreateBalancedPreset());
        AddPresetMenuItem(menu, CreateHighPerformancePreset());
        menu.Show(owner, new Point(0, -menu.PreferredSize.Height));
    }

    private void AddPresetMenuItem(ContextMenuStrip menu, PerformancePreset preset)
    {
        ToolStripMenuItem item = new(UiLocalization.T(preset.Name));
        item.Click += (_, _) => ApplyPerformancePreset(preset);
        menu.Items.Add(item);
    }

    private static PerformancePreset CreateWeakPreset() => new(
        "Weak / virtual 6-core", "best_416_int8_qdq_experimental.onnx", "face_yunet_2023mar_int8.onnx",
        Threads: 1, BufferCount: 0,
        PlateInputSize: 416, FaceInputSize: 640, ProcessingFps: 5,
        ActiveFps: 5, IdleFps: 2, MotionFps: 5, FaceTopK: 2000, MotionHoldMs: 1500);

    private static PerformancePreset CreateBalancedPreset() => new(
        "Balanced / normal system", "best.onnx", "face_yunet_2023mar.onnx", Threads: 2, BufferCount: 0,
        PlateInputSize: 416, FaceInputSize: 640, ProcessingFps: 8,
        ActiveFps: 8, IdleFps: 0, MotionFps: 8, FaceTopK: 5000, MotionHoldMs: 1200);

    private PerformancePreset CreateHighPerformancePreset()
    {
        return new(
            "High performance / realtime", "best.onnx", "face_yunet_2023mar.onnx", Threads: 4, BufferCount: 0,
            PlateInputSize: 640, FaceInputSize: 640, ProcessingFps: 15,
            ActiveFps: 15, IdleFps: 5, MotionFps: 15, FaceTopK: 10000, MotionHoldMs: 700);
    }

    private void ApplyPerformancePreset(PerformancePreset preset)
    {
        if (!CommitSelectedProperties()) return;

        _settings.BufferCount = preset.BufferCount;
        _settings.Threads = preset.Threads;
        _settings.ModelFile = SelectAvailableModel(_cmbModel, preset.PlateModelFile, _settings.ModelFile);
        _settings.FaceModelFile = SelectAvailableModel(_cmbFaceModel, preset.FaceModelFile, _settings.FaceModelFile);
        _settings.InputSize = preset.PlateInputSize;
        _settings.FaceInputSize = preset.FaceInputSize;
        _settings.MaxFps = preset.ProcessingFps;
        _settings.FaceMaxFps = preset.ProcessingFps;
        _settings.FaceTopK = preset.FaceTopK;
        _settings.MotionFps = preset.MotionFps;
        _settings.MotionHoldMs = preset.MotionHoldMs;
        _settings.ActiveDetectionFps = preset.ActiveFps;
        _settings.IdleDetectionFps = preset.IdleFps;

        SetNumber(_numPlateBuffer, preset.BufferCount);
        SetNumber(_numFaceBuffer, preset.BufferCount);
        SetNumber(_numThreads, preset.Threads);
        SetNumber(_numFaceThreads, preset.Threads);
        SetNumber(_numMotionFps, preset.MotionFps);
        SetNumber(_numMotionHold, preset.MotionHoldMs);
        SetNumber(_numActiveDetectionFps, preset.ActiveFps);
        SetNumber(_numIdleDetectionFps, preset.IdleFps);
        SetNumber(_numFaceTopK, preset.FaceTopK);
        SelectComboValue(_cmbModel, _settings.ModelFile);
        SelectComboValue(_cmbFaceModel, _settings.FaceModelFile);
        SelectComboValue(_cmbInputSize, preset.PlateInputSize.ToString());
        SelectComboValue(_cmbFaceInputSize, preset.FaceInputSize.ToString());
        SetNumber(_numMaxFps, preset.ProcessingFps);
        SetNumber(_numFaceMaxFps, preset.ProcessingFps);

        foreach (CameraProcessingSettings item in AllProcessingItems())
        {
            item.MaxFps = preset.ProcessingFps;
            item.Threads = preset.Threads;
            if (item.Kind == ProcessingType.Plate)
            {
                PlateProcessingOptions options = item.GetOptions<PlateProcessingOptions>();
                options.ModelFile = SelectAvailableModel(_cmbModel, preset.PlateModelFile, options.ModelFile);
                options.InputSize = preset.PlateInputSize;
                item.SetOptions(options);
            }
            else if (item.Kind == ProcessingType.Face)
            {
                FaceProcessingOptions options = item.GetOptions<FaceProcessingOptions>();
                options.ModelFile = SelectAvailableModel(_cmbFaceModel, preset.FaceModelFile, options.ModelFile);
                options.InputSize = preset.FaceInputSize;
                options.TopK = preset.FaceTopK;
                item.SetOptions(options);
            }
        }

        RefreshProcessingList();
        ShowSelectedProcessingProperties();
        MessageBox.Show(this, UiLocalization.T("Performance preset applied. Press Save to keep it."),
            UiLocalization.T("Camera settings"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private IEnumerable<CameraProcessingSettings> AllProcessingItems() =>
        _settings.Processing.Concat(_settings.Rois.SelectMany(roi => roi.Processing));

    private static void SetNumber(NumericUpDown control, int value) =>
        control.Value = Math.Clamp(value, (int)control.Minimum, (int)control.Maximum);

    private static void SelectComboValue(ComboBox combo, string value)
    {
        int index = combo.Items.IndexOf(value);
        if (index >= 0) combo.SelectedIndex = index;
    }

    private static string SelectAvailableModel(ComboBox combo, string? preferred, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && combo.Items.Contains(preferred)) return preferred;
        if (!string.IsNullOrWhiteSpace(fallback) && combo.Items.Contains(fallback)) return fallback;
        return combo.Items.Count > 0 ? combo.Items[0]?.ToString() ?? fallback : fallback;
    }

    private bool ValidateRoiNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (NamedRoi roi in _settings.Rois)
        {
            string name = roi.Name?.Trim() ?? string.Empty;
            if (name.Length == 0 || !names.Add(name))
            {
                MessageBox.Show(this, "ROI names must be non-empty and unique for this camera.",
                    "Camera settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        return true;
    }

    private void UpdateProcessingFlags()
    {
        CameraProcessingSettings[] all = _settings.Processing
            .Concat(_settings.Rois.SelectMany(roi => roi.Processing))
            .ToArray();
        _settings.PlateEnabled = all.Any(item => item.Enabled && item.Kind == ProcessingType.Plate);
        _settings.FaceEnabled = all.Any(item => item.Enabled && item.Kind == ProcessingType.Face);
    }

    private void LoadModels()
    {
        string[] modelDirectories = GetModelDirectories("Plate", "HshDetectionEngin.Plate");
        string[] modelNames = modelDirectories
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.hshmodel"))
                .Select(path => Path.ChangeExtension(Path.GetFileName(path), ".onnx"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Where(name => !name!.StartsWith("face_", StringComparison.OrdinalIgnoreCase))
                .Where(name => !name!.EndsWith("_ort_optimized.onnx", StringComparison.OrdinalIgnoreCase))
                .Where(name => !name!.Contains("_int8_dynamic", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray()!;

        if (modelNames.Length == 0)
        {
            modelNames = ["best.onnx"];
        }

        _cmbModel.BeginUpdate();
        try
        {
            _cmbModel.Items.Clear();
            _cmbModel.Items.AddRange(modelNames);
        }
        finally
        {
            _cmbModel.EndUpdate();
        }
    }

    private void LoadFaceModels()
    {
        string[] modelDirectories = GetModelDirectories("Face", "HshDetectionEngin.Face");
        string[] modelNames = modelDirectories
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.hshmodel"))
                .Select(path => Path.ChangeExtension(Path.GetFileName(path), ".onnx"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Where(name => name!.Contains("yunet", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToArray()!;
        if (modelNames.Length == 0) modelNames = ["face_yunet_2023mar.onnx"];
        _cmbFaceModel.Items.Clear(); _cmbFaceModel.Items.AddRange(modelNames);
    }

    private void LoadFaceRecognitionModels()
    {
        string[] modelDirectories = GetModelDirectories("Face", "HshDetectionEngin.Face");
        string[] modelNames = modelDirectories
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.hshmodel"))
                .Select(path => Path.ChangeExtension(Path.GetFileName(path), ".onnx"))
                .Where(name => name!.Contains("sface", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToArray()!;
        if (modelNames.Length == 0) modelNames = ["face_recognition_sface_2021dec.onnx"];
        _cmbFaceRecognitionModel.Items.Clear(); _cmbFaceRecognitionModel.Items.AddRange(modelNames);
    }

    private static string[] GetModelDirectories(string capability, string projectDirectory)
    {
        List<string> directories =
        [
            Path.Combine(AppContext.BaseDirectory, "Models", capability),
            Path.Combine(AppContext.BaseDirectory, "Models"),
            Path.Combine(AppContext.BaseDirectory, "Modules", capability, "Models")
        ];

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int i = 0; i < 7 && directory is not null; i++, directory = directory.Parent)
        {
            directories.Add(Path.Combine(directory.FullName, projectDirectory, "Models"));
        }

        return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void RefreshFaceInputSizes()
    {
        string? modelFile = _cmbFaceModel.SelectedItem?.ToString();
        int? detectedSize = string.IsNullOrWhiteSpace(modelFile)
            ? null
            : FaceModelInspector.TryGetSquareInputSize(modelFile);
        var sizes = new HashSet<int>();
        if (detectedSize is > 0) sizes.Add(detectedSize.Value);
        // YuNet models supported by FacePipeline use a fixed 640x640 tensor.
        if (sizes.Count == 0) sizes.Add(640);

        string? previous = _cmbFaceInputSize.SelectedItem?.ToString();
        _cmbFaceInputSize.Items.Clear();
        foreach (int size in sizes.OrderBy(size => size)) _cmbFaceInputSize.Items.Add(size.ToString());
        if (previous is not null && _cmbFaceInputSize.Items.Contains(previous)) _cmbFaceInputSize.SelectedItem = previous;
        else if (_cmbFaceInputSize.Items.Count > 0) _cmbFaceInputSize.SelectedIndex = 0;
    }

    private void RefreshInputSizes()
    {
        string? modelFile = _cmbModel.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(modelFile)) return;

        IReadOnlyList<int> sizes = PlateModelInspector.GetSquareInputSizes(modelFile);
        if (sizes.Count == 0) sizes = [320, 416, 480, 512, 640];

        string? previous = _cmbInputSize.SelectedItem?.ToString();
        _cmbInputSize.Items.Clear();
        foreach (int size in sizes.OrderBy(size => size).Distinct()) _cmbInputSize.Items.Add(size.ToString());
        if (previous is not null && _cmbInputSize.Items.Contains(previous))
            _cmbInputSize.SelectedItem = previous;
        else if (_cmbInputSize.Items.Count > 0)
            _cmbInputSize.SelectedIndex = 0;
    }
}
