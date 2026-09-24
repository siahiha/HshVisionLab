namespace HshDetectionEngin.Plate;

internal static class PlateOcrRecognizerFactory
{
    public static IPlateTextRecognizer Create(string modelPath, int threads, float confidence)
    {
        if (!PlateOcrModelCatalog.TryDescribe(modelPath, out PlateOcrModelDescriptor descriptor))
            throw new InvalidOperationException(
                $"The selected OCR model is not registered as a supported plate OCR model: {Path.GetFileName(modelPath)}");

        return descriptor.Decoder.ToLowerInvariant() switch
        {
            "crnn_ctc" => new CrnnPlateRecognizer(modelPath, threads),
            "cnn_glyph" => new CnnPlateRecognizer(modelPath, threads),
            "yolo_character" => new Yolo26PlateRecognizer(modelPath, threads, confidence),
            _ => throw new InvalidOperationException($"Unsupported plate OCR decoder: {descriptor.Decoder}")
        };
    }
}
