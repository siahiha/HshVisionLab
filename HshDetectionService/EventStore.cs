using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace HshDetectionService;

public sealed class EventStore : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public EventStore(ServicePaths paths)
    {
        DatabasePath = paths.EventDatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        _connection.Open();
        using SqliteCommand pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        EnsureSchema();
    }

    public string DatabasePath { get; }

    public DetectionEventEnvelope Append(DetectionEventEnvelope envelope, string? historyKey = null, IReadOnlyList<InvocationDefinition>? invocations = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO DetectionEvents(EventId, EventType, Scenario, OccurredAtUtc, PayloadJson) VALUES ($id, $type, $scenario, $occurred, $payload); SELECT last_insert_rowid();";
            Add(insert, "$id", envelope.EventId);
            Add(insert, "$type", envelope.EventType);
            Add(insert, "$scenario", envelope.Scenario);
            Add(insert, "$occurred", envelope.OccurredAtUtc.ToUniversalTime().ToString("O"));
            Add(insert, "$payload", "{}");
            long sequence = Convert.ToInt64(insert.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            envelope.Sequence = sequence;

            using SqliteCommand update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE DetectionEvents SET PayloadJson = $payload WHERE Sequence = $sequence;";
            Add(update, "$payload", JsonSerializer.Serialize(envelope, ServiceJson.Options));
            Add(update, "$sequence", sequence);
            update.ExecuteNonQuery();

            if (!string.IsNullOrWhiteSpace(historyKey))
            {
                using SqliteCommand detectionHistory = _connection.CreateCommand();
                detectionHistory.Transaction = transaction;
                detectionHistory.CommandText = "INSERT INTO DetectionHistory(EventSequence, HistoryKey, OccurredAtUtc) VALUES ($sequence, $key, $occurred);";
                Add(detectionHistory, "$sequence", sequence);
                Add(detectionHistory, "$key", historyKey);
                Add(detectionHistory, "$occurred", envelope.OccurredAtUtc.ToUniversalTime().ToString("O"));
                detectionHistory.ExecuteNonQuery();
            }

            if (envelope.Trigger["matchingTriggerKeys"] is JsonObject triggerKeys)
            {
                foreach ((string triggerId, JsonNode? keyNode) in triggerKeys)
                {
                    if (keyNode?.GetValue<string>() is not string triggerKey) continue;
                    using SqliteCommand history = _connection.CreateCommand();
                    history.Transaction = transaction;
                    history.CommandText = "INSERT INTO TriggerHistory(EventSequence, TriggerId, TriggerKey, OccurredAtUtc) VALUES ($sequence, $triggerId, $triggerKey, $occurred);";
                    Add(history, "$sequence", sequence);
                    Add(history, "$triggerId", triggerId);
                    Add(history, "$triggerKey", triggerKey);
                    Add(history, "$occurred", envelope.OccurredAtUtc.ToUniversalTime().ToString("O"));
                    history.ExecuteNonQuery();
                }
            }

            if (invocations is not null)
            {
                foreach (InvocationDefinition invocation in invocations.Where(item => item.Enabled))
                {
                    using SqliteCommand job = _connection.CreateCommand();
                    job.Transaction = transaction;
                    job.CommandText = "INSERT OR IGNORE INTO InvocationJobs(EventSequence, EventId, InvocationId, Status, AttemptCount, CreatedAtUtc) VALUES ($sequence, $eventId, $invocationId, 'Pending', 0, $created);";
                    Add(job, "$sequence", sequence);
                    Add(job, "$eventId", envelope.EventId);
                    Add(job, "$invocationId", invocation.Id);
                    Add(job, "$created", DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                    job.ExecuteNonQuery();
                }
            }
            transaction.Commit();
            return envelope;
        }
    }

    public IReadOnlyList<InvocationJobRecord> ReadPendingInvocationJobs(DateTime utcNow, int limit = 50)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            limit = Math.Clamp(limit, 1, 500);
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT JobId, EventSequence, EventId, InvocationId, Status, AttemptCount, NextAttemptUtc, LastError, CreatedAtUtc, UpdatedAtUtc FROM InvocationJobs WHERE Status = 'Pending' AND (NextAttemptUtc IS NULL OR NextAttemptUtc <= $now) ORDER BY JobId LIMIT $limit;";
            Add(command, "$now", utcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            Add(command, "$limit", limit);
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<InvocationJobRecord>();
            while (reader.Read()) result.Add(ReadJob(reader));
            return result;
        }
    }

    public InvocationJobRecord? GetInvocationJob(long jobId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT JobId, EventSequence, EventId, InvocationId, Status, AttemptCount, NextAttemptUtc, LastError, CreatedAtUtc, UpdatedAtUtc FROM InvocationJobs WHERE JobId = $id;";
            Add(command, "$id", jobId);
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadJob(reader) : null;
        }
    }

    public void MarkInvocationJob(long jobId, string status, int attemptCount, DateTime? nextAttemptUtc, string? lastError)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "UPDATE InvocationJobs SET Status = $status, AttemptCount = $attempt, NextAttemptUtc = $next, LastError = $error, UpdatedAtUtc = $updated WHERE JobId = $id;";
            Add(command, "$status", status);
            Add(command, "$attempt", attemptCount);
            Add(command, "$next", nextAttemptUtc?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            Add(command, "$error", lastError ?? (object)DBNull.Value);
            Add(command, "$updated", DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            Add(command, "$id", jobId);
            command.ExecuteNonQuery();
        }
    }

    public void ResetRunningInvocationJobs()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "UPDATE InvocationJobs SET Status = 'Pending', NextAttemptUtc = NULL, UpdatedAtUtc = $updated WHERE Status = 'Running';";
            Add(command, "$updated", DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
    }

    public void AddInvocationLog(InvocationLogRecord log)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO InvocationLogs(JobId, EventSequence, EventId, InvocationId, InvocationName, StepOrder, Status, Attempt, StartedAtUtc, CompletedAtUtc, Method, Target, RequestPayload, ResponseStatusCode, ResponseBody, Error) VALUES ($job, $sequence, $eventId, $invocationId, $name, $step, $status, $attempt, $started, $completed, $method, $target, $payload, $code, $body, $error);";
            Add(command, "$job", log.JobId); Add(command, "$sequence", log.EventSequence); Add(command, "$eventId", log.EventId);
            Add(command, "$invocationId", log.InvocationId); Add(command, "$name", log.InvocationName); Add(command, "$step", log.StepOrder);
            Add(command, "$status", log.Status); Add(command, "$attempt", log.Attempt); Add(command, "$started", log.StartedAtUtc.ToUniversalTime().ToString("O"));
            Add(command, "$completed", log.CompletedAtUtc?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value); Add(command, "$method", log.Method);
            Add(command, "$target", log.Target); Add(command, "$payload", log.RequestPayload ?? (object)DBNull.Value); Add(command, "$code", log.ResponseStatusCode ?? (object)DBNull.Value);
            Add(command, "$body", log.ResponseBody ?? (object)DBNull.Value); Add(command, "$error", log.Error ?? (object)DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<InvocationLogRecord> ReadInvocationLogs(int limit = 200, string? invocationId = null, string? status = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            limit = Math.Clamp(limit, 1, 1000);
            var predicates = new List<string>();
            if (!string.IsNullOrWhiteSpace(invocationId)) predicates.Add("l.InvocationId = $invocationId");
            if (!string.IsNullOrWhiteSpace(status)) predicates.Add("l.Status = $status");
            string where = predicates.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", predicates);
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = $"SELECT l.LogId, l.JobId, l.EventSequence, l.EventId, l.InvocationId, l.InvocationName, l.StepOrder, l.Status, l.Attempt, l.StartedAtUtc, l.CompletedAtUtc, l.Method, l.Target, l.RequestPayload, l.ResponseStatusCode, l.ResponseBody, l.Error, e.OccurredAtUtc FROM InvocationLogs l LEFT JOIN DetectionEvents e ON e.Sequence = l.EventSequence{where} ORDER BY l.LogId DESC LIMIT $limit;";
            if (!string.IsNullOrWhiteSpace(invocationId)) Add(command, "$invocationId", invocationId!);
            if (!string.IsNullOrWhiteSpace(status)) Add(command, "$status", status!);
            Add(command, "$limit", limit);
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<InvocationLogRecord>();
            while (reader.Read()) result.Add(ReadLog(reader));
            return result;
        }
    }

    public string? GetInvocationJobStatus(long eventSequence, string invocationId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT Status FROM InvocationJobs WHERE EventSequence = $sequence AND InvocationId = $invocationId LIMIT 1;";
            Add(command, "$sequence", eventSequence); Add(command, "$invocationId", invocationId);
            return command.ExecuteScalar() as string;
        }
    }

    private static InvocationJobRecord ReadJob(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
        ParseDate(reader.IsDBNull(6) ? null : reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetString(7),
        ParseDate(reader.GetString(8)) ?? DateTime.UtcNow, ParseDate(reader.IsDBNull(9) ? null : reader.GetString(9)));

    private static InvocationLogRecord ReadLog(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
        reader.GetInt32(6), reader.GetString(7), reader.GetInt32(8), ParseDate(reader.GetString(9)) ?? DateTime.UtcNow,
        ParseDate(reader.IsDBNull(10) ? null : reader.GetString(10)), reader.GetString(11), reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetInt32(14),
        reader.IsDBNull(15) ? null : reader.GetString(15), reader.IsDBNull(16) ? null : reader.GetString(16),
        ParseDate(reader.IsDBNull(17) ? null : reader.GetString(17)));

    private static DateTime? ParseDate(string? value) => DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed) ? parsed : null;

    public IReadOnlyList<DetectionEventEnvelope> ReadAfter(long afterSequence, int limit, long? maximumSequence = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            limit = Math.Clamp(limit, 1, 2000);
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = maximumSequence is null
                ? "SELECT Sequence, PayloadJson FROM DetectionEvents WHERE Sequence > $after ORDER BY Sequence LIMIT $limit;"
                : "SELECT Sequence, PayloadJson FROM DetectionEvents WHERE Sequence > $after AND Sequence <= $maximum ORDER BY Sequence LIMIT $limit;";
            Add(command, "$after", afterSequence);
            Add(command, "$limit", limit);
            if (maximumSequence is not null) Add(command, "$maximum", maximumSequence.Value);
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<DetectionEventEnvelope>();
            while (reader.Read())
            {
                DetectionEventEnvelope? item = JsonSerializer.Deserialize<DetectionEventEnvelope>(reader.GetString(1), ServiceJson.Options);
                if (item is null) continue;
                item.Sequence = reader.GetInt64(0);
                result.Add(item);
            }
            return result;
        }
    }

    public DetectionEventEnvelope? Get(string eventId)
    {
        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT Sequence, PayloadJson FROM DetectionEvents WHERE EventId = $id;";
            Add(command, "$id", eventId);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            DetectionEventEnvelope? item = JsonSerializer.Deserialize<DetectionEventEnvelope>(reader.GetString(1), ServiceJson.Options);
            if (item is not null) item.Sequence = reader.GetInt64(0);
            return item;
        }
    }

    public DetectionEventEnvelope? ReadLatest()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT Sequence, PayloadJson FROM DetectionEvents ORDER BY Sequence DESC LIMIT 1;";
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            DetectionEventEnvelope? item = JsonSerializer.Deserialize<DetectionEventEnvelope>(reader.GetString(1), ServiceJson.Options);
            if (item is not null) item.Sequence = reader.GetInt64(0);
            return item;
        }
    }

    public IReadOnlyList<DetectionEventEnvelope> ReadLatest(int limit)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            limit = Math.Clamp(limit, 1, 2000);
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT Sequence, PayloadJson FROM DetectionEvents ORDER BY Sequence DESC LIMIT $limit;";
            Add(command, "$limit", limit);
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new List<DetectionEventEnvelope>();
            while (reader.Read())
            {
                DetectionEventEnvelope? item = JsonSerializer.Deserialize<DetectionEventEnvelope>(reader.GetString(1), ServiceJson.Options);
                if (item is null) continue;
                item.Sequence = reader.GetInt64(0);
                result.Add(item);
            }
            return result;
        }
    }

    public bool HasRecentTriggerEvent(string triggerId, string triggerKey, DateTime sinceUtc)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM TriggerHistory WHERE TriggerId = $triggerId AND TriggerKey = $triggerKey AND OccurredAtUtc >= $since LIMIT 1);";
            Add(command, "$triggerId", triggerId);
            Add(command, "$triggerKey", triggerKey);
            Add(command, "$since", sinceUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
        }
    }

    public bool HasRecentDetectionEvent(string historyKey, DateTime sinceUtc)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM DetectionHistory WHERE HistoryKey = $key AND OccurredAtUtc >= $since LIMIT 1);";
            Add(command, "$key", historyKey);
            Add(command, "$since", sinceUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
        }
    }

    public IReadOnlyList<string> Delete(DateTime? fromUtc, DateTime? toUtc)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            string? from = fromUtc?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            string? to = toUtc?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            var predicates = new List<string>();
            if (from is not null) predicates.Add("OccurredAtUtc >= $from");
            if (to is not null) predicates.Add("OccurredAtUtc <= $to");
            string where = predicates.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", predicates);

            using SqliteTransaction transaction = _connection.BeginTransaction();
            using SqliteCommand select = _connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = $"SELECT Sequence, EventId FROM DetectionEvents{where};";
            if (from is not null) Add(select, "$from", from);
            if (to is not null) Add(select, "$to", to);
            var eventIds = new List<string>();
            var eventSequences = new List<long>();
            using (SqliteDataReader reader = select.ExecuteReader())
            {
                while (reader.Read())
                {
                    eventSequences.Add(reader.GetInt64(0));
                    eventIds.Add(reader.GetString(1));
                }
            }

            if (eventIds.Count > 0)
            {
                foreach (long eventSequence in eventSequences)
                {
                    using SqliteCommand deleteHistory = _connection.CreateCommand();
                    deleteHistory.Transaction = transaction;
                    deleteHistory.CommandText = "DELETE FROM DetectionHistory WHERE EventSequence = $sequence; DELETE FROM TriggerHistory WHERE EventSequence = $sequence;";
                    Add(deleteHistory, "$sequence", eventSequence);
                    deleteHistory.ExecuteNonQuery();
                }

                using SqliteCommand delete = _connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM DetectionEvents{where};";
                if (from is not null) Add(delete, "$from", from);
                if (to is not null) Add(delete, "$to", to);
                delete.ExecuteNonQuery();
            }

            transaction.Commit();
            return eventIds;
        }
    }

    public long CurrentSequence()
    {
        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(MAX(Sequence), 0) FROM DetectionEvents;";
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public long? OldestSequence()
    {
        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT MIN(Sequence) FROM DetectionEvents;";
            object? value = command.ExecuteScalar();
            return value is null || value == DBNull.Value ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void EnsureSchema()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS DetectionEvents(
                Sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                EventId TEXT NOT NULL UNIQUE,
                EventType TEXT NOT NULL,
                Scenario TEXT NOT NULL,
                OccurredAtUtc TEXT NOT NULL,
                PayloadJson TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_DetectionEvents_EventId ON DetectionEvents(EventId);
            CREATE INDEX IF NOT EXISTS IX_DetectionEvents_OccurredAtUtc ON DetectionEvents(OccurredAtUtc);
            CREATE TABLE IF NOT EXISTS DetectionHistory(
                EventSequence INTEGER PRIMARY KEY,
                HistoryKey TEXT NOT NULL,
                OccurredAtUtc TEXT NOT NULL,
                FOREIGN KEY(EventSequence) REFERENCES DetectionEvents(Sequence) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_DetectionHistory_Lookup ON DetectionHistory(HistoryKey, OccurredAtUtc);
            CREATE TABLE IF NOT EXISTS TriggerHistory(
                EventSequence INTEGER NOT NULL,
                TriggerId TEXT NOT NULL,
                TriggerKey TEXT NOT NULL,
                OccurredAtUtc TEXT NOT NULL,
                PRIMARY KEY(EventSequence, TriggerId),
                FOREIGN KEY(EventSequence) REFERENCES DetectionEvents(Sequence) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_TriggerHistory_Lookup ON TriggerHistory(TriggerId, TriggerKey, OccurredAtUtc);
            CREATE TABLE IF NOT EXISTS InvocationJobs(
                JobId INTEGER PRIMARY KEY AUTOINCREMENT,
                EventSequence INTEGER NOT NULL,
                EventId TEXT NOT NULL,
                InvocationId TEXT NOT NULL,
                Status TEXT NOT NULL,
                AttemptCount INTEGER NOT NULL DEFAULT 0,
                NextAttemptUtc TEXT NULL,
                LastError TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NULL,
                UNIQUE(EventSequence, InvocationId),
                FOREIGN KEY(EventSequence) REFERENCES DetectionEvents(Sequence) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_InvocationJobs_Pending ON InvocationJobs(Status, NextAttemptUtc);
            CREATE TABLE IF NOT EXISTS InvocationLogs(
                LogId INTEGER PRIMARY KEY AUTOINCREMENT,
                JobId INTEGER NOT NULL,
                EventSequence INTEGER NOT NULL,
                EventId TEXT NOT NULL,
                InvocationId TEXT NOT NULL,
                InvocationName TEXT NOT NULL,
                StepOrder INTEGER NOT NULL,
                Status TEXT NOT NULL,
                Attempt INTEGER NOT NULL,
                StartedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL,
                Method TEXT NOT NULL,
                Target TEXT NOT NULL,
                RequestPayload TEXT NULL,
                ResponseStatusCode INTEGER NULL,
                ResponseBody TEXT NULL,
                Error TEXT NULL,
                FOREIGN KEY(JobId) REFERENCES InvocationJobs(JobId) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_InvocationLogs_Lookup ON InvocationLogs(InvocationId, Status, LogId);
            """;
        command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(EventStore)); }

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
