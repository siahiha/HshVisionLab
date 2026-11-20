namespace HshVisionLab;

/// <summary>Native trigger and outbound workflow editor for HshVisionLab.</summary>
public sealed class AutomationManagerForm : Form
{
    private readonly DesktopAutomationStore _store;
    private readonly Func<IReadOnlyList<CameraRuntime>> _cameraProvider;
    private readonly DataGridView _historySubscriptionGrid = NewGrid();
    private readonly DataGridView _triggerGrid = NewGrid();
    private readonly DataGridView _invocationGrid = NewGrid();
    private readonly DataGridView _logGrid = NewGrid();
    private readonly TextBox _triggerName = new();
    private readonly CheckedListBox _triggerCameraScope = new()
    {
        CheckOnClick = true,
        IntegralHeight = false,
        Height = 126,
        MinimumSize = new Size(0, 126)
    };
    private readonly TextBox _triggerLabel = new();
    private readonly TextBox _triggerPlate = new();
    private readonly TextBox _triggerIdentity = new();
    private readonly ComboBox _triggerScenario = NewCombo("Any", "Plate", "Face", "Palm", "PlateFace", "PlatePalm");
    private readonly ComboBox _triggerAction = NewCombo("WindowsNotification", "Webhook");
    private readonly TextBox _triggerTarget = new();
    private readonly CheckBox _triggerEnabled = new() { Text = "Enabled", Checked = true, AutoSize = true };
    private readonly NumericUpDown _triggerConfidence = Number(0, 1, 2, .0m);
    private readonly NumericUpDown _triggerCooldown = Number(0, 3600, 0, 0);
    private readonly TextBox _invocationName = new();
    private readonly TextBox _invocationCameras = new();
    private readonly TextBox _invocationEvents = new();
    private readonly NumericUpDown _invocationConfidence = Number(0, 1, 2, .0m);
    private readonly ComboBox _invocationType = NewCombo("Web", "Sqlite");
    private readonly ComboBox _invocationMethod = NewCombo("POST", "PUT", "PATCH");
    private readonly TextBox _invocationUrl = new();
    private readonly TextBox _invocationApiKey = new();
    private readonly TextBox _invocationSqlitePath = new();
    private readonly TextBox _invocationCommand = new();
    private readonly CheckBox _invocationEnabled = new() { Text = "Enabled", Checked = true, AutoSize = true };
    private readonly NumericUpDown _invocationRetries = Number(0, 10, 0, 2);
    private readonly NumericUpDown _invocationTimeout = Number(1, 300, 0, 15);
    private readonly NumericUpDown _invocationRetryDelay = Number(1, 3600, 0, 5);
    private readonly NumericUpDown _eventRetentionDays = Number(0, 3650, 0, 30);
    private readonly NumericUpDown _artifactRetentionDays = Number(0, 3650, 0, 30);
    private readonly TextBox _historyName = new();
    private readonly ComboBox _historyMode = NewCombo("All", "Plate", "Palm", "KnownFace", "KnownPalm");
    private readonly CheckedListBox _historyCameraScope = new()
    {
        CheckOnClick = true,
        IntegralHeight = false,
        Height = 126,
        MinimumSize = new Size(0, 126)
    };
    private readonly TextBox _historyRois = new();
    private readonly CheckBox _historyFaceRequired = new() { Text = "Require Face", AutoSize = true };
    private readonly CheckBox _historyPlateRequired = new() { Text = "Require Plate", AutoSize = true };
    private readonly CheckBox _historyPalmRequired = new() { Text = "Require Palm", AutoSize = true };
    private readonly CheckBox _historyIncludeFace = new() { Text = "Record Face", Checked = true, AutoSize = true };
    private readonly CheckBox _historyIncludePlate = new() { Text = "Record Plate", Checked = true, AutoSize = true };
    private readonly CheckBox _historyIncludePalm = new() { Text = "Record Palm", Checked = true, AutoSize = true };
    private readonly CheckBox _historyUnknownFace = new() { Text = "Include unknown Face", Checked = true, AutoSize = true };
    private readonly CheckBox _historyUnknownPalm = new() { Text = "Include unknown Palm", Checked = true, AutoSize = true };
    private readonly CheckBox _historyArtifacts = new() { Text = "Save artifacts", Checked = true, AutoSize = true };
    private readonly NumericUpDown _historyWindow = Number(0, 10000, 0, 1500);
    private readonly NumericUpDown _historyCooldown = Number(0, 3600, 0, 0);
    private string? _selectedHistoryProfileId;
    private string? _selectedTriggerId;
    private string? _selectedInvocationId;
    private bool _creatingHistoryProfile;
    private bool _creatingTrigger;

    public AutomationManagerForm(DesktopAutomationStore store, Func<IReadOnlyList<CameraRuntime>> cameraProvider)
    {
        _store = store;
        _cameraProvider = cameraProvider;
        Text = "Triggers and workflows";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1280, 780);
        MinimumSize = new Size(980, 620);
        BuildUi();
        UiLocalization.Apply(this);
        RefreshAll();
    }

    private void BuildUi()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildHistorySubscriptionPage());
        tabs.TabPages.Add(BuildTriggersPage());
        tabs.TabPages.Add(BuildInvocationsPage());
        tabs.TabPages.Add(BuildLogsPage());
        Controls.Add(tabs);
    }

    private TabPage BuildHistorySubscriptionPage()
    {
        var page = new TabPage("Application subscription");
        var split = CreateEditorLayout();
        ConfigureHistorySubscriptionGrid(); split.Controls.Add(_historySubscriptionGrid, 0, 0);
        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 18, Padding = new Padding(14), AutoScroll = true };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(editor, 0, "Name", _historyName); AddRow(editor, 1, "Detection axis", _historyMode); AddRow(editor, 2, "Cameras", _historyCameraScope); AddRow(editor, 3, "ROI IDs", _historyRois);
        AddRow(editor, 4, "Required", new FlowLayoutPanel { AutoSize = true, Controls = { _historyFaceRequired, _historyPlateRequired, _historyPalmRequired } });
        AddRow(editor, 5, "Components", new FlowLayoutPanel { AutoSize = true, Controls = { _historyIncludeFace, _historyIncludePlate, _historyIncludePalm } });
        AddRow(editor, 6, "Unknown", new FlowLayoutPanel { AutoSize = true, Controls = { _historyUnknownFace, _historyUnknownPalm } });
        AddRow(editor, 7, "Artifacts", _historyArtifacts); AddRow(editor, 8, "Association window ms", _historyWindow); AddRow(editor, 9, "History cooldown sec", _historyCooldown);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        Button add = MakeButton("New"); add.Click += (_, _) => ClearHistoryProfileEditor(true);
        Button save = MakeButton("Save"); save.Click += (_, _) => SaveHistoryProfile();
        Button delete = MakeButton("Delete", true); delete.Click += (_, _) => DeleteHistoryProfile();
        actions.Controls.AddRange([delete, save, add]); editor.Controls.Add(actions, 1, 11);
        editor.Controls.Add(new Label { Text = "Select the cameras for this subscription. Empty selection means every local camera. These profiles control only what this Windows application records in its local history; the dashboard/history are not query-filtered.", AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.Gray }, 1, 12);
        split.Controls.Add(editor, 1, 0); page.Controls.Add(split); return page;
    }

    private TabPage BuildTriggersPage()
    {
        var page = new TabPage("Triggers");
        var split = CreateEditorLayout();
        ConfigureTriggerGrid(); split.Controls.Add(_triggerGrid, 0, 0);
        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 13, Padding = new Padding(14), AutoScroll = true };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(editor, 0, "Name", _triggerName); AddRow(editor, 1, "Scenario", _triggerScenario); AddRow(editor, 2, "Cameras", _triggerCameraScope);
        AddRow(editor, 3, "Label equals", _triggerLabel); AddRow(editor, 4, "Plate equals", _triggerPlate); AddRow(editor, 5, "Identity equals", _triggerIdentity);
        AddRow(editor, 6, "Min confidence", _triggerConfidence); AddRow(editor, 7, "Cooldown seconds", _triggerCooldown); AddRow(editor, 8, "Action", _triggerAction); AddRow(editor, 9, "Target / URL", _triggerTarget);
        editor.Controls.Add(_triggerEnabled, 1, 10);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        Button add = MakeButton("New"); add.Click += (_, _) => ClearTriggerEditor(true);
        Button save = MakeButton("Save"); save.Click += (_, _) => SaveTrigger();
        Button delete = MakeButton("Delete", true); delete.Click += (_, _) => DeleteTrigger();
        actions.Controls.AddRange([delete, save, add]); editor.Controls.Add(actions, 1, 11);
        editor.Controls.Add(new Label { Text = "Select the cameras for this trigger. Empty selection means every local camera. Webhook actions run directly from this Windows process.", AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.Gray }, 1, 12);
        split.Controls.Add(editor, 1, 0); page.Controls.Add(split); return page;
    }

    private TabPage BuildInvocationsPage()
    {
        var page = new TabPage("Invocations");
        var split = CreateEditorLayout();
        ConfigureInvocationGrid(); split.Controls.Add(_invocationGrid, 0, 0);
        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 16, Padding = new Padding(14), AutoScroll = true };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(editor, 0, "Name", _invocationName); AddRow(editor, 1, "Type", _invocationType); AddRow(editor, 2, "Camera IDs", _invocationCameras); AddRow(editor, 3, "Event types", _invocationEvents);
        AddRow(editor, 4, "Min confidence", _invocationConfidence); AddRow(editor, 5, "HTTP method", _invocationMethod); AddRow(editor, 6, "URL", _invocationUrl); AddRow(editor, 7, "API key", _invocationApiKey);
        AddRow(editor, 8, "SQLite path", _invocationSqlitePath); AddRow(editor, 9, "Command text", _invocationCommand);
        AddRow(editor, 10, "Timeout sec", _invocationTimeout); AddRow(editor, 11, "Retries", _invocationRetries); AddRow(editor, 12, "Retry delay sec", _invocationRetryDelay);
        editor.Controls.Add(_invocationEnabled, 1, 13);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        Button add = MakeButton("New"); add.Click += (_, _) => ClearInvocationEditor();
        Button save = MakeButton("Save"); save.Click += (_, _) => SaveInvocation();
        Button delete = MakeButton("Delete", true); delete.Click += (_, _) => DeleteInvocation();
        actions.Controls.AddRange([delete, save, add]); editor.Controls.Add(actions, 1, 14);
        editor.Controls.Add(new Label { Text = "SQLite commands can use $eventId, $cameraId, $label, $confidence, $occurredAtUtc and $payloadJson parameters.", AutoSize = true, ForeColor = Color.Gray }, 1, 15);
        split.Controls.Add(editor, 1, 0); page.Controls.Add(split); return page;
    }

    private TabPage BuildLogsPage()
    {
        var page = new TabPage("Invocation logs");
        ConfigureLogGrid();
        var retention = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8), WrapContents = false };
        retention.Controls.Add(new Label { Text = "Event retention (days)", AutoSize = true, Padding = new Padding(0, 7, 4, 0) }); retention.Controls.Add(_eventRetentionDays);
        retention.Controls.Add(new Label { Text = "Artifact retention (days)", AutoSize = true, Padding = new Padding(12, 7, 4, 0) }); retention.Controls.Add(_artifactRetentionDays);
        Button save = MakeButton("Save retention"); save.Click += (_, _) => { _store.Settings.EventRetentionDays = (int)_eventRetentionDays.Value; _store.Settings.ArtifactRetentionDays = (int)_artifactRetentionDays.Value; _store.Save(); MessageBox.Show(this, "Retention settings saved. They apply at the next application start.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }; retention.Controls.Add(save);
        page.Controls.Add(_logGrid); page.Controls.Add(retention);
        _eventRetentionDays.Value = Math.Clamp(_store.Settings.EventRetentionDays, 0, 3650); _artifactRetentionDays.Value = Math.Clamp(_store.Settings.ArtifactRetentionDays, 0, 3650);
        return page;
    }

    private void ConfigureTriggerGrid()
    {
        _triggerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", DataPropertyName = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _triggerGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Scenario", DataPropertyName = "Scenario", Width = 85 });
        _triggerGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "On", DataPropertyName = "Enabled", Width = 45 });
        _triggerGrid.SelectionChanged += (_, _) => LoadSelectedTrigger();
    }

    private void ConfigureHistorySubscriptionGrid()
    {
        _historySubscriptionGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", DataPropertyName = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _historySubscriptionGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Mode", DataPropertyName = "Mode", Width = 85 });
        _historySubscriptionGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Cameras", DataPropertyName = "Cameras", Width = 125 });
        _historySubscriptionGrid.SelectionChanged += (_, _) => LoadSelectedHistoryProfile();
    }

    private static TableLayoutPanel CreateEditorLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 285));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return layout;
    }

    private void ConfigureInvocationGrid()
    {
        _invocationGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", DataPropertyName = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _invocationGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Type", DataPropertyName = "Type", Width = 80 });
        _invocationGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "On", DataPropertyName = "Enabled", Width = 45 });
        _invocationGrid.SelectionChanged += (_, _) => LoadSelectedInvocation();
    }

    private void ConfigureLogGrid()
    {
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Time", DataPropertyName = "Time", Width = 145 });
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Invocation", DataPropertyName = "Invocation", Width = 160 });
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", DataPropertyName = "Status", Width = 80 });
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Attempt", DataPropertyName = "Attempt", Width = 65 });
        _logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Response / Error", DataPropertyName = "Message", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
    }

    private void RefreshAll(string? triggerIdToSelect = null, string? historyProfileIdToSelect = null)
    {
        _historySubscriptionGrid.DataSource = _store.Settings.HistorySubscription.Profiles.Select(item => new HistoryProfileRow(item)).ToArray();
        _triggerGrid.DataSource = _store.Settings.Triggers.Select(item => new TriggerRow(item)).ToArray();
        _invocationGrid.DataSource = _store.Settings.Invocations.Select(item => new InvocationRow(item)).ToArray();
        _logGrid.DataSource = _store.Logs.Take(300).Select(item => new LogRow(item)).ToArray();
        RefreshHistoryCameraScope();
        RefreshTriggerCameraScope();
        if (_historySubscriptionGrid.Rows.Count > 0 && historyProfileIdToSelect is not null)
        {
            DataGridViewRow row = _historySubscriptionGrid.Rows
                .Cast<DataGridViewRow>()
                .FirstOrDefault(value => value.DataBoundItem is HistoryProfileRow profile && profile.Id == historyProfileIdToSelect)
                ?? _historySubscriptionGrid.Rows[0];
            row.Selected = true;
            _historySubscriptionGrid.CurrentCell = row.Cells[0];
        }
        if (_triggerGrid.Rows.Count > 0)
        {
            DataGridViewRow row = _triggerGrid.Rows
                .Cast<DataGridViewRow>()
                .FirstOrDefault(value => value.DataBoundItem is TriggerRow trigger && trigger.Id == triggerIdToSelect)
                ?? _triggerGrid.Rows[0];
            row.Selected = true;
            _triggerGrid.CurrentCell = row.Cells[0];
        }
        if (_invocationGrid.Rows.Count > 0) _invocationGrid.Rows[0].Selected = true;
    }

    private void LoadSelectedHistoryProfile()
    {
        if (_creatingHistoryProfile) return;
        string? id = (_historySubscriptionGrid.CurrentRow?.DataBoundItem as HistoryProfileRow)?.Id;
        DesktopHistorySubscriptionProfile? item = _store.Settings.HistorySubscription.Profiles.FirstOrDefault(value => value.Id == id);
        if (item is null) return;
        _selectedHistoryProfileId = item.Id;
        _historyName.Text = item.Name; _historyMode.SelectedItem = item.Mode;
        RefreshHistoryCameraScope(item.CameraIds); _historyRois.Text = string.Join(", ", item.RoiIds);
        _historyFaceRequired.Checked = item.FaceRequired; _historyPlateRequired.Checked = item.PlateRequired; _historyPalmRequired.Checked = item.PalmRequired;
        _historyIncludeFace.Checked = item.IncludeFace; _historyIncludePlate.Checked = item.IncludePlate; _historyIncludePalm.Checked = item.IncludePalm;
        _historyUnknownFace.Checked = item.IncludeUnknownFace; _historyUnknownPalm.Checked = item.IncludeUnknownPalm; _historyArtifacts.Checked = item.IncludeArtifacts;
        _historyWindow.Value = Math.Clamp(item.WindowMs, 0, 10000); _historyCooldown.Value = Math.Clamp(item.CooldownSeconds, 0, 3600);
    }

    private void SaveHistoryProfile()
    {
        DesktopHistorySubscriptionProfile? existing = !_creatingHistoryProfile && !string.IsNullOrWhiteSpace(_selectedHistoryProfileId)
            ? _store.Settings.HistorySubscription.Profiles.FirstOrDefault(value => value.Id == _selectedHistoryProfileId)
            : null;
        DesktopHistorySubscriptionProfile item = existing ?? new DesktopHistorySubscriptionProfile();
        item.Name = string.IsNullOrWhiteSpace(_historyName.Text) ? "History scenario" : _historyName.Text.Trim(); item.Mode = _historyMode.Text;
        item.CameraIds = _historyCameraScope.CheckedItems.Cast<CameraScopeItem>().Select(value => value.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); item.RoiIds = Split(_historyRois.Text);
        item.FaceRequired = _historyFaceRequired.Checked; item.PlateRequired = _historyPlateRequired.Checked; item.PalmRequired = _historyPalmRequired.Checked;
        item.IncludeFace = _historyIncludeFace.Checked; item.IncludePlate = _historyIncludePlate.Checked; item.IncludePalm = _historyIncludePalm.Checked;
        item.IncludeUnknownFace = _historyUnknownFace.Checked; item.IncludeUnknownPalm = _historyUnknownPalm.Checked; item.IncludeArtifacts = _historyArtifacts.Checked;
        item.WindowMs = (int)_historyWindow.Value; item.CooldownSeconds = (int)_historyCooldown.Value;
        if (!_store.Settings.HistorySubscription.Profiles.Contains(item)) _store.Settings.HistorySubscription.Profiles.Add(item);
        _selectedHistoryProfileId = item.Id; _creatingHistoryProfile = false; _store.Save(); RefreshAll(historyProfileIdToSelect: item.Id);
    }

    private void DeleteHistoryProfile()
    {
        string? id = (_historySubscriptionGrid.CurrentRow?.DataBoundItem as HistoryProfileRow)?.Id;
        DesktopHistorySubscriptionProfile? item = _store.Settings.HistorySubscription.Profiles.FirstOrDefault(value => value.Id == id);
        if (item is null) return;
        if (MessageBox.Show(this, $"Delete history scenario '{item.Name}'?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _store.Settings.HistorySubscription.Profiles.Remove(item); _store.Save(); ClearHistoryProfileEditor(); RefreshAll();
    }

    private void ClearHistoryProfileEditor(bool creating = false)
    {
        _creatingHistoryProfile = creating; _selectedHistoryProfileId = null;
        _historyName.Text = creating ? $"History scenario {_store.Settings.HistorySubscription.Profiles.Count + 1}" : string.Empty; _historyMode.SelectedIndex = 0;
        RefreshHistoryCameraScope([]); _historyRois.Clear(); _historyFaceRequired.Checked = false; _historyPlateRequired.Checked = false; _historyPalmRequired.Checked = false;
        _historyIncludeFace.Checked = true; _historyIncludePlate.Checked = true; _historyIncludePalm.Checked = true; _historyUnknownFace.Checked = true; _historyUnknownPalm.Checked = true; _historyArtifacts.Checked = true;
        _historyWindow.Value = 1500; _historyCooldown.Value = 0; _historySubscriptionGrid.ClearSelection();
    }

    private void RefreshHistoryCameraScope(IEnumerable<string>? selectedIds = null)
    {
        HashSet<string> selected = (selectedIds ?? _historyCameraScope.CheckedItems.Cast<CameraScopeItem>().Select(value => value.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _historyCameraScope.Items.Clear();
        IReadOnlyList<CameraRuntime> cameras = _cameraProvider() ?? [];
        foreach (CameraRuntime camera in cameras.OrderBy(value => value.Settings.Name).ThenBy(value => value.Settings.Id))
        {
            CameraScopeItem item = new(camera.Settings.Id, string.IsNullOrWhiteSpace(camera.Settings.Name) ? camera.Settings.Id : camera.Settings.Name);
            _historyCameraScope.Items.Add(item, selected.Contains(item.Id));
        }
    }

    private void LoadSelectedTrigger()
    {
        if (_creatingTrigger) return;
        string? id = (_triggerGrid.CurrentRow?.DataBoundItem as TriggerRow)?.Id;
        DesktopTriggerDefinition? item = _store.Settings.Triggers.FirstOrDefault(value => value.Id == id);
        if (item is null) return;
        _selectedTriggerId = item.Id; _triggerName.Text = item.Name; _triggerEnabled.Checked = item.Enabled; _triggerScenario.SelectedItem = item.Scenario;
        RefreshTriggerCameraScope(item.CameraIds); _triggerLabel.Text = item.LabelEquals ?? string.Empty; _triggerPlate.Text = item.PlateTextEquals ?? string.Empty; _triggerIdentity.Text = item.IdentityEquals ?? string.Empty;
        _triggerConfidence.Value = (decimal)Math.Clamp(item.MinimumConfidence ?? 0, 0, 1); _triggerCooldown.Value = Math.Clamp(item.CooldownSeconds, 0, 3600);
        DesktopTriggerActionDefinition action = item.Actions.FirstOrDefault() ?? new DesktopTriggerActionDefinition(); _triggerAction.SelectedItem = action.Type; _triggerTarget.Text = action.Target ?? string.Empty;
    }

    private void LoadSelectedInvocation()
    {
        string? id = (_invocationGrid.CurrentRow?.DataBoundItem as InvocationRow)?.Id;
        DesktopInvocationDefinition? item = _store.Settings.Invocations.FirstOrDefault(value => value.Id == id);
        if (item is null) return;
        _selectedInvocationId = item.Id; _invocationName.Text = item.Name; _invocationEnabled.Checked = item.Enabled; _invocationType.SelectedItem = item.Type; _invocationMethod.SelectedItem = item.Method;
        _invocationCameras.Text = string.Join(", ", item.CameraIds); _invocationEvents.Text = string.Join(", ", item.EventTypes); _invocationUrl.Text = item.Url; _invocationApiKey.Text = item.ApiKey ?? string.Empty; _invocationSqlitePath.Text = item.SqlitePath; _invocationCommand.Text = item.CommandText;
        _invocationConfidence.Value = (decimal)Math.Clamp(item.MinimumConfidence ?? 0, 0, 1); _invocationTimeout.Value = Math.Clamp(item.TimeoutSeconds, 1, 300); _invocationRetries.Value = Math.Clamp(item.MaxRetries, 0, 10); _invocationRetryDelay.Value = Math.Clamp(item.RetryDelaySeconds, 1, 3600);
    }

    private void SaveTrigger()
    {
        DesktopTriggerDefinition? existing = !_creatingTrigger && !string.IsNullOrWhiteSpace(_selectedTriggerId)
            ? _store.Settings.Triggers.FirstOrDefault(value => value.Id == _selectedTriggerId)
            : null;
        DesktopTriggerDefinition item = existing ?? new DesktopTriggerDefinition();
        item.Name = string.IsNullOrWhiteSpace(_triggerName.Text) ? "Trigger" : _triggerName.Text.Trim(); item.Enabled = _triggerEnabled.Checked; item.Scenario = _triggerScenario.Text;
        item.CameraIds = _triggerCameraScope.CheckedItems.Cast<CameraScopeItem>().Select(value => value.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); item.LabelEquals = EmptyToNull(_triggerLabel.Text); item.PlateTextEquals = EmptyToNull(_triggerPlate.Text); item.IdentityEquals = EmptyToNull(_triggerIdentity.Text); item.MinimumConfidence = _triggerConfidence.Value <= 0 ? null : (float)_triggerConfidence.Value; item.CooldownSeconds = (int)_triggerCooldown.Value;
        item.Actions = [new DesktopTriggerActionDefinition { Type = _triggerAction.Text, Target = EmptyToNull(_triggerTarget.Text), Enabled = true }];
        if (!_store.Settings.Triggers.Contains(item)) _store.Settings.Triggers.Add(item); _selectedTriggerId = item.Id; _creatingTrigger = false; _store.Save(); RefreshAll(item.Id);
    }

    private void DeleteTrigger() { DesktopTriggerDefinition? item = _store.Settings.Triggers.FirstOrDefault(value => value.Id == _selectedTriggerId); if (item is null) return; if (MessageBox.Show(this, $"Delete trigger '{item.Name}'?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return; _store.Settings.Triggers.Remove(item); _store.Save(); ClearTriggerEditor(); RefreshAll(); }
    private void ClearTriggerEditor(bool creating = false) { _creatingTrigger = creating; _selectedTriggerId = null; _triggerName.Text = creating ? $"Trigger {_store.Settings.Triggers.Count + 1}" : string.Empty; RefreshTriggerCameraScope([]); _triggerLabel.Clear(); _triggerPlate.Clear(); _triggerIdentity.Clear(); _triggerScenario.SelectedIndex = 0; _triggerAction.SelectedIndex = 0; _triggerTarget.Clear(); _triggerEnabled.Checked = true; _triggerConfidence.Value = 0; _triggerCooldown.Value = 0; _triggerGrid.ClearSelection(); }

    private void RefreshTriggerCameraScope(IEnumerable<string>? selectedIds = null)
    {
        HashSet<string> selected = (selectedIds ?? _triggerCameraScope.CheckedItems.Cast<CameraScopeItem>().Select(value => value.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _triggerCameraScope.Items.Clear();
        IReadOnlyList<CameraRuntime> cameras = _cameraProvider() ?? [];
        foreach (CameraRuntime camera in cameras.OrderBy(value => value.Settings.Name).ThenBy(value => value.Settings.Id))
        {
            CameraScopeItem item = new(camera.Settings.Id, string.IsNullOrWhiteSpace(camera.Settings.Name) ? camera.Settings.Id : camera.Settings.Name);
            _triggerCameraScope.Items.Add(item, selected.Contains(item.Id));
        }
    }

    private void SaveInvocation()
    {
        DesktopInvocationDefinition item = _store.Settings.Invocations.FirstOrDefault(value => value.Id == _selectedInvocationId) ?? new DesktopInvocationDefinition();
        item.Name = string.IsNullOrWhiteSpace(_invocationName.Text) ? "Invocation" : _invocationName.Text.Trim(); item.Enabled = _invocationEnabled.Checked; item.Type = _invocationType.Text; item.Method = _invocationMethod.Text; item.CameraIds = Split(_invocationCameras.Text); item.EventTypes = Split(_invocationEvents.Text); item.MinimumConfidence = _invocationConfidence.Value <= 0 ? null : (float)_invocationConfidence.Value; item.Url = _invocationUrl.Text.Trim(); item.ApiKey = EmptyToNull(_invocationApiKey.Text); item.SqlitePath = _invocationSqlitePath.Text.Trim(); item.CommandText = _invocationCommand.Text; item.TimeoutSeconds = (int)_invocationTimeout.Value; item.MaxRetries = (int)_invocationRetries.Value; item.RetryDelaySeconds = (int)_invocationRetryDelay.Value;
        if (!_store.Settings.Invocations.Contains(item)) _store.Settings.Invocations.Add(item); _selectedInvocationId = item.Id; _store.Save(); RefreshAll();
    }

    private void DeleteInvocation() { DesktopInvocationDefinition? item = _store.Settings.Invocations.FirstOrDefault(value => value.Id == _selectedInvocationId); if (item is null) return; if (MessageBox.Show(this, $"Delete invocation '{item.Name}'?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return; _store.Settings.Invocations.Remove(item); _store.Save(); ClearInvocationEditor(); RefreshAll(); }
    private void ClearInvocationEditor() { _selectedInvocationId = null; _invocationName.Clear(); _invocationCameras.Clear(); _invocationEvents.Clear(); _invocationUrl.Clear(); _invocationApiKey.Clear(); _invocationSqlitePath.Clear(); _invocationCommand.Clear(); _invocationType.SelectedIndex = 0; _invocationMethod.SelectedIndex = 0; _invocationConfidence.Value = 0; _invocationEnabled.Checked = true; _invocationGrid.ClearSelection(); }

    private static void AddRow(TableLayoutPanel table, int row, string label, Control control) { control.Dock = DockStyle.Fill; table.RowStyles.Add(new RowStyle(SizeType.AutoSize)); table.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 7, 4, 0) }, 0, row); table.Controls.Add(control, 1, row); }
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static List<string> Split(string value) => value.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static DataGridView NewGrid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoGenerateColumns = false };
    private static ComboBox NewCombo(params string[] values) { var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList }; combo.Items.AddRange(values); combo.SelectedIndex = 0; return combo; }
    private static NumericUpDown Number(decimal min, decimal max, int decimals, decimal value) => new() { Dock = DockStyle.Fill, Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals > 0 ? .05m : 1m, Value = value };
    private static Button MakeButton(string text, bool danger = false) => new() { Text = text, AutoSize = true, MinimumSize = new Size(80, 30), BackColor = danger ? Color.FromArgb(175, 55, 55) : Color.FromArgb(61, 65, 76), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private sealed record CameraScopeItem(string Id, string Name) { public override string ToString() => $"{Name} ({Id})"; }
    private sealed record TriggerRow(DesktopTriggerDefinition Value) { public string Id => Value.Id; public string Name => Value.Name; public string Scenario => Value.Scenario; public bool Enabled => Value.Enabled; }
    private sealed record HistoryProfileRow(DesktopHistorySubscriptionProfile Value) { public string Id => Value.Id; public string Name => Value.Name; public string Mode => Value.Mode; public string Cameras => Value.CameraIds.Count == 0 ? "All" : string.Join(", ", Value.CameraIds); }
    private sealed record InvocationRow(DesktopInvocationDefinition Value) { public string Id => Value.Id; public string Name => Value.Name; public string Type => Value.Type; public bool Enabled => Value.Enabled; }
    private sealed record LogRow(DesktopInvocationLog Value) { public string Time => Value.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); public string Invocation => Value.InvocationName; public string Status => Value.Status; public int Attempt => Value.Attempt; public string Message => Value.Error ?? Value.Response ?? string.Empty; }
}
