using Emgu.CV;

namespace HshDetectionEngin.Plate;

internal sealed class PlateCompletionRecognizer : IRecognitionProcessor
{
    private readonly IPlateTextRecognizer _ocr;
    private readonly RecognitionDefinition _definition;
    public PlateCompletionRecognizer(RecognitionDefinition definition)
    {
        _definition = definition;
        string path = PlateModelPaths.Find(definition.ModelFile) ?? throw new FileNotFoundException("OCR model not found.", definition.ModelFile);
        string temporary = SecureModelLoader.Materialize(path);
        try { _ocr = PlateOcrRecognizerFactory.Create(temporary, path, definition.Threads, definition.Confidence); }
        finally { try { File.Delete(temporary); } catch { } }
    }

    public AnalysisDetection? Complete(Mat crop, AnalysisDetection detection)
    {
        var result = _ocr.Read(crop);
        if (!float.IsFinite(result.Confidence) || result.Confidence < _definition.Confidence || !PersianPlate.IsValidIranianPlate(result.Text)) return null;
        var metadata = detection.Metadata is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(detection.Metadata);
        metadata["Accepted"] = true;
        metadata["PlateText"] = result.Text;
        metadata["RecognitionConfidence"] = result.Confidence;
        metadata["RecognitionModel"] = _definition.ModelFile;
        metadata["HasCharacterDetails"] = result.Characters.Count > 0;
        metadata["Characters"] = result.Characters.Select((c, i) => new Dictionary<string, object?>
        {
            ["index"] = i, ["symbol"] = c.Symbol, ["confidence"] = c.Confidence, ["classId"] = null,
            ["bounds"] = c.Bounds is System.Drawing.RectangleF b ? new Dictionary<string, object?>
            { ["x"] = detection.Bounds.X + b.X, ["y"] = detection.Bounds.Y + b.Y, ["width"] = b.Width, ["height"] = b.Height } : null
        }).ToList();
        return detection with { Label = result.Text, Metadata = metadata };
    }
    public void Dispose() => _ocr.Dispose();
}
