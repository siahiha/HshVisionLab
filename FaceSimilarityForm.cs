using System.Drawing.Imaging;
using System.Text;
using HshDetectionEngin.Face;

namespace HshVisionLab;

public sealed class FaceSimilarityForm : Form
{
    private readonly FaceDatabase _database;
    private readonly FlowLayoutPanel _pairsPanel = new();
    private readonly NumericUpDown _threshold = new();
    private readonly CheckBox _differentPeople = new();
    private readonly Label _status = new();
    private readonly List<FaceSimilarityPair> _pairs = [];
    private readonly List<Bitmap> _images = [];

    public FaceSimilarityForm(FaceDatabase database)
    {
        _database = database;
        Text = "Similar face samples";
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
        _threshold.DecimalPlaces = 2;
        _threshold.Increment = 0.01m;
        _threshold.Minimum = 0.30m;
        _threshold.Maximum = 0.99m;
        // SFace recognition in the runtime uses 0.40 as its default cosine
        // threshold; using 0.55 here hides many valid same-person pairs.
        _threshold.Value = 0.40m;
        _threshold.Width = 70;
        toolbar.Controls.Add(_threshold);
        _differentPeople.Text = "Only different people";
        // Show duplicate/similar samples belonging to the same person by default.
        // Users can enable this filter when they only want cross-person matches.
        _differentPeople.Checked = false;
        _differentPeople.AutoSize = true;
        toolbar.Controls.Add(_differentPeople);
        Button check = MakeButton("Check similarity");
        Button export = MakeButton("Export to folder");
        Button close = MakeButton("Close");
        check.Click += (_, _) => CheckSimilarity();
        export.Click += (_, _) => ExportToFolder();
        close.Click += (_, _) => Close();
        toolbar.Controls.Add(check);
        toolbar.Controls.Add(export);
        toolbar.Controls.Add(close);
        root.Controls.Add(toolbar, 0, 0);

        _pairsPanel.Dock = DockStyle.Fill;
        _pairsPanel.AutoScroll = true;
        _pairsPanel.FlowDirection = FlowDirection.TopDown;
        _pairsPanel.WrapContents = false;
        _pairsPanel.Padding = new Padding(4);
        root.Controls.Add(_pairsPanel, 0, 1);

        _status.Text = UiLocalization.T("Choose a threshold and check for similar samples.");
        _status.AutoSize = true;
        _status.Padding = new Padding(0, 8, 0, 0);
        root.Controls.Add(_status, 0, 2);
        Controls.Add(root);
    }

    private void CheckSimilarity()
    {
        ClearResults();
        _pairs.AddRange(_database.FindSimilar((float)_threshold.Value, _differentPeople.Checked));
        foreach (FaceSimilarityPair pair in _pairs) AddPair(pair);
        _status.Text = _pairs.Count == 0
            ? UiLocalization.T("No similar samples were found.")
            : $"{_pairs.Count} similar pair(s). The merge button keeps the first person's number.";
    }

    private void AddPair(FaceSimilarityPair pair)
    {
        var card = new Panel
        {
            Width = Math.Max(620, ClientSize.Width - 70), Height = 225, Margin = new Padding(4),
            BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(245, 245, 245)
        };
        Bitmap? leftImage = DecodeImage(pair.Left.FaceImage);
        Bitmap? rightImage = DecodeImage(pair.Right.FaceImage);
        if (leftImage is not null) _images.Add(leftImage);
        if (rightImage is not null) _images.Add(rightImage);
        var left = new PictureBox { Left = 10, Top = 34, Width = 180, Height = 180, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, Image = leftImage };
        var right = new PictureBox { Left = 210, Top = 34, Width = 180, Height = 180, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, Image = rightImage };
        card.Controls.Add(left);
        card.Controls.Add(right);
        card.Controls.Add(new Label { Left = 10, Top = 8, Width = 180, Text = FormatSample(pair.Left), AutoEllipsis = true });
        card.Controls.Add(new Label { Left = 210, Top = 8, Width = 180, Text = FormatSample(pair.Right), AutoEllipsis = true });
        card.Controls.Add(new Label { Left = 420, Top = 45, Width = 175, Text = $"Similarity: {pair.Similarity:0.000}", Font = new Font("Segoe UI", 10F, FontStyle.Bold) });
        var merge = new Button
        {
            Left = 420, Top = 82, Width = 175, Height = 38, Text = "Merge into first person",
            Image = MaterialIconRenderer.CreateForAction("Merge into first person"),
            ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText
        };
        merge.Click += (_, _) => MergePair(pair);
        card.Controls.Add(merge);
        card.Controls.Add(new Label { Left = 420, Top = 132, Width = 175, Height = 70, Text = "The second person's samples will be moved to the first person. The 10-sample limit still applies." });
        _pairsPanel.Controls.Add(card);
    }

    private void MergePair(FaceSimilarityPair pair)
    {
        if (MessageBox.Show(this,
            $"Merge person #{pair.Right.PersonNumber} ({pair.Right.PersonName}) into person #{pair.Left.PersonNumber} ({pair.Left.PersonName})?",
            "Merge people", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            if (_database.MergePeople(pair.Left.PersonId, pair.Right.PersonId)) CheckSimilarity();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Merge people", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ExportToFolder()
    {
        if (_pairs.Count == 0)
        {
            MessageBox.Show(this, UiLocalization.T("Run similarity check first."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new FolderBrowserDialog { Description = "Select the output folder" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string root = Path.Combine(dialog.SelectedPath, $"FaceSimilarity_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Pairs"));
        var csv = new StringBuilder("Pair,LeftFile,RightFile,Similarity\r\n");
        var exported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _pairs.Count; i++)
        {
            FaceSimilarityPair pair = _pairs[i];
            string leftName = ExportSample(root, pair.Left, exported);
            string rightName = ExportSample(root, pair.Right, exported);
            string pairName = $"Pair_{i + 1:0000}_{leftName}_{rightName}.jpg";
            string pairPath = Path.Combine(root, "Pairs", pairName);
            using Bitmap composite = CreateComposite(pair);
            composite.Save(pairPath, ImageFormat.Jpeg);
            csv.Append(i + 1).Append(',').Append(Csv(leftName)).Append(',').Append(Csv(rightName)).Append(',').Append(pair.Similarity.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
        }
        File.WriteAllText(Path.Combine(root, "similarity-report.csv"), csv.ToString(), Encoding.UTF8);
        MessageBox.Show(this, $"Export completed:\r\n{root}", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string ExportSample(string root, FaceSample sample, HashSet<string> exported)
    {
        string name = $"{sample.PersonNumber:0000}_{SafeName(sample.PersonName)}_{sample.SampleNumber:00}.jpg";
        string path = Path.Combine(root, name);
        if (exported.Add(name) && sample.FaceImage.Length > 0) File.WriteAllBytes(path, sample.FaceImage);
        return name;
    }

    private static Bitmap CreateComposite(FaceSimilarityPair pair)
    {
        using Bitmap left = DecodeImage(pair.Left.FaceImage) ?? new Bitmap(180, 180);
        using Bitmap right = DecodeImage(pair.Right.FaceImage) ?? new Bitmap(180, 180);
        var result = new Bitmap(500, 260);
        using Graphics graphics = Graphics.FromImage(result);
        graphics.Clear(Color.White);
        graphics.DrawImage(left, new Rectangle(10, 35, 220, 190));
        graphics.DrawImage(right, new Rectangle(270, 35, 220, 190));
        using var font = new Font("Segoe UI", 9F);
        graphics.DrawString($"#{pair.Left.PersonNumber} {pair.Left.PersonName} / sample {pair.Left.SampleNumber}", font, Brushes.Black, 10, 10);
        graphics.DrawString($"#{pair.Right.PersonNumber} {pair.Right.PersonName} / sample {pair.Right.SampleNumber}", font, Brushes.Black, 270, 10);
        graphics.DrawString($"Similarity: {pair.Similarity:0.000}", font, Brushes.DarkRed, 10, 232);
        return result;
    }

    private void ClearResults()
    {
        _pairs.Clear();
        foreach (Control control in _pairsPanel.Controls.Cast<Control>().ToArray()) control.Dispose();
        _pairsPanel.Controls.Clear();
        foreach (Bitmap image in _images) image.Dispose();
        _images.Clear();
    }

    private static string FormatSample(FaceSample sample) => $"#{sample.PersonNumber} {sample.PersonName} / {sample.SampleNumber}";
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string SafeName(string value)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        return string.Concat(value.Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch));
    }
    private static Button MakeButton(string text) => new()
    {
        Text = text, AutoSize = true, MinimumSize = new Size(125, 34),
        Image = MaterialIconRenderer.CreateForAction(text),
        ImageAlign = ContentAlignment.MiddleLeft,
        TextImageRelation = TextImageRelation.ImageBeforeText
    };
    private static Bitmap? DecodeImage(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        try { using var stream = new MemoryStream(bytes); using Image image = Image.FromStream(stream); return new Bitmap(image); }
        catch { return null; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearResults();
        }
        base.Dispose(disposing);
    }
}
