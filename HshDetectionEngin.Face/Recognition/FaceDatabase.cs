using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

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

    // Compatibility view for older consumers. New code should use Samples.
    public List<float> Embedding
    {
        get => Samples.FirstOrDefault()?.Embedding.ToList() ?? [];
        set
        {
            if (value is { Count: > 0 } && Samples.Count == 0)
                Samples.Add(new FaceSample { PersonId = Id, PersonName = Name, Embedding = value.ToArray() });
        }
    }
}

public sealed record FaceMatch(
    string Id,
    string Name,
    float Similarity,
    int PersonNumber = 0,
    bool IsUnknown = false,
    string? MatchedSampleId = null);
public sealed record FaceSimilarityPair(FaceSample Left, FaceSample Right, float Similarity);

/// <summary>
/// Local SQLite face store. Person metadata, aligned face images and SFace embeddings
/// are all stored in one database file. A person can have at most ten samples.
/// </summary>
public sealed class FaceDatabase : IDisposable
{
    public const int MaxSamplesPerPerson = 10;

    private readonly object _gate = new();
    private readonly List<FaceIdentity> _identities = [];
    private readonly SqliteConnection _connection;
    private bool _disposed;

    private FaceDatabase(string path)
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
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
            pragma.ExecuteNonQuery();
        }
        EnsureSchema();
        LoadMemory();
    }

    public string DatabasePath { get; }

    public IReadOnlyList<FaceIdentity> Identities
    {
        get
        {
            lock (_gate)
                return _identities.Select(CloneIdentity).ToArray();
        }
    }

    public static FaceDatabase Load(string path)
    {
        string databasePath = ResolveDatabasePath(path);
        bool hadDatabase = File.Exists(databasePath);
        var database = new FaceDatabase(databasePath);
        if (!hadDatabase && database.HasNoSamples())
            database.TryMigrateLegacyJson(Path.ChangeExtension(path, ".json"));
        return database;
    }

    public IReadOnlyList<FaceSample> GetSamples(bool includeImages = false)
    {
        lock (_gate)
        {
            var samples = _identities.SelectMany(x => x.Samples).Select(CloneSample).ToList();
            if (includeImages)
            {
                foreach (FaceSample sample in samples)
                    sample.FaceImage = ReadImageUnsafe(sample.Id);
            }
            return samples;
        }
    }

    public byte[] GetFaceImage(string sampleId)
    {
        lock (_gate)
            return ReadImageUnsafe(sampleId);
    }

    public FaceIdentity CreatePerson(string name, bool isUnknown = false)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A person name is required.", nameof(name));
        lock (_gate)
        {
            ThrowIfDisposed();
            string normalizedName = name.Trim();
            if (_identities.Any(item => item.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A person named '{normalizedName}' already exists.");

            int number = _identities.Count == 0 ? 1 : _identities.Max(item => item.PersonNumber) + 1;
            DateTime now = DateTime.UtcNow;
            var person = new FaceIdentity
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = normalizedName,
                IsUnknown = isUnknown,
                PersonNumber = number,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            using SqliteTransaction transaction = _connection.BeginTransaction();
            using SqliteCommand command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO People(PersonId, PersonNumber, Name, IsUnknown, CreatedAtUtc, UpdatedAtUtc) VALUES ($id, $number, $name, $unknown, $created, $updated);";
            Add(command, "$id", person.Id);
            Add(command, "$number", person.PersonNumber);
            Add(command, "$name", person.Name);
            Add(command, "$unknown", person.IsUnknown ? 1 : 0);
            Add(command, "$created", ToText(now));
            Add(command, "$updated", ToText(now));
            command.ExecuteNonQuery();
            transaction.Commit();
            _identities.Add(person);
            return CloneIdentity(person);
        }
    }

    public FaceSample RegisterSample(string name, IReadOnlyList<float> embedding, byte[] faceImage,
        string originalFileName, string? personId = null, float detectionConfidence = 0,
        DateTime? createdAtUtc = null, bool isUnknown = false)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A person name is required.", nameof(name));
        if (embedding.Count == 0) throw new ArgumentException("An embedding is required.", nameof(embedding));
        if (faceImage.Length == 0) throw new ArgumentException("A cropped face image is required.", nameof(faceImage));

        lock (_gate)
        {
            ThrowIfDisposed();
            string normalizedName = name.Trim();
            FaceIdentity? person = personId is null
                ? _identities.FirstOrDefault(x => x.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
                : _identities.FirstOrDefault(x => x.Id == personId);

            if (person is null)
            {
                int personNumber = _identities.Count == 0 ? 1 : _identities.Max(x => x.PersonNumber) + 1;
                DateTime now = (createdAtUtc ?? DateTime.UtcNow).ToUniversalTime();
                person = new FaceIdentity
                {
                    Id = personId ?? Guid.NewGuid().ToString("N"),
                    Name = normalizedName,
                    IsUnknown = isUnknown,
                    PersonNumber = personNumber,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
            }
            else if (person.Samples.Count >= MaxSamplesPerPerson)
            {
                throw new InvalidOperationException($"Person '{person.Name}' already has the maximum of {MaxSamplesPerPerson} samples.");
            }
                else if (!person.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase) && personId is not null)
            {
                throw new InvalidOperationException("The selected person name does not match the existing person.");
            }

            DateTime sampleTime = (createdAtUtc ?? DateTime.UtcNow).ToUniversalTime();
            string extension = NormalizeExtension(originalFileName);
            var sample = new FaceSample
            {
                Id = Guid.NewGuid().ToString("N"),
                PersonId = person.Id,
                PersonNumber = person.PersonNumber,
                PersonName = person.Name,
                SampleNumber = person.Samples.Count == 0 ? 1 : person.Samples.Max(x => x.SampleNumber) + 1,
                OriginalFileName = Path.GetFileName(originalFileName),
                FileExtension = extension,
                CreatedAtUtc = sampleTime,
                DetectionConfidence = detectionConfidence,
                Embedding = embedding.ToArray(),
                FaceImage = faceImage.ToArray()
            };

            using SqliteTransaction transaction = _connection.BeginTransaction();
            if (person.Samples.Count == 0 && !_identities.Contains(person))
            {
                using SqliteCommand insertPerson = _connection.CreateCommand();
                insertPerson.Transaction = transaction;
                insertPerson.CommandText = "INSERT INTO People(PersonId, PersonNumber, Name, IsUnknown, CreatedAtUtc, UpdatedAtUtc) VALUES ($id, $number, $name, $unknown, $created, $updated);";
                Add(insertPerson, "$id", person.Id);
                Add(insertPerson, "$number", person.PersonNumber);
                Add(insertPerson, "$name", person.Name);
                Add(insertPerson, "$unknown", person.IsUnknown ? 1 : 0);
                Add(insertPerson, "$created", ToText(person.CreatedAtUtc));
                Add(insertPerson, "$updated", ToText(person.UpdatedAtUtc));
                insertPerson.ExecuteNonQuery();
            }

            using (SqliteCommand insertSample = _connection.CreateCommand())
            {
                insertSample.Transaction = transaction;
                insertSample.CommandText = """
                    INSERT INTO FaceSamples(SampleId, PersonId, SampleNumber, OriginalFileName, FileExtension,
                        CreatedAtUtc, DetectionConfidence, FaceImage, Embedding)
                    VALUES ($id, $personId, $sampleNumber, $fileName, $extension, $created, $confidence, $image, $embedding);
                    """;
                Add(insertSample, "$id", sample.Id);
                Add(insertSample, "$personId", sample.PersonId);
                Add(insertSample, "$sampleNumber", sample.SampleNumber);
                Add(insertSample, "$fileName", sample.OriginalFileName);
                Add(insertSample, "$extension", sample.FileExtension);
                Add(insertSample, "$created", ToText(sample.CreatedAtUtc));
                Add(insertSample, "$confidence", sample.DetectionConfidence);
                Add(insertSample, "$image", sample.FaceImage);
                Add(insertSample, "$embedding", EmbeddingToBytes(sample.Embedding));
                insertSample.ExecuteNonQuery();
            }

            person.UpdatedAtUtc = DateTime.UtcNow;
            if (_identities.Contains(person))
            {
                using SqliteCommand update = _connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE People SET UpdatedAtUtc = $updated WHERE PersonId = $id;";
                Add(update, "$updated", ToText(person.UpdatedAtUtc));
                Add(update, "$id", person.Id);
                update.ExecuteNonQuery();
            }
            transaction.Commit();

            person.Samples.Add(sample);
            if (!_identities.Contains(person)) _identities.Add(person);
            return CloneSample(sample);
        }
    }

    private FaceSample RegisterSampleUnsafe(FaceIdentity person, IReadOnlyList<float> embedding, byte[] faceImage,
        string originalFileName, float detectionConfidence, DateTime createdAtUtc)
    {
        if (person.Samples.Count >= MaxSamplesPerPerson)
            return person.Samples.OrderByDescending(x => x.CreatedAtUtc).First();

        var sample = new FaceSample
        {
            Id = Guid.NewGuid().ToString("N"), PersonId = person.Id, PersonNumber = person.PersonNumber,
            PersonName = person.Name, SampleNumber = person.Samples.Count == 0 ? 1 : person.Samples.Max(x => x.SampleNumber) + 1,
            OriginalFileName = Path.GetFileName(originalFileName), FileExtension = NormalizeExtension(originalFileName),
            CreatedAtUtc = createdAtUtc.ToUniversalTime(), DetectionConfidence = detectionConfidence,
            Embedding = embedding.ToArray(), FaceImage = faceImage.ToArray()
        };
        using SqliteTransaction transaction = _connection.BeginTransaction();
        using SqliteCommand insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO FaceSamples(SampleId, PersonId, SampleNumber, OriginalFileName, FileExtension, CreatedAtUtc, DetectionConfidence, FaceImage, Embedding) VALUES ($id, $person, $number, $file, $extension, $created, $confidence, $image, $embedding);";
        Add(insert, "$id", sample.Id); Add(insert, "$person", person.Id); Add(insert, "$number", sample.SampleNumber);
        Add(insert, "$file", sample.OriginalFileName); Add(insert, "$extension", sample.FileExtension);
        Add(insert, "$created", ToText(sample.CreatedAtUtc)); Add(insert, "$confidence", sample.DetectionConfidence);
        Add(insert, "$image", sample.FaceImage); Add(insert, "$embedding", EmbeddingToBytes(sample.Embedding));
        insert.ExecuteNonQuery();
        using SqliteCommand update = _connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE People SET UpdatedAtUtc = $updated WHERE PersonId = $person;";
        Add(update, "$updated", ToText(DateTime.UtcNow)); Add(update, "$person", person.Id); update.ExecuteNonQuery();
        transaction.Commit();
        person.Samples.Add(sample);
        person.UpdatedAtUtc = DateTime.UtcNow;
        return CloneSample(sample);
    }

    // Compatibility API. New enrollment should use RegisterSample so every record has an image.
    public void Register(string name, IReadOnlyList<float> embedding)
    {
        RegisterSample(name, embedding, [1], "legacy.jpg");
    }

    public FaceMatch? Identify(IReadOnlyList<float> embedding, float minimumSimilarity = 0.40f)
    {
        lock (_gate)
        {
            FaceIdentity? bestPerson = null;
            FaceSample? bestSample = null;
            float bestScore = float.MinValue;
            foreach (FaceIdentity person in _identities)
            {
                if (person.IsUnknown || person.Name.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (FaceSample sample in person.Samples)
                {
                    float score = Cosine(embedding, sample.Embedding);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPerson = person;
                        bestSample = sample;
                    }
                }
            }
            return bestPerson is not null && bestScore >= minimumSimilarity
                ? new FaceMatch(bestPerson.Id, bestPerson.Name, bestScore, bestPerson.PersonNumber, bestPerson.IsUnknown, bestSample?.Id)
                : null;
        }
    }

    public FaceMatch IdentifyOrCreateUnknown(IReadOnlyList<float> embedding, float minimumSimilarity = 0.40f, float unknownSimilarity = 0.35f,
        byte[]? faceImage = null, string originalFileName = "runtime-face.jpg", float detectionConfidence = 0)
    {
        lock (_gate)
        {
            FaceMatch? known = Identify(embedding, minimumSimilarity);
            if (known is not null) return known;

            FaceIdentity? bestUnknown = null;
            FaceSample? bestUnknownSample = null;
            float bestScore = float.MinValue;
            foreach (FaceIdentity person in _identities.Where(x => x.IsUnknown || x.Name.StartsWith("Unknown #", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (FaceSample sample in person.Samples)
                {
                    float score = Cosine(embedding, sample.Embedding);
                    if (score > bestScore) { bestScore = score; bestUnknown = person; bestUnknownSample = sample; }
                }
            }

            DateTime now = DateTime.UtcNow;
            if (bestUnknown is not null && bestScore >= unknownSimilarity)
            {
                if (faceImage is { Length: > 0 } && bestUnknown.Samples.Count < MaxSamplesPerPerson &&
                    (bestUnknown.Samples.Count == 0 || now - bestUnknown.Samples.Max(x => x.CreatedAtUtc) >= TimeSpan.FromSeconds(10)))
                {
                    RegisterSampleUnsafe(bestUnknown, embedding, faceImage, originalFileName, detectionConfidence, now);
                }
                return new FaceMatch(bestUnknown.Id, bestUnknown.Name, bestScore, bestUnknown.PersonNumber, true, bestUnknownSample?.Id);
            }

            int personNumber = _identities.Count == 0 ? 1 : _identities.Max(x => x.PersonNumber) + 1;
            string personName = $"Unknown #{personNumber:0000}";
            DateTime created = now;
            FaceIdentity createdPerson = new()
            {
                Id = Guid.NewGuid().ToString("N"), Name = personName, IsUnknown = true,
                PersonNumber = personNumber, CreatedAtUtc = created, UpdatedAtUtc = created
            };
            _identities.Add(createdPerson);
            using (SqliteTransaction transaction = _connection.BeginTransaction())
            {
                using SqliteCommand insertPerson = _connection.CreateCommand();
                insertPerson.Transaction = transaction;
                insertPerson.CommandText = "INSERT INTO People(PersonId, PersonNumber, Name, IsUnknown, CreatedAtUtc, UpdatedAtUtc) VALUES ($id, $number, $name, 1, $created, $updated);";
                Add(insertPerson, "$id", createdPerson.Id); Add(insertPerson, "$number", personNumber); Add(insertPerson, "$name", personName);
                Add(insertPerson, "$created", ToText(created)); Add(insertPerson, "$updated", ToText(created)); insertPerson.ExecuteNonQuery();
                transaction.Commit();
            }
            if (faceImage is { Length: > 0 })
                RegisterSampleUnsafe(createdPerson, embedding, faceImage, originalFileName, detectionConfidence, created);
            return new FaceMatch(createdPerson.Id, createdPerson.Name, 1f, createdPerson.PersonNumber, true,
                createdPerson.Samples.LastOrDefault()?.Id);
        }
    }

    public bool Rename(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return false;
        lock (_gate)
        {
            FaceIdentity? person = _identities.FirstOrDefault(x => x.Id == id);
            if (person is null) return false;
            string normalizedName = name.Trim();
            if (_identities.Any(x => x.Id != id && x.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))) return false;
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "UPDATE People SET Name = $name, IsUnknown = 0, UpdatedAtUtc = $updated WHERE PersonId = $id;";
            Add(command, "$name", normalizedName);
            Add(command, "$updated", ToText(DateTime.UtcNow));
            Add(command, "$id", id);
            if (command.ExecuteNonQuery() != 1) return false;
            person.Name = normalizedName;
            person.IsUnknown = false;
            person.UpdatedAtUtc = DateTime.UtcNow;
            foreach (FaceSample sample in person.Samples) sample.PersonName = normalizedName;
            return true;
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            FaceIdentity? person = _identities.FirstOrDefault(x => x.Id == id);
            if (person is null) return false;
            using SqliteTransaction transaction = _connection.BeginTransaction();
            using (SqliteCommand samples = _connection.CreateCommand())
            {
                samples.Transaction = transaction;
                samples.CommandText = "DELETE FROM FaceSamples WHERE PersonId = $id;";
                Add(samples, "$id", id);
                samples.ExecuteNonQuery();
            }
            using SqliteCommand command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM People WHERE PersonId = $id;";
            Add(command, "$id", id);
            if (command.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return false;
            }
            transaction.Commit();
            _identities.Remove(person);
            return true;
        }
    }

    public bool RemoveSample(string sampleId)
    {
        lock (_gate)
        {
            FaceIdentity? person = _identities.FirstOrDefault(x => x.Samples.Any(s => s.Id == sampleId));
            if (person is null) return false;
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM FaceSamples WHERE SampleId = $id;";
            Add(command, "$id", sampleId);
            if (command.ExecuteNonQuery() != 1) return false;
            person.Samples.RemoveAll(x => x.Id == sampleId);
            person.UpdatedAtUtc = DateTime.UtcNow;
            if (person.Samples.Count == 0)
            {
                _identities.Remove(person);
                using SqliteCommand deletePerson = _connection.CreateCommand();
                deletePerson.CommandText = "DELETE FROM People WHERE PersonId = $id;";
                Add(deletePerson, "$id", person.Id);
                deletePerson.ExecuteNonQuery();
            }
            return true;
        }
    }

    /// <summary>Moves one stored sample to another person without re-encoding the image.</summary>
    public bool MoveSample(string sampleId, string targetPersonId)
    {
        lock (_gate)
        {
            FaceIdentity? source = _identities.FirstOrDefault(x => x.Samples.Any(s => s.Id == sampleId));
            FaceIdentity? target = _identities.FirstOrDefault(x => x.Id == targetPersonId);
            if (source is null || target is null || source.Id == target.Id) return false;
            if (target.Samples.Count >= MaxSamplesPerPerson)
                throw new InvalidOperationException($"Person '{target.Name}' already has the maximum of {MaxSamplesPerPerson} samples.");

            FaceSample? sample = source.Samples.FirstOrDefault(x => x.Id == sampleId);
            if (sample is null) return false;
            int sampleNumber = target.Samples.Count == 0 ? 1 : target.Samples.Max(x => x.SampleNumber) + 1;
            DateTime now = DateTime.UtcNow;

            using SqliteTransaction transaction = _connection.BeginTransaction();
            using (SqliteCommand move = _connection.CreateCommand())
            {
                move.Transaction = transaction;
                move.CommandText = "UPDATE FaceSamples SET PersonId = $target, SampleNumber = $number WHERE SampleId = $sample;";
                Add(move, "$target", target.Id);
                Add(move, "$number", sampleNumber);
                Add(move, "$sample", sample.Id);
                if (move.ExecuteNonQuery() != 1) return false;
            }

            using (SqliteCommand updateTarget = _connection.CreateCommand())
            {
                updateTarget.Transaction = transaction;
                updateTarget.CommandText = "UPDATE People SET UpdatedAtUtc = $updated WHERE PersonId = $id;";
                Add(updateTarget, "$updated", ToText(now));
                Add(updateTarget, "$id", target.Id);
                updateTarget.ExecuteNonQuery();
            }

            source.Samples.Remove(sample);
            sample.PersonId = target.Id;
            sample.PersonNumber = target.PersonNumber;
            sample.PersonName = target.Name;
            sample.SampleNumber = sampleNumber;
            sample.CreatedAtUtc = sample.CreatedAtUtc.ToUniversalTime();
            target.Samples.Add(sample);
            source.UpdatedAtUtc = now;
            target.UpdatedAtUtc = now;

            if (source.Samples.Count == 0)
            {
                using SqliteCommand deleteSource = _connection.CreateCommand();
                deleteSource.Transaction = transaction;
                deleteSource.CommandText = "DELETE FROM People WHERE PersonId = $id;";
                Add(deleteSource, "$id", source.Id);
                deleteSource.ExecuteNonQuery();
                _identities.Remove(source);
            }

            transaction.Commit();
            return true;
        }
    }

    public bool MergePeople(string targetPersonId, string sourcePersonId)
    {
        if (targetPersonId == sourcePersonId) return false;
        lock (_gate)
        {
            FaceIdentity? target = _identities.FirstOrDefault(x => x.Id == targetPersonId);
            FaceIdentity? source = _identities.FirstOrDefault(x => x.Id == sourcePersonId);
            if (target is null || source is null) return false;
            if (target.Samples.Count + source.Samples.Count > MaxSamplesPerPerson)
                throw new InvalidOperationException($"Merging these people would exceed the {MaxSamplesPerPerson}-sample limit.");

            using SqliteTransaction transaction = _connection.BeginTransaction();
            int nextSampleNumber = target.Samples.Count == 0 ? 1 : target.Samples.Max(x => x.SampleNumber) + 1;
            foreach (FaceSample sample in source.Samples)
            {
                using SqliteCommand move = _connection.CreateCommand();
                move.Transaction = transaction;
                move.CommandText = "UPDATE FaceSamples SET PersonId = $target, SampleNumber = $sampleNumber WHERE SampleId = $sampleId;";
                Add(move, "$target", target.Id);
                Add(move, "$sampleNumber", nextSampleNumber++);
                Add(move, "$sampleId", sample.Id);
                move.ExecuteNonQuery();
            }
            using SqliteCommand command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM People WHERE PersonId = $source; UPDATE People SET UpdatedAtUtc = $updated WHERE PersonId = $target;";
            Add(command, "$source", source.Id);
            Add(command, "$updated", ToText(DateTime.UtcNow));
            Add(command, "$target", target.Id);
            command.ExecuteNonQuery();
            transaction.Commit();

            foreach (FaceSample sample in source.Samples)
            {
                sample.PersonId = target.Id;
                sample.PersonNumber = target.PersonNumber;
                sample.PersonName = target.Name;
                sample.SampleNumber = target.Samples.Count == 0 ? 1 : target.Samples.Max(x => x.SampleNumber) + 1;
                target.Samples.Add(sample);
            }
            _identities.Remove(source);
            target.UpdatedAtUtc = DateTime.UtcNow;
            return true;
        }
    }

    public IReadOnlyList<FaceSimilarityPair> FindSimilar(float minimumSimilarity, bool onlyDifferentPeople = true)
    {
        lock (_gate)
        {
            var samples = _identities.SelectMany(x => x.Samples).ToArray();
            var pairs = new List<FaceSimilarityPair>();
            for (int i = 0; i < samples.Length; i++)
            {
                for (int j = i + 1; j < samples.Length; j++)
                {
                    if (onlyDifferentPeople && samples[i].PersonId == samples[j].PersonId) continue;
                    float similarity = Cosine(samples[i].Embedding, samples[j].Embedding);
                    if (similarity >= minimumSimilarity)
                    {
                        FaceSample left = CloneSample(samples[i]);
                        FaceSample right = CloneSample(samples[j]);
                        left.FaceImage = ReadImageUnsafe(left.Id);
                        right.FaceImage = ReadImageUnsafe(right.Id);
                        pairs.Add(new FaceSimilarityPair(left, right, similarity));
                    }
                }
            }
            return pairs.OrderByDescending(x => x.Similarity).ToArray();
        }
    }

    /// <summary>
    /// Persists the current database or exports it to the requested path.
    /// A null/empty path means face-database.db beside the application.
    /// </summary>
    public void Save(string? path = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            string targetPath = ResolveDatabasePath(path);
            Checkpoint();
            if (string.Equals(targetPath, DatabasePath, StringComparison.OrdinalIgnoreCase)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory);
            string temporaryPath = Path.Combine(
                Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory,
                $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (SqliteCommand export = _connection.CreateCommand())
                {
                    export.CommandText = "VACUUM INTO $target;";
                    export.Parameters.AddWithValue("$target", temporaryPath);
                    export.ExecuteNonQuery();
                }

                if (File.Exists(targetPath)) File.Replace(temporaryPath, targetPath, null);
                else File.Move(temporaryPath, targetPath);
                TryDeleteSidecar(targetPath + "-wal");
                TryDeleteSidecar(targetPath + "-shm");
            }
            finally
            {
                TryDeleteSidecar(temporaryPath);
            }
        }
    }

    private void Checkpoint()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        command.ExecuteNonQuery();
    }

    private static string ResolveDatabasePath(string? path)
    {
        string requestedPath = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(AppContext.BaseDirectory, "face-database.db")
            : path;
        return Path.GetFullPath(Path.ChangeExtension(requestedPath, ".db"));
    }

    private static void TryDeleteSidecar(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private void EnsureSchema()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS People(
                PersonId TEXT PRIMARY KEY,
                PersonNumber INTEGER NOT NULL UNIQUE,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                IsUnknown INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS FaceSamples(
                SampleId TEXT PRIMARY KEY,
                PersonId TEXT NOT NULL,
                SampleNumber INTEGER NOT NULL,
                OriginalFileName TEXT NOT NULL,
                FileExtension TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                DetectionConfidence REAL NOT NULL,
                FaceImage BLOB NOT NULL,
                Embedding BLOB NOT NULL,
                FOREIGN KEY(PersonId) REFERENCES People(PersonId) ON DELETE CASCADE,
                UNIQUE(PersonId, SampleNumber)
            );
            CREATE INDEX IF NOT EXISTS IX_FaceSamples_PersonId ON FaceSamples(PersonId);
            """;
        command.ExecuteNonQuery();
        try
        {
            using SqliteCommand alter = _connection.CreateCommand();
            alter.CommandText = "ALTER TABLE People ADD COLUMN IsUnknown INTEGER NOT NULL DEFAULT 0;";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // The column already exists on a newly-created or previously migrated database.
        }
    }

    private void LoadMemory()
    {
        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT PersonId, PersonNumber, Name, IsUnknown, CreatedAtUtc, UpdatedAtUtc FROM People ORDER BY PersonNumber;";
            using SqliteDataReader people = command.ExecuteReader();
            while (people.Read())
            {
                _identities.Add(new FaceIdentity
                {
                    Id = people.GetString(0), PersonNumber = people.GetInt32(1), Name = people.GetString(2), IsUnknown = people.GetInt64(3) != 0,
                    CreatedAtUtc = ParseDate(people.GetString(4)), UpdatedAtUtc = ParseDate(people.GetString(5))
                });
            }

            using SqliteCommand samplesCommand = _connection.CreateCommand();
            samplesCommand.CommandText = "SELECT SampleId, PersonId, SampleNumber, OriginalFileName, FileExtension, CreatedAtUtc, DetectionConfidence, Embedding FROM FaceSamples ORDER BY PersonId, SampleNumber;";
            using SqliteDataReader samples = samplesCommand.ExecuteReader();
            while (samples.Read())
            {
                string personId = samples.GetString(1);
                FaceIdentity? person = _identities.FirstOrDefault(x => x.Id == personId);
                if (person is null) continue;
                person.Samples.Add(new FaceSample
                {
                    Id = samples.GetString(0), PersonId = personId, PersonNumber = person.PersonNumber, PersonName = person.Name,
                    SampleNumber = samples.GetInt32(2), OriginalFileName = samples.GetString(3), FileExtension = samples.GetString(4),
                    CreatedAtUtc = ParseDate(samples.GetString(5)), DetectionConfidence = samples.GetFloat(6),
                    Embedding = BytesToEmbedding((byte[])samples[7])
                });
            }
            // Keep people without samples. The service API can create a person
            // first and enroll samples later; removing the row from memory here
            // would make the next restart lose that definition and could cause
            // a PersonNumber UNIQUE conflict on the next enrollment/creation.
        }
    }

    private bool HasNoSamples()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM FaceSamples;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0;
    }

    private void TryMigrateLegacyJson(string jsonPath)
    {
        if (!File.Exists(jsonPath)) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                string name = entry.TryGetProperty("Name", out JsonElement nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(name) || !entry.TryGetProperty("Embedding", out JsonElement embeddingElement)) continue;
                float[] embedding = embeddingElement.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                if (embedding.Length > 0) RegisterLegacySample(name, embedding);
            }
        }
        catch
        {
            // A broken legacy file must not prevent the new database from opening.
        }
    }

    private void RegisterLegacySample(string name, float[] embedding)
    {
        lock (_gate)
        {
            FaceIdentity? person = _identities.FirstOrDefault(x => x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (person is null)
            {
                int number = _identities.Count == 0 ? 1 : _identities.Max(x => x.PersonNumber) + 1;
                DateTime now = DateTime.UtcNow;
                person = new FaceIdentity { Id = Guid.NewGuid().ToString("N"), Name = name.Trim(), IsUnknown = name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase), PersonNumber = number, CreatedAtUtc = now, UpdatedAtUtc = now };
                _identities.Add(person);
                using SqliteCommand insert = _connection.CreateCommand();
                insert.CommandText = "INSERT INTO People(PersonId, PersonNumber, Name, IsUnknown, CreatedAtUtc, UpdatedAtUtc) VALUES ($id, $number, $name, $unknown, $created, $updated);";
                Add(insert, "$id", person.Id); Add(insert, "$number", person.PersonNumber); Add(insert, "$name", person.Name);
                Add(insert, "$unknown", person.IsUnknown ? 1 : 0);
                Add(insert, "$created", ToText(now)); Add(insert, "$updated", ToText(now)); insert.ExecuteNonQuery();
            }
            string sampleId = Guid.NewGuid().ToString("N");
            var sample = new FaceSample { Id = sampleId, PersonId = person.Id, PersonNumber = person.PersonNumber, PersonName = person.Name, SampleNumber = 1, OriginalFileName = "legacy-image-missing.jpg", FileExtension = ".jpg", Embedding = embedding };
            using SqliteCommand insertSample = _connection.CreateCommand();
            insertSample.CommandText = "INSERT INTO FaceSamples(SampleId, PersonId, SampleNumber, OriginalFileName, FileExtension, CreatedAtUtc, DetectionConfidence, FaceImage, Embedding) VALUES ($id, $person, 1, $file, '.jpg', $created, 0, X'', $embedding);";
            Add(insertSample, "$id", sampleId); Add(insertSample, "$person", person.Id); Add(insertSample, "$file", sample.OriginalFileName); Add(insertSample, "$created", ToText(DateTime.UtcNow)); Add(insertSample, "$embedding", EmbeddingToBytes(embedding)); insertSample.ExecuteNonQuery();
            person.Samples.Add(sample);
        }
    }

    private byte[] ReadImageUnsafe(string sampleId)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT FaceImage FROM FaceSamples WHERE SampleId = $id;";
        Add(command, "$id", sampleId);
        object? value = command.ExecuteScalar();
        return value is byte[] bytes ? bytes : [];
    }

    private static FaceIdentity CloneIdentity(FaceIdentity source) => new()
    {
        Id = source.Id, Name = source.Name, IsUnknown = source.IsUnknown, PersonNumber = source.PersonNumber,
        CreatedAtUtc = source.CreatedAtUtc, UpdatedAtUtc = source.UpdatedAtUtc,
        Samples = source.Samples.Select(CloneSample).ToList()
    };

    private static FaceSample CloneSample(FaceSample source) => new()
    {
        Id = source.Id, PersonId = source.PersonId, PersonNumber = source.PersonNumber, PersonName = source.PersonName,
        SampleNumber = source.SampleNumber, OriginalFileName = source.OriginalFileName, FileExtension = source.FileExtension,
        CreatedAtUtc = source.CreatedAtUtc, DetectionConfidence = source.DetectionConfidence,
        FaceImage = source.FaceImage.ToArray(), Embedding = source.Embedding.ToArray()
    };

    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
    private static string ToText(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime ParseDate(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static string NormalizeExtension(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" ? extension : ".jpg";
    }
    private static byte[] EmbeddingToBytes(IReadOnlyList<float> embedding)
    {
        byte[] bytes = new byte[embedding.Count * sizeof(float)];
        Buffer.BlockCopy(embedding.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }
    private static float[] BytesToEmbedding(byte[] bytes)
    {
        if (bytes.Length % sizeof(float) != 0) return [];
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }
    private static float Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count != b.Count || a.Count == 0) return 0;
        double dot = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Count; i++) { dot += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        return aa <= 0 || bb <= 0 ? 0 : (float)(dot / Math.Sqrt(aa * bb));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FaceDatabase));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
