using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Microsoft.ML.OnnxRuntime;

namespace HshDetectionEngin.Face;

internal sealed class FaceCompletionRecognizer : IRecognitionProcessor
{
    private readonly InferenceSession _session;
    private readonly FaceDatabase _database;
    private readonly RecognitionDefinition _definition;
    private sealed record PendingIdentity(float[] Embedding, byte[] Jpeg);

    public FaceCompletionRecognizer(RecognitionDefinition definition, FaceDatabase database)
    {
        _definition = definition;
        _database = database;
        string path = FaceModelPaths.Find(definition.ModelFile) ?? throw new FileNotFoundException("Face recognition model not found.", definition.ModelFile);
        _session = FacePipeline.CreateSession(path, definition.Threads);
    }

    public AnalysisDetection? Complete(Mat crop, AnalysisDetection detection)
    {
        if (detection.Metadata?.TryGetValue("TrackingLandmarks", out object? value) != true || value is not PointF[] landmarks)
            throw new InvalidDataException("Tracked face has no alignment landmarks.");
        using var aligned = FacePipeline.AlignCrop(crop, landmarks);
        using var rgb = new Mat();
        CvInvoke.CvtColor(aligned, rgb, ColorConversion.Bgr2Rgb);
        float[] buffer = new float[3 * 112 * 112];
        FacePipeline.CopyImageToNchw(rgb, buffer, false);
        using var tensor = OrtValue.CreateTensorValueFromMemory(buffer, [1, 3, 112, 112]);
        using var runOptions = new RunOptions();
        using var outputs = _session.Run(runOptions, new Dictionary<string, OrtValue> { [_session.InputNames.First()] = tensor }, _session.OutputNames);
        float[] embedding = outputs.First().GetTensorDataAsSpan<float>().ToArray();
        if (embedding.Length == 0 || embedding.Any(x => !float.IsFinite(x)) || !embedding.Any(x => x != 0)) throw new InvalidDataException("Invalid face embedding.");
        byte[] jpeg = FacePipeline.EncodeJpeg(aligned);
        var metadata = detection.Metadata is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(detection.Metadata);
        metadata["PendingIdentity"] = new PendingIdentity(embedding, jpeg);
        return detection with { Metadata = metadata };
    }

    public AnalysisDetection FinalizeResult(AnalysisDetection detection)
    {
        var metadata = new Dictionary<string, object?>(detection.Metadata!);
        var pending = (PendingIdentity)metadata["PendingIdentity"]!;
        metadata.Remove("PendingIdentity");
        var match = _database.IdentifyOrCreateUnknown(pending.Embedding, _definition.Confidence,
            _definition.UnknownMatchThreshold, pending.Jpeg, "tracked-face.jpg", detection.Confidence);
        metadata["Accepted"] = true;
        metadata["Recognized"] = true;
        metadata["IdentityId"] = match.Id;
        metadata["Similarity"] = match.Similarity;
        metadata["PersonNumber"] = match.PersonNumber;
        metadata["IsUnknown"] = match.IsUnknown;
        metadata["MatchedSampleId"] = match.MatchedSampleId;
        metadata["AlignedFaceJpeg"] = pending.Jpeg;
        metadata["RecognitionThreshold"] = _definition.Confidence;
        metadata["RecognitionModel"] = _definition.ModelFile;
        return detection with { Label = match.Name, Metadata = metadata };
    }

    public void Dispose() => _session.Dispose();
}
