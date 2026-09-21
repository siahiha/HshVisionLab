using HshDetectionEngin.Licensing;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new LicenseRequestForm());
    }
}

internal sealed class LicenseRequestForm : Form
{
    private readonly LicenseRequest _request = LicenseRequestCodec.CreateCurrent();
    private readonly TextBox _code = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10) };

    public LicenseRequestForm()
    {
        Text = "HshDetection - Activation Request";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(660, 390);
        Size = new Size(760, 460);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Activation request", AutoSize = true, Font = new Font("Segoe UI", 14, FontStyle.Bold) }, 0, 0);
        layout.Controls.Add(new Label { Text = "Send the code below, or the saved .hshrequest file, to your software provider. This request does not contain a license key.", AutoSize = true, MaximumSize = new Size(700, 0) }, 0, 1);
        _code.Text = LicenseRequestCodec.Encode(_request);
        layout.Controls.Add(_code, 0, 2);
        layout.Controls.Add(new Label { Text = $"Computer: {_request.ComputerName}    Device signature: {_request.MachineId}", AutoSize = true, MaximumSize = new Size(700, 0) }, 0, 3);
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) };
        var close = new Button { Text = "Close", AutoSize = true };
        var save = new Button { Text = "Save request file", AutoSize = true };
        var copy = new Button { Text = "Copy request code", AutoSize = true };
        close.Click += (_, _) => Close();
        copy.Click += (_, _) => { Clipboard.SetText(_code.Text); MessageBox.Show(this, "Request code copied.", Text); };
        save.Click += SaveRequest;
        actions.Controls.AddRange([close, save, copy]);
        layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);
    }

    private void SaveRequest(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog { Filter = "HSH activation request|*.hshrequest", FileName = $"{Environment.MachineName}.hshrequest" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        LicenseRequestCodec.Save(dialog.FileName, _request);
        MessageBox.Show(this, "Activation request saved. Send this file to your provider.", Text);
    }
}
