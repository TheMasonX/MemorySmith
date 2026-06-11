namespace MemorySmith.App.Services.AgentSessions;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using MemorySmith.Storage;

/// <summary>
/// SQLite-backed agent session store (TSK-0278). Opt in via
/// <c>MemorySmith:AgentSession:PersistSessions=true</c>; sessions then survive server restarts.
/// Rows live in the <c>AgentSessions</c> table of the existing security/audit metadata database
/// (<see cref="IMemorySmithDatabase"/>), created by this store's own idempotent migration
/// following the <c>SchemaMigrations</c> pattern in <c>SqliteMemorySmithDatabase</c>.
///
/// <para><b>Identity-map design.</b> <see cref="AgentSession"/> embeds a SemaphoreSlim that
/// serializes all mutations, so every concurrent caller must observe the <i>same instance</i>
/// for a given session id during the process lifetime (see the lock-identity note in
/// <see cref="AgentSession"/>). This store therefore keeps a live-instance map in front of the
/// durable rows: reads return the cached instance when present and only materialize from SQLite
/// on a cold miss (typically the first touch after a restart). While the process is alive the
/// map is the source of truth; SQLite is the durable copy refreshed on every
/// <see cref="SaveAsync"/>.</para>
///
/// <para><b>Save semantics.</b> Callers must either hold the session lock or be the sole owner
/// of the instance (true for every <see cref="AgentSessionService"/> and
/// <see cref="AgentSessionCleanupService"/> call site), so snapshotting <c>History</c> inside
/// <see cref="SaveAsync"/> is race-free.</para>
/// </summary>
public sealed class SqliteAgentSessionStore : IAgentSessionStore
{
    private const string MigrationId = "20260611_agent_sessions_v1";

    // Web defaults (camelCase) for the JSON columns; symmetric serialize/deserialize so the
    // casing choice only affects how rows look to a human inspecting the database.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS SchemaMigrations (
            MigrationId TEXT PRIMARY KEY,
            AppliedAtUtc TEXT NOT NULL,
            ProductVersion TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS AgentSessions (
            SessionId TEXT NOT NULL PRIMARY KEY,
            PrincipalId TEXT NOT NULL,
            RequestedScope TEXT NOT NULL,
            EffectiveToolNamesJson TEXT NOT NULL,
            ModelOverride TEXT NULL,
            ProviderOverride TEXT NULL,
            CreatedAtUtc TEXT NOT NULL,
            MaxTurns INTEGER NOT NULL,
            TimeoutSeconds INTEGER NOT NULL,
            IdleTimeoutMinutes INTEGER NOT NULL,
            SystemPromptAddendum TEXT NULL,
            ParentSessionId TEXT NULL,
            NestingDepth INTEGER NOT NULL,
            TurnCount INTEGER NOT NULL,
            LastAccessedAtUtc TEXT NOT NULL,
            Status TEXT NOT NULL,
            HistoryJson TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_AgentSessions_Status
            ON AgentSessions(Status);

        CREATE INDEX IF NOT EXISTS IX_AgentSessions_Principal_Status
            ON AgentSessions(PrincipalId, Status);
        """;

    private readonly IMemorySmithDatabase _database;
    private readonly ILogger<SqliteAgentSessionStore> _logger;
    private readonly ConcurrentDictionary<string, AgentSession> _live = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaReady;

    public SqliteAgentSessionStore(IMemorySmithDatabase database, ILogger<SqliteAgentSessionStore> logger)
    {
        _database = database;
        _logger = logger;
    }

    public async Task<AgentSession?> GetAsync(string sessionId, CancellationToken ct)
    {
        if (_live.TryGetValue(sessionId, out var cached))
        {
            return cached;
        }

        await EnsureSchemaAsync(ct);
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM AgentSessions WHERE SessionId = $sessionId;";
        AddParameter(command, "$sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var loaded = ReadSession(reader);
        // GetOrAdd guarantees concurrent cold misses converge on a single instance, preserving
        // the embedded-lock identity contract.
        return _live.GetOrAdd(sessionId, loaded);
    }

    public async Task SaveAsync(AgentSession session, CancellationToken ct)
    {
        // Publish to the identity map first so subsequent GetAsync calls observe this instance.
        _live[session.SessionId] = session;

        // Snapshot mutable state. The caller holds the session lock (or is the sole owner of a
        // just-created session), so these reads are consistent.
        var toolsJson = JsonSerializer.Serialize(session.EffectiveToolNames, JsonOptions);
        var historyJson = JsonSerializer.Serialize(session.History, JsonOptions);
        var status = session.Status.ToString();
        var turnCount = session.TurnCount;
        var lastAccessed = FormatDate(session.LastAccessedAt);

        await EnsureSchemaAsync(ct);
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AgentSessions (
                SessionId, PrincipalId, RequestedScope, EffectiveToolNamesJson,
                ModelOverride, ProviderOverride, CreatedAtUtc, MaxTurns, TimeoutSeconds,
                IdleTimeoutMinutes, SystemPromptAddendum, ParentSessionId, NestingDepth,
                TurnCount, LastAccessedAtUtc, Status, HistoryJson
            ) VALUES (
                $sessionId, $principalId, $requestedScope, $toolsJson,
                $modelOverride, $providerOverride, $createdAtUtc, $maxTurns, $timeoutSeconds,
                $idleTimeoutMinutes, $systemPromptAddendum, $parentSessionId, $nestingDepth,
                $turnCount, $lastAccessedAtUtc, $status, $historyJson
            )
            ON CONFLICT(SessionId) DO UPDATE SET
                TurnCount = excluded.TurnCount,
                LastAccessedAtUtc = excluded.LastAccessedAtUtc,
                Status = excluded.Status,
                HistoryJson = excluded.HistoryJson;
            """;
        AddParameter(command, "$sessionId", session.SessionId);
        AddParameter(command, "$principalId", session.PrincipalId);
        AddParameter(command, "$requestedScope", session.RequestedScope);
        AddParameter(command, "$toolsJson", toolsJson);
        AddParameter(command, "$modelOverride", session.ModelOverride);
        AddParameter(command, "$providerOverride", session.ProviderOverride);
        AddParameter(command, "$createdAtUtc", FormatDate(session.CreatedAt));
        AddParameter(command, "$maxTurns", session.MaxTurns);
        AddParameter(command, "$timeoutSeconds", session.TimeoutSeconds);
        AddParameter(command, "$idleTimeoutMinutes", session.IdleTimeoutMinutes);
        AddParameter(command, "$systemPromptAddendum", session.SystemPromptAddendum);
        AddParameter(command, "$parentSessionId", session.ParentSessionId);
        AddParameter(command, "$nestingDepth", session.NestingDepth);
        AddParameter(command, "$turnCount", turnCount);
        AddParameter(command, "$lastAccessedAtUtc", lastAccessed);
        AddParameter(command, "$status", status);
        AddParameter(command, "$historyJson", historyJson);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        _live.TryRemove(sessionId, out _);

        await EnsureSchemaAsync(ct);
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AgentSessions WHERE SessionId = $sessionId;";
        AddParameter(command, "$sessionId", sessionId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AgentSession>> GetActiveAndIdleAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var results = new List<AgentSession>();
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM AgentSessions WHERE Status IN ('Active', 'Idle');";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sessionId = reader.GetString(reader.GetOrdinal("SessionId"));
            if (_live.TryGetValue(sessionId, out var cached))
            {
                // The live instance is fresher than the row (rows lag until the next SaveAsync),
                // so filter on its current status rather than the persisted one.
                if (cached.Status is AgentSessionStatus.Active or AgentSessionStatus.Idle)
                {
                    results.Add(cached);
                }

                continue;
            }

            var loaded = ReadSession(reader);
            results.Add(_live.GetOrAdd(sessionId, loaded));
        }

        return results;
    }

    public async Task<int> GetActiveCountForPrincipalAsync(string principalId, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM AgentSessions
            WHERE PrincipalId = $principalId AND Status IN ('Active', 'Idle');
            """;
        AddParameter(command, "$principalId", principalId);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    // ── Schema ────────────────────────────────────────────────────────────────

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaReady)
        {
            return;
        }

        await _schemaLock.WaitAsync(ct);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            // Make sure the metadata database itself is initialized (WAL, base migration) before
            // layering the AgentSessions table on top.
            await _database.InitializeAsync(ct);

            await using var connection = await _database.OpenConnectionAsync(ct);
            await using (var schema = connection.CreateCommand())
            {
                schema.CommandText = SchemaSql;
                await schema.ExecuteNonQueryAsync(ct);
            }

            await using (var migration = connection.CreateCommand())
            {
                migration.CommandText = """
                    INSERT OR IGNORE INTO SchemaMigrations (MigrationId, AppliedAtUtc, ProductVersion)
                    VALUES ($migrationId, $appliedAtUtc, $productVersion);
                    """;
                AddParameter(migration, "$migrationId", MigrationId);
                AddParameter(migration, "$appliedAtUtc", FormatDate(DateTimeOffset.UtcNow));
                AddParameter(migration, "$productVersion",
                    typeof(SqliteAgentSessionStore).Assembly.GetName().Version?.ToString() ?? "0.0.0");
                await migration.ExecuteNonQueryAsync(ct);
            }

            _schemaReady = true;
            _logger.LogInformation("Agent session persistence ready (migration {MigrationId})", MigrationId);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    // ── Row mapping ───────────────────────────────────────────────────────────

    private static AgentSession ReadSession(DbDataReader reader)
    {
        var toolsJson = reader.GetString(reader.GetOrdinal("EffectiveToolNamesJson"));
        var historyJson = reader.GetString(reader.GetOrdinal("HistoryJson"));
        var tools = JsonSerializer.Deserialize<List<string>>(toolsJson, JsonOptions) ?? [];
        var history = JsonSerializer.Deserialize<List<ChatMessage>>(historyJson, JsonOptions) ?? [];

        var session = new AgentSession
        {
            SessionId = reader.GetString(reader.GetOrdinal("SessionId")),
            PrincipalId = reader.GetString(reader.GetOrdinal("PrincipalId")),
            RequestedScope = reader.GetString(reader.GetOrdinal("RequestedScope")),
            EffectiveToolNames = tools,
            ModelOverride = GetNullableString(reader, "ModelOverride"),
            ProviderOverride = GetNullableString(reader, "ProviderOverride"),
            CreatedAt = ParseDate(reader.GetString(reader.GetOrdinal("CreatedAtUtc"))),
            MaxTurns = reader.GetInt32(reader.GetOrdinal("MaxTurns")),
            TimeoutSeconds = reader.GetInt32(reader.GetOrdinal("TimeoutSeconds")),
            IdleTimeoutMinutes = reader.GetInt32(reader.GetOrdinal("IdleTimeoutMinutes")),
            SystemPromptAddendum = GetNullableString(reader, "SystemPromptAddendum"),
            ParentSessionId = GetNullableString(reader, "ParentSessionId"),
            NestingDepth = reader.GetInt32(reader.GetOrdinal("NestingDepth")),
        };

        session.RestorePersistedState(
            reader.GetInt32(reader.GetOrdinal("TurnCount")),
            ParseDate(reader.GetString(reader.GetOrdinal("LastAccessedAtUtc"))),
            Enum.Parse<AgentSessionStatus>(reader.GetString(reader.GetOrdinal("Status"))),
            history);

        return session;
    }

    private static string? GetNullableString(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
