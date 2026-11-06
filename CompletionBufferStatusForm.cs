namespace HshVisionLab;

public sealed class CompletionBufferStatusForm : Form
{
    private readonly CameraRuntime _camera;
    private readonly Label _summary = new() { Dock = DockStyle.Top, Height = 100, Padding = new Padding(10), AutoEllipsis = true };
    private readonly DataGridView _entries = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 500 };

    public CompletionBufferStatusForm(CameraRuntime camera)
    {
        _camera = camera;
        Text = $"{UiLocalization.T("Sample buffers")} — {camera.Settings.Name}";
        Size = new Size(980, 520);
        MinimumSize = new Size(760, 420);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(30, 33, 40); ForeColor = Color.Gainsboro;
        _entries.BackgroundColor = BackColor;
        _entries.DefaultCellStyle.BackColor = Color.FromArgb(38, 42, 50);
        _entries.DefaultCellStyle.ForeColor = ForeColor;
        foreach (string column in new[] { "Sample type", "Track", "ROI", "Recognition destination", "Status", "Sample age (ms)", "Attempts" })
            _entries.Columns.Add(column, UiLocalization.T(column));
        Controls.Add(_entries); Controls.Add(_summary);
        _refresh.Tick += (_, _) => RefreshStatus();
        Shown += (_, _) => { RefreshStatus(); _refresh.Start(); };
        UiLocalization.Apply(this);
    }

    private void RefreshStatus()
    {
        var statuses = _camera.CompletionBufferStatus;
        _summary.Text = statuses.Count == 0 ? UiLocalization.T("Camera is stopped or no buffered recognitions are active.") : string.Join(Environment.NewLine,
            statuses.Select(s => $"{UiLocalization.T(s.Kind == AnalysisKind.Face ? "Face buffer" : "Plate buffer")}: {s.Pending}/{s.Capacity} | " +
                $"{UiLocalization.T("Processing")}: {s.InFlight} | {s.MemoryBytes / (1024d * 1024):0.0} MB | " +
                $"{UiLocalization.T("Completed")}: {s.Completed} | {UiLocalization.T("Replaced")}: {s.Replaced} | " +
                $"{UiLocalization.T("Dropped")}: {s.Dropped} | {UiLocalization.T("Expired")}: {s.Expired} | {UiLocalization.T("Retried")}: {s.Retried} | {s.LastCompletionMs:0} ms"));
        _entries.Rows.Clear();
        foreach (var entry in _camera.CompletionBufferEntries)
            _entries.Rows.Add(entry.Kind, entry.TrackId, entry.RoiName, entry.RecognitionName, UiLocalization.T(entry.State), $"{entry.AgeMs:0}", entry.Attempts);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refresh.Dispose();
        base.Dispose(disposing);
    }
}
