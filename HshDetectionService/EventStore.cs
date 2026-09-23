using System.Text.Json;
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
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        EnsureSchema();
    }

    public string DatabasePath { get; }

    public DetectionEventEnvelope Append(DetectionEventEnvelope envelope)
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
            transaction.Commit();
            return envelope;
        }
    }

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
            select.CommandText = $"SELECT EventId FROM DetectionEvents{where};";
            if (from is not null) Add(select, "$from", from);
            if (to is not null) Add(select, "$to", to);
            var eventIds = new List<string>();
            using (SqliteDataReader reader = select.ExecuteReader())
            {
                while (reader.Read()) eventIds.Add(reader.GetString(0));
            }

            if (eventIds.Count > 0)
            {
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
