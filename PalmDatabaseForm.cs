using HshDetectionEngin.Palm;

namespace HshVisionLab;

/// <summary>Small enrollment manager for palmprint identities.</summary>
public sealed class PalmDatabaseForm : Form
{
    private readonly PalmDatabase _database;
    private readonly Func<string, string, bool> _registerFromImage;
    private readonly Func<string, string, bool> _addSampleToPerson;
    private readonly DataGridView _grid = new();
    private readonly List<Bitmap> _images = [];

    private sealed class Row
    {
        public required PalmSample Sample { get; init; }
        public Bitmap? Photo { get; init; }
        public int PersonNumber => Sample.PersonNumber;
        public string Name => Sample.PersonName;
        public int SampleNumber => Sample.SampleNumber;
        public string Confidence => Sample.DetectionConfidence.ToString("P1");
        public string Created => Sample.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string FileName => Sample.OriginalFileName;
    }

    public PalmDatabaseForm(PalmDatabase database, Func<string, string, bool> registerFromImage,
        Func<string, string, bool> addSampleToPerson)
    {
        _database = database;
        _registerFromImage = registerFromImage;
        _addSampleToPerson = addSampleToPerson;
        Text = "Palm identity database";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(980, 560);
        MinimumSize = new Size(760, 420);
        BuildUi();
        RefreshGrid();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoGenerateColumns = false;
        _grid.RowTemplate.Height = 84;
        _grid.Columns.Add(new DataGridViewImageColumn { Name = "Photo", HeaderText = "Palm crop", DataPropertyName = "Photo", Width = 100, ImageLayout = DataGridViewImageCellLayout.Zoom });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Person #", DataPropertyName = "PersonNumber", Width = 75 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", DataPropertyName = "Name", Width = 180 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sample #", DataPropertyName = "SampleNumber", Width = 75 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Detection", DataPropertyName = "Confidence", Width = 95 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Created", DataPropertyName = "Created", Width = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Source file", DataPropertyName = "FileName", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        root.Controls.Add(_grid, 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        Button close = MakeButton("Close");
        Button add = MakeButton("Add image");
        Button addSample = MakeButton("Add to selected person");
        close.Click += (_, _) => Close();
        add.Click += (_, _) => AddImage();
        addSample.Click += (_, _) => AddSample();
        actions.Controls.AddRange([close, addSample, add]);
        root.Controls.Add(actions, 0, 1);
        Controls.Add(root);
    }

    private void RefreshGrid()
    {
        foreach (Bitmap image in _images) image.Dispose();
        _images.Clear();
        var rows = _database.GetSamples()
            .OrderBy(sample => sample.PersonNumber)
            .ThenBy(sample => sample.SampleNumber)
            .Select(sample =>
            {
                Bitmap? image = Decode(sample.PalmImage);
                if (image is not null) _images.Add(image);
                return new Row { Sample = sample, Photo = image };
            })
            .ToArray();
        _grid.DataSource = rows;
    }

    private PalmSample? SelectedSample() =>
        (_grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0] : _grid.CurrentRow)?.DataBoundItem is Row row ? row.Sample : null;

    private void AddImage()
    {
        using var dialog = new OpenFileDialog { Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.webp" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string? name = Prompt("Palm owner name", string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return;
        try { if (_registerFromImage(name, dialog.FileName)) RefreshGrid(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void AddSample()
    {
        PalmSample? sample = SelectedSample();
        if (sample is null) { MessageBox.Show(this, "Select a palm identity first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        using var dialog = new OpenFileDialog { Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.webp" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { if (_addSampleToPerson(sample.PersonId, dialog.FileName)) RefreshGrid(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static Button MakeButton(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(120, 34), Padding = new Padding(8, 4, 10, 4) };

    private static Bitmap? Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        try { using var stream = new MemoryStream(bytes); using Image image = Image.FromStream(stream); return new Bitmap(image); }
        catch { return null; }
    }

    private static string? Prompt(string title, string initial)
    {
        using var dialog = new Form { Text = title, Size = new Size(390, 145), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var text = new TextBox { Left = 12, Top = 12, Width = 350, Text = initial };
        var ok = new Button { Text = "OK", Left = 200, Top = 55, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 287, Top = 55, Width = 75, DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange([text, ok, cancel]); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        return dialog.ShowDialog() == DialogResult.OK ? text.Text.Trim() : null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { foreach (Bitmap image in _images) image.Dispose(); _images.Clear(); }
        base.Dispose(disposing);
    }
}
