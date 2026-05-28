using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services.Training;

public enum FeedbackRating
{
    ThumbsDown = -1,
    Cleared = 0,
    ThumbsUp = 1
}

public sealed record ChatFeedbackRecord(
    string Id,
    string TurnId,
    string SessionId,
    string PrincipalId,
    FeedbackRating Rating,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IChatFeedbackStore
{
    Task<ChatFeedbackRecord> UpsertAsync(string turnId, string sessionId, string principalId, FeedbackRating rating, string? note, CancellationToken cancellationToken);
    Task<ChatFeedbackRecord?> GetForTurnAsync(string turnId, string principalId, CancellationToken cancellationToken);
    IAsyncEnumerable<ChatFeedbackRecord> EnumerateAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken);
}

public sealed class SqliteChatFeedbackStore : IChatFeedbackStore
{
    private readonly IOptionsMonitor<MemorySmithOptions> _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteChatFeedbackStore(IOptionsMonitor<MemorySmithOptions> options)
    {
        _options = options;
    }

    public async Task<ChatFeedbackRecord> UpsertAsync(string turnId, string sessionId, string principalId, FeedbackRating rating, string? note, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(turnId)) throw new ArgumentException("turnId is required", nameof(turnId));
        if (string.IsNullOrWhiteSpace(principalId)) throw new ArgumentException("principalId is required", nameof(principalId));

        await EnsureSchemaAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var existing = await GetForTurnInternalAsync(connection, turnId, principalId, cancellationToken);
            var now = DateTimeOffset.UtcNow;

            if (existing is null)
            {
                var created = new ChatFeedbackRecord(
                    Guid.NewGuid().ToString("N"),
                    turnId,
                    string.IsNullOrWhiteSpace(sessionId) ? "session-unknown" : sessionId,
                    principalId,
                    rating,
                    note,
                    now,
                    now);

                await using var insert = connection.CreateCommand();
                insert.CommandText = @"
                    INSERT INTO ChatFeedback (Id, TurnId, SessionId, PrincipalId, Rating, Note, CreatedAtUtc, UpdatedAtUtc)
                    VALUES ($id, $turnId, $sessionId, $principalId, $rating, $note, $createdAt, $updatedAt);";
                Bind(insert, created);
                await insert.ExecuteNonQueryAsync(cancellationToken);
                return created;
            }

            var updated = existing with { Rating = rating, Note = note, UpdatedAt = now };
            await using var update = connection.CreateCommand();
            update.CommandText = @"
                UPDATE ChatFeedback
                SET Rating = $rating, Note = $note, UpdatedAtUtc = $updatedAt
                WHERE Id = $id;";
            update.Parameters.AddWithValue("$id", updated.Id);
            update.Parameters.AddWithValue("$rating", (int)updated.Rating);
            update.Parameters.AddWithValue("$note", (object?)updated.Note ?? DBNull.Value);
            update.Parameters.AddWithValue("$updatedAt", updated.UpdatedAt.ToString("O"));
            await update.ExecuteNonQueryAsync(cancellationToken);
            return updated;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<ChatFeedbackRecord?> GetForTurnAsync(string turnId, string principalId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await GetForTurnInternalAsync(connection, turnId, principalId, cancellationToken);
    }

    public async IAsyncEnumerable<ChatFeedbackRecord> EnumerateAsync(DateTimeOffset since, DateTimeOffset until, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, TurnId, SessionId, PrincipalId, Rating, Note, CreatedAtUtc, UpdatedAtUtc
            FROM ChatFeedback
            WHERE CreatedAtUtc >= $since AND CreatedAtUtc < $until
            ORDER BY CreatedAtUtc ASC;";
        command.Parameters.AddWithValue("$since", since.ToString("O"));
        command.Parameters.AddWithValue("$until", until.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return Read(reader);
        }
    }

    private async Task<ChatFeedbackRecord?> GetForTurnInternalAsync(SqliteConnection connection, string turnId, string principalId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, TurnId, SessionId, PrincipalId, Rating, Note, CreatedAtUtc, UpdatedAtUtc
            FROM ChatFeedback
            WHERE TurnId = $turnId AND PrincipalId = $principalId
            LIMIT 1;";
        command.Parameters.AddWithValue("$turnId", turnId);
        command.Parameters.AddWithValue("$principalId", principalId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS ChatFeedback (
                    Id TEXT PRIMARY KEY,
                    TurnId TEXT NOT NULL,
                    SessionId TEXT NOT NULL,
                    PrincipalId TEXT NOT NULL,
                    Rating INTEGER NOT NULL,
                    Note TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    UNIQUE(TurnId, PrincipalId)
                );
                CREATE INDEX IF NOT EXISTS IX_ChatFeedback_CreatedAtUtc ON ChatFeedback(CreatedAtUtc);
                CREATE INDEX IF NOT EXISTS IX_ChatFeedback_SessionId ON ChatFeedback(SessionId);
                CREATE INDEX IF NOT EXISTS IX_ChatFeedback_PrincipalId ON ChatFeedback(PrincipalId);";
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var connectionString = string.IsNullOrWhiteSpace(options.Database.ConnectionString)
            ? "Data Source=../Data/memorysmith.db"
            : options.Database.ConnectionString;
        EnsureDatabaseDirectory(connectionString);
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void EnsureDatabaseDirectory(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void Bind(SqliteCommand command, ChatFeedbackRecord record)
    {
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$turnId", record.TurnId);
        command.Parameters.AddWithValue("$sessionId", record.SessionId);
        command.Parameters.AddWithValue("$principalId", record.PrincipalId);
        command.Parameters.AddWithValue("$rating", (int)record.Rating);
        command.Parameters.AddWithValue("$note", (object?)record.Note ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", record.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", record.UpdatedAt.ToString("O"));
    }

    private static ChatFeedbackRecord Read(IDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        (FeedbackRating)reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind));
}
