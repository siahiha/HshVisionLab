namespace HshVisionLab;

/// <summary>Separate camera list and camera management window.</summary>
public sealed class CameraManagerForm : Form
{
    private readonly Func<CameraRuntime[]> _getCameras;
    private readonly Action _addCamera;
    private readonly Action<CameraRuntime> _toggleCamera;
    private readonly Action<CameraRuntime> _editCamera;
    private readonly Action<CameraRuntime> _removeCamera;
    private readonly Action<CameraRuntime> _selectCamera;
    private readonly DataGridView _grid = new();
    private bool _refreshing;

    private static readonly Color BgPanel = Color.FromArgb(34, 37, 45);
    private static readonly Color BgSurface = Color.FromArgb(42, 45, 54);
    private static readonly Color BgButton = Color.FromArgb(61, 65, 76);
    private static readonly Color AccentBlue = Color.FromArgb(0, 122, 204);
    private static readonly Color AccentRed = Color.FromArgb(190, 60, 60);

    public CameraManagerForm(
        Func<CameraRuntime[]> getCameras,
        Action addCamera,
        Action<CameraRuntime> toggleCamera,
        Action<CameraRuntime> editCamera,
        Action<CameraRuntime> removeCamera,
        Action<CameraRuntime> selectCamera)
    {
        _getCameras = getCameras;
        _addCamera = addCamera;
        _toggleCamera = toggleCamera;
        _editCamera = editCamera;
        _removeCamera = removeCamera;
        _selectCamera = selectCamera;

        Text = UiLocalization.T("Camera manager");
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 360);
        Size = new Size(920, 500);
        BackColor = BgPanel;
        ForeColor = Color.Gainsboro;
        Font = new Font(UiLocalization.FontFamily, UiLocalization.DefaultFontSize);

        var add = new Button
        {
            Text = UiLocalization.T("Add camera"),
            AutoSize = true,
            BackColor = AccentBlue,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Padding = new Padding(12, 5, 12, 5),
            Cursor = Cursors.Hand
        };
        add.FlatAppearance.BorderSize = 0;
        add.Click += (_, _) => _addCamera();

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 8, 12, 6), BackColor = BgPanel };
        toolbar.Controls.Add(add);

        ConfigureGrid();
        _grid.CellContentClick += Grid_CellContentClick;
        _grid.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _grid.CurrentRow?.Tag is CameraRuntime camera) _selectCamera(camera);
        };
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].Tag is CameraRuntime camera) _editCamera(camera);
        };

        Controls.Add(_grid);
        Controls.Add(toolbar);
        UiLocalization.Apply(this);
        RefreshCameras(_getCameras(), null);
    }

    public void RefreshCameras(CameraRuntime[] cameras, string? selectedId)
    {
        if (IsDisposed) return;
        _refreshing = true;
        _grid.SuspendLayout();
        try
        {
            _grid.Rows.Clear();
            foreach (CameraRuntime camera in cameras)
            {
                int row = _grid.Rows.Add(
                    camera.Settings.Name,
                    camera.Settings.SourceUrl,
                    camera.IsRunning ? "RUNNING" : "STOPPED",
                    camera.IsRunning ? camera.ProcessingFps.ToString("0.0") : "--",
                    camera.IsRunning ? UiLocalization.T("Stop") : UiLocalization.T("Start"),
                    UiLocalization.T("Edit"),
                    UiLocalization.T("Delete"));
                _grid.Rows[row].Tag = camera;
                _grid.Rows[row].Selected = camera.Settings.Id == selectedId;
                _grid.Rows[row].Cells[2].Style.ForeColor = camera.IsRunning ? Color.LightGreen : Color.Silver;
                _grid.Rows[row].Cells[4].Style.ForeColor = camera.IsRunning ? Color.Orange : Color.LightGreen;
                _grid.Rows[row].Cells[6].Style.ForeColor = Color.Salmon;
            }
            if (_grid.Rows.Count == 0) _grid.ClearSelection();
        }
        finally
        {
            _grid.ResumeLayout();
            _refreshing = false;
        }
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AutoGenerateColumns = false;
        _grid.BackgroundColor = BgSurface;
        _grid.BorderStyle = BorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.MultiSelect = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.ColumnHeadersHeight = 30;
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(50, 54, 64), ForeColor = Color.White, Font = new Font(Font, FontStyle.Bold) };
        _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = BgSurface, ForeColor = Color.Gainsboro, SelectionBackColor = Color.FromArgb(0, 90, 150), SelectionForeColor = Color.White, Padding = new Padding(6, 3, 6, 3) };
        _grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(38, 41, 49), ForeColor = Color.Gainsboro };
        _grid.RowTemplate.Height = 28;

        AddTextColumn("Name", "Name", 150);
        AddTextColumn("Source", "Source", 320);
        AddTextColumn("Status", "Status", 110);
        AddTextColumn("FPS", "FPS", 75);
        AddButtonColumn("StartStop", "Start / Stop", 105);
        AddButtonColumn("Edit", "Edit", 80);
        AddButtonColumn("Delete", "Delete", 80);
    }

    private void AddTextColumn(string name, string header, int weight) => _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = UiLocalization.T(header), FillWeight = weight, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
    private void AddButtonColumn(string name, string header, int weight) => _grid.Columns.Add(new DataGridViewButtonColumn { Name = name, HeaderText = UiLocalization.T(header), FillWeight = weight, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FlatStyle = FlatStyle.Flat, UseColumnTextForButtonValue = false, SortMode = DataGridViewColumnSortMode.NotSortable });

    private void Grid_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not CameraRuntime camera) return;
        switch (_grid.Columns[e.ColumnIndex].Name)
        {
            case "StartStop":
                _toggleCamera(camera);
                break;
            case "Edit": _editCamera(camera); break;
            case "Delete": _removeCamera(camera); break;
        }
    }
}
