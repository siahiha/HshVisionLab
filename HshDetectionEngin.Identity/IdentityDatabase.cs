using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HshDetectionEngin.Identity;

public sealed class IdentityPersonRecord
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsUnknown { get; init; }
    public int PersonNumber { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class IdentityFaceSampleRecord
{
    public string Id { get; init; } = string.Empty;
    public string PersonId { get; init; } = string.Empty;
    public int PersonNumber { get; init; }
    public string PersonName { get; init; } = string.Empty;
    public int SampleNumber { get; init; }
    public string OriginalFileName { get; init; } = string.Empty;
    public string FileExtension { get; init; } = ".jpg";
    public DateTime CreatedAtUtc { get; init; }
    public float DetectionConfidence { get; init; }
    public byte[] FaceImage { get; init; } = [];
    public float[] Embedding { get; init; } = [];
}

public sealed class IdentityPalmSampleRecord
{
    public string Id { get; init; } = string.Empty;
    public string PersonId { get; init; } = string.Empty;
    public int PersonNumber { get; init; }
    public string PersonName { get; init; } = string.Empty;
    public int SampleNumber { get; init; }
    public string OriginalFileName { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public float DetectionConfidence { get; init; }
    public byte[] PalmImage { get; init; } = [];
    public float[] Embedding { get; init; } = [];
}

public sealed class PersonPlateRecord
{
    public string Id { get; init; } = string.Empty;
    public string PersonId { get; init; } = string.Empty;
    public string PlateText { get; init; } = string.Empty;
    public string NormalizedText { get; init; } = string.Empty;
    public bool IsPrimary { get; init; }
    public string Notes { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed record IdentityMatch(string PersonId, string Name, float Similarity, int PersonNumber,
    bool IsUnknown = false, string? MatchedSampleId = null);

/// <summary>
/// Shared SQLite store for people and all identity modalities.
/// Face, palm and future modality adapters must use the same PersonId.
/// </summary>
public sealed class IdentityDatabase : IDisposable
{
    public const int MaxSamplesPerPerson = 10;
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    private IdentityDatabase(string path)
    {
        DatabasePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? AppContext.BaseDirectory);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        _connection.Open();
        using SqliteCommand pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        EnsureSchema();
    }

    public string DatabasePath { get; }

    public static IdentityDatabase Load(string path, string? legacyFacePath = null, string? legacyPalmPath = null)
    {
        bool wasEmpty = !File.Exists(path) || new FileInfo(path).Length == 0;
        var database = new IdentityDatabase(path);
        if (wasEmpty || database.IsEmpty())
        {
            database.ImportLegacyFaceDatabase(legacyFacePath);
            database.ImportLegacyPalmDatabase(legacyPalmPath);
        }
        return database;
    }

    public IReadOnlyList<IdentityPersonRecord> GetPeople()
    {
        lock (_gate)
        {
            using SqliteCommand command = CreateCommand("SELECT PersonId, PersonNumber, Name, IsUnknown, CreatedAtUtc, UpdatedAtUtc FROM People ORDER BY PersonNumber;");
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<IdentityPersonRecord>();
            while (reader.Read()) result.Add(ReadPerson(reader));
            return result;
        }
    }

    public IdentityPersonRecord? FindPerson(string id) => GetPeople().FirstOrDefault(item => item.Id == id);

    public IdentityPersonRecord CreatePerson(string name, bool isUnknown = false)
    {
        string normalized = NormalizeName(name);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (GetPeopleUnsafe().Any(item => item.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A person named '{normalized}' already exists.");
            DateTime now = DateTime.UtcNow;
            IdentityPersonRecord person = new()
            {
                Id = Guid.NewGuid().ToString("N"), Name = normalized, IsUnknown = isUnknown,
                PersonNumber = NextPersonNumberUnsafe(), CreatedAtUtc = now, UpdatedAtUtc = now
            };
            using SqliteCommand command = CreateCommand("INSERT INTO People(PersonId,PersonNumber,Name,IsUnknown,CreatedAtUtc,UpdatedAtUtc) VALUES($id,$number,$name,$unknown,$created,$updated);");
            Add(command, "$id", person.Id); Add(command, "$number", person.PersonNumber); Add(command, "$name", person.Name);
            Add(command, "$unknown", person.IsUnknown ? 1 : 0); Add(command, "$created", ToText(now)); Add(command, "$updated", ToText(now));
            command.ExecuteNonQuery();
            return person;
        }
    }

    public bool RenamePerson(string personId, string name)
    {
        string normalized = NormalizeName(name);
        lock (_gate)
        {
            IdentityPersonRecord? person = GetPeopleUnsafe().FirstOrDefault(item => item.Id == personId);
            if (person is null || GetPeopleUnsafe().Any(item => item.Id != personId && item.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase))) return false;
            using SqliteCommand command = CreateCommand("UPDATE People SET Name=$name, IsUnknown=0, UpdatedAtUtc=$updated WHERE PersonId=$id;");
            Add(command, "$name", normalized); Add(command, "$updated", ToText(DateTime.UtcNow)); Add(command, "$id", personId);
            if (command.ExecuteNonQuery() == 0) return false;
            using SqliteCommand updateFace = CreateCommand("UPDATE FaceSamples SET PersonName=$name WHERE PersonId=$id;");
            Add(updateFace, "$name", normalized); Add(updateFace, "$id", personId); updateFace.ExecuteNonQuery();
            using SqliteCommand updatePalm = CreateCommand("UPDATE PalmSamples SET PersonName=$name WHERE PersonId=$id;");
            Add(updatePalm, "$name", normalized); Add(updatePalm, "$id", personId); updatePalm.ExecuteNonQuery();
            return true;
        }
    }

    public bool RemovePerson(string personId)
    {
        lock (_gate)
        {
            using SqliteCommand command = CreateCommand("DELETE FROM People WHERE PersonId=$id;");
            Add(command, "$id", personId); return command.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<PersonPlateRecord> GetPlates(string? personId = null)
    {
        lock (_gate)
        {
            using SqliteCommand command = CreateCommand(personId is null
                ? "SELECT PlateId,PersonId,PlateText,NormalizedText,IsPrimary,Notes,CreatedAtUtc,UpdatedAtUtc FROM PersonPlates ORDER BY NormalizedText;"
                : "SELECT PlateId,PersonId,PlateText,NormalizedText,IsPrimary,Notes,CreatedAtUtc,UpdatedAtUtc FROM PersonPlates WHERE PersonId=$person ORDER BY NormalizedText;");
            if (personId is not null) Add(command, "$person", personId);
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<PersonPlateRecord>();
            while (reader.Read()) result.Add(new PersonPlateRecord
            {
                Id = reader.GetString(0), PersonId = reader.GetString(1), PlateText = reader.GetString(2), NormalizedText = reader.GetString(3),
                IsPrimary = reader.GetInt32(4) != 0, Notes = reader.GetString(5), CreatedAtUtc = ParseTime(reader.GetString(6)), UpdatedAtUtc = ParseTime(reader.GetString(7))
            });
            return result;
        }
    }

    public PersonPlateRecord AddPlate(string personId, string plateText, bool isPrimary = false, string? notes = null)
    {
        string display = plateText.Trim(); string normalized = NormalizePlate(display);
        if (normalized.Length == 0) throw new ArgumentException("A plate number is required.", nameof(plateText));
        lock (_gate)
        {
            if (FindPersonUnsafe(personId) is null) throw new InvalidOperationException("The selected person does not exist.");
            DateTime now = DateTime.UtcNow;
            PersonPlateRecord plate = new()
            {
                Id = Guid.NewGuid().ToString("N"), PersonId = personId, PlateText = display, NormalizedText = normalized,
                IsPrimary = isPrimary, Notes = notes?.Trim() ?? string.Empty, CreatedAtUtc = now, UpdatedAtUtc = now
            };
            using SqliteTransaction transaction = _connection.BeginTransaction();
            if (isPrimary) SetPrimaryPlateUnsafe(personId, null, transaction);
            using SqliteCommand command = CreateCommand("INSERT INTO PersonPlates(PlateId,PersonId,PlateText,NormalizedText,IsPrimary,Notes,CreatedAtUtc,UpdatedAtUtc) VALUES($id,$person,$text,$normalized,$primary,$notes,$created,$updated);", transaction);
            Add(command, "$id", plate.Id); Add(command, "$person", personId); Add(command, "$text", display); Add(command, "$normalized", normalized);
            Add(command, "$primary", isPrimary ? 1 : 0); Add(command, "$notes", plate.Notes); Add(command, "$created", ToText(now)); Add(command, "$updated", ToText(now));
            command.ExecuteNonQuery(); transaction.Commit(); return plate;
        }
    }

    public bool RemovePlate(string plateId)
    {
        lock (_gate)
        {
            using SqliteCommand command = CreateCommand("DELETE FROM PersonPlates WHERE PlateId=$id;");
            Add(command, "$id", plateId); return command.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<IdentityFaceSampleRecord> GetFaceSamples(bool includeImages = false, string? personId = null)
    {
        lock (_gate)
        {
            using SqliteCommand command = CreateCommand(personId is null
                ? "SELECT SampleId,PersonId,PersonNumber,PersonName,SampleNumber,OriginalFileName,FileExtension,CreatedAtUtc,DetectionConfidence,FaceImage,Embedding FROM FaceSamples ORDER BY PersonId,SampleNumber;"
                : "SELECT SampleId,PersonId,PersonNumber,PersonName,SampleNumber,OriginalFileName,FileExtension,CreatedAtUtc,DetectionConfidence,FaceImage,Embedding FROM FaceSamples WHERE PersonId=$person ORDER BY SampleNumber;");
            if (personId is not null) Add(command, "$person", personId);
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<IdentityFaceSampleRecord>();
            while (reader.Read()) result.Add(new IdentityFaceSampleRecord
            {
                Id = reader.GetString(0), PersonId = reader.GetString(1), PersonNumber = reader.GetInt32(2), PersonName = reader.GetString(3), SampleNumber = reader.GetInt32(4),
                OriginalFileName = reader.GetString(5), FileExtension = reader.GetString(6), CreatedAtUtc = ParseTime(reader.GetString(7)), DetectionConfidence = reader.GetFloat(8),
                FaceImage = includeImages ? (byte[])reader[9] : [], Embedding = BytesToEmbedding((byte[])reader[10])
            });
            return result;
        }
    }

    public byte[] GetFaceImage(string sampleId) => GetFaceSamples(true).FirstOrDefault(item => item.Id == sampleId)?.FaceImage ?? [];

    public IReadOnlyList<IdentityPalmSampleRecord> GetPalmSamples(string? personId = null)
    {
        lock (_gate)
        {
            using SqliteCommand command = CreateCommand(personId is null
                ? "SELECT SampleId,PersonId,PersonNumber,PersonName,SampleNumber,OriginalFileName,CreatedAtUtc,DetectionConfidence,PalmImage,Embedding FROM PalmSamples ORDER BY PersonId,SampleNumber;"
                : "SELECT SampleId,PersonId,PersonNumber,PersonName,SampleNumber,OriginalFileName,CreatedAtUtc,DetectionConfidence,PalmImage,Embedding FROM PalmSamples WHERE PersonId=$person ORDER BY SampleNumber;");
            if (personId is not null) Add(command, "$person", personId);
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<IdentityPalmSampleRecord>();
            while (reader.Read()) result.Add(new IdentityPalmSampleRecord
            {
                Id = reader.GetString(0), PersonId = reader.GetString(1), PersonNumber = reader.GetInt32(2), PersonName = reader.GetString(3), SampleNumber = reader.GetInt32(4),
                OriginalFileName = reader.GetString(5), CreatedAtUtc = ParseTime(reader.GetString(6)), DetectionConfidence = reader.GetFloat(7), PalmImage = (byte[])reader[8], Embedding = BytesToEmbedding((byte[])reader[9])
            });
            return result;
        }
    }

    public IdentityFaceSampleRecord RegisterFaceSample(string name, IReadOnlyList<float> embedding, byte[] image, string originalFileName,
        string? personId = null, float detectionConfidence = 0, DateTime? createdAtUtc = null, bool isUnknown = false)
    {
        IdentityPersonRecord person = GetOrCreatePerson(name, personId, isUnknown);
        lock (_gate)
        {
            EnsureCapacityUnsafe(person.Id, "FaceSamples", person.Name);
            DateTime created = (createdAtUtc ?? DateTime.UtcNow).ToUniversalTime();
            IdentityFaceSampleRecord sample = new()
            {
                Id = Guid.NewGuid().ToString("N"), PersonId = person.Id, PersonNumber = person.PersonNumber, PersonName = person.Name,
                SampleNumber = NextSampleNumberUnsafe("FaceSamples", person.Id), OriginalFileName = Path.GetFileName(originalFileName),
                FileExtension = NormalizeExtension(originalFileName), CreatedAtUtc = created, DetectionConfidence = detectionConfidence,
                FaceImage = image.ToArray(), Embedding = embedding.ToArray()
            };
            using SqliteTransaction transaction = _connection.BeginTransaction();
            using SqliteCommand command = CreateCommand("INSERT INTO FaceSamples(SampleId,PersonId,PersonNumber,PersonName,SampleNumber,OriginalFileName,FileExtension,CreatedAtUtc,DetectionConfidence,FaceImage,Embedding) VALUES($id,$person,$personNumber,$personName,$number,$file,$extension,$created,$confidence,$image,$embedding);", transaction);
            Add(command, "$id", sample.Id); Add(command, "$person", sample.PersonId); Add(command, "$personNumber", sample.PersonNumber); Add(command, "$personName", sample.PersonName);
            Add(command, "$number", sample.SampleNumber); Add(command, "$file", sample.OriginalFileName); Add(command, "$extension", sample.FileExtension); Add(command, "$created", ToText(created));
            Add(command, "$confidence", detectionConfidence); Add(command, "$image", sample.FaceImage); Add(command, "$embedding", EmbeddingToBytes(sample.Embedding)); command.ExecuteNonQuery();
            TouchPersonUnsafe(person.Id, transaction); transaction.Commit(); return sample;
        }
    }

    public IdentityPalmSampleRecord RegisterPalmSample(string name, IReadOnlyList<float> embedding, byte[] image, string originalFileName,
        string? personId = null, float detectionConfidence = 0, DateTime? createdAtUtc = null)
    {
        IdentityPersonRecord person = GetOrCreatePerson(name, personId, false);
        lock (_gate)
        {
            EnsureCapacityUnsafe(person.Id, "PalmSamples", person.Name);
            DateTime created = (createdAtUtc ?? DateTime.UtcNow).ToUniversalTime();
            IdentityPalmSampleRecord sample = new()
            {
                Id = Guid.NewGuid().ToString("N"), PersonId = person.Id, PersonNumber = person.PersonNumber, PersonName = person.Name,
                SampleNumber = NextSampleNumberUnsafe("PalmSamples", person.Id), OriginalFileName = Path.GetFileName(originalFileName), CreatedAtUtc = created,
                DetectionConfidence = detectionConfidence, PalmImage = image.ToArray(), Embedding = embedding.ToArray()
            };
            using SqliteTransaction transaction = _connection.BeginTransaction();
            using SqliteCommand command = CreateCommand("INSERT INTO PalmSamples(SampleId,PersonId,PersonNumber,PersonName,SampleNumber,OriginalFileName,CreatedAtUtc,DetectionConfidence,PalmImage,Embedding) VALUES($id,$person,$personNumber,$personName,$number,$file,$created,$confidence,$image,$embedding);", transaction);
            Add(command, "$id", sample.Id); Add(command, "$person", sample.PersonId); Add(command, "$personNumber", sample.PersonNumber); Add(command, "$personName", sample.PersonName);
            Add(command, "$number", sample.SampleNumber); Add(command, "$file", sample.OriginalFileName); Add(command, "$created", ToText(created)); Add(command, "$confidence", detectionConfidence);
            Add(command, "$image", sample.PalmImage); Add(command, "$embedding", EmbeddingToBytes(sample.Embedding)); command.ExecuteNonQuery();
            TouchPersonUnsafe(person.Id, transaction); transaction.Commit(); return sample;
        }
    }

    public IdentityMatch? IdentifyFace(IReadOnlyList<float> embedding, float minimumSimilarity)
    {
        lock (_gate)
        {
            IdentityMatch? best = null;
            foreach (IdentityFaceSampleRecord sample in GetFaceSamples())
            {
                IdentityPersonRecord? person = FindPersonUnsafe(sample.PersonId);
                if (person is null || person.IsUnknown || person.Name.StartsWith("Unknown #", StringComparison.OrdinalIgnoreCase)) continue;
                float score = Cosine(embedding, sample.Embedding);
                if (best is null || score > best.Similarity) best = new IdentityMatch(person.Id, person.Name, score, person.PersonNumber, person.IsUnknown, sample.Id);
            }
            return best is not null && best.Similarity >= minimumSimilarity ? best : null;
        }
    }

    public IdentityMatch IdentifyOrCreateUnknown(IReadOnlyList<float> embedding, float minimumSimilarity, float unknownSimilarity,
        byte[]? image, string fileName, float detectionConfidence)
    {
        lock (_gate)
        {
            IdentityMatch? known = IdentifyFace(embedding, minimumSimilarity);
            if (known is not null) return known;
            IdentityMatch? bestUnknown = null;
            foreach (IdentityFaceSampleRecord sample in GetFaceSamples())
            {
                IdentityPersonRecord? person = FindPersonUnsafe(sample.PersonId);
                if (person is null || (!person.IsUnknown && !person.Name.StartsWith("Unknown #", StringComparison.OrdinalIgnoreCase))) continue;
                float score = Cosine(embedding, sample.Embedding);
                if (bestUnknown is null || score > bestUnknown.Similarity) bestUnknown = new IdentityMatch(person.Id, person.Name, score, person.PersonNumber, true, sample.Id);
            }
            if (bestUnknown is not null && bestUnknown.Similarity >= unknownSimilarity)
            {
                IdentityPersonRecord person = FindPersonUnsafe(bestUnknown.PersonId)!;
                IdentityFaceSampleRecord[] samples = GetFaceSamples(false, person.Id).ToArray();
                if (image is { Length: > 0 } && samples.Length < MaxSamplesPerPerson && (samples.Length == 0 || DateTime.UtcNow - samples.Max(item => item.CreatedAtUtc) >= TimeSpan.FromSeconds(10)))
                    RegisterFaceSample(person.Name, embedding, image, fileName, person.Id, detectionConfidence, DateTime.UtcNow, true);
                return bestUnknown;
            }
            int number = NextPersonNumberUnsafe();
            IdentityPersonRecord created = CreatePerson($"Unknown #{number:0000}", true);
            IdentityFaceSampleRecord added = RegisterFaceSample(created.Name, embedding, image ?? new byte[] { 1 }, fileName, created.Id, detectionConfidence, DateTime.UtcNow, true);
            return new IdentityMatch(created.Id, created.Name, 1f, created.PersonNumber, true, added.Id);
        }
    }

    public IdentityMatch? IdentifyPalm(IReadOnlyList<float> embedding, float minimumSimilarity)
    {
        lock (_gate)
        {
            return FindBestPalmMatchUnsafe(embedding, minimumSimilarity, includeUnknown: true);
        }
    }

    public IdentityMatch IdentifyOrCreateUnknownPalm(IReadOnlyList<float> embedding, float minimumSimilarity,
        float unknownSimilarity, byte[]? image, string fileName, float detectionConfidence)
    {
        lock (_gate)
        {
            IdentityMatch? known = FindBestPalmMatchUnsafe(embedding, minimumSimilarity, includeUnknown: false);
            if (known is not null) return known;

            IdentityMatch? bestUnknown = FindBestPalmMatchUnsafe(embedding, unknownSimilarity, includeUnknown: true, onlyUnknown: true);
            if (bestUnknown is not null)
            {
                IdentityPersonRecord person = FindPersonUnsafe(bestUnknown.PersonId)!;
                IdentityPalmSampleRecord[] samples = GetPalmSamples(person.Id).ToArray();
                if (image is { Length: > 0 } && samples.Length < MaxSamplesPerPerson &&
                    (samples.Length == 0 || DateTime.UtcNow - samples.Max(item => item.CreatedAtUtc) >= TimeSpan.FromSeconds(10)))
                    RegisterPalmSample(person.Name, embedding, image, fileName, person.Id, detectionConfidence, DateTime.UtcNow);
                return bestUnknown;
            }

            int number = NextPersonNumberUnsafe();
            IdentityPersonRecord created = CreatePerson($"Unknown Palm #{number:0000}", true);
            IdentityPalmSampleRecord added = RegisterPalmSample(
                created.Name, embedding, image is { Length: > 0 } ? image : new byte[] { 1 },
                fileName, created.Id, detectionConfidence, DateTime.UtcNow);
            return new IdentityMatch(created.Id, created.Name, 1f, created.PersonNumber, true, added.Id);
        }
    }

    /// <summary>Creates a visible unknown Palm record even when no embedding model is available.</summary>
    public IdentityMatch RegisterUnknownPalmObservation(byte[] palmImage, string fileName, float detectionConfidence,
        string? personId = null)
    {
        if (palmImage.Length == 0) throw new ArgumentException("A palm image is required.", nameof(palmImage));
        lock (_gate)
        {
            IdentityPersonRecord person = personId is null
                ? CreatePerson($"Unknown Palm #{NextPersonNumberUnsafe():0000}", true)
                : FindPersonUnsafe(personId) ?? throw new InvalidOperationException("The unknown Palm person does not exist.");
            IdentityPalmSampleRecord[] samples = GetPalmSamples(person.Id).ToArray();
            IdentityPalmSampleRecord? sample = samples.Length == 0 ||
                DateTime.UtcNow - samples.Max(item => item.CreatedAtUtc) >= TimeSpan.FromSeconds(10)
                ? RegisterPalmSample(person.Name, [], palmImage, fileName, person.Id, detectionConfidence, DateTime.UtcNow)
                : samples.OrderByDescending(item => item.CreatedAtUtc).First();
            return new IdentityMatch(person.Id, person.Name, 0f, person.PersonNumber, true, sample.Id);
        }
    }

    public bool RemoveFaceSample(string sampleId) => RemoveSample("FaceSamples", "SampleId", sampleId);
    public bool RemovePalmSample(string sampleId) => RemoveSample("PalmSamples", "SampleId", sampleId);

    public bool MoveFaceSample(string sampleId, string targetPersonId)
    {
        lock (_gate)
        {
            IdentityPersonRecord? target = FindPersonUnsafe(targetPersonId); IdentityFaceSampleRecord? source = GetFaceSamples(false).FirstOrDefault(item => item.Id == sampleId);
            if (target is null || source is null || GetFaceSamples(false, targetPersonId).Count >= MaxSamplesPerPerson) return false;
            using SqliteCommand command = CreateCommand("UPDATE FaceSamples SET PersonId=$person,PersonNumber=$number,PersonName=$name,SampleNumber=$sample WHERE SampleId=$id;");
            Add(command, "$person", target.Id); Add(command, "$number", target.PersonNumber); Add(command, "$name", target.Name); Add(command, "$sample", NextSampleNumberUnsafe("FaceSamples", target.Id)); Add(command, "$id", sampleId);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool MovePalmSample(string sampleId, string targetPersonId)
    {
        lock (_gate)
        {
            IdentityPersonRecord? target = FindPersonUnsafe(targetPersonId);
            IdentityPalmSampleRecord? source = GetPalmSamples().FirstOrDefault(item => item.Id == sampleId);
            if (target is null || source is null || source.PersonId == target.Id || GetPalmSamples(target.Id).Count >= MaxSamplesPerPerson) return false;
            using SqliteCommand command = CreateCommand("UPDATE PalmSamples SET PersonId=$person,PersonNumber=$number,PersonName=$name,SampleNumber=$sample WHERE SampleId=$id;");
            Add(command, "$person", target.Id); Add(command, "$number", target.PersonNumber); Add(command, "$name", target.Name);
            Add(command, "$sample", NextSampleNumberUnsafe("PalmSamples", target.Id)); Add(command, "$id", sampleId);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool MergePeople(string targetPersonId, string sourcePersonId)
    {
        if (targetPersonId == sourcePersonId) return false;
        lock (_gate)
        {
            IdentityPersonRecord? target = FindPersonUnsafe(targetPersonId); IdentityPersonRecord? source = FindPersonUnsafe(sourcePersonId);
            if (target is null || source is null) return false;
            int faceCount = GetFaceSamples(false, target.Id).Count; int palmCount = GetPalmSamples(target.Id).Count;
            if (faceCount + GetFaceSamples(false, source.Id).Count > MaxSamplesPerPerson || palmCount + GetPalmSamples(source.Id).Count > MaxSamplesPerPerson) throw new InvalidOperationException("Merging would exceed the maximum sample count for the target person.");
            using SqliteTransaction transaction = _connection.BeginTransaction();
            MoveSamplesUnsafe("FaceSamples", source, target, transaction); MoveSamplesUnsafe("PalmSamples", source, target, transaction);
            using SqliteCommand plates = CreateCommand("UPDATE PersonPlates SET PersonId=$target WHERE PersonId=$source;", transaction); Add(plates, "$target", target.Id); Add(plates, "$source", source.Id); plates.ExecuteNonQuery();
            using SqliteCommand delete = CreateCommand("DELETE FROM People WHERE PersonId=$source;", transaction); Add(delete, "$source", source.Id); delete.ExecuteNonQuery(); transaction.Commit(); return true;
        }
    }

    public bool RemoveSample(string table, string key, string id)
    {
        lock (_gate)
        {
            using SqliteCommand command = CreateCommand($"DELETE FROM {table} WHERE {key}=$id;"); Add(command, "$id", id); return command.ExecuteNonQuery() > 0;
        }
    }

    public void Backup(string path)
    {
        string target = Path.GetFullPath(path);
        if (string.Equals(target, DatabasePath, StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? AppContext.BaseDirectory);
        lock (_gate)
        {
            using SqliteCommand command = CreateCommand("VACUUM INTO $path;"); Add(command, "$path", target); command.ExecuteNonQuery();
        }
    }

    private IdentityPersonRecord GetOrCreatePerson(string name, string? personId, bool isUnknown)
    {
        lock (_gate)
        {
            IdentityPersonRecord? existing = personId is null ? GetPeopleUnsafe().FirstOrDefault(item => item.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) : FindPersonUnsafe(personId);
            if (existing is not null) return existing;
            return CreatePerson(name, isUnknown);
        }
    }

    private void EnsureCapacityUnsafe(string personId, string table, string name)
    {
        using SqliteCommand command = CreateCommand($"SELECT COUNT(*) FROM {table} WHERE PersonId=$person;"); Add(command, "$person", personId);
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) >= MaxSamplesPerPerson) throw new InvalidOperationException($"Person '{name}' already has the maximum of {MaxSamplesPerPerson} samples.");
    }

    private int NextSampleNumberUnsafe(string table, string personId)
    {
        using SqliteCommand command = CreateCommand($"SELECT COALESCE(MAX(SampleNumber),0)+1 FROM {table} WHERE PersonId=$person;"); Add(command, "$person", personId); return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void TouchPersonUnsafe(string personId, SqliteTransaction transaction)
    {
        using SqliteCommand update = CreateCommand("UPDATE People SET UpdatedAtUtc=$updated WHERE PersonId=$id;", transaction); Add(update, "$updated", ToText(DateTime.UtcNow)); Add(update, "$id", personId); update.ExecuteNonQuery();
    }

    private void MoveSamplesUnsafe(string table, IdentityPersonRecord source, IdentityPersonRecord target, SqliteTransaction transaction)
    {
        using SqliteCommand command = CreateCommand($"SELECT SampleId FROM {table} WHERE PersonId=$source ORDER BY SampleNumber;", transaction); Add(command, "$source", source.Id);
        List<string> ids = []; using (SqliteDataReader reader = command.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
        int number = NextSampleNumberUnsafe(table, target.Id);
        foreach (string id in ids)
        {
            using SqliteCommand update = CreateCommand($"UPDATE {table} SET PersonId=$target,PersonNumber=$personNumber,PersonName=$name,SampleNumber=$number WHERE SampleId=$id;", transaction);
            Add(update, "$target", target.Id); Add(update, "$personNumber", target.PersonNumber); Add(update, "$name", target.Name); Add(update, "$number", number++); Add(update, "$id", id); update.ExecuteNonQuery();
        }
    }

    private void SetPrimaryPlateUnsafe(string personId, string? exceptPlateId, SqliteTransaction transaction)
    {
        using SqliteCommand command = CreateCommand("UPDATE PersonPlates SET IsPrimary=0 WHERE PersonId=$person AND ($except IS NULL OR PlateId<>$except);", transaction); Add(command, "$person", personId); Add(command, "$except", exceptPlateId ?? (object)DBNull.Value); command.ExecuteNonQuery();
    }

    private List<IdentityPersonRecord> GetPeopleUnsafe()
    {
        using SqliteCommand command = CreateCommand("SELECT PersonId, PersonNumber, Name, IsUnknown, CreatedAtUtc, UpdatedAtUtc FROM People ORDER BY PersonNumber;"); using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<IdentityPersonRecord>(); while (reader.Read()) result.Add(ReadPerson(reader)); return result;
    }

    private IdentityPersonRecord? FindPersonUnsafe(string id) => GetPeopleUnsafe().FirstOrDefault(item => item.Id == id);
    private IdentityMatch? FindBestPalmMatchUnsafe(IReadOnlyList<float> embedding, float minimumSimilarity,
        bool includeUnknown, bool onlyUnknown = false)
    {
        IdentityMatch? best = null;
        foreach (IdentityPalmSampleRecord sample in GetPalmSamples())
        {
            IdentityPersonRecord? person = FindPersonUnsafe(sample.PersonId);
            if (person is null) continue;
            bool unknown = person.IsUnknown || person.Name.StartsWith("Unknown Palm #", StringComparison.OrdinalIgnoreCase);
            if ((!includeUnknown && unknown) || (onlyUnknown && !unknown)) continue;
            float score = Cosine(embedding, sample.Embedding);
            if (best is null || score > best.Similarity)
                best = new IdentityMatch(person.Id, person.Name, score, person.PersonNumber, unknown, sample.Id);
        }
        return best is not null && best.Similarity >= minimumSimilarity ? best : null;
    }
    private int NextPersonNumberUnsafe() => GetPeopleUnsafe().Select(item => item.PersonNumber).DefaultIfEmpty(0).Max() + 1;
    private bool IsEmpty() { using SqliteCommand command = CreateCommand("SELECT (SELECT COUNT(*) FROM People)+(SELECT COUNT(*) FROM FaceSamples)+(SELECT COUNT(*) FROM PalmSamples);"); return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0; }

    private void EnsureSchema()
    {
        using SqliteCommand command = CreateCommand("""
            CREATE TABLE IF NOT EXISTS People(PersonId TEXT PRIMARY KEY, PersonNumber INTEGER NOT NULL UNIQUE, Name TEXT NOT NULL UNIQUE, IsUnknown INTEGER NOT NULL DEFAULT 0, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS FaceSamples(SampleId TEXT PRIMARY KEY, PersonId TEXT NOT NULL, PersonNumber INTEGER NOT NULL, PersonName TEXT NOT NULL, SampleNumber INTEGER NOT NULL, OriginalFileName TEXT NOT NULL, FileExtension TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, DetectionConfidence REAL NOT NULL, FaceImage BLOB NOT NULL, Embedding BLOB NOT NULL, FOREIGN KEY(PersonId) REFERENCES People(PersonId) ON DELETE CASCADE, UNIQUE(PersonId,SampleNumber));
            CREATE TABLE IF NOT EXISTS PalmSamples(SampleId TEXT PRIMARY KEY, PersonId TEXT NOT NULL, PersonNumber INTEGER NOT NULL, PersonName TEXT NOT NULL, SampleNumber INTEGER NOT NULL, OriginalFileName TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, DetectionConfidence REAL NOT NULL, PalmImage BLOB NOT NULL, Embedding BLOB NOT NULL, FOREIGN KEY(PersonId) REFERENCES People(PersonId) ON DELETE CASCADE, UNIQUE(PersonId,SampleNumber));
            CREATE TABLE IF NOT EXISTS PersonPlates(PlateId TEXT PRIMARY KEY, PersonId TEXT NOT NULL, PlateText TEXT NOT NULL, NormalizedText TEXT NOT NULL UNIQUE, IsPrimary INTEGER NOT NULL DEFAULT 0, Notes TEXT NOT NULL DEFAULT '', CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, FOREIGN KEY(PersonId) REFERENCES People(PersonId) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_FaceSamples_PersonId ON FaceSamples(PersonId);
            CREATE INDEX IF NOT EXISTS IX_PalmSamples_PersonId ON PalmSamples(PersonId);
            CREATE INDEX IF NOT EXISTS IX_PersonPlates_PersonId ON PersonPlates(PersonId);
            """); command.ExecuteNonQuery();
    }

    private void ImportLegacyFaceDatabase(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || string.Equals(Path.GetFullPath(path), DatabasePath, StringComparison.OrdinalIgnoreCase)) return;
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString()); source.Open();
        using SqliteCommand people = source.CreateCommand(); people.CommandText = "SELECT PersonId,PersonNumber,Name,IsUnknown,CreatedAtUtc,UpdatedAtUtc FROM People;";
        using SqliteDataReader reader = people.ExecuteReader(); var mapping = new Dictionary<string, string>();
        while (reader.Read()) { IdentityPersonRecord person = ImportPerson(reader.GetString(0), reader.GetString(2), reader.GetInt32(3) != 0); mapping[reader.GetString(0)] = person.Id; }
        reader.Close();
        using SqliteCommand samples = source.CreateCommand(); samples.CommandText = "SELECT PersonId,SampleNumber,OriginalFileName,FileExtension,CreatedAtUtc,DetectionConfidence,FaceImage,Embedding FROM FaceSamples;";
        using SqliteDataReader sampleReader = samples.ExecuteReader();
        while (sampleReader.Read() && mapping.TryGetValue(sampleReader.GetString(0), out string? personId))
        {
            IdentityPersonRecord? person = FindPerson(personId); if (person is null) continue;
            try { RegisterFaceSample(person.Name, BytesToEmbedding((byte[])sampleReader[7]), (byte[])sampleReader[6], sampleReader.GetString(2), person.Id, sampleReader.GetFloat(5), ParseTime(sampleReader.GetString(4)), person.IsUnknown); } catch (InvalidOperationException) { }
        }
    }

    private void ImportLegacyPalmDatabase(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || string.Equals(Path.GetFullPath(path), DatabasePath, StringComparison.OrdinalIgnoreCase)) return;
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString()); source.Open();
        using SqliteCommand people = source.CreateCommand(); people.CommandText = "SELECT PersonId,PersonNumber,Name,CreatedAtUtc,UpdatedAtUtc FROM PalmPeople;";
        using SqliteDataReader reader = people.ExecuteReader(); var mapping = new Dictionary<string, string>();
        while (reader.Read()) { IdentityPersonRecord person = ImportPerson(reader.GetString(0), reader.GetString(2), false); mapping[reader.GetString(0)] = person.Id; }
        reader.Close();
        using SqliteCommand samples = source.CreateCommand(); samples.CommandText = "SELECT PersonId,SampleNumber,OriginalFileName,CreatedAtUtc,DetectionConfidence,PalmImage,Embedding FROM PalmSamples;";
        using SqliteDataReader sampleReader = samples.ExecuteReader();
        while (sampleReader.Read() && mapping.TryGetValue(sampleReader.GetString(0), out string? personId))
        {
            IdentityPersonRecord? person = FindPerson(personId); if (person is null) continue;
            try { RegisterPalmSample(person.Name, BytesToEmbedding((byte[])sampleReader[6]), (byte[])sampleReader[5], sampleReader.GetString(2), person.Id, sampleReader.GetFloat(4), ParseTime(sampleReader.GetString(3))); } catch (InvalidOperationException) { }
        }
    }

    private IdentityPersonRecord ImportPerson(string oldId, string name, bool unknown)
    {
        IdentityPersonRecord? existing = GetPeople().FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return existing ?? CreatePerson(name, unknown);
    }

    private SqliteCommand CreateCommand(string sql, SqliteTransaction? transaction = null) { ThrowIfDisposed(); SqliteCommand command = _connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; return command; }
    private static IdentityPersonRecord ReadPerson(SqliteDataReader reader) => new() { Id = reader.GetString(0), PersonNumber = reader.GetInt32(1), Name = reader.GetString(2), IsUnknown = reader.GetInt32(3) != 0, CreatedAtUtc = ParseTime(reader.GetString(4)), UpdatedAtUtc = ParseTime(reader.GetString(5)) };
    private static string NormalizeName(string value) { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A person name is required.", nameof(value)); return value.Trim(); }
    public static string NormalizePlate(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string NormalizeExtension(string value) => Path.GetExtension(value).ToLowerInvariant() switch { ".jpeg" => ".jpg", ".jpg" => ".jpg", ".png" => ".png", _ => ".jpg" };
    private static string ToText(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime ParseTime(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static byte[] EmbeddingToBytes(IReadOnlyList<float> values) { byte[] bytes = new byte[values.Count * sizeof(float)]; Buffer.BlockCopy(values.ToArray(), 0, bytes, 0, bytes.Length); return bytes; }
    private static float[] BytesToEmbedding(byte[] value) { float[] result = new float[value.Length / sizeof(float)]; Buffer.BlockCopy(value, 0, result, 0, value.Length); return result; }
    private static float Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b) { if (a.Count != b.Count || a.Count == 0) return 0; double dot = 0, aa = 0, bb = 0; for (int i = 0; i < a.Count; i++) { dot += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; } return aa <= 0 || bb <= 0 ? 0 : (float)(dot / Math.Sqrt(aa * bb)); }
    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(IdentityDatabase)); }
    public void Dispose() { if (_disposed) return; _disposed = true; _connection.Dispose(); GC.SuppressFinalize(this); }
}
