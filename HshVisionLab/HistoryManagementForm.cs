using System.Drawing.Imaging;

namespace HshVisionLab;

/// <summary>Native event-store browser for the standalone Windows application.</summary>
public sealed class HistoryManagementForm : Form
{
    private readonly DesktopEventStore _store;
    private readonly Action<DateTime?, DateTime?> _clearRuntimeHistory;
    private readonly TextBox _search = new();
    private readonly ComboBox _scenario = new();
    private readonly ComboBox _deletePreset = new();
    private readonly DataGridView _grid = new();
    private readonly PictureBox _frame = new();
    private readonly PictureBox _crop = new();
    private readonly TextBox _payload = new();
    private readonly Label _pageInfo = new();
    private readonly Label _detail = new();
    private readonly Button _previous = new();
    private readonly Button _next = new();
    private readonly List<DesktopEventRecord> _items = [];
    private int _page;
    private const int PageSize = 50;

    public HistoryManagementForm(DesktopEventStore store, Action<DateTime?, DateTime?> clearRuntimeHistory)
    {
        _store = store;
        _clearRuntimeHistory = clearRuntimeHistory;
        Text = "Detection history";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1320, 780);
        MinimumSize = new Size(980, 620);
        BuildUi();
        UiLocalization.Apply(this);
        RefreshRows();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        filters.Controls.Add(new Label { Text = "Search", AutoSize = true, Padding = new Padding(0, 8, 4, 0) });
        _search.Width = 230;
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) RefreshRows(); };
        filters.Controls.Add(_search);
        filters.Controls.Add(new Label { Text = "Scenario", AutoSize = true, Padding = new Padding(12, 8, 4, 0) });
        _scenario.DropDownStyle = ComboBoxStyle.DropDownList;
        _scenario.Items.AddRange(["All", "Plate", "Face", "Palm"]);
        _scenario.SelectedIndex = 0;
        _scenario.SelectedIndexChanged += (_, _) => RefreshRows();
        filters.Controls.Add(_scenario);
        Button refresh = MakeButton("Refresh"); refresh.Click += (_, _) => RefreshRows(); filters.Controls.Add(refresh);
        Button deleteAll = MakeButton("Delete all", true); deleteAll.Click += (_, _) => DeleteAll(); filters.Controls.Add(deleteAll);
        filters.Controls.Add(new Label { Text = "Delete range", AutoSize = true, Padding = new Padding(12, 8, 4, 0) });
        _deletePreset.DropDownStyle = ComboBoxStyle.DropDownList;
        _deletePreset.Items.AddRange(["Today", "Last 7 days", "Last 30 days", "Before selected date"]);
        _deletePreset.SelectedIndex = 0;
        filters.Controls.Add(_deletePreset);
        var deleteDate = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 105, Value = DateTime.Today };
        Button deleteRange = MakeButton("Delete"); deleteRange.Click += (_, _) => DeleteRange(_deletePreset.SelectedIndex, deleteDate.Value.Date); filters.Controls.Add(deleteDate); filters.Controls.Add(deleteRange);
        root.Controls.Add(filters, 0, 0);

        var split = CreateHistoryLayout();
        ConfigureGrid();
        split.Controls.Add(_grid, 0, 0);

        var details = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(8, 0, 0, 0) };
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        _detail.Dock = DockStyle.Fill; _detail.AutoSize = true; _detail.Padding = new Padding(0, 0, 0, 6);
        details.Controls.Add(_detail, 0, 0);
        var images = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2 };
        images.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); images.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        ConfigurePicture(_frame); ConfigurePicture(_crop);
        images.Controls.Add(WrapImage(_frame, "Full frame"), 0, 0); images.Controls.Add(WrapImage(_crop, "Crop"), 1, 0);
        details.Controls.Add(images, 0, 1);
        _payload.Multiline = true; _payload.ReadOnly = true; _payload.ScrollBars = ScrollBars.Both; _payload.Dock = DockStyle.Fill; _payload.Font = new Font("Consolas", 9F);
        details.Controls.Add(_payload, 0, 3);
        split.Controls.Add(details, 1, 0);
        root.Controls.Add(split, 0, 1);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        Button close = MakeButton("Close"); close.Click += (_, _) => Close(); footer.Controls.Add(close);
        _next.Text = "Next"; _next.AutoSize = true; _next.Click += (_, _) => { _page++; RefreshPage(); }; footer.Controls.Add(_next);
        _pageInfo.AutoSize = true; _pageInfo.Padding = new Padding(8, 8, 8, 0); footer.Controls.Add(_pageInfo);
        _previous.Text = "Previous"; _previous.AutoSize = true; _previous.Click += (_, _) => { _page--; RefreshPage(); }; footer.Controls.Add(_previous);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
    }

    private static TableLayoutPanel CreateHistoryLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 430));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return layout;
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill; _grid.ReadOnly = true; _grid.AllowUserToAddRows = false; _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.MultiSelect = false; _grid.AutoGenerateColumns = false;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Time", DataPropertyName = nameof(HistoryRow.Time), Width = 145 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Camera", DataPropertyName = nameof(HistoryRow.Camera), Width = 140 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Scenario", DataPropertyName = nameof(HistoryRow.Scenario), Width = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Label", DataPropertyName = nameof(HistoryRow.Label), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Confidence", DataPropertyName = nameof(HistoryRow.Confidence), Width = 85 });
        _grid.SelectionChanged += (_, _) => ShowSelected();
    }

    private void RefreshRows()
    {
        _items.Clear();
        _items.AddRange(_store.Query(_search.Text, _scenario.SelectedItem?.ToString(), 500));
        _page = 0;
        RefreshPage();
    }

    private void RefreshPage()
    {
        int pageCount = Math.Max(1, (int)Math.Ceiling(_items.Count / (double)PageSize));
        _page = Math.Clamp(_page, 0, pageCount - 1);
        DesktopEventRecord[] page = _items.Skip(_page * PageSize).Take(PageSize).ToArray();
        _grid.DataSource = page.Select(item => new HistoryRow(item)).ToArray();
        _pageInfo.Text = $"Page {_page + 1} of {pageCount} · {_items.Count} events";
        _previous.Enabled = _page > 0; _next.Enabled = _page < pageCount - 1;
        if (_grid.Rows.Count > 0) _grid.Rows[0].Selected = true;
        else ClearDetails();
    }

    private void ShowSelected()
    {
        DesktopEventRecord? item = (_grid.CurrentRow?.DataBoundItem as HistoryRow)?.Record;
        if (item is null) { ClearDetails(); return; }
        _detail.Text = $"{item.CameraName} · {item.Scenario} · {item.Label} · {item.Confidence:P1} · {item.OccurredAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\nTrigger: {(item.TriggerMatched ? string.Join(", ", item.TriggerIds) : "none")}";
        LoadImage(_frame, item.FullFramePath); LoadImage(_crop, item.CropPath); _payload.Text = item.PayloadJson;
    }

    private void ClearDetails()
    {
        _detail.Text = "No event selected"; _payload.Clear(); ClearImage(_frame); ClearImage(_crop);
    }

    private void DeleteAll()
    {
        if (MessageBox.Show(this, "Delete all local events and image evidence?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _store.Delete(); _clearRuntimeHistory(null, null); RefreshRows();
    }

    private void DeleteRange(int mode, DateTime date)
    {
        DateTime now = DateTime.UtcNow;
        DateTime? from = mode switch
        {
            0 => DateTime.Today.ToUniversalTime(),
            1 => now.AddDays(-7),
            2 => now.AddDays(-30),
            _ => null
        };
        DateTime? to = mode == 3 ? date.ToUniversalTime() : now;
        string label = mode == 3 ? $"before {date:d}" : _deletePreset.Text.ToLowerInvariant();
        if (MessageBox.Show(this, $"Delete local events for {label}?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _store.Delete(from, to); _clearRuntimeHistory(from, to); RefreshRows();
    }

    private static void ConfigurePicture(PictureBox picture) { picture.Dock = DockStyle.Fill; picture.SizeMode = PictureBoxSizeMode.Zoom; picture.BackColor = Color.Black; }
    private static Panel WrapImage(PictureBox picture, string title) { var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(3) }; panel.Controls.Add(picture); panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 22, ForeColor = Color.White }); return panel; }
    private static void LoadImage(PictureBox picture, string? path) { ClearImage(picture); string full = DesktopEventStore.ResolvePath(path); if (!File.Exists(full)) return; try { using var image = Image.FromFile(full); picture.Image = new Bitmap(image); } catch { } }
    private static void ClearImage(PictureBox picture) { picture.Image?.Dispose(); picture.Image = null; }
    private static Button MakeButton(string text, bool danger = false) => new() { Text = text, AutoSize = true, MinimumSize = new Size(85, 30), BackColor = danger ? Color.FromArgb(175, 55, 55) : Color.FromArgb(61, 65, 76), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private sealed record HistoryRow(DesktopEventRecord Record) { public string Time => Record.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); public string Camera => Record.CameraName; public string Scenario => Record.Scenario; public string Label => Record.Label; public string Confidence => Record.Confidence.ToString("P1"); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { ClearImage(_frame); ClearImage(_crop); }
        base.Dispose(disposing);
    }
}
