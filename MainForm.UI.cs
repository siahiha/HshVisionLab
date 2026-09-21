namespace HshVisionLab;

public sealed partial class MainForm
{
    private sealed class BufferedPictureBox : PictureBox
    {
        public BufferedPictureBox()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            UpdateStyles();
        }
    }

    private DataGridView _cameraGrid = null!;
    private Button _btnManageCameras = null!;
    private Button _btnSaveAll = null!;
    private Button _btnManageFaces = null!;
    private Button _btnStartAll = null!;
    private Button _btnStopAll = null!;
    private Button _btnBackToThumbnails = null!;
    private Button _btnLanguage = null!;
    private Label _lblViewTitle = null!;
    private Panel _topBar = null!;
    private Panel _cameraPanel = null!;
    private Button _cameraHeader = null!;
    private Panel _roiPanel = null!;
    private Panel _platesPanel = null!;
    private StatusStrip _statusStrip = null!;
    private Panel _viewHost = null!;
    private TableLayoutPanel _mainLayout = null!;
    private TableLayoutPanel _rightColumnLayout = null!;
    private TableLayoutPanel _roiLayout = null!;
    private PictureBox _picView = null!;
    private TableLayoutPanel _multiViewGrid = null!;
    private Label _roiTitle = null!;
    private Panel _roiActions = null!;
    private bool _cameraPanelExpanded;
    private bool _isCameraMaximized;
    private string? _maximizedCameraId;
    private readonly Dictionary<string, PictureBox> _cameraTilePictures = new();
    private readonly Dictionary<string, Label> _cameraTileLabels = new();
    private readonly ToolTip _cameraActionToolTip = new();
    private CameraManagerForm? _cameraManagerForm;
    private readonly ToolTip _mainActionToolTip = new();

    private ListBox _lstRois = null!;
    private Button _btnAddRoi = null!;
    private Button _btnEditRoi = null!;
    private Button _btnRenameRoi = null!;
    private Button _btnDeleteRoi = null!;
    private Button _btnClearRoi = null!;
    private readonly ToolTip _roiToolTip = new();

    private enum RoiActionIcon
    {
        Add,
        Edit,
        Rename,
        Delete,
        Clear
    }

    private FlowLayoutPanel _flowPlates = null!;
    private ToolStripStatusLabel _stState = null!;
    private ToolStripStatusLabel _stFps = null!;
    private ToolStripStatusLabel _stInfer = null!;
    private ToolStripStatusLabel _stRes = null!;
    private ToolStripStatusLabel _stDropped = null!;

    private static readonly Color BgDark = Color.FromArgb(24, 26, 32);
    private static readonly Color BgPanel = Color.FromArgb(34, 37, 45);
    private static readonly Color BgSurface = Color.FromArgb(42, 45, 54);
    private static readonly Color BgButton = Color.FromArgb(61, 65, 76);
    private static readonly Color AccentBlue = Color.FromArgb(0, 122, 204);
    private static readonly Color AccentGreen = Color.FromArgb(35, 145, 88);
    private static readonly Color AccentRed = Color.FromArgb(190, 60, 60);

    private void BuildUi()
    {
        Text = UiLocalization.T("Camera Management");
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(960, 620);
        BackColor = BgDark;
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9F);

        BuildTopBar();
        BuildCameraGrid();
        BuildViewArea();
        BuildRoiBar();
        BuildPlateHistory();
        BuildStatusBar();
        ArrangeDockOrder();

        Shown += (_, _) => UpdateCameraGrid();
        Resize += (_, _) => { if (IsHandleCreated && !IsDisposed) RebuildAllHistoryCards(); };
    }

    private void BuildTopBar()
    {
        _topBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 58,
            BackColor = BgPanel,
            Padding = new Padding(16, 8, 16, 8)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 8,
            RowCount = 1
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 1; i < layout.ColumnCount; i++)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        var title = new Label
        {
            Text = UiLocalization.T("Camera Management"),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            ForeColor = Color.White
        };

        _btnManageCameras = MakeButton(string.Empty, BgButton);
        _btnSaveAll = MakeButton(string.Empty, BgButton);
        _btnManageFaces = MakeButton(string.Empty, BgButton);
        _btnStartAll = MakeButton(string.Empty, AccentGreen);
        _btnStopAll = MakeButton(string.Empty, AccentRed);
        _btnBackToThumbnails = MakeButton(string.Empty, BgButton);
        _btnLanguage = MakeButton(string.Empty, BgButton);
        StyleMainActionButton(_btnManageCameras, "view_module", "Manage cameras", BgButton);
        StyleMainActionButton(_btnSaveAll, "save", "Save", BgButton);
        StyleMainActionButton(_btnManageFaces, "face", "Face database", BgButton);
        StyleMainActionButton(_btnStartAll, "play_arrow", "Start All", AccentGreen);
        StyleMainActionButton(_btnStopAll, "stop", "Stop All", AccentRed);
        StyleMainActionButton(_btnBackToThumbnails, "view_module", "Thumbnails", BgButton);
        StyleMainActionButton(_btnLanguage, "language", "Switch language", BgButton);
        _btnBackToThumbnails.Visible = false;

        _btnManageCameras.Click += (_, _) => ShowCameraManager();
        _btnSaveAll.Click += (_, _) => OnSaveSettingsClick();
        _btnManageFaces.Click += (_, _) => ManageFaceDatabase();
        _btnStartAll.Click += (_, _) => StartAllCameras();
        _btnStopAll.Click += (_, _) => StopAllCameras();
        _btnBackToThumbnails.Click += (_, _) => ShowThumbnails();
        _btnLanguage.Click += (_, _) => UiLocalization.ToggleAndRestart(this);

        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(_btnManageCameras, 1, 0);
        layout.Controls.Add(_btnStartAll, 2, 0);
        layout.Controls.Add(_btnStopAll, 3, 0);
        layout.Controls.Add(_btnSaveAll, 4, 0);
        layout.Controls.Add(_btnManageFaces, 5, 0);
        layout.Controls.Add(_btnBackToThumbnails, 6, 0);
        layout.Controls.Add(_btnLanguage, 7, 0);
        _topBar.Controls.Add(layout);
        Controls.Add(_topBar);
    }

    private void BuildCameraGrid()
    {
        _cameraPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 154,
            BackColor = BgDark,
            Padding = new Padding(12, 6, 12, 4)
        };

        _cameraHeader = new Button
        {
            Text = "Cameras  •  hover to open  •  double-click preview to maximize",
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = BgPanel,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            FlatAppearance = { BorderSize = 0 },
            Padding = new Padding(8, 0, 8, 0),
            Margin = Padding.Empty,
            Cursor = Cursors.Hand
        };

        _cameraGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            BackgroundColor = BgSurface,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            ColumnHeadersHeight = 30,
            EnableHeadersVisualStyles = false,
            MultiSelect = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            ScrollBars = ScrollBars.Both,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        _cameraGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(50, 54, 64),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Alignment = DataGridViewContentAlignment.MiddleLeft
        };
        _cameraGrid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = BgSurface,
            ForeColor = Color.Gainsboro,
            SelectionBackColor = Color.FromArgb(0, 90, 150),
            SelectionForeColor = Color.White,
            Padding = new Padding(6, 3, 6, 3)
        };
        _cameraGrid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(38, 41, 49),
            ForeColor = Color.Gainsboro
        };
        _cameraGrid.RowTemplate.Height = 28;

        AddGridTextColumn("Name", "Name", 150);
        AddGridTextColumn("Source", "Source", 320);
        AddGridTextColumn("Status", "Status", 110);
        AddGridTextColumn("FPS", "FPS", 75);
        AddGridButtonColumn("StartStop", "Start / Stop", 105);
        AddGridButtonColumn("Edit", "Edit", 80);
        AddGridButtonColumn("Delete", "Delete", 80);

        _cameraHeader.Dock = DockStyle.None;
        _cameraHeader.Dock = DockStyle.Top;
        _cameraHeader.Height = 30;
        _cameraGrid.Dock = DockStyle.Fill;
        _cameraGrid.Margin = Padding.Empty;
        _cameraPanel.Controls.Add(_cameraGrid);
        _cameraPanel.Controls.Add(_cameraHeader);
        _cameraHeader.BringToFront();
        WireCameraPanelHover(_cameraPanel);
        WireCameraPanelHover(_cameraHeader);
        WireCameraPanelHover(_cameraGrid);
        // The camera grid is hosted by CameraManagerForm. It is still built here
        // because the main form uses it as the canonical selection/state model.
    }

    private void WireCameraPanelHover(Control control)
    {
        control.MouseEnter += (_, _) => SetCameraPanelExpanded(true);
        control.MouseLeave += (_, _) => ScheduleCameraPanelCollapse();
    }

    private void ScheduleCameraPanelCollapse()
    {
        if (IsDisposed || Disposing || !_cameraPanelExpanded)
        {
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed || Disposing || _cameraPanel.IsDisposed)
                {
                    return;
                }

                Point cursor = _cameraPanel.PointToClient(Cursor.Position);
                if (!_cameraPanel.ClientRectangle.Contains(cursor))
                {
                    SetCameraPanelExpanded(false);
                }
            });
        }
        catch
        {
            // The form can close while a mouse event is being dispatched.
        }
    }

    private void SetCameraPanelExpanded(bool expanded)
    {
        _cameraPanelExpanded = expanded;
        _cameraPanel.Visible = true;
        if (_cameraHeader is not null)
        {
            _cameraHeader.Text = expanded
                ? "Cameras  •  move away to close  •  double-click preview to maximize"
                : "Cameras  •  hover to open  •  double-click preview to maximize";
        }
        if (_cameraGrid is not null)
        {
            _cameraGrid.Visible = expanded;
        }

        if (_mainLayout is null || _cameraPanel is null || _mainLayout.RowStyles.Count < 1)
        {
            return;
        }

        _mainLayout.RowStyles[0].Height = expanded ? 154 : 38;
        _cameraPanel.BringToFront();
        if (_cameraHeader is not null)
        {
            _cameraHeader.BringToFront();
        }
        _mainLayout.PerformLayout();
    }

    private void AddGridTextColumn(string name, string header, int width)
    {
        _cameraGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            FillWeight = width,
            MinimumWidth = Math.Min(width, 120),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    private void AddGridButtonColumn(string name, string header, int width)
    {
        _cameraGrid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = name,
            HeaderText = header,
            FillWeight = width,
            MinimumWidth = Math.Min(width, 92),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FlatStyle = FlatStyle.Flat,
            UseColumnTextForButtonValue = false,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    private void BuildViewArea()
    {
        _platesPanel = new Panel
        {
            Dock = DockStyle.Right,
            Width = 365,
            Padding = new Padding(8, 8, 16, 8),
            BackColor = BgDark
        };

        var platesTitle = new Label
        {
            Text = "Detected events • All cameras",
            Dock = DockStyle.Top,
            Height = 32,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Color.White
        };

        _flowPlates = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.FromArgb(16, 17, 22),
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(6)
        };
        _platesPanel.Controls.Add(_flowPlates);
        _platesPanel.Controls.Add(platesTitle);
        Controls.Add(_platesPanel);

        _viewHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 8, 8, 4),
            BackColor = Color.FromArgb(12, 13, 17)
        };

        _lblViewTitle = new Label
        {
            Text = "Camera thumbnails",
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            ForeColor = Color.White
        };

        _multiViewGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(9, 10, 13),
            Padding = new Padding(4),
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single
        };

        _picView = new BufferedPictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black,
            Visible = false,
            Cursor = Cursors.Hand
        };
        _picView.DoubleClick += (_, _) =>
        {
            if (!_roiEditMode)
            {
                ShowThumbnails();
            }
        };
        _picView.MouseClick += PicView_MouseClick;
        _picView.Paint += PicView_Paint;

        _viewHost.Controls.Add(_multiViewGrid);
        _viewHost.Controls.Add(_picView);
        _viewHost.Controls.Add(_lblViewTitle);
        Controls.Add(_viewHost);

    }

    private void BuildRoiBar()
    {
        _roiPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Width = 390,
            Height = 190,
            BackColor = BgPanel,
            Padding = new Padding(8)
        };

        _roiTitle = new Label
        {
            Text = "ROI manager",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.White
        };

        _lstRois = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            ItemHeight = 24,
            BackColor = BgSurface,
            ForeColor = Color.Gainsboro,
            BorderStyle = BorderStyle.FixedSingle
        };
        _lstRois.SelectedIndexChanged += (_, _) =>
        {
            UpdateRoiActions();
            _picView.Invalidate();
        };

        _roiActions = new Panel
        {
            Dock = DockStyle.Top,
            Height = 32,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            BackColor = BgPanel,
        };
        _btnAddRoi = MakeButton(string.Empty, AccentBlue);
        _btnEditRoi = MakeButton(string.Empty, BgButton);
        _btnRenameRoi = MakeButton(string.Empty, BgButton);
        _btnDeleteRoi = MakeButton(string.Empty, AccentRed);
        _btnClearRoi = MakeButton(string.Empty, BgButton);
        StyleRoiActionButton(_btnAddRoi, RoiActionIcon.Add, "Add ROI", AccentBlue, Color.FromArgb(0, 160, 220));
        StyleRoiActionButton(_btnEditRoi, RoiActionIcon.Edit, "Edit ROI points", BgButton, Color.FromArgb(105, 112, 130));
        StyleRoiActionButton(_btnRenameRoi, RoiActionIcon.Rename, "Rename selected ROI", BgButton, Color.FromArgb(105, 112, 130));
        StyleRoiActionButton(_btnDeleteRoi, RoiActionIcon.Delete, "Delete selected ROI", AccentRed, Color.FromArgb(220, 90, 90));
        StyleRoiActionButton(_btnClearRoi, RoiActionIcon.Clear, "Clear all ROIs", BgButton, Color.FromArgb(105, 112, 130));
        _btnAddRoi.Click += (_, _) => AddRoi();
        _btnEditRoi.Click += (_, _) => ToggleRoiEditMode();
        _btnRenameRoi.Click += (_, _) => RenameSelectedRoi();
        _btnDeleteRoi.Click += (_, _) => DeleteSelectedRoi();
        _btnClearRoi.Click += (_, _) => ClearRoi();
        Button[] roiActionButtons = [_btnAddRoi, _btnEditRoi, _btnRenameRoi, _btnDeleteRoi, _btnClearRoi];
        for (int i = 0; i < roiActionButtons.Length; i++)
        {
            roiActionButtons[i].Dock = DockStyle.Left;
            _roiActions.Controls.Add(roiActionButtons[i]);
            roiActionButtons[i].BringToFront();
        }

        var roiHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = BgPanel
        };
        roiHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        roiHeader.Controls.Add(_roiTitle, 0, 0);

        _roiLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = BgPanel,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        _roiLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _roiLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        _roiLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        _roiLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _roiLayout.Controls.Add(roiHeader, 0, 0);
        _roiLayout.Controls.Add(_roiActions, 0, 1);
        _roiLayout.Controls.Add(_lstRois, 0, 2);
        _roiLayout.Controls.SetChildIndex(roiHeader, 0);
        _roiLayout.Controls.SetChildIndex(_roiActions, 1);
        _roiLayout.Controls.SetChildIndex(_lstRois, 2);
        _roiPanel.Controls.Add(_roiLayout);
        Controls.Add(_roiPanel);
    }

    private void BuildPlateHistory()
    {
        // The actual history panel is built in BuildViewArea so docking remains simple.
    }

    private void BuildStatusBar()
    {
        _statusStrip = new StatusStrip
        {
            SizingGrip = false,
            BackColor = BgPanel,
            Padding = new Padding(10, 3, 10, 3)
        };
        _stState = new ToolStripStatusLabel { Text = "Ready", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _stFps = new ToolStripStatusLabel { Text = "FPS: --" };
        _stInfer = new ToolStripStatusLabel { Text = "| infer: -- ms" };
        _stRes = new ToolStripStatusLabel { Text = "| res: --" };
        _stDropped = new ToolStripStatusLabel { Text = "| dropped: 0" };
        _statusStrip.Items.AddRange([_stState, _stFps, _stInfer, _stRes, _stDropped]);
        Controls.Add(_statusStrip);
    }

    private void ArrangeDockOrder()
    {
        // Keep all major regions in one explicit layout so panels cannot overlap the view.
        Controls.Remove(_cameraPanel);
        Controls.Remove(_viewHost);
        Controls.Remove(_roiPanel);
        Controls.Remove(_platesPanel);
        _mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = BgDark,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        _mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 390));
        _mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _rightColumnLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = BgDark,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        _rightColumnLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _rightColumnLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _rightColumnLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));

        _viewHost.Dock = DockStyle.Fill;
        _platesPanel.Dock = DockStyle.Fill;
        _roiPanel.Dock = DockStyle.Fill;
        _mainLayout.Controls.Add(_viewHost, 0, 0);
        _mainLayout.Controls.Add(_rightColumnLayout, 1, 0);
        _rightColumnLayout.Controls.Add(_platesPanel, 0, 0);
        _rightColumnLayout.Controls.Add(_roiPanel, 0, 1);
        Controls.Add(_mainLayout);
        Controls.SetChildIndex(_mainLayout, 0);
        Controls.SetChildIndex(_topBar, 1);
        Controls.SetChildIndex(_statusStrip, 2);
        UpdateRoiPanelVisibility();
    }

    private void UpdateRoiPanelVisibility()
    {
        if (_mainLayout is null || _rightColumnLayout is null || _rightColumnLayout.RowStyles.Count < 2 || _roiPanel is null)
        {
            return;
        }

        bool visible = _isCameraMaximized;
        _roiPanel.Visible = visible;
        _rightColumnLayout.RowStyles[1].Height = visible ? 230 : 0;
        _roiLayout.RowStyles[0].Height = visible ? 38 : 0;
        _roiLayout.RowStyles[1].Height = visible ? 34 : 0;
        _roiLayout.RowStyles[2].Height = visible ? 100 : 0;
        _roiTitle.Visible = visible;
        _lstRois.Visible = visible;
        _roiActions.Visible = visible;
        _mainLayout.PerformLayout();
    }

    private void RebuildMultiCameraGrid()
    {
        foreach (Control control in _multiViewGrid.Controls.Cast<Control>().ToArray())
        {
            foreach (PictureBox picture in control.Controls.OfType<PictureBox>())
            {
                picture.Image?.Dispose();
                picture.Image = null;
            }

            _multiViewGrid.Controls.Remove(control);
            control.Dispose();
        }
        _cameraTilePictures.Clear();
        _cameraTileLabels.Clear();

        var cameras = _cameras.Values.ToArray();
        if (cameras.Length == 0)
        {
            _multiViewGrid.ColumnCount = 1;
            _multiViewGrid.RowCount = 1;
            var empty = new Label
            {
                Text = "No cameras configured\r\nClick '+ Add camera' to create the first camera.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 11F)
            };
            _multiViewGrid.Controls.Add(empty, 0, 0);
            return;
        }

        int columns = cameras.Length <= 1 ? 1 : cameras.Length <= 4 ? 2 : cameras.Length <= 9 ? 3 : 4;
        int rows = (int)Math.Ceiling(cameras.Length / (double)columns);
        _multiViewGrid.ColumnCount = columns;
        _multiViewGrid.RowCount = rows;
        _multiViewGrid.ColumnStyles.Clear();
        _multiViewGrid.RowStyles.Clear();
        for (int i = 0; i < columns; i++)
            _multiViewGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        for (int i = 0; i < rows; i++)
            _multiViewGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));

        for (int index = 0; index < cameras.Length; index++)
        {
            CameraRuntime camera = cameras[index];
            var tile = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(4),
                Padding = new Padding(1),
                BackColor = BgSurface,
                BorderStyle = BorderStyle.FixedSingle,
                Cursor = Cursors.Hand
            };
            var picture = new BufferedPictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black,
                Cursor = Cursors.Hand
            };
            var status = new Label
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(205, 20, 22, 28),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8, 4, 4, 4),
                Cursor = Cursors.Hand
            };

            var tileActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 102,
                Height = 32,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(2, 2, 2, 2),
                Margin = Padding.Empty,
                BackColor = Color.FromArgb(205, 20, 22, 28)
            };

            Button startStop = MakeTileActionButton(
                camera.IsRunning ? "Stop" : "Start",
                camera.IsRunning ? Color.FromArgb(155, 95, 35) : AccentGreen,
                camera.IsRunning ? "Stop camera" : "Start camera");
            Button edit = MakeTileActionButton("Edit", BgButton, "Edit camera");
            Button delete = MakeTileActionButton("Delete", AccentRed, "Delete camera");
            _cameraActionToolTip.SetToolTip(startStop, UiLocalization.T(startStop.AccessibleName ?? string.Empty));
            _cameraActionToolTip.SetToolTip(edit, UiLocalization.T(edit.AccessibleName ?? string.Empty));
            _cameraActionToolTip.SetToolTip(delete, UiLocalization.T(delete.AccessibleName ?? string.Empty));
            startStop.Click += (_, _) => ToggleCamera(camera);
            edit.Click += (_, _) => EditCamera(camera);
            delete.Click += (_, _) => RemoveCamera(camera);
            tileActions.Controls.Add(startStop);
            tileActions.Controls.Add(edit);
            tileActions.Controls.Add(delete);

            var tileHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 34,
                BackColor = Color.FromArgb(205, 20, 22, 28),
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            tileHeader.Controls.Add(status);
            tileHeader.Controls.Add(tileActions);

            tile.Controls.Add(picture);
            tile.Controls.Add(tileHeader);

            _cameraTilePictures[camera.Settings.Id] = picture;
            _cameraTileLabels[camera.Settings.Id] = status;
            UpdateCameraTileLabel(camera);

            EventHandler click = (_, _) => SelectCamera(camera);
            EventHandler doubleClick = (_, _) => ShowMaximizedCamera(camera);
            tile.Click += click;
            picture.Click += click;
            status.Click += click;
            tile.DoubleClick += doubleClick;
            picture.DoubleClick += doubleClick;
            status.DoubleClick += doubleClick;

            lock (_frameGate)
            {
                if (_latestFrames.TryGetValue(camera.Settings.Id, out Bitmap? latest))
                    picture.Image = new Bitmap(latest);
            }

            _multiViewGrid.Controls.Add(tile, index % columns, index / columns);
        }
    }

    private static Button MakeTileActionButton(string text, Color backColor, string accessibleName)
    {
        string icon = accessibleName switch
        {
            "Start camera" => "play_arrow",
            "Stop camera" => "stop",
            "Edit camera" => "edit",
            "Delete camera" => "delete",
            _ => "settings"
        };
        return new Button
        {
            Text = string.Empty,
            AutoSize = false,
            Size = new Size(30, 27),
            Margin = new Padding(1, 1, 1, 1),
            Padding = Padding.Empty,
            BackColor = backColor,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            FlatAppearance = { BorderSize = 0 },
            Font = new Font("Segoe UI", 7.5F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            AccessibleName = accessibleName,
            Image = MaterialIconRenderer.Create(icon, Color.White, 16),
            ImageAlign = ContentAlignment.MiddleCenter
        };
    }

    private void UpdateCameraTile(CameraRuntime camera, Bitmap frame)
    {
        if (!_cameraTilePictures.TryGetValue(camera.Settings.Id, out PictureBox? picture)) return;
        Bitmap copy = new(frame);
        SafeInvoke(() =>
        {
            if (picture.IsDisposed)
            {
                copy.Dispose();
                return;
            }
            Bitmap? old = picture.Image as Bitmap;
            picture.Image = copy;
            old?.Dispose();
            UpdateCameraTileLabel(camera);
        });
    }

    private void UpdateCameraTileLabel(CameraRuntime camera)
    {
        if (_cameraTileLabels.TryGetValue(camera.Settings.Id, out Label? label))
        {
            string state = camera.IsRunning ? "RUNNING" : "STOPPED";
            label.Text = $"{camera.Settings.Name}   •   {state}   •   {camera.ProcessingFps:0.0} FPS";
            label.ForeColor = camera.IsRunning ? Color.LightGreen : Color.LightGray;
        }
    }

    private static Button MakeButton(string text, Color backColor)
    {
        string icon = text switch
        {
            "+ Add camera" or "+ افزودن دوربین" => "add",
            "Save" or "ذخیره" => "save",
            "Face database" or "بانک اطلاعات چهره" => "face",
            "Start All" or "شروع همه" => "play_arrow",
            "Stop All" or "توقف همه" => "stop",
            "Thumbnails" or "تصاویر کوچک" => "view_module",
            "English" or "فارسی" => "language",
            _ => string.Empty
        };
        Image? image = string.IsNullOrEmpty(icon) ? null : MaterialIconRenderer.Create(icon, Color.White, 18);
        return new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(92, 34),
            BackColor = backColor,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Padding = new Padding(image is null ? 12 : 8, 4, 12, 4),
            Margin = new Padding(4),
            Image = image,
            ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText
        };
    }

    private void StyleRoiActionButton(Button button, RoiActionIcon icon, string hint, Color backColor, Color borderColor)
    {
        button.MinimumSize = new Size(0, 0);
        button.AutoSize = false;
        button.Size = new Size(30, 27);
        button.Width = 30;
        button.Height = 27;
        button.Text = string.Empty;
        button.UseVisualStyleBackColor = false;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.BorderColor = borderColor;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(backColor, 0.12F);
        button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(backColor, 0.12F);
        button.BackColor = backColor;
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI", 7.5F, FontStyle.Bold);
        button.Padding = Padding.Empty;
        button.Margin = Padding.Empty;
        button.Dock = DockStyle.Left;
        button.ImageAlign = ContentAlignment.MiddleCenter;
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.Cursor = Cursors.Hand;
        button.AccessibleName = hint;
        SetRoiActionImage(button, icon);
        button.EnabledChanged += (_, _) => SetRoiActionImage(button, icon);
        _roiToolTip.SetToolTip(button, hint);
    }

    private void SetRoiActionImage(Button button, RoiActionIcon icon)
    {
        Image? previous = button.Image;
        string iconName = icon switch
        {
            RoiActionIcon.Add => "add",
            RoiActionIcon.Edit => _roiEditMode ? "check" : "edit",
            RoiActionIcon.Rename => "edit",
            RoiActionIcon.Delete => "delete",
            RoiActionIcon.Clear => "clear_all",
            _ => "settings"
        };
        button.Image = MaterialIconRenderer.Create(
            iconName,
            button.Enabled ? Color.White : Color.FromArgb(105, 110, 120),
            16);
        previous?.Dispose();
    }

    private void StyleMainActionButton(Button button, string iconName, string hint, Color backColor)
    {
        button.MinimumSize = new Size(0, 0);
        button.AutoSize = false;
        button.Size = new Size(30, 27);
        button.UseVisualStyleBackColor = false;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(backColor, 0.12F);
        button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(backColor, 0.12F);
        button.BackColor = backColor;
        button.ForeColor = Color.White;
        button.Padding = Padding.Empty;
        button.Margin = new Padding(4);
        button.Image = MaterialIconRenderer.Create(iconName, Color.White, 16);
        button.ImageAlign = ContentAlignment.MiddleCenter;
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.Cursor = Cursors.Hand;
        button.AccessibleName = UiLocalization.T(hint);
        _mainActionToolTip.SetToolTip(button, UiLocalization.T(hint));
    }
}


