using System.Drawing.Imaging;
using HshDetectionEngin.Face;

namespace HshVisionLab;

public sealed record FaceFolderImportResult(int Imported, int Skipped, string Details);

public sealed class FaceDatabaseForm : Form
{
    private readonly FaceDatabase _database;
    private readonly Func<string, string, bool> _registerFromImage;
    private readonly Func<string, string, FaceFolderImportResult> _importFromFolder;
    private readonly Func<string, string, bool>? _addSampleToPerson;
    private readonly DataGridView _grid = new();
    private readonly List<Bitmap> _rowImages = [];
    private readonly CheckBox _groupByPerson = new() { Text = "Group by person", AutoSize = true, Checked = true };
    private readonly HashSet<string> _collapsedPeople = new(StringComparer.Ordinal);

    private sealed class GridRow
    {
        public required FaceSample Sample { get; init; }
        public Bitmap? Image { get; init; }
        public Bitmap? Photo => Image;
        public int PersonNumber => Sample.PersonNumber;
        public string Name { get => Sample.PersonName; set => Sample.PersonName = value; }
        public string Type => UiLocalization.T(Sample.PersonName.StartsWith("Unknown #", StringComparison.OrdinalIgnoreCase) ? "Unknown" : "Named");
        public int SampleNumber => Sample.SampleNumber;
        public string DetectionConfidence => Sample.DetectionConfidence.ToString("P1");
        public string CreatedAt => Sample.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string FileName => Sample.OriginalFileName;
        public string Status => UiLocalization.T(Sample.FaceImage.Length == 0 ? "Image missing" : "Ready");
    }

    private sealed class DisplayRow
    {
        public GridRow? Data { get; init; }
        public bool IsGroupHeader { get; init; }
        public string PersonId { get; init; } = string.Empty;
        public string GroupTitle { get; init; } = string.Empty;
        public Bitmap? Photo => Data?.Photo;
        public int PersonNumber => Data?.PersonNumber ?? 0;
        public string Name { get => Data?.Name ?? GroupTitle; set { if (Data is not null) Data.Name = value; } }
        public string Type => Data?.Type ?? UiLocalization.T("Person group");
        public int SampleNumber => Data?.SampleNumber ?? 0;
        public string DetectionConfidence => Data?.DetectionConfidence ?? string.Empty;
        public string CreatedAt => Data?.CreatedAt ?? string.Empty;
        public string FileName => Data?.FileName ?? string.Empty;
        public string Status => Data?.Status ?? string.Empty;
    }

    public FaceDatabaseForm(FaceDatabase database, Func<string, string, bool> registerFromImage,
        Func<string, string, FaceFolderImportResult> importFromFolder,
        Func<string, string, bool>? addSampleToPerson = null)
    {
        _database = database;
        _registerFromImage = registerFromImage;
        _importFromFolder = importFromFolder;
        _addSampleToPerson = addSampleToPerson;
        Text = "Face database manager";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1120, 650);
        MinimumSize = new Size(850, 480);
        BuildUi();
        UiLocalization.Apply(this);
        RefreshGrid();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        _grid.AutoGenerateColumns = false;
        _grid.RowTemplate.Height = 92;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.Columns.Add(new DataGridViewImageColumn { Name = "Photo", HeaderText = "Face crop", DataPropertyName = "Photo", Width = 100, ImageLayout = DataGridViewImageCellLayout.Zoom });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PersonNumber", HeaderText = "Person #", DataPropertyName = "PersonNumber", Width = 85 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Name", DataPropertyName = "Name", ReadOnly = false, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 28 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Type", DataPropertyName = "Type", Width = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "SampleNumber", HeaderText = "Sample #", DataPropertyName = "SampleNumber", Width = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DetectionConfidence", HeaderText = "Detection confidence", DataPropertyName = "DetectionConfidence", Width = 125 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CreatedAt", HeaderText = "Created", DataPropertyName = "CreatedAt", Width = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FileName", HeaderText = "Source file", DataPropertyName = "FileName", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 28 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", DataPropertyName = "Status", Width = 95 });
        foreach (DataGridViewColumn column in _grid.Columns)
            if (!string.Equals(column.Name, "Name", StringComparison.Ordinal)) column.ReadOnly = true;
        _grid.CellValidating += Grid_CellValidating;
        _grid.CellEndEdit += Grid_CellEndEdit;
        _grid.CellBeginEdit += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].DataBoundItem is DisplayRow row && row.IsGroupHeader)
                e.Cancel = true;
        };
        _grid.CellPainting += Grid_CellPainting;
        _grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == _grid.Columns["Name"].Index)
                e.ToolTipText = UiLocalization.T("Double-click or press F2 to rename");
        };
        root.Controls.Add(_grid, 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = true, Padding = new Padding(0, 10, 0, 0) };
        Button close = MakeButton("Close");
        Button deletePerson = MakeButton("Delete person");
        Button deleteSample = MakeButton("Delete sample");
        Button moveSample = MakeButton("Move selected sample");
        Button rename = MakeButton("Rename person");
        Button add = MakeButton("Add image");
        Button addToPerson = MakeButton("Add to selected person");
        Button import = MakeButton("Import folder");
        Button similar = MakeButton("Check similarity");
        close.Click += (_, _) => Close();
        deletePerson.Click += (_, _) => DeletePerson();
        deleteSample.Click += (_, _) => DeleteSample();
        moveSample.Click += (_, _) => MoveSelectedSample();
        rename.Click += (_, _) => RenameSelected();
        add.Click += (_, _) => AddFromImage();
        addToPerson.Click += (_, _) => AddToSelectedPerson();
        import.Click += (_, _) => ImportFolder();
        similar.Click += (_, _) => ShowSimilar();
        _groupByPerson.CheckedChanged += (_, _) => RefreshGrid();
        _grid.CellClick += Grid_CellClick;
        actions.Controls.Add(_groupByPerson);
        actions.Controls.AddRange([close, deletePerson, deleteSample, moveSample, rename, add, addToPerson, import, similar]);
        root.Controls.Add(actions, 0, 1);
        Controls.Add(root);
    }

    private void RefreshGrid()
    {
        foreach (Bitmap image in _rowImages) image.Dispose();
        _rowImages.Clear();
        var samples = _database.GetSamples(includeImages: true)
            .OrderBy(sample => sample.PersonNumber)
            .ThenBy(sample => sample.SampleNumber)
            .ToList();
        var rows = new List<DisplayRow>();
        foreach (IGrouping<string, FaceSample> group in samples.GroupBy(sample => sample.PersonId))
        {
            FaceSample first = group.First();
            string personId = first.PersonId;
            if (_groupByPerson.Checked)
            {
                bool collapsed = _collapsedPeople.Contains(personId);
                rows.Add(new DisplayRow
                {
                    IsGroupHeader = true,
                    PersonId = personId,
                    GroupTitle = $"{(collapsed ? "+" : "-")}  {first.PersonName}  (Person #{first.PersonNumber}, {group.Count()} samples)"
                });
                if (collapsed) continue;
            }

            foreach (FaceSample sample in group)
            {
                Bitmap? image = DecodeImage(sample.FaceImage);
                if (image is not null) _rowImages.Add(image);
                rows.Add(new DisplayRow
                {
                    PersonId = personId,
                    Data = new GridRow { Sample = sample, Image = image }
                });
            }
        }
        _grid.DataSource = rows;
        foreach (DataGridViewRow gridRow in _grid.Rows)
            gridRow.Height = (gridRow.DataBoundItem as DisplayRow)?.IsGroupHeader == true ? 30 : 92;
    }

    private void Grid_CellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (!_groupByPerson.Checked || e.RowIndex < 0 ||
            _grid.Rows[e.RowIndex].DataBoundItem is not DisplayRow row || !row.IsGroupHeader)
            return;

        if (!_collapsedPeople.Add(row.PersonId)) _collapsedPeople.Remove(row.PersonId);
        RefreshGrid();
    }

    private void Grid_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (!_groupByPerson.Checked || e.RowIndex < 0 || e.ColumnIndex < 0 ||
            _grid.Rows[e.RowIndex].DataBoundItem is not DisplayRow row || !row.IsGroupHeader)
            return;

        e.PaintBackground(e.ClipBounds, true);
        if (e.Graphics is not null)
        {
            using var brush = new SolidBrush(Color.FromArgb(225, 235, 250));
            using var pen = new Pen(Color.SteelBlue, 1.5f);
            using var font = new Font(_grid.Font, FontStyle.Bold);
            e.Graphics.FillRectangle(brush, e.CellBounds);
            e.Graphics.DrawLine(pen, e.CellBounds.Left, e.CellBounds.Top, e.CellBounds.Right, e.CellBounds.Top);
            if (e.ColumnIndex == 0)
                e.Graphics.DrawString(row.GroupTitle, font, Brushes.MidnightBlue,
                    e.CellBounds.Left + 8, e.CellBounds.Top + 5);
        }
        e.Handled = true;
    }

    private GridRow? SelectedRow()
    {
        DataGridViewRow? selected = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0] : _grid.CurrentRow;
        return (selected?.DataBoundItem as DisplayRow)?.Data;
    }

    private void Grid_CellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != _grid.Columns["Name"].Index) return;
        if (_grid.Rows[e.RowIndex].DataBoundItem is not DisplayRow display || display.Data is null) return;
        GridRow row = display.Data;

        string name = Convert.ToString(e.FormattedValue)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            e.Cancel = true;
            _grid.Rows[e.RowIndex].ErrorText = UiLocalization.T("Name cannot be empty.");
            return;
        }

        if (!_database.Rename(row.Sample.PersonId, name))
        {
            e.Cancel = true;
            _grid.Rows[e.RowIndex].ErrorText = UiLocalization.T("This name is already in use.");
            return;
        }

        _grid.Rows[e.RowIndex].ErrorText = string.Empty;
    }

    private void Grid_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex >= 0 && e.ColumnIndex == _grid.Columns["Name"].Index)
            BeginInvoke(RefreshGrid);
    }

    private void RenameSelected()
    {
        GridRow? row = SelectedRow();
        if (row is null) return;
        string? name = Prompt("Rename person", row.Name);
        if (!string.IsNullOrWhiteSpace(name) && !_database.Rename(row.Sample.PersonId, name))
        {
            MessageBox.Show(this, "The name is already in use or could not be changed.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        RefreshGrid();
    }

    private void DeleteSample()
    {
        GridRow? row = SelectedRow();
        if (row is null) return;
        if (MessageBox.Show(this, $"Delete sample {row.SampleNumber} of '{row.Name}'?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            _database.RemoveSample(row.Sample.Id);
            RefreshGrid();
        }
    }

    private void MoveSelectedSample()
    {
        GridRow? source = SelectedRow();
        if (source is null) return;
        FaceIdentity[] targets = _database.Identities
            .Where(person => person.Id != source.Sample.PersonId && !person.IsUnknown)
            .OrderBy(person => person.PersonNumber)
            .ToArray();
        if (targets.Length == 0)
        {
            MessageBox.Show(this, "No named person is available as a target.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        FaceIdentity? target = SelectPerson(targets);
        if (target is null) return;
        if (MessageBox.Show(this, $"Move sample {source.SampleNumber} from '{source.Name}' to '{target.Name}'?",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        try
        {
            if (_database.MoveSample(source.Sample.Id, target.Id)) RefreshGrid();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DeletePerson()
    {
        GridRow? row = SelectedRow();
        if (row is null) return;
        if (MessageBox.Show(this, $"Delete person '{row.Name}' and all samples?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            try
            {
                if (_database.Remove(row.Sample.PersonId))
                    RefreshGrid();
                else
                    MessageBox.Show(this, "The person could not be found or deleted.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void AddFromImage()
    {
        using var dialog = new OpenFileDialog { Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.webp" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        GridRow? selected = SelectedRow();
        string? name = Prompt("Person name", selected?.Name ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            if (_registerFromImage(name, dialog.FileName)) RefreshGrid();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AddToSelectedPerson()
    {
        GridRow? selected = SelectedRow();
        if (selected is null || _addSampleToPerson is null)
        {
            MessageBox.Show(this, "Select a person first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new OpenFileDialog { Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.webp" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (_addSampleToPerson(selected.Sample.PersonId, dialog.FileName)) RefreshGrid();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ImportFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select a folder containing face images" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        GridRow? selected = SelectedRow();
        string? name = Prompt("Person name for imported images", selected?.Name ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return;
        Cursor oldCursor = Cursor;
        Cursor = Cursors.WaitCursor;
        try
        {
            FaceFolderImportResult result = _importFromFolder(name, dialog.SelectedPath);
            RefreshGrid();
            MessageBox.Show(this, result.Details, "Folder import", MessageBoxButtons.OK,
                result.Skipped == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            Cursor = oldCursor;
        }
    }

    private void ShowSimilar()
    {
        using var form = new FaceSimilarityForm(_database);
        form.ShowDialog(this);
        RefreshGrid();
    }

    private static Button MakeButton(string text) => new()
    {
        Text = text, AutoSize = true, MinimumSize = new Size(105, 34), Padding = new Padding(8, 4, 10, 4),
        Image = MaterialIconRenderer.CreateForAction(text),
        ImageAlign = ContentAlignment.MiddleLeft,
        TextImageRelation = TextImageRelation.ImageBeforeText
    };

    private static Bitmap? DecodeImage(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            using Image image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch
        {
            return null;
        }
    }

    private static string? Prompt(string title, string initial)
    {
        using var dialog = new Form { Text = title, Size = new Size(390, 145), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var text = new TextBox { Left = 12, Top = 12, Width = 350, Text = initial };
        var ok = new Button
        {
            Text = "OK", Left = 200, Top = 55, Width = 75, DialogResult = DialogResult.OK,
            Image = MaterialIconRenderer.Create("check", Color.Black, 16), ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText
        };
        var cancel = new Button
        {
            Text = "Cancel", Left = 287, Top = 55, Width = 75, DialogResult = DialogResult.Cancel,
            Image = MaterialIconRenderer.CreateForAction("Cancel", 16), ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText
        };
        dialog.Controls.AddRange([text, ok, cancel]); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        return dialog.ShowDialog() == DialogResult.OK ? text.Text.Trim() : null;
    }

    private static FaceIdentity? SelectPerson(IReadOnlyList<FaceIdentity> people)
    {
        using var dialog = new Form
        {
            Text = UiLocalization.T("Select target person"), Size = new Size(440, 150),
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false
        };
        var combo = new ComboBox { Left = 12, Top = 12, Width = 400, DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(people.Select(person => $"#{person.PersonNumber} - {person.Name}").ToArray());
        combo.SelectedIndex = 0;
        var ok = new Button { Text = "OK", Left = 250, Top = 55, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 337, Top = 55, Width = 75, DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange([combo, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog() == DialogResult.OK && combo.SelectedIndex >= 0
            ? people[combo.SelectedIndex]
            : null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Bitmap image in _rowImages) image.Dispose();
            _rowImages.Clear();
        }
        base.Dispose(disposing);
    }
}
