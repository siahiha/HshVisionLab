using HshDetectionEngin.Face;
using HshDetectionEngin.Identity;
using HshDetectionEngin.Palm;

namespace HshVisionLab;

/// <summary>Windows manager for one person and all registered identity modalities.</summary>
public sealed class IdentityDatabaseForm : Form
{
    private readonly IdentityDatabase _database;
    private readonly FaceDatabase _faces;
    private readonly PalmDatabase _palms;
    private readonly Func<string, string, bool> _registerFace;
    private readonly Func<string, string, bool> _addFace;
    private readonly Func<string, string, FaceFolderImportResult> _importFaces;
    private readonly Func<string, string, bool> _registerPalm;
    private readonly Func<string, string, bool> _addPalm;
    private readonly ListBox _people = new();
    private readonly Label _personInfo = new();
    private readonly DataGridView _plates = CreateGrid();
    private readonly DataGridView _faceSamples = CreateGrid();
    private readonly DataGridView _palmSamples = CreateGrid();
    private readonly List<IdentityPersonRecord> _personRows = [];
    private IdentityPersonRecord? _selected;

    private sealed class PersonRow
    {
        public required IdentityPersonRecord Person { get; init; }
        public string Display => $"#{Person.PersonNumber}  {Person.Name}";
        public override string ToString() => Display;
    }

    public IdentityDatabaseForm(
        IdentityDatabase database,
        FaceDatabase faces,
        PalmDatabase palms,
        Func<string, string, bool> registerFace,
        Func<string, string, bool> addFace,
        Func<string, string, FaceFolderImportResult> importFaces,
        Func<string, string, bool> registerPalm,
        Func<string, string, bool> addPalm)
    {
        _database = database;
        _faces = faces;
        _palms = palms;
        _registerFace = registerFace;
        _addFace = addFace;
        _importFaces = importFaces;
        _registerPalm = registerPalm;
        _addPalm = addPalm;
        Text = "Identity database";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1180, 700);
        MinimumSize = new Size(900, 540);
        BuildUi();
        RefreshPeople();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(12) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var peoplePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(0, 0, 10, 0) };
        peoplePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        peoplePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        peoplePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        peoplePanel.Controls.Add(new Label { Text = "People", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11F, FontStyle.Bold), Padding = new Padding(0, 0, 0, 8) }, 0, 0);
        _people.Dock = DockStyle.Fill;
        _people.SelectedIndexChanged += (_, _) => SelectPerson();
        peoplePanel.Controls.Add(_people, 0, 1);
        var personActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        Button addPerson = MakeButton("Add person");
        Button rename = MakeButton("Rename");
        Button delete = MakeButton("Delete");
        addPerson.Click += (_, _) => AddPerson(); rename.Click += (_, _) => RenamePerson(); delete.Click += (_, _) => DeletePerson();
        personActions.Controls.AddRange([addPerson, rename, delete]);
        peoplePanel.Controls.Add(personActions, 0, 2);
        root.Controls.Add(peoplePanel, 0, 0);

        var details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _personInfo.Dock = DockStyle.Fill;
        _personInfo.AutoSize = true;
        _personInfo.Padding = new Padding(4, 0, 0, 8);
        details.Controls.Add(_personInfo, 0, 0);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildPlatesTab());
        tabs.TabPages.Add(BuildSamplesTab("Face samples", _faceSamples, AddFaceSample, DeleteFaceSample, import: true));
        tabs.TabPages.Add(BuildSamplesTab("Palm samples", _palmSamples, AddPalmSample, DeletePalmSample, import: false, assign: AssignPalmSample));
        details.Controls.Add(tabs, 0, 1);
        root.Controls.Add(details, 1, 0);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        Button close = MakeButton("Close");
        Button similarity = MakeButton("Face similarity");
        close.Click += (_, _) => Close();
        similarity.Click += (_, _) => { using var form = new FaceSimilarityForm(_faces); form.ShowDialog(this); RefreshPeople(); };
        footer.Controls.AddRange([close, similarity]);
        root.Controls.Add(footer, 0, 1); root.SetColumnSpan(footer, 2);
        Controls.Add(root);
    }

    private TabPage BuildPlatesTab()
    {
        var page = new TabPage("Plates");
        _plates.Dock = DockStyle.Fill;
        _plates.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Plate", DataPropertyName = "PlateText", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _plates.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Primary", DataPropertyName = "IsPrimary", Width = 80 });
        _plates.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Notes", DataPropertyName = "Notes", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        Button remove = MakeButton("Remove plate"); Button add = MakeButton("Add plate");
        remove.Click += (_, _) => RemovePlate(); add.Click += (_, _) => AddPlate(); actions.Controls.AddRange([remove, add]);
        page.Controls.Add(_plates); page.Controls.Add(actions); return page;
    }

    private TabPage BuildSamplesTab(string title, DataGridView grid, Action add, Action remove, bool import, Action? assign = null)
    {
        var page = new TabPage(title);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Person #", DataPropertyName = "PersonNumber", Width = 80 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Sample #", DataPropertyName = "SampleNumber", Width = 80 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Detection", DataPropertyName = "Detection", Width = 100 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Created", DataPropertyName = "Created", Width = 160 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Source file", DataPropertyName = "FileName", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        Button removeButton = MakeButton("Delete sample"); Button addButton = MakeButton("Add image");
        removeButton.Click += (_, _) => remove(); addButton.Click += (_, _) => add(); actions.Controls.AddRange([removeButton, addButton]);
        if (assign is not null)
        {
            Button assignButton = MakeButton("Assign to person"); assignButton.Click += (_, _) => assign(); actions.Controls.Add(assignButton);
        }
        if (import)
        {
            Button importButton = MakeButton("Import folder"); importButton.Click += (_, _) => ImportFaceFolder(); actions.Controls.Add(importButton);
        }
        page.Controls.Add(grid); page.Controls.Add(actions); return page;
    }

    private void RefreshPeople()
    {
        _personRows.Clear(); _personRows.AddRange(_database.GetPeople());
        string? previous = _selected?.Id;
        _people.Items.Clear();
        foreach (IdentityPersonRecord person in _personRows) _people.Items.Add(new PersonRow { Person = person });
        int index = _personRows.FindIndex(item => item.Id == previous);
        _people.SelectedIndex = index >= 0 ? index : (_personRows.Count > 0 ? 0 : -1);
        RefreshDetails();
    }

    private void SelectPerson()
    {
        _selected = _people.SelectedItem is PersonRow row ? row.Person : null;
        RefreshDetails();
    }

    private void RefreshDetails()
    {
        _personInfo.Text = _selected is null ? "No person selected" : $"Person #{_selected.PersonNumber}  •  {_selected.Name}  •  {_database.GetPlates(_selected.Id).Count} plates  •  {_faces.GetSamples(false).Count(item => item.PersonId == _selected.Id)} face samples  •  {_palms.GetSamples().Count(item => item.PersonId == _selected.Id)} palm samples";
        _plates.DataSource = _selected is null ? Array.Empty<PersonPlateRecord>() : _database.GetPlates(_selected.Id).ToArray();
        _faceSamples.DataSource = _selected is null ? Array.Empty<object>() : _faces.GetSamples(true).Where(item => item.PersonId == _selected.Id).Select(item => new SampleRow(item.PersonNumber, item.SampleNumber, item.DetectionConfidence, item.CreatedAtUtc, item.OriginalFileName, item.Id)).ToArray();
        _palmSamples.DataSource = _selected is null ? Array.Empty<object>() : _palms.GetSamples().Where(item => item.PersonId == _selected.Id).Select(item => new SampleRow(item.PersonNumber, item.SampleNumber, item.DetectionConfidence, item.CreatedAtUtc, item.OriginalFileName, item.Id)).ToArray();
    }

    private sealed record SampleRow(int PersonNumber, int SampleNumber, float Confidence, DateTime CreatedAtUtc, string FileName, string Id)
    {
        public string Detection => Confidence.ToString("P1");
        public string Created => CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    private void AddPerson()
    {
        string? name = Prompt("Person name", string.Empty); if (string.IsNullOrWhiteSpace(name)) return;
        try { _database.CreatePerson(name); RefreshPeople(); } catch (Exception ex) { ShowError(ex); }
    }
    private void RenamePerson()
    {
        if (_selected is null) return; string? name = Prompt("Person name", _selected.Name); if (string.IsNullOrWhiteSpace(name)) return;
        try { if (!_database.RenamePerson(_selected.Id, name)) throw new InvalidOperationException("The name is already in use."); RefreshPeople(); } catch (Exception ex) { ShowError(ex); }
    }
    private void DeletePerson()
    {
        if (_selected is null || MessageBox.Show(this, $"Delete '{_selected.Name}' and all identity data?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _database.RemovePerson(_selected.Id); RefreshPeople();
    }
    private void AddPlate()
    {
        if (_selected is null) return; string? plate = Prompt("Plate number", string.Empty); if (string.IsNullOrWhiteSpace(plate)) return;
        try { _database.AddPlate(_selected.Id, plate); RefreshDetails(); } catch (Exception ex) { ShowError(ex); }
    }
    private void RemovePlate()
    {
        if (_plates.CurrentRow?.DataBoundItem is not PersonPlateRecord plate) return;
        _database.RemovePlate(plate.Id); RefreshDetails();
    }
    private void AddFaceSample()
    {
        using var dialog = new OpenFileDialog { Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.webp" }; if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (_selected is null)
            {
                string? name = Prompt("Person name", string.Empty);
                if (string.IsNullOrWhiteSpace(name)) return;
                if (_registerFace(name, dialog.FileName)) RefreshPeople();
            }
            else if (_addFace(_selected.Id, dialog.FileName)) RefreshPeople();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void ImportFaceFolder()
    {
        if (_selected is null) return; using var dialog = new FolderBrowserDialog(); if (dialog.ShowDialog(this) != DialogResult.OK) return;
        FaceFolderImportResult result = _importFaces(_selected.Name, dialog.SelectedPath); MessageBox.Show(this, result.Details, "Face import", MessageBoxButtons.OK, result.Skipped == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning); RefreshPeople();
    }
    private void DeleteFaceSample()
    {
        if (_faceSamples.CurrentRow?.DataBoundItem is SampleRow row) { _faces.RemoveSample(row.Id); RefreshDetails(); }
    }
    private void AddPalmSample()
    {
        using var dialog = new OpenFileDialog { Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.webp" }; if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (_selected is null)
            {
                string? name = Prompt("Person name", string.Empty);
                if (string.IsNullOrWhiteSpace(name)) return;
                if (_registerPalm(name, dialog.FileName)) RefreshPeople();
            }
            else if (_addPalm(_selected.Id, dialog.FileName)) RefreshPeople();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void DeletePalmSample()
    {
        if (_palmSamples.CurrentRow?.DataBoundItem is SampleRow row) { _palms.RemoveSample(row.Id); RefreshDetails(); }
    }

    private void AssignPalmSample()
    {
        if (_palmSamples.CurrentRow?.DataBoundItem is not SampleRow row) return;
        IdentityPersonRecord[] choices = _personRows.Where(person => person.Id != _selected?.Id).ToArray();
        IdentityPersonRecord? target = ChoosePerson(choices);
        if (target is null) return;
        try
        {
            if (!_palms.MoveSample(row.Id, target.Id)) throw new InvalidOperationException("The Palm sample could not be assigned. The target may already have the maximum number of samples.");
            RefreshPeople();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static IdentityPersonRecord? ChoosePerson(IReadOnlyList<IdentityPersonRecord> people)
    {
        if (people.Count == 0) return null;
        using var dialog = new Form { Text = "Assign Palm to person", Size = new Size(430, 360), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var list = new ListBox { Dock = DockStyle.Fill, DisplayMember = nameof(IdentityPersonRecord.Name) };
        foreach (IdentityPersonRecord person in people) list.Items.Add(person);
        list.SelectedIndex = 0;
        var ok = new Button { Text = "Assign", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        buttons.Controls.AddRange([cancel, ok]);
        dialog.Controls.Add(list); dialog.Controls.Add(buttons); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        return dialog.ShowDialog() == DialogResult.OK && list.SelectedItem is IdentityPersonRecord selected ? selected : null;
    }
    private static DataGridView CreateGrid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoGenerateColumns = false };
    private static Button MakeButton(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(110, 32), Padding = new Padding(8, 4, 8, 4) };
    private static string? Prompt(string title, string initial)
    {
        using var dialog = new Form { Text = title, Size = new Size(390, 145), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var text = new TextBox { Left = 12, Top = 12, Width = 350, Text = initial }; var ok = new Button { Text = "OK", Left = 200, Top = 55, Width = 75, DialogResult = DialogResult.OK }; var cancel = new Button { Text = "Cancel", Left = 287, Top = 55, Width = 75, DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange([text, ok, cancel]); dialog.AcceptButton = ok; dialog.CancelButton = cancel; return dialog.ShowDialog() == DialogResult.OK ? text.Text.Trim() : null;
    }
    private void ShowError(Exception ex) => MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
}
