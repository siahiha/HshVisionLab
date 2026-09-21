using System.Text.Json;
using HshDetectionEngin.Licensing;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new CustomerListForm());
    }
}

internal sealed class CustomerRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CompanyName { get; set; } = string.Empty;
    public string ContactName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<LicenseArchiveEntry> Licenses { get; set; } = [];
}

internal sealed class LicenseArchiveEntry
{
    public string LicenseId { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime IssuedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
    public string Features { get; set; } = string.Empty;
}

internal static class CustomerStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static string DataPath => Path.Combine(AppContext.BaseDirectory, "customers.json");
    public static string ArchiveRoot => Path.Combine(AppContext.BaseDirectory, "LicenseArchive");

    public static List<CustomerRecord> Load()
    {
        try { return File.Exists(DataPath) ? JsonSerializer.Deserialize<List<CustomerRecord>>(File.ReadAllText(DataPath)) ?? [] : []; }
        catch { return []; }
    }

    public static void Save(IReadOnlyCollection<CustomerRecord> customers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
        File.WriteAllText(DataPath, JsonSerializer.Serialize(customers, Options));
    }

    public static string CreateArchivePath(CustomerRecord customer, LicenseClaims claims)
    {
        string directory = Path.Combine(ArchiveRoot, customer.Id);
        Directory.CreateDirectory(directory);
        string company = string.Concat(customer.CompanyName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        if (string.IsNullOrWhiteSpace(company)) company = "customer";
        return Path.Combine(directory, $"{company}_{DateTime.Now:yyyyMMdd_HHmmss}_{claims.LicenseId[..8]}.hshlic");
    }
}

internal static class IssuerPreferences
{
    private sealed class Data { public string PrivateKeyPath { get; set; } = string.Empty; }
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "issuer-settings.json");

    public static string? LoadPrivateKeyPath()
    {
        try
        {
            var data = File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) : null;
            return string.IsNullOrWhiteSpace(data?.PrivateKeyPath) ? null : data.PrivateKeyPath;
        }
        catch { return null; }
    }

    public static void SavePrivateKeyPath(string path)
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(new Data { PrivateKeyPath = path }, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}

internal sealed class CustomerListForm : Form
{
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AllowUserToAddRows = false };
    private readonly Label _storage = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Label _summary = new() { AutoSize = true, ForeColor = Color.FromArgb(70, 70, 70) };
    private readonly TextBox _search = new() { Width = 250, PlaceholderText = "Search customers..." };
    private List<CustomerRecord> _customers = [];

    public CustomerListForm()
    {
        Text = "HshDetection - Customers"; StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(980, 580); Size = new Size(1180, 720); Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(245, 247, 250);
        _grid.BackgroundColor = Color.White; _grid.BorderStyle = BorderStyle.FixedSingle; _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; _grid.GridColor = Color.FromArgb(225, 230, 235); _grid.RowHeadersVisible = false; _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None; _grid.RowTemplate.Height = 34; _grid.ColumnHeadersHeight = 38; _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(31, 78, 121), ForeColor = Color.White, Font = new Font("Segoe UI", 10, FontStyle.Bold), Alignment = DataGridViewContentAlignment.MiddleLeft }; _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = Color.FromArgb(40, 40, 40), SelectionBackColor = Color.FromArgb(218, 234, 248), SelectionForeColor = Color.FromArgb(20, 20, 20), Padding = new Padding(8, 0, 8, 0) }; _grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(248, 250, 252) };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Company", DataPropertyName = nameof(CustomerRow.CompanyName), Width = 220 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Contact", DataPropertyName = nameof(CustomerRow.ContactName), Width = 180 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Phone", DataPropertyName = nameof(CustomerRow.Phone), Width = 140 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Mobile", DataPropertyName = nameof(CustomerRow.Mobile), Width = 140 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Licenses", DataPropertyName = nameof(CustomerRow.Licenses), Width = 80 });
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ManageSelected(); };
        _search.TextChanged += (_, _) => LoadCustomers();
        var add = StyledButton("New customer", Color.FromArgb(31, 120, 74)); var edit = StyledButton("Edit", Color.FromArgb(31, 78, 121)); var manage = StyledButton("Manage licenses", Color.FromArgb(65, 105, 160)); var delete = StyledButton("Delete", Color.FromArgb(170, 55, 55));
        add.Click += (_, _) => CreateCustomer(); edit.Click += (_, _) => EditCustomer(); manage.Click += (_, _) => ManageSelected(); delete.Click += (_, _) => DeleteCustomer();
        var heading = new Panel { Dock = DockStyle.Fill, Height = 72, BackColor = Color.White, Padding = new Padding(20, 12, 20, 8) }; heading.Controls.Add(new Label { Text = "Customer management", AutoSize = true, Font = new Font("Segoe UI", 16, FontStyle.Bold), ForeColor = Color.FromArgb(31, 78, 121), Location = new Point(20, 10) }); heading.Controls.Add(new Label { Text = "Create customers and manage their issued licenses", AutoSize = true, ForeColor = Color.FromArgb(100, 105, 110), Location = new Point(22, 42) });
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 4, 0, 4) }; buttons.Controls.AddRange([add, edit, manage, delete]); var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent }; toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); toolbar.Controls.Add(buttons, 0, 0); toolbar.Controls.Add(_search, 1, 0);
        _storage.Text = $"Data: {CustomerStore.DataPath}"; var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true }; footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); footer.Controls.Add(_storage, 0, 0); footer.Controls.Add(_summary, 1, 0);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 4 }; layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.Controls.Add(heading, 0, 0); layout.Controls.Add(toolbar, 0, 1); layout.Controls.Add(_grid, 0, 2); layout.Controls.Add(footer, 0, 3); Controls.Add(layout); LoadCustomers();
    }

    private static Button StyledButton(string text, Color color) => new() { Text = text, AutoSize = true, Height = 34, Padding = new Padding(13, 0, 13, 0), BackColor = color, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 8, 0) };
    private void LoadCustomers() { _customers = CustomerStore.Load(); string query = _search.Text.Trim(); var rows = _customers.Where(c => string.IsNullOrWhiteSpace(query) || string.Join(" ", c.CompanyName, c.ContactName, c.Phone, c.Mobile, c.Address).Contains(query, StringComparison.OrdinalIgnoreCase)).Select(c => new CustomerRow(c)).ToList(); _grid.DataSource = rows; _summary.Text = $"{_customers.Count} customer(s)  •  {_customers.Sum(c => c.Licenses.Count)} license(s)"; }
    private CustomerRecord? Selected() => _grid.CurrentRow?.DataBoundItem is CustomerRow row ? _customers.FirstOrDefault(c => c.Id == row.Id) : null;
    private void CreateCustomer() { using var d = new CustomerEditorDialog(); if (d.ShowDialog(this) == DialogResult.OK) { _customers.Add(d.Customer); CustomerStore.Save(_customers); LoadCustomers(); } }
    private void EditCustomer() { var c = Selected(); if (c is null) return; using var d = new CustomerEditorDialog(c); if (d.ShowDialog(this) == DialogResult.OK) { CustomerStore.Save(_customers); LoadCustomers(); } }
    private void ManageSelected() { var c = Selected(); if (c is null) { MessageBox.Show(this, "Select a customer first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; } using var f = new LicenseForm(c, _customers); f.ShowDialog(this); CustomerStore.Save(_customers); LoadCustomers(); }
    private void DeleteCustomer() { var c = Selected(); if (c is null) return; if (c.Licenses.Count > 0) { MessageBox.Show(this, $"Customer '{c.CompanyName}' cannot be deleted because {c.Licenses.Count} license(s) have been issued. Archive the records and licenses before removing the customer.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; } if (MessageBox.Show(this, $"Delete '{c.CompanyName}'?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return; _customers.Remove(c); CustomerStore.Save(_customers); LoadCustomers(); }

    private sealed record CustomerRow(string Id, string CompanyName, string ContactName, string Phone, string Mobile, int Licenses)
    { public CustomerRow(CustomerRecord c) : this(c.Id, c.CompanyName, c.ContactName, c.Phone, c.Mobile, c.Licenses.Count) { } }
}

internal sealed class CustomerEditorDialog : Form
{
    public CustomerRecord Customer { get; }
    private readonly TextBox _company = new(); private readonly TextBox _contact = new(); private readonly TextBox _phone = new(); private readonly TextBox _mobile = new(); private readonly TextBox _address = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, MinimumSize = new Size(0, 68) }; private readonly TextBox _description = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, MinimumSize = new Size(0, 82) };
    public CustomerEditorDialog(CustomerRecord? existing = null)
    {
        Customer = existing ?? new CustomerRecord(); Text = existing is null ? "New customer" : "Edit customer"; StartPosition = FormStartPosition.CenterParent; MinimumSize = new Size(620, 560); Size = new Size(760, 650); Font = new Font("Segoe UI", 10); BackColor = Color.White;
        _company.Text = Customer.CompanyName; _contact.Text = Customer.ContactName; _phone.Text = Customer.Phone; _mobile.Text = Customer.Mobile; _address.Text = Customer.Address; _description.Text = Customer.Description;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 18), ColumnCount = 2, RowCount = 8, BackColor = Color.White }; layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 106)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new Label { Text = existing is null ? "Create customer" : "Customer details", AutoSize = true, Font = new Font("Segoe UI", 15, FontStyle.Bold), ForeColor = Color.FromArgb(31, 78, 121), Margin = new Padding(0, 0, 0, 12) }; layout.Controls.Add(heading, 0, 0); layout.SetColumnSpan(heading, 2);
        AddRow(layout, "Company name *", _company, 1); AddRow(layout, "Contact person", _contact, 2); AddRow(layout, "Company phone", _phone, 3); AddRow(layout, "Contact mobile", _mobile, 4); AddRow(layout, "Company address", _address, 5); AddRow(layout, "Description", _description, 6);
        var save = new Button { Text = "Save customer", DialogResult = DialogResult.OK, AutoSize = true, Height = 36, Padding = new Padding(16, 0, 16, 0), BackColor = Color.FromArgb(31, 120, 74), ForeColor = Color.White, FlatStyle = FlatStyle.Flat }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Height = 36, Padding = new Padding(16, 0, 16, 0) }; var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 12, 0, 0) }; actions.Controls.AddRange([save, cancel]); layout.Controls.Add(actions, 1, 7);
        save.Click += (_, _) => { if (string.IsNullOrWhiteSpace(_company.Text)) { MessageBox.Show(this, "Enter the company name.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); DialogResult = DialogResult.None; return; } Customer.CompanyName = _company.Text.Trim(); Customer.ContactName = _contact.Text.Trim(); Customer.Phone = _phone.Text.Trim(); Customer.Mobile = _mobile.Text.Trim(); Customer.Address = _address.Text.Trim(); Customer.Description = _description.Text.Trim(); }; AcceptButton = save; CancelButton = cancel; Controls.Add(layout);
    }
    private static void AddRow(TableLayoutPanel p, string label, Control control, int row) { control.Dock = DockStyle.Fill; p.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 7, 8, 0), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = Color.FromArgb(55, 55, 55) }, 0, row); p.Controls.Add(control, 1, row); }
}

internal sealed class LicenseForm : Form
{
    private readonly CustomerRecord _customer; private readonly IReadOnlyCollection<CustomerRecord> _customers;
    private readonly TextBox _privateKey = new() { ReadOnly = true }; private readonly TextBox _requestCode = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9) }; private readonly Label _requestInfo = new() { AutoSize = true, ForeColor = Color.DimGray }; private readonly DateTimePicker _expires = new() { Format = DateTimePickerFormat.Short, Value = DateTime.Today.AddYears(1) }; private readonly CheckBox _plate = new() { Text = "Plate detection", Checked = true, AutoSize = true }; private readonly CheckBox _face = new() { Text = "Face detection and recognition", Checked = true, AutoSize = true }; private readonly ListView _archive = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, MultiSelect = false };
    public LicenseForm(CustomerRecord customer, IReadOnlyCollection<CustomerRecord> customers)
    {
        _customer = customer; _customers = customers; Text = $"Licenses - {customer.CompanyName}"; StartPosition = FormStartPosition.CenterParent; MinimumSize = new Size(950, 600); Size = new Size(1150, 720); Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(245, 247, 250); _privateKey.Text = IssuerPreferences.LoadPrivateKeyPath() ?? FindDefaultPrivateKey() ?? string.Empty; _privateKey.PlaceholderText = "Default key not found - choose a PEM file"; _requestCode.MinimumSize = new Size(0, 130); _archive.BorderStyle = BorderStyle.FixedSingle; _archive.BackColor = Color.White; _archive.Font = new Font("Segoe UI", 9.5f); _archive.Columns.Add("Issued", 90); _archive.Columns.Add("Features", 80); _archive.Columns.Add("Expires", 85); _archive.Columns.Add("Computer / device", 140); _archive.Columns.Add("Archived file", 185);
        var issue = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 18), ColumnCount = 2, RowCount = 8, BackColor = Color.White }; issue.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155)); issue.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); issue.RowStyles.Add(new RowStyle(SizeType.AutoSize)); issue.RowStyles.Add(new RowStyle(SizeType.AutoSize)); issue.RowStyles.Add(new RowStyle(SizeType.AutoSize)); issue.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); for (int i = 4; i < 8; i++) issue.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var issueHeading = new Label { Text = "Issue a license", AutoSize = true, Font = new Font("Segoe UI", 15, FontStyle.Bold), ForeColor = Color.FromArgb(31, 78, 121), Margin = new Padding(0, 0, 0, 12) }; issue.Controls.Add(issueHeading, 0, 0); issue.SetColumnSpan(issueHeading, 2);
        AddRow(issue, "Customer", new Label { Text = $"{customer.CompanyName}  •  {customer.ContactName}", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }, 1); AddRow(issue, "Private key", KeyPicker(), 2); AddRow(issue, "Customer request", _requestCode, 3); issue.Controls.Add(_requestInfo, 1, 4); AddRow(issue, "Expiry date", _expires, 5); var features = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; features.Controls.AddRange([_plate, _face]); AddRow(issue, "Licensed features", features, 6); var button = new Button { Text = "Issue and archive license", AutoSize = true, Height = 38, Padding = new Padding(16, 0, 16, 0), BackColor = Color.FromArgb(31, 120, 74), ForeColor = Color.White, FlatStyle = FlatStyle.Flat }; button.Click += IssueLicense; issue.Controls.Add(button, 1, 7);
        var saveAs = new Button { Text = "Save selected license as...", AutoSize = true, Height = 32, Padding = new Padding(12, 0, 12, 0), BackColor = Color.FromArgb(31, 78, 121), ForeColor = Color.White, FlatStyle = FlatStyle.Flat }; saveAs.Click += SaveSelectedAs; var archiveHeader = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 0, 0, 10) }; archiveHeader.Controls.Add(new Label { Text = "License archive", AutoSize = true, Font = new Font("Segoe UI", 14, FontStyle.Bold), ForeColor = Color.FromArgb(31, 78, 121), Padding = new Padding(0, 4, 14, 0) }); archiveHeader.Controls.Add(saveAs); var archive = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 18), RowCount = 2, BackColor = Color.White }; archive.RowStyles.Add(new RowStyle(SizeType.AutoSize)); archive.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); archive.Controls.Add(archiveHeader, 0, 0); archive.Controls.Add(_archive, 0, 1); var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, BackColor = Color.FromArgb(220, 225, 230) }; split.Panel1.Controls.Add(issue); split.Panel2.Controls.Add(archive); Controls.Add(split); void BalancePanels() { if (split.Width > split.SplitterWidth) split.SplitterDistance = (split.Width - split.SplitterWidth) / 2; } Shown += (_, _) => BeginInvoke((Action)BalancePanels); _requestCode.TextChanged += (_, _) => ValidateRequest(); RefreshArchive();
    }
    private void RefreshArchive() { _archive.Items.Clear(); foreach (var item in _customer.Licenses.OrderByDescending(x => x.IssuedUtc)) { var row = new ListViewItem(item.IssuedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")); row.SubItems.Add(item.Features); row.SubItems.Add(item.ExpiresUtc.ToLocalTime().ToString("yyyy-MM-dd")); row.SubItems.Add(item.MachineId); row.SubItems.Add(item.FilePath); _archive.Items.Add(row); } }
    private Control KeyPicker() { var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 }; p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); _privateKey.Dock = DockStyle.Fill; var browse = new Button { Text = "Browse...", AutoSize = true }; var generate = new Button { Text = "Generate pair...", AutoSize = true }; browse.Click += (_, _) => { using var d = new OpenFileDialog { Filter = "PEM private key|*.pem|All files|*.*" }; if (d.ShowDialog(this) == DialogResult.OK) { _privateKey.Text = d.FileName; IssuerPreferences.SavePrivateKeyPath(d.FileName); } }; generate.Click += GenerateKeyPair; p.Controls.Add(_privateKey, 0, 0); p.Controls.Add(browse, 1, 0); p.Controls.Add(generate, 2, 0); return p; }
    private void GenerateKeyPair(object? sender, EventArgs e) { using var d = new SaveFileDialog { Filter = "PEM private key|*.pem", FileName = "vendor-private.pem" }; if (d.ShowDialog(this) != DialogResult.OK) return; string directory = Path.GetDirectoryName(d.FileName) ?? AppContext.BaseDirectory; string pub = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(d.FileName)}-public.pem"); try { LicenseIssuer.GenerateKeyPair(d.FileName, pub); _privateKey.Text = d.FileName; IssuerPreferences.SavePrivateKeyPath(d.FileName); MessageBox.Show(this, $"Key pair created.\n\nPrivate key: {d.FileName}\nPublic key: {pub}\n\nCopy the public key into LicenseValidator.PublicKeyPem and rebuild before issuing licenses.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); } catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); } }
    private void ValidateRequest() { try { var request = LicenseRequestCodec.Decode(_requestCode.Text); _requestInfo.ForeColor = Color.DarkGreen; _requestInfo.Text = $"Device signature: {request.MachineId}   Computer: {request.ComputerName}"; } catch { _requestInfo.ForeColor = Color.Firebrick; _requestInfo.Text = "Paste a valid request code generated by HshDetection License Request."; } }
    private static string? FindDefaultPrivateKey()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "vendor-private.pem"),
            Path.Combine(AppContext.BaseDirectory, "HshDetectionEngin.Tools", "LicenseKeys", "vendor-private.pem"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "HshDetectionEngin.Tools", "LicenseKeys", "vendor-private.pem"))
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private void IssueLicense(object? sender, EventArgs e)
    {
        try
        {
            string privateKeyPath = string.IsNullOrWhiteSpace(_privateKey.Text) ? FindDefaultPrivateKey() ?? string.Empty : _privateKey.Text.Trim(); if (!File.Exists(privateKeyPath)) throw new InvalidOperationException("The saved vendor private key was not found. Select a PEM private-key file."); IssuerPreferences.SavePrivateKeyPath(privateKeyPath); var features = (_plate.Checked ? LicensedFeature.Plate : LicensedFeature.None) | (_face.Checked ? LicensedFeature.Face : LicensedFeature.None); if (features == LicensedFeature.None) throw new InvalidOperationException("Select at least one feature."); var request = LicenseRequestCodec.Decode(_requestCode.Text); var claims = new LicenseClaims { CustomerName = _customer.CompanyName, MachineId = request.MachineId, ExpiresUtc = DateTime.SpecifyKind(_expires.Value.Date, DateTimeKind.Utc).AddDays(1).AddTicks(-1), Features = features }; string path = CustomerStore.CreateArchivePath(_customer, claims); LicenseIssuer.Issue(privateKeyPath, claims, path); _customer.Licenses.Add(new LicenseArchiveEntry { LicenseId = claims.LicenseId, MachineId = claims.MachineId, FilePath = path, IssuedUtc = DateTime.UtcNow, ExpiresUtc = claims.ExpiresUtc, Features = features.ToString() }); CustomerStore.Save(_customers); RefreshArchive(); MessageBox.Show(this, $"License created and archived.\n\n{path}", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private void SaveSelectedAs(object? sender, EventArgs e)
    {
        var items = _customer.Licenses.OrderByDescending(x => x.IssuedUtc).ToList();
        if (_archive.SelectedIndices.Count == 0 || _archive.SelectedIndices[0] >= items.Count) { MessageBox.Show(this, "Select an archived license first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var entry = items[_archive.SelectedIndices[0]];
        if (!File.Exists(entry.FilePath)) { MessageBox.Show(this, "The archived license file was not found.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        using var dialog = new SaveFileDialog { Filter = "HSH license|*.hshlic", FileName = Path.GetFileName(entry.FilePath), OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.Copy(entry.FilePath, dialog.FileName, true); MessageBox.Show(this, "License file saved.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private static void AddRow(TableLayoutPanel p, string label, Control control, int row) { control.Dock = DockStyle.Fill; p.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 7, 8, 0), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = Color.FromArgb(55, 55, 55) }, 0, row); p.Controls.Add(control, 1, row); }
}
