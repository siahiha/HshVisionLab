using HshDetectionEngin.Palm;

namespace HshVisionLab;

public sealed class PalmSimilarityForm : Form
{
    private readonly PalmDatabase _database;
    private readonly FlowLayoutPanel _pairsPanel = new();
    private readonly NumericUpDown _threshold = new();
    private readonly CheckBox _differentPeople = new();
    private readonly Label _status = new();
    private readonly List<Bitmap> _images = [];

    public PalmSimilarityForm(PalmDatabase database)
    {
        _database = database;
        Text = "Similar palm samples";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(900, 700);
        MinimumSize = new Size(700, 480);
        BuildUi();
        UiLocalization.Apply(this);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        toolbar.Controls.Add(new Label { Text = "Minimum similarity:", AutoSize = true, Padding = new Padding(0, 9, 0, 0) });
        _threshold.DecimalPlaces = 2; _threshold.Increment = .01m; _threshold.Minimum = .30m; _threshold.Maximum = .99m; _threshold.Value = .40m; _threshold.Width = 70;
        toolbar.Controls.Add(_threshold);
        _differentPeople.Text = "Only different people"; _differentPeople.AutoSize = true; toolbar.Controls.Add(_differentPeople);
        Button check = MakeButton("Check similarity"); check.Click += (_, _) => CheckSimilarity();
        Button close = MakeButton("Close"); close.Click += (_, _) => Close();
        toolbar.Controls.Add(check); toolbar.Controls.Add(close); root.Controls.Add(toolbar, 0, 0);
        _pairsPanel.Dock = DockStyle.Fill; _pairsPanel.AutoScroll = true; _pairsPanel.FlowDirection = FlowDirection.TopDown; _pairsPanel.WrapContents = false; _pairsPanel.Padding = new Padding(4); root.Controls.Add(_pairsPanel, 0, 1);
        _status.Text = "Choose a threshold and check for similar samples."; _status.AutoSize = true; _status.Padding = new Padding(0, 8, 0, 0); root.Controls.Add(_status, 0, 2);
        Controls.Add(root);
    }

    private void CheckSimilarity()
    {
        ClearResults();
        foreach (PalmSimilarityPair pair in _database.FindSimilar((float)_threshold.Value, _differentPeople.Checked)) AddPair(pair);
        _status.Text = _pairsPanel.Controls.Count == 0 ? "No similar samples were found." : $"{_pairsPanel.Controls.Count} similar pair(s) found.";
    }

    private void AddPair(PalmSimilarityPair pair)
    {
        var card = new Panel { Width = Math.Max(620, ClientSize.Width - 70), Height = 225, Margin = new Padding(4), BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(245, 245, 245) };
        Bitmap? leftImage = DecodeImage(pair.Left.PalmImage); Bitmap? rightImage = DecodeImage(pair.Right.PalmImage);
        if (leftImage is not null) _images.Add(leftImage); if (rightImage is not null) _images.Add(rightImage);
        card.Controls.Add(new PictureBox { Left = 10, Top = 34, Width = 180, Height = 180, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, Image = leftImage });
        card.Controls.Add(new PictureBox { Left = 210, Top = 34, Width = 180, Height = 180, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, Image = rightImage });
        card.Controls.Add(new Label { Left = 10, Top = 8, Width = 180, Text = FormatSample(pair.Left), AutoEllipsis = true });
        card.Controls.Add(new Label { Left = 210, Top = 8, Width = 180, Text = FormatSample(pair.Right), AutoEllipsis = true });
        card.Controls.Add(new Label { Left = 420, Top = 45, Width = 175, Text = $"Similarity: {pair.Similarity:0.000}", Font = new Font("Segoe UI", 10F, FontStyle.Bold) });
        var merge = new Button { Left = 420, Top = 82, Width = 175, Height = 38, Text = "Merge into first person" };
        merge.Click += (_, _) => MergePair(pair); card.Controls.Add(merge);
        card.Controls.Add(new Label { Left = 420, Top = 132, Width = 175, Height = 70, Text = "The second person's samples will be moved to the first person. The 10-sample limit still applies." });
        _pairsPanel.Controls.Add(card);
    }

    private void MergePair(PalmSimilarityPair pair)
    {
        if (MessageBox.Show(this, $"Merge person #{pair.Right.PersonNumber} ({pair.Right.PersonName}) into person #{pair.Left.PersonNumber} ({pair.Left.PersonName})?", "Merge people", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { if (_database.MergePeople(pair.Left.PersonId, pair.Right.PersonId)) CheckSimilarity(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Merge people", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void ClearResults()
    {
        foreach (Control control in _pairsPanel.Controls.Cast<Control>().ToArray()) control.Dispose();
        _pairsPanel.Controls.Clear(); foreach (Bitmap image in _images) image.Dispose(); _images.Clear();
    }

    private static string FormatSample(PalmSample sample) => $"#{sample.PersonNumber} {sample.PersonName} / {sample.SampleNumber}";
    private static Bitmap? DecodeImage(byte[] bytes) { if (bytes.Length == 0) return null; try { using var stream = new MemoryStream(bytes); using Image image = Image.FromStream(stream); return new Bitmap(image); } catch { return null; } }
    private static Button MakeButton(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(125, 34) };
    protected override void Dispose(bool disposing) { if (disposing) ClearResults(); base.Dispose(disposing); }
}
