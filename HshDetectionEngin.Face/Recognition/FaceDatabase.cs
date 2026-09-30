using HshDetectionEngin.Identity;

namespace HshDetectionEngin.Face;

public sealed class FaceSample
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PersonId { get; set; } = string.Empty;
    public int PersonNumber { get; set; }
    public string PersonName { get; set; } = string.Empty;
    public int SampleNumber { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string FileExtension { get; set; } = ".jpg";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public float DetectionConfidence { get; set; }
    public byte[] FaceImage { get; set; } = [];
    public float[] Embedding { get; set; } = [];
}

public sealed class FaceIdentity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public bool IsUnknown { get; set; }
    public int PersonNumber { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<FaceSample> Samples { get; set; } = [];
    public List<float> Embedding
    {
        get => Samples.FirstOrDefault()?.Embedding.ToList() ?? [];
        set { if (value is { Count: > 0 } && Samples.Count == 0) Samples.Add(new FaceSample { PersonId = Id, PersonName = Name, Embedding = value.ToArray() }); }
    }
}

public sealed record FaceMatch(string Id, string Name, float Similarity, int PersonNumber = 0,
    bool IsUnknown = false, string? MatchedSampleId = null);
public sealed record FaceSimilarityPair(FaceSample Left, FaceSample Right, float Similarity);

/// <summary>Compatibility adapter over the shared identity database.</summary>
public sealed class FaceDatabase : IDisposable
{
    public const int MaxSamplesPerPerson = IdentityDatabase.MaxSamplesPerPerson;
    private readonly IdentityDatabase _store;
    private readonly bool _ownsStore;

    private FaceDatabase(IdentityDatabase store, bool ownsStore) { _store = store ?? throw new ArgumentNullException(nameof(store)); _ownsStore = ownsStore; }
    public string DatabasePath => _store.DatabasePath;
    public static FaceDatabase Load(string path) => new(IdentityDatabase.Load(path), true);
    public static FaceDatabase FromStore(IdentityDatabase store) => new(store, false);
    public IReadOnlyList<FaceIdentity> Identities => _store.GetPeople().Select(MapPerson).ToArray();
    public IReadOnlyList<FaceSample> GetSamples(bool includeImages = false) => _store.GetFaceSamples(includeImages).Select(MapSample).ToArray();
    public byte[] GetFaceImage(string sampleId) => _store.GetFaceImage(sampleId);
    public FaceIdentity CreatePerson(string name, bool isUnknown = false) => MapPerson(_store.CreatePerson(name, isUnknown));
    public FaceSample RegisterSample(string name, IReadOnlyList<float> embedding, byte[] faceImage, string originalFileName,
        string? personId = null, float detectionConfidence = 0, DateTime? createdAtUtc = null, bool isUnknown = false) =>
        MapSample(_store.RegisterFaceSample(name, embedding, faceImage, originalFileName, personId, detectionConfidence, createdAtUtc, isUnknown));
    public void Register(string name, IReadOnlyList<float> embedding) => RegisterSample(name, embedding, [1], "legacy.jpg");
    public FaceMatch? Identify(IReadOnlyList<float> embedding, float minimumSimilarity = 0.40f) => MapMatch(_store.IdentifyFace(embedding, minimumSimilarity));
    public FaceMatch IdentifyOrCreateUnknown(IReadOnlyList<float> embedding, float minimumSimilarity = 0.40f, float unknownSimilarity = 0.35f,
        byte[]? faceImage = null, string originalFileName = "runtime-face.jpg", float detectionConfidence = 0) =>
        MapMatch(_store.IdentifyOrCreateUnknown(embedding, minimumSimilarity, unknownSimilarity, faceImage, originalFileName, detectionConfidence))!;
    public bool Rename(string id, string name) => _store.RenamePerson(id, name);
    public bool Remove(string id) => _store.RemovePerson(id);
    public bool RemoveSample(string sampleId) => _store.RemoveFaceSample(sampleId);
    public bool MoveSample(string sampleId, string targetPersonId) => _store.MoveFaceSample(sampleId, targetPersonId);
    public bool MergePeople(string targetPersonId, string sourcePersonId) => _store.MergePeople(targetPersonId, sourcePersonId);

    public IReadOnlyList<FaceSimilarityPair> FindSimilar(float minimumSimilarity = 0.40f, bool onlyDifferentPeople = false)
    {
        FaceSample[] samples = GetSamples(includeImages: true).ToArray();
        var result = new List<FaceSimilarityPair>();
        for (int i = 0; i < samples.Length; i++)
        for (int j = i + 1; j < samples.Length; j++)
        {
            if (onlyDifferentPeople && samples[i].PersonId == samples[j].PersonId) continue;
            float score = Cosine(samples[i].Embedding, samples[j].Embedding);
            if (score >= minimumSimilarity) result.Add(new FaceSimilarityPair(samples[i], samples[j], score));
        }
        return result.OrderByDescending(item => item.Similarity).ToArray();
    }

    public void Save(string? path = null) { if (!string.IsNullOrWhiteSpace(path)) _store.Backup(path); }

    private FaceIdentity MapPerson(IdentityPersonRecord person) => new()
    {
        Id = person.Id, Name = person.Name, IsUnknown = person.IsUnknown, PersonNumber = person.PersonNumber,
        CreatedAtUtc = person.CreatedAtUtc, UpdatedAtUtc = person.UpdatedAtUtc,
        Samples = _store.GetFaceSamples(true, person.Id).Select(MapSample).ToList()
    };
    private static FaceSample MapSample(IdentityFaceSampleRecord sample) => new()
    {
        Id = sample.Id, PersonId = sample.PersonId, PersonNumber = sample.PersonNumber, PersonName = sample.PersonName,
        SampleNumber = sample.SampleNumber, OriginalFileName = sample.OriginalFileName, FileExtension = sample.FileExtension,
        CreatedAtUtc = sample.CreatedAtUtc, DetectionConfidence = sample.DetectionConfidence,
        FaceImage = sample.FaceImage.ToArray(), Embedding = sample.Embedding.ToArray()
    };
    private static FaceMatch? MapMatch(IdentityMatch? match) => match is null ? null : new FaceMatch(match.PersonId, match.Name, match.Similarity, match.PersonNumber, match.IsUnknown, match.MatchedSampleId);
    private static float Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count != b.Count || a.Count == 0) return 0;
        double dot = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Count; i++) { dot += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        return aa <= 0 || bb <= 0 ? 0 : (float)(dot / Math.Sqrt(aa * bb));
    }
    public void Dispose() { if (_ownsStore) _store.Dispose(); GC.SuppressFinalize(this); }
}
