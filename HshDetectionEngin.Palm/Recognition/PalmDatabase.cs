using Microsoft.Data.Sqlite;

namespace HshDetectionEngin.Palm;

public sealed class PalmSample
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PersonId { get; set; } = string.Empty;
    public int PersonNumber { get; set; }
    public string PersonName { get; set; } = string.Empty;
    public int SampleNumber { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public float DetectionConfidence { get; set; }
    public byte[] PalmImage { get; set; } = [];
    public float[] Embedding { get; set; } = [];
}

public sealed class PalmIdentity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public int PersonNumber { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<PalmSample> Samples { get; set; } = [];
}

public sealed record PalmMatch(string Id, string Name, float Similarity, int PersonNumber, string? MatchedSampleId);

/// <summary>SQLite enrollment store for palmprint embeddings and normalized ROI images.</summary>
public sealed class PalmDatabase : IDisposable
{
    public const int MaxSamplesPerPerson = 10;
    private readonly object _gate = new();
    private readonly List<PalmIdentity> _identities = [];
    private readonly SqliteConnection _connection;
    private bool _disposed;

    private PalmDatabase(string path)
    {
        DatabasePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? AppContext.BaseDirectory);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared
        }.ToString());
        _connection.Open();
        using SqliteCommand pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        EnsureSchema();
        LoadMemory();
    }

    public string DatabasePath { get; }
    public IReadOnlyList<PalmIdentity> Identities { get { lock (_gate) return _identities.Select(CloneIdentity).ToArray(); } }

    public static PalmDatabase Load(string path) => new(path);

    public PalmSample RegisterSample(string name, IReadOnlyList<float> embedding, byte[] palmImage,
        string originalFileName, string? personId = null, float detectionConfidence = 0)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A palm owner name is required.", nameof(name));
        if (embedding.Count == 0) throw new ArgumentException("An embedding is required.", nameof(embedding));
        if (palmImage.Length == 0) throw new ArgumentException("A normalized palm image is required.", nameof(palmImage));
        lock (_gate)
        {
            ThrowIfDisposed();
            string normalized = name.Trim();
            PalmIdentity? person = personId is null
                ? _identities.FirstOrDefault(x => x.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                : _identities.FirstOrDefault(x => x.Id == personId);
            if (person is null)
            {
                int number = _identities.Count == 0 ? 1 : _identities.Max(x => x.PersonNumber) + 1;
                person = new PalmIdentity { Id = personId ?? Guid.NewGuid().ToString("N"), Name = normalized, PersonNumber = number };
            }
            if (person.Samples.Count >= MaxSamplesPerPerson)
                throw new InvalidOperationException($"Person '{person.Name}' already has the maximum of {MaxSamplesPerPerson} samples.");
            PalmSample sample = new()
            {
                PersonId = person.Id, PersonNumber = person.PersonNumber, PersonName = person.Name,
                SampleNumber = person.Samples.Count == 0 ? 1 : person.Samples.Max(x => x.SampleNumber) + 1,
                OriginalFileName = Path.GetFileName(originalFileName), CreatedAtUtc = DateTime.UtcNow,
                DetectionConfidence = detectionConfidence, PalmImage = palmImage.ToArray(), Embedding = embedding.ToArray()
            };
            using SqliteTransaction tx = _connection.BeginTransaction();
            if (!_identities.Contains(person))
            {
                using SqliteCommand insertPerson = _connection.CreateCommand();
                insertPerson.Transaction = tx;
                insertPerson.CommandText = "INSERT INTO PalmPeople(PersonId, PersonNumber, Name, CreatedAtUtc, UpdatedAtUtc) VALUES ($id,$number,$name,$created,$updated);";
                Add(insertPerson, "$id", person.Id); Add(insertPerson, "$number", person.PersonNumber); Add(insertPerson, "$name", person.Name);
                Add(insertPerson, "$created", person.CreatedAtUtc.ToString("O")); Add(insertPerson, "$updated", person.UpdatedAtUtc.ToString("O")); insertPerson.ExecuteNonQuery();
            }
            using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO PalmSamples(SampleId,PersonId,SampleNumber,OriginalFileName,CreatedAtUtc,DetectionConfidence,PalmImage,Embedding) VALUES ($id,$person,$number,$file,$created,$confidence,$image,$embedding);";
            Add(insert, "$id", sample.Id); Add(insert, "$person", sample.PersonId); Add(insert, "$number", sample.SampleNumber); Add(insert, "$file", sample.OriginalFileName);
            Add(insert, "$created", sample.CreatedAtUtc.ToString("O")); Add(insert, "$confidence", sample.DetectionConfidence); Add(insert, "$image", sample.PalmImage); Add(insert, "$embedding", EmbeddingToBytes(sample.Embedding)); insert.ExecuteNonQuery();
            tx.Commit();
            person.Samples.Add(sample); person.UpdatedAtUtc = DateTime.UtcNow;
            if (!_identities.Contains(person)) _identities.Add(person);
            return CloneSample(sample);
        }
    }

    public PalmMatch? Identify(IReadOnlyList<float> embedding, float minimumSimilarity)
    {
        lock (_gate)
        {
            PalmIdentity? best = null; PalmSample? bestSample = null; float score = float.MinValue;
            foreach (PalmIdentity person in _identities)
                foreach (PalmSample sample in person.Samples)
                {
                    float current = Cosine(embedding, sample.Embedding);
                    if (current > score) { score = current; best = person; bestSample = sample; }
                }
            return best is not null && score >= minimumSimilarity
                ? new PalmMatch(best.Id, best.Name, score, best.PersonNumber, bestSample?.Id) : null;
        }
    }

    public PalmIdentity? Find(string id) => Identities.FirstOrDefault(x => x.Id == id);
    public IReadOnlyList<PalmSample> GetSamples() { lock (_gate) return _identities.SelectMany(x => x.Samples).Select(CloneSample).ToArray(); }

    public void Save(string? path = null) { /* SQLite commits each enrollment; kept for API symmetry. */ }

    private void EnsureSchema()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS PalmPeople(PersonId TEXT PRIMARY KEY, PersonNumber INTEGER NOT NULL, Name TEXT NOT NULL UNIQUE, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS PalmSamples(SampleId TEXT PRIMARY KEY, PersonId TEXT NOT NULL, SampleNumber INTEGER NOT NULL, OriginalFileName TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, DetectionConfidence REAL NOT NULL, PalmImage BLOB NOT NULL, Embedding BLOB NOT NULL, FOREIGN KEY(PersonId) REFERENCES PalmPeople(PersonId) ON DELETE CASCADE);
            """;
        command.ExecuteNonQuery();
    }

    private void LoadMemory()
    {
        using SqliteCommand people = _connection.CreateCommand(); people.CommandText = "SELECT PersonId,PersonNumber,Name,CreatedAtUtc,UpdatedAtUtc FROM PalmPeople ORDER BY PersonNumber;";
        using SqliteDataReader reader = people.ExecuteReader();
        while (reader.Read()) _identities.Add(new PalmIdentity { Id = reader.GetString(0), PersonNumber = reader.GetInt32(1), Name = reader.GetString(2), CreatedAtUtc = DateTime.Parse(reader.GetString(3)).ToUniversalTime(), UpdatedAtUtc = DateTime.Parse(reader.GetString(4)).ToUniversalTime() });
        reader.Close();
        using SqliteCommand samples = _connection.CreateCommand(); samples.CommandText = "SELECT SampleId,PersonId,SampleNumber,OriginalFileName,CreatedAtUtc,DetectionConfidence,PalmImage,Embedding FROM PalmSamples ORDER BY SampleNumber;";
        using SqliteDataReader sampleReader = samples.ExecuteReader();
        while (sampleReader.Read())
        {
            PalmIdentity? person = _identities.FirstOrDefault(x => x.Id == sampleReader.GetString(1)); if (person is null) continue;
            person.Samples.Add(new PalmSample { Id = sampleReader.GetString(0), PersonId = person.Id, PersonNumber = person.PersonNumber, PersonName = person.Name, SampleNumber = sampleReader.GetInt32(2), OriginalFileName = sampleReader.GetString(3), CreatedAtUtc = DateTime.Parse(sampleReader.GetString(4)).ToUniversalTime(), DetectionConfidence = sampleReader.GetFloat(5), PalmImage = (byte[])sampleReader[6], Embedding = BytesToEmbedding((byte[])sampleReader[7]) });
        }
    }

    private static PalmIdentity CloneIdentity(PalmIdentity value) => new() { Id = value.Id, Name = value.Name, PersonNumber = value.PersonNumber, CreatedAtUtc = value.CreatedAtUtc, UpdatedAtUtc = value.UpdatedAtUtc, Samples = value.Samples.Select(CloneSample).ToList() };
    private static PalmSample CloneSample(PalmSample value) => new() { Id = value.Id, PersonId = value.PersonId, PersonNumber = value.PersonNumber, PersonName = value.PersonName, SampleNumber = value.SampleNumber, OriginalFileName = value.OriginalFileName, CreatedAtUtc = value.CreatedAtUtc, DetectionConfidence = value.DetectionConfidence, PalmImage = value.PalmImage.ToArray(), Embedding = value.Embedding.ToArray() };
    private static float Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b) { if (a.Count != b.Count || a.Count == 0) return 0; double dot = 0, aa = 0, bb = 0; for (int i = 0; i < a.Count; i++) { dot += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; } return aa <= 0 || bb <= 0 ? 0 : (float)(dot / Math.Sqrt(aa * bb)); }
    private static byte[] EmbeddingToBytes(IReadOnlyList<float> value) { byte[] bytes = new byte[value.Count * sizeof(float)]; Buffer.BlockCopy(value.ToArray(), 0, bytes, 0, bytes.Length); return bytes; }
    private static float[] BytesToEmbedding(byte[] value) { float[] result = new float[value.Length / sizeof(float)]; Buffer.BlockCopy(value, 0, result, 0, value.Length); return result; }
    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(PalmDatabase)); }
    public void Dispose() { if (_disposed) return; _disposed = true; _connection.Dispose(); GC.SuppressFinalize(this); }
}
