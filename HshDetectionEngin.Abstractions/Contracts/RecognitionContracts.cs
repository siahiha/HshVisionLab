using Emgu.CV;

namespace HshDetectionEngin;

/// <summary>One camera-owned recognizer; called only by its single completion worker.</summary>
public interface IRecognitionProcessor : IDisposable
{
    AnalysisDetection? Complete(Mat crop, AnalysisDetection trackedDetection);
    /// <summary>Commit identity changes only after generation/expiry validation.</summary>
    AnalysisDetection FinalizeResult(AnalysisDetection result) => result;
}

public sealed record SampleBufferStatus(AnalysisKind Kind, int Pending, int Capacity, int InFlight,
    long MemoryBytes, long Replaced, long Dropped, long Expired, long Retried, long Completed, double LastCompletionMs);

public sealed record SampleBufferEntry(AnalysisKind Kind, int? TrackId, string RoiName, string RecognitionName,
    string State, double AgeMs, int Attempts);
