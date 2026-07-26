using System.Data.Common;
using System.Globalization;
using MemorySmith.Core.Models;
using Microsoft.Data.Sqlite;

namespace MemorySmith.Storage;

public sealed class DatabaseProviderFactory : IDatabaseProviderFactory
{
    public IMemorySmithDatabase Create(DatabaseOptions options)
    {
        if (!string.Equals(options.Provider, "SQLite", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Database provider '{options.Provider}' is not supported. The current implementation supports SQLite only.");
        }

        return new SqliteMemorySmithDatabase(options);
    }
}

public sealed class SqliteMemorySmithDatabase :
    IMemorySmithDatabase,
    IMemorySmithUserStore,
    IMemorySmithRoleStore,
    IProviderLinkStore,
    ILoginHistoryStore,
    IAuditLogStore,
    ISettingsStore,
    IVersionHistoryStore,
    ISemanticIndexMetadataStore,
    IApiTokenStore
{
    private static readonly Lazy<IReadOnlyList<SchemaMigration>> MigrationsLazy = new(
        () => new List<SchemaMigration>
        {
            new("20260517_auth_rbac_audit_history_v1", InitialSchemaSql, SeedSql),
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly string _connectionString;
    private readonly bool _applyMigrationsOnStartup;
    private readonly bool _useWal;
    private readonly int _busyTimeoutMilliseconds;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public SqliteMemorySmithDatabase(DatabaseOptions options)
    {
        var connectionString = string.IsNullOrWhiteSpace(options.ConnectionString)
            ? "Data Source=../Data/memorysmith.db"
            : options.ConnectionString;
        _connectionString = ResolveConnectionString(connectionString);
        _applyMigrationsOnStartup = options.ApplyMigrationsOnStartup;
        _useWal = options.UseWal;
        _busyTimeoutMilliseconds = Math.Max(0, options.BusyTimeoutSeconds) * 1000;
        EnsureDatabaseDirectory(_connectionString);
    }

    public string ProviderName => "SQLite";
    public IMemorySmithUserStore Users => this;
    public IMemorySmithRoleStore Roles => this;
    public IProviderLinkStore ProviderLinks => this;
    public ILoginHistoryStore LoginHistory => this;
    public IAuditLogStore AuditLogs => this;
    public ISettingsStore Settings => this;
    public IVersionHistoryStore VersionHistory => this;
    public ISemanticIndexMetadataStore SemanticIndexMetadata => this;
    public IApiTokenStore ApiTokens => this;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
            if (_useWal && !IsInMemoryDatabase(_connectionString))
            {
                await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
            }

            if (_applyMigrationsOnStartup)
            {
                await ApplyPendingMigrationsAsync(connection, cancellationToken);
            }

            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        await OpenSqliteConnectionAsync(cancellationToken);

    public async Task<UserAccount?> GetByIdAsync(string userId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Users WHERE UserId = @userId;";
        Add(command, "@userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    public async Task<UserAccount?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Users WHERE NormalizedEmail = @normalizedEmail;";
        Add(command, "@normalizedEmail", normalizedEmail);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    public async Task<UserAccount?> GetByNormalizedDisplayNameAsync(string normalizedDisplayName, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Users WHERE NormalizedDisplayName = @normalizedDisplayName;";
        Add(command, "@normalizedDisplayName", normalizedDisplayName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    public async Task<PagedResult<UserAccount>> ListAsync(UserQuery query, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var search = query.Search?.Trim();
        var where = string.IsNullOrWhiteSpace(search)
            ? string.Empty
            : "WHERE DisplayName LIKE @search OR Email LIKE @search OR UserId LIKE @search";

        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        var total = await ExecuteScalarLongAsync(connection, $"SELECT COUNT(*) FROM Users {where};", command =>
        {
            if (!string.IsNullOrWhiteSpace(search)) Add(command, "@search", $"%{search}%");
        }, cancellationToken);

        await using var listCommand = connection.CreateCommand();
        listCommand.CommandText = $"SELECT * FROM Users {where} ORDER BY CreatedAtUtc DESC, DisplayName LIMIT @limit OFFSET @offset;";
        if (!string.IsNullOrWhiteSpace(search)) Add(listCommand, "@search", $"%{search}%");
        Add(listCommand, "@limit", pageSize);
        Add(listCommand, "@offset", (page - 1) * pageSize);

        var users = new List<UserAccount>();
        await using var reader = await listCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(ReadUser(reader));
        }

        return new PagedResult<UserAccount> { TotalCount = (int)total, Page = page, PageSize = pageSize, Data = users };
    }

    public async Task CreateAsync(UserAccount user, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Users (UserId, DisplayName, NormalizedDisplayName, Email, NormalizedEmail, IsDisabled, LocalPasswordEnabled, PasswordHash, PasswordHashVersion, SecurityStamp, CreatedAtUtc, UpdatedAtUtc, LastLoginAtUtc)
            VALUES (@userId, @displayName, @normalizedDisplayName, @email, @normalizedEmail, @isDisabled, @localPasswordEnabled, @passwordHash, @passwordHashVersion, @securityStamp, @createdAtUtc, @updatedAtUtc, @lastLoginAtUtc);
            """;
        AddUserParameters(command, user);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(UserAccount user, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Users
            SET DisplayName = @displayName,
                NormalizedDisplayName = @normalizedDisplayName,
                Email = @email,
                NormalizedEmail = @normalizedEmail,
                IsDisabled = @isDisabled,
                LocalPasswordEnabled = @localPasswordEnabled,
                PasswordHash = @passwordHash,
                PasswordHashVersion = @passwordHashVersion,
                SecurityStamp = @securityStamp,
                CreatedAtUtc = @createdAtUtc,
                UpdatedAtUtc = @updatedAtUtc,
                LastLoginAtUtc = @lastLoginAtUtc
            WHERE UserId = @userId;
            """;
        AddUserParameters(command, user);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DisableAsync(string userId, string disabledByUserId, CancellationToken cancellationToken)
    {
        var user = await GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return;
        }

        user.IsDisabled = true;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await UpdateAsync(user, cancellationToken);
    }

    public async Task<bool> HasAnyAdminAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        var count = await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*)
            FROM Users
            INNER JOIN UserRoles ON Users.UserId = UserRoles.UserId
            INNER JOIN Roles ON UserRoles.RoleId = Roles.RoleId
            WHERE Users.IsDisabled = 0 AND Roles.NormalizedName = @normalizedName;
            """, cmd => Add(cmd, "@normalizedName", MemorySmithRoles.Admin.ToUpperInvariant()), cancellationToken);
        return count > 0;
    }

    public async Task<IReadOnlyList<RoleRecord>> ListRolesAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Roles ORDER BY Name;";
        var roles = new List<RoleRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(ReadRole(reader));
        }

        return roles;
    }

    public async Task<IReadOnlyList<RoleRecord>> GetRolesForUserAsync(string userId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Roles.*
            FROM Roles
            INNER JOIN UserRoles ON Roles.RoleId = UserRoles.RoleId
            WHERE UserRoles.UserId = @userId
            ORDER BY Roles.Name;
            """;
        Add(command, "@userId", userId);

        var roles = new List<RoleRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(ReadRole(reader));
        }

        return roles;
    }

    public async Task AssignRoleAsync(string userId, string roleName, string? assignedByUserId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO UserRoles (UserId, RoleId, AssignedAtUtc, AssignedByUserId)
            SELECT @userId, RoleId, @assignedAtUtc, @assignedByUserId
            FROM Roles
            WHERE NormalizedName = @normalizedRole;
            """;
        Add(command, "@userId", userId);
        Add(command, "@assignedAtUtc", FormatDate(DateTime.UtcNow));
        Add(command, "@assignedByUserId", assignedByUserId);
        Add(command, "@normalizedRole", Normalize(roleName));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveRoleAsync(string userId, string roleName, string? removedByUserId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM UserRoles
            WHERE UserId = @userId AND RoleId IN (SELECT RoleId FROM Roles WHERE NormalizedName = @normalizedRole);
            """;
        Add(command, "@userId", userId);
        Add(command, "@normalizedRole", Normalize(roleName));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderLink>> GetLinksForUserAsync(string userId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM UserProviderLinks WHERE UserId = @userId ORDER BY LinkedAtUtc;";
        Add(command, "@userId", userId);
        var links = new List<ProviderLink>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            links.Add(ReadProviderLink(reader));
        }

        return links;
    }

    public async Task<ProviderLink?> GetByProviderSubjectAsync(string providerName, string providerSubject, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM UserProviderLinks WHERE ProviderName = @providerName AND ProviderSubject = @providerSubject;";
        Add(command, "@providerName", providerName);
        Add(command, "@providerSubject", providerSubject);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProviderLink(reader) : null;
    }

    public async Task LinkAsync(ProviderLink link, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO UserProviderLinks (LinkId, UserId, ProviderName, ProviderSubject, ProviderDisplayName, ProviderEmail, ProviderEmailVerified, LinkedAtUtc, LastUsedAtUtc)
            VALUES (@linkId, @userId, @providerName, @providerSubject, @providerDisplayName, @providerEmail, @providerEmailVerified, @linkedAtUtc, @lastUsedAtUtc);
            """;
        Add(command, "@linkId", link.LinkId);
        Add(command, "@userId", link.UserId);
        Add(command, "@providerName", link.ProviderName);
        Add(command, "@providerSubject", link.ProviderSubject);
        Add(command, "@providerDisplayName", link.ProviderDisplayName);
        Add(command, "@providerEmail", link.ProviderEmail);
        Add(command, "@providerEmailVerified", ToInt(link.ProviderEmailVerified));
        Add(command, "@linkedAtUtc", FormatDate(link.LinkedAtUtc));
        Add(command, "@lastUsedAtUtc", FormatNullableDate(link.LastUsedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UnlinkAsync(string linkId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM UserProviderLinks WHERE LinkId = @linkId;";
        Add(command, "@linkId", linkId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetProviderEnabledAsync(string providerName, bool enabled, string? updatedByUserId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Providers
            SET IsEnabled = @enabled, UpdatedAtUtc = @updatedAtUtc, UpdatedByUserId = @updatedByUserId
            WHERE ProviderName = @providerName;
            """;
        Add(command, "@enabled", ToInt(enabled));
        Add(command, "@updatedAtUtc", FormatDate(DateTime.UtcNow));
        Add(command, "@updatedByUserId", updatedByUserId);
        Add(command, "@providerName", providerName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuthProviderRecord>> ListProvidersAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Providers ORDER BY SortOrder, DisplayName;";
        var providers = new List<AuthProviderRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            providers.Add(ReadProvider(reader));
        }

        return providers;
    }

    public async Task RecordAsync(LoginHistoryEntry entry, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO LoginHistory (LoginId, UserId, ProviderName, ProviderSubject, OccurredAtUtc, Succeeded, FailureCode, IpHash, UserAgentHash, RequestId)
            VALUES (@loginId, @userId, @providerName, @providerSubject, @occurredAtUtc, @succeeded, @failureCode, @ipHash, @userAgentHash, @requestId);
            """;
        Add(command, "@loginId", string.IsNullOrWhiteSpace(entry.LoginId) ? Guid.NewGuid().ToString("N") : entry.LoginId);
        Add(command, "@userId", entry.UserId);
        Add(command, "@providerName", entry.ProviderName);
        Add(command, "@providerSubject", entry.ProviderSubject);
        Add(command, "@occurredAtUtc", FormatDate(entry.OccurredAtUtc));
        Add(command, "@succeeded", ToInt(entry.Succeeded));
        Add(command, "@failureCode", entry.FailureCode);
        Add(command, "@ipHash", entry.IpHash);
        Add(command, "@userAgentHash", entry.UserAgentHash);
        Add(command, "@requestId", entry.RequestId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PagedResult<LoginHistoryEntry>> QueryAsync(LoginHistoryQuery query, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var rows = await QueryRowsAsync("LoginHistory", BuildLoginWhere(query), query.Page, query.PageSize, ReadLoginHistory, cancellationToken);
        return rows;
    }

    public async Task AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        entry.AuditId = string.IsNullOrWhiteSpace(entry.AuditId) ? Guid.NewGuid().ToString("N") : entry.AuditId;
        entry.RecordedAtUtc = entry.RecordedAtUtc == default ? DateTime.UtcNow : entry.RecordedAtUtc;

        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AuditMetadata (AuditId, OccurredAtUtc, RecordedAtUtc, ActorUserId, ActorDisplay, ActorKind, AuthScheme, ProviderName, RoleSnapshotJson, Action, TargetKind, TargetId, Outcome, Reason, BeforeHash, AfterHash, DiffRef, RequestId, CorrelationId, IpHash, UserAgentHash, DetailsJson, PreviousAuditHash, AuditHash)
            VALUES (@auditId, @occurredAtUtc, @recordedAtUtc, @actorUserId, @actorDisplay, @actorKind, @authScheme, @providerName, @roleSnapshotJson, @action, @targetKind, @targetId, @outcome, @reason, @beforeHash, @afterHash, @diffRef, @requestId, @correlationId, @ipHash, @userAgentHash, @detailsJson, @previousAuditHash, @auditHash);
            """;
        AddAuditParameters(command, entry);
        await command.ExecuteNonQueryAsync(cancellationToken);
        entry.Sequence = await ExecuteScalarLongAsync(connection, "SELECT Sequence FROM AuditMetadata WHERE AuditId = @auditId;", cmd => Add(cmd, "@auditId", entry.AuditId), cancellationToken);
    }

    public async Task<PagedResult<AuditLogEntry>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var where = BuildAuditWhere(query);
        return await QueryRowsAsync("AuditMetadata", where, query.Page, query.PageSize, ReadAudit, cancellationToken, "Sequence DESC");
    }

    public async Task<AuditLogEntry?> GetAsync(string auditId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM AuditMetadata WHERE AuditId = @auditId;";
        Add(command, "@auditId", auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAudit(reader) : null;
    }

    public async Task<AuditLogEntry?> GetLatestAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM AuditMetadata ORDER BY Sequence DESC LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAudit(reader) : null;
    }

    async Task<AdminSetting?> ISettingsStore.GetAsync(string key, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Settings WHERE Key = @key;";
        Add(command, "@key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSetting(reader) : null;
    }

    public async Task<IReadOnlyList<AdminSetting>> ListAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Settings ORDER BY Key;";
        var settings = new List<AdminSetting>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            settings.Add(ReadSetting(reader));
        }

        return settings;
    }

    public async Task SetAsync(AdminSetting setting, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Settings (Key, ValueJson, ValueHash, UpdatedByUserId, UpdatedAtUtc)
            VALUES (@key, @valueJson, @valueHash, @updatedByUserId, @updatedAtUtc)
            ON CONFLICT(Key) DO UPDATE SET
                ValueJson = excluded.ValueJson,
                ValueHash = excluded.ValueHash,
                UpdatedByUserId = excluded.UpdatedByUserId,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        Add(command, "@key", setting.Key);
        Add(command, "@valueJson", setting.ValueJson);
        Add(command, "@valueHash", setting.ValueHash);
        Add(command, "@updatedByUserId", setting.UpdatedByUserId);
        Add(command, "@updatedAtUtc", FormatDate(setting.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<VersionHistoryEntry> CreateVersionAsync(VersionCreateRequest request, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var latest = await ExecuteScalarLongAsync(connection, """
            SELECT COALESCE(MAX(VersionNumber), 0)
            FROM VersionHistory
            WHERE TargetKind = @targetKind AND TargetId = @targetId;
            """, command =>
        {
            command.Transaction = (SqliteTransaction)transaction;
            Add(command, "@targetKind", request.TargetKind);
            Add(command, "@targetId", request.TargetId);
        }, cancellationToken);

        string? parentVersionId = null;
        if (latest > 0)
        {
            parentVersionId = await ExecuteScalarStringAsync(connection, """
                SELECT VersionId
                FROM VersionHistory
                WHERE TargetKind = @targetKind AND TargetId = @targetId AND VersionNumber = @versionNumber;
                """, command =>
            {
                command.Transaction = (SqliteTransaction)transaction;
                Add(command, "@targetKind", request.TargetKind);
                Add(command, "@targetId", request.TargetId);
                Add(command, "@versionNumber", latest);
            }, cancellationToken);
        }

        var entry = new VersionHistoryEntry
        {
            VersionId = Guid.NewGuid().ToString("N"),
            TargetKind = request.TargetKind,
            TargetId = request.TargetId,
            VersionNumber = checked((int)latest + 1),
            ParentVersionId = parentVersionId,
            Format = request.Format,
            HistoryPath = request.HistoryPath,
            BeforeHash = request.BeforeHash,
            AfterHash = request.AfterHash,
            ByteSize = request.ByteSize,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = request.CreatedByUserId,
            AuditId = request.AuditId,
            RestoreSupported = request.RestoreSupported
        };

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO VersionHistory (VersionId, TargetKind, TargetId, VersionNumber, ParentVersionId, Format, HistoryPath, BeforeHash, AfterHash, ByteSize, CreatedAtUtc, CreatedByUserId, AuditId, RestoreSupported)
            VALUES (@versionId, @targetKind, @targetId, @versionNumber, @parentVersionId, @format, @historyPath, @beforeHash, @afterHash, @byteSize, @createdAtUtc, @createdByUserId, @auditId, @restoreSupported);
            """;
        AddVersionParameters(command, entry);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return entry;
    }

    public async Task<IReadOnlyList<VersionHistoryEntry>> GetHistoryAsync(string targetKind, string targetId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM VersionHistory
            WHERE TargetKind = @targetKind AND TargetId = @targetId
            ORDER BY VersionNumber DESC;
            """;
        Add(command, "@targetKind", targetKind);
        Add(command, "@targetId", targetId);
        var versions = new List<VersionHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(ReadVersion(reader));
        }

        return versions;
    }

    public async Task<VersionHistoryEntry?> GetVersionAsync(string versionId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM VersionHistory WHERE VersionId = @versionId;";
        Add(command, "@versionId", versionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadVersion(reader) : null;
    }

    public async Task UpsertChunkAsync(SemanticIndexMetadata metadata, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SemanticIndexMetadata (MetadataId, CorpusKind, SourceId, ChunkId, SourceContentHash, EmbeddingModelId, TokenizerId, VectorDimensions, IndexPath, IndexedAtUtc, LastBuildId, Status)
            VALUES (@metadataId, @corpusKind, @sourceId, @chunkId, @sourceContentHash, @embeddingModelId, @tokenizerId, @vectorDimensions, @indexPath, @indexedAtUtc, @lastBuildId, @status)
            ON CONFLICT(CorpusKind, SourceId, ChunkId, EmbeddingModelId, TokenizerId) DO UPDATE SET
                SourceContentHash = excluded.SourceContentHash,
                VectorDimensions = excluded.VectorDimensions,
                IndexPath = excluded.IndexPath,
                IndexedAtUtc = excluded.IndexedAtUtc,
                LastBuildId = excluded.LastBuildId,
                Status = excluded.Status;
            """;
        AddSemanticParameters(command, metadata);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SemanticIndexMetadata>> GetBySourceAsync(string corpusKind, string sourceId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM SemanticIndexMetadata WHERE CorpusKind = @corpusKind AND SourceId = @sourceId ORDER BY ChunkId;";
        Add(command, "@corpusKind", corpusKind);
        Add(command, "@sourceId", sourceId);
        var rows = new List<SemanticIndexMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadSemantic(reader));
        }

        return rows;
    }

    public async Task<IReadOnlyList<SemanticIndexMetadata>> GetStaleAsync(string modelId, string tokenizerId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM SemanticIndexMetadata
            WHERE EmbeddingModelId <> @modelId OR TokenizerId <> @tokenizerId OR Status <> 'Ready'
            ORDER BY IndexedAtUtc;
            """;
        Add(command, "@modelId", modelId);
        Add(command, "@tokenizerId", tokenizerId);
        var rows = new List<SemanticIndexMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadSemantic(reader));
        }

        return rows;
    }

    public async Task RecordBuildAsync(IndexBuildRecord build, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO IndexBuilds (BuildId, StartedAtUtc, CompletedAtUtc, RequestedByUserId, Kind, Status, DetailsJson, AuditId)
            VALUES (@buildId, @startedAtUtc, @completedAtUtc, @requestedByUserId, @kind, @status, @detailsJson, @auditId)
            ON CONFLICT(BuildId) DO UPDATE SET
                CompletedAtUtc = excluded.CompletedAtUtc,
                Status = excluded.Status,
                DetailsJson = excluded.DetailsJson,
                AuditId = excluded.AuditId;
            """;
        Add(command, "@buildId", string.IsNullOrWhiteSpace(build.BuildId) ? Guid.NewGuid().ToString("N") : build.BuildId);
        Add(command, "@startedAtUtc", FormatDate(build.StartedAtUtc));
        Add(command, "@completedAtUtc", FormatNullableDate(build.CompletedAtUtc));
        Add(command, "@requestedByUserId", build.RequestedByUserId);
        Add(command, "@kind", build.Kind);
        Add(command, "@status", build.Status);
        Add(command, "@detailsJson", build.DetailsJson);
        Add(command, "@auditId", build.AuditId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ApiTokenRecord?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ApiTokens WHERE TokenHash = @tokenHash;";
        Add(command, "@tokenHash", tokenHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadApiToken(reader) : null;
    }

    public async Task<PagedResult<ApiTokenRecord>> ListAsync(ApiTokenQuery query, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        return await QueryRowsAsync("ApiTokens", BuildApiTokenWhere(query), query.Page, query.PageSize, ReadApiToken, cancellationToken, "CreatedAtUtc DESC");
    }

    public async Task CreateAsync(ApiTokenRecord token, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ApiTokens (TokenId, Name, TokenHash, TokenPrefix, OwnerUserId, ServicePrincipalName, ScopesJson, IsDisabled, CreatedAtUtc, CreatedByUserId, LastUsedAtUtc, ExpiresAtUtc)
            VALUES (@tokenId, @name, @tokenHash, @tokenPrefix, @ownerUserId, @servicePrincipalName, @scopesJson, @isDisabled, @createdAtUtc, @createdByUserId, @lastUsedAtUtc, @expiresAtUtc);
            """;
        AddApiTokenParameters(command, token);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RevokeAsync(string tokenId, string? revokedByUserId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ApiTokens SET IsDisabled = 1 WHERE TokenId = @tokenId;";
        Add(command, "@tokenId", tokenId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordUseAsync(string tokenId, DateTime usedAtUtc, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ApiTokens SET LastUsedAtUtc = @lastUsedAtUtc WHERE TokenId = @tokenId;";
        Add(command, "@lastUsedAtUtc", FormatDate(usedAtUtc));
        Add(command, "@tokenId", tokenId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenSqliteConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        if (_busyTimeoutMilliseconds > 0)
        {
            await ExecuteNonQueryAsync(connection, $"PRAGMA busy_timeout = {_busyTimeoutMilliseconds};", cancellationToken);
        }

        return connection;
    }

    private static async Task ApplyPendingMigrationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Ensure SchemaMigrations tracking table exists first
        await using var createMigTable = connection.CreateCommand();
        createMigTable.CommandText = """
            CREATE TABLE IF NOT EXISTS SchemaMigrations (
                MigrationId TEXT PRIMARY KEY,
                AppliedAtUtc TEXT NOT NULL,
                ProductVersion TEXT NOT NULL
            );
            """;
        await createMigTable.ExecuteNonQueryAsync(cancellationToken);

        // Load already-applied migration IDs
        var applied = new HashSet<string>();
        await using var listCmd = connection.CreateCommand();
        listCmd.CommandText = "SELECT MigrationId FROM SchemaMigrations;";
        await using var reader = await listCmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            applied.Add(reader.GetString(0));
        }

        // Apply unapplied migrations in order
        foreach (var migration in MigrationsLazy.Value)
        {
            if (applied.Contains(migration.Id))
            {
                continue;
            }

            // Run the schema DDL
            await ExecuteNonQueryAsync(connection, migration.SchemaSql, cancellationToken);

            // Run the seed data (if any)
            if (!string.IsNullOrWhiteSpace(migration.SeedSql))
            {
                await ExecuteNonQueryAsync(connection, migration.SeedSql, cancellationToken);
            }

            // Record the migration
            await using var recordCmd = connection.CreateCommand();
            recordCmd.CommandText = """
                INSERT INTO SchemaMigrations (MigrationId, AppliedAtUtc, ProductVersion)
                VALUES (@migrationId, @appliedAtUtc, @productVersion);
                """;
            Add(recordCmd, "@migrationId", migration.Id);
            Add(recordCmd, "@appliedAtUtc", FormatDate(DateTime.UtcNow));
            Add(recordCmd, "@productVersion", typeof(SqliteMemorySmithDatabase).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            await recordCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ExecuteScalarLongAsync(SqliteConnection connection, string sql, Action<SqliteCommand>? configure, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ExecuteScalarStringAsync(SqliteConnection connection, string sql, Action<SqliteCommand>? configure, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private async Task<PagedResult<T>> QueryRowsAsync<T>(string table, SqlWhereClause where, int page, int pageSize, Func<SqliteDataReader, T> read, CancellationToken cancellationToken, string orderBy = "rowid DESC")
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        await using var connection = await OpenSqliteConnectionAsync(cancellationToken);
        var total = await ExecuteScalarLongAsync(connection, $"SELECT COUNT(*) FROM {table} {where.Sql};", where.Apply, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} {where.Sql} ORDER BY {orderBy} LIMIT @limit OFFSET @offset;";
        where.Apply(command);
        Add(command, "@limit", pageSize);
        Add(command, "@offset", (page - 1) * pageSize);
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(read(reader));
        }

        return new PagedResult<T> { TotalCount = (int)total, Page = page, PageSize = pageSize, Data = rows };
    }

    private static SqlWhereClause BuildLoginWhere(LoginHistoryQuery query)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.UserId)) filters.Add("UserId = @userId");
        if (!string.IsNullOrWhiteSpace(query.ProviderName)) filters.Add("ProviderName = @providerName");
        if (query.SinceUtc.HasValue) filters.Add("OccurredAtUtc >= @sinceUtc");
        return new SqlWhereClause(filters, command =>
        {
            if (!string.IsNullOrWhiteSpace(query.UserId)) Add(command, "@userId", query.UserId);
            if (!string.IsNullOrWhiteSpace(query.ProviderName)) Add(command, "@providerName", query.ProviderName);
            if (query.SinceUtc.HasValue) Add(command, "@sinceUtc", FormatDate(query.SinceUtc.Value));
        });
    }

    private static SqlWhereClause BuildAuditWhere(AuditLogQuery query)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.ActorUserId)) filters.Add("ActorUserId = @actorUserId");
        if (!string.IsNullOrWhiteSpace(query.Action)) filters.Add("Action = @action");
        if (!string.IsNullOrWhiteSpace(query.TargetKind)) filters.Add("TargetKind = @targetKind");
        if (!string.IsNullOrWhiteSpace(query.TargetId)) filters.Add("TargetId = @targetId");
        if (!string.IsNullOrWhiteSpace(query.Outcome)) filters.Add("Outcome = @outcome");
        if (query.SinceUtc.HasValue) filters.Add("OccurredAtUtc >= @sinceUtc");
        if (query.UntilUtc.HasValue) filters.Add("OccurredAtUtc <= @untilUtc");
        return new SqlWhereClause(filters, command =>
        {
            if (!string.IsNullOrWhiteSpace(query.ActorUserId)) Add(command, "@actorUserId", query.ActorUserId);
            if (!string.IsNullOrWhiteSpace(query.Action)) Add(command, "@action", query.Action);
            if (!string.IsNullOrWhiteSpace(query.TargetKind)) Add(command, "@targetKind", query.TargetKind);
            if (!string.IsNullOrWhiteSpace(query.TargetId)) Add(command, "@targetId", query.TargetId);
            if (!string.IsNullOrWhiteSpace(query.Outcome)) Add(command, "@outcome", query.Outcome);
            if (query.SinceUtc.HasValue) Add(command, "@sinceUtc", FormatDate(query.SinceUtc.Value));
            if (query.UntilUtc.HasValue) Add(command, "@untilUtc", FormatDate(query.UntilUtc.Value));
        });
    }

    private static SqlWhereClause BuildApiTokenWhere(ApiTokenQuery query)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.OwnerUserId)) filters.Add("OwnerUserId = @ownerUserId");
        if (!query.IncludeDisabled) filters.Add("IsDisabled = 0");
        return new SqlWhereClause(filters, command =>
        {
            if (!string.IsNullOrWhiteSpace(query.OwnerUserId)) Add(command, "@ownerUserId", query.OwnerUserId);
        });
    }

    private static void AddUserParameters(SqliteCommand command, UserAccount user)
    {
        Add(command, "@userId", user.UserId);
        Add(command, "@displayName", user.DisplayName);
        Add(command, "@normalizedDisplayName", user.NormalizedDisplayName);
        Add(command, "@email", user.Email);
        Add(command, "@normalizedEmail", user.NormalizedEmail);
        Add(command, "@isDisabled", ToInt(user.IsDisabled));
        Add(command, "@localPasswordEnabled", ToInt(user.LocalPasswordEnabled));
        Add(command, "@passwordHash", user.PasswordHash);
        Add(command, "@passwordHashVersion", user.PasswordHashVersion);
        Add(command, "@securityStamp", user.SecurityStamp);
        Add(command, "@createdAtUtc", FormatDate(user.CreatedAtUtc));
        Add(command, "@updatedAtUtc", FormatDate(user.UpdatedAtUtc));
        Add(command, "@lastLoginAtUtc", FormatNullableDate(user.LastLoginAtUtc));
    }

    private static void AddAuditParameters(SqliteCommand command, AuditLogEntry entry)
    {
        Add(command, "@auditId", entry.AuditId);
        Add(command, "@occurredAtUtc", FormatDate(entry.OccurredAtUtc));
        Add(command, "@recordedAtUtc", FormatDate(entry.RecordedAtUtc));
        Add(command, "@actorUserId", entry.ActorUserId);
        Add(command, "@actorDisplay", entry.ActorDisplay);
        Add(command, "@actorKind", entry.ActorKind);
        Add(command, "@authScheme", entry.AuthScheme);
        Add(command, "@providerName", entry.ProviderName);
        Add(command, "@roleSnapshotJson", entry.RoleSnapshotJson);
        Add(command, "@action", entry.Action);
        Add(command, "@targetKind", entry.TargetKind);
        Add(command, "@targetId", entry.TargetId);
        Add(command, "@outcome", entry.Outcome);
        Add(command, "@reason", entry.Reason);
        Add(command, "@beforeHash", entry.BeforeHash);
        Add(command, "@afterHash", entry.AfterHash);
        Add(command, "@diffRef", entry.DiffRef);
        Add(command, "@requestId", entry.RequestId);
        Add(command, "@correlationId", entry.CorrelationId);
        Add(command, "@ipHash", entry.IpHash);
        Add(command, "@userAgentHash", entry.UserAgentHash);
        Add(command, "@detailsJson", entry.DetailsJson);
        Add(command, "@previousAuditHash", entry.PreviousAuditHash);
        Add(command, "@auditHash", entry.AuditHash);
    }

    private static void AddVersionParameters(SqliteCommand command, VersionHistoryEntry entry)
    {
        Add(command, "@versionId", entry.VersionId);
        Add(command, "@targetKind", entry.TargetKind);
        Add(command, "@targetId", entry.TargetId);
        Add(command, "@versionNumber", entry.VersionNumber);
        Add(command, "@parentVersionId", entry.ParentVersionId);
        Add(command, "@format", entry.Format);
        Add(command, "@historyPath", entry.HistoryPath);
        Add(command, "@beforeHash", entry.BeforeHash);
        Add(command, "@afterHash", entry.AfterHash);
        Add(command, "@byteSize", entry.ByteSize);
        Add(command, "@createdAtUtc", FormatDate(entry.CreatedAtUtc));
        Add(command, "@createdByUserId", entry.CreatedByUserId);
        Add(command, "@auditId", entry.AuditId);
        Add(command, "@restoreSupported", ToInt(entry.RestoreSupported));
    }

    private static void AddSemanticParameters(SqliteCommand command, SemanticIndexMetadata metadata)
    {
        Add(command, "@metadataId", string.IsNullOrWhiteSpace(metadata.MetadataId) ? Guid.NewGuid().ToString("N") : metadata.MetadataId);
        Add(command, "@corpusKind", metadata.CorpusKind);
        Add(command, "@sourceId", metadata.SourceId);
        Add(command, "@chunkId", metadata.ChunkId);
        Add(command, "@sourceContentHash", metadata.SourceContentHash);
        Add(command, "@embeddingModelId", metadata.EmbeddingModelId);
        Add(command, "@tokenizerId", metadata.TokenizerId);
        Add(command, "@vectorDimensions", metadata.VectorDimensions);
        Add(command, "@indexPath", metadata.IndexPath);
        Add(command, "@indexedAtUtc", FormatDate(metadata.IndexedAtUtc));
        Add(command, "@lastBuildId", metadata.LastBuildId);
        Add(command, "@status", metadata.Status);
    }

    private static void AddApiTokenParameters(SqliteCommand command, ApiTokenRecord token)
    {
        Add(command, "@tokenId", string.IsNullOrWhiteSpace(token.TokenId) ? Guid.NewGuid().ToString("N") : token.TokenId);
        Add(command, "@name", token.Name);
        Add(command, "@tokenHash", token.TokenHash);
        Add(command, "@tokenPrefix", token.TokenPrefix);
        Add(command, "@ownerUserId", token.OwnerUserId);
        Add(command, "@servicePrincipalName", token.ServicePrincipalName);
        Add(command, "@scopesJson", token.ScopesJson);
        Add(command, "@isDisabled", ToInt(token.IsDisabled));
        Add(command, "@createdAtUtc", FormatDate(token.CreatedAtUtc));
        Add(command, "@createdByUserId", token.CreatedByUserId);
        Add(command, "@lastUsedAtUtc", FormatNullableDate(token.LastUsedAtUtc));
        Add(command, "@expiresAtUtc", FormatNullableDate(token.ExpiresAtUtc));
    }

    private static UserAccount ReadUser(SqliteDataReader reader) => new()
    {
        UserId = reader.GetString("UserId"),
        DisplayName = reader.GetString("DisplayName"),
        NormalizedDisplayName = reader.GetString("NormalizedDisplayName"),
        Email = reader.GetNullableString("Email"),
        NormalizedEmail = reader.GetNullableString("NormalizedEmail"),
        IsDisabled = reader.GetBooleanInt("IsDisabled"),
        LocalPasswordEnabled = reader.GetBooleanInt("LocalPasswordEnabled"),
        PasswordHash = reader.GetNullableString("PasswordHash"),
        PasswordHashVersion = reader.GetInt32("PasswordHashVersion"),
        SecurityStamp = reader.GetString("SecurityStamp"),
        CreatedAtUtc = ParseDate(reader.GetString("CreatedAtUtc")),
        UpdatedAtUtc = ParseDate(reader.GetString("UpdatedAtUtc")),
        LastLoginAtUtc = reader.GetNullableDate("LastLoginAtUtc")
    };

    private static RoleRecord ReadRole(SqliteDataReader reader) => new()
    {
        RoleId = reader.GetString("RoleId"),
        Name = reader.GetString("Name"),
        NormalizedName = reader.GetString("NormalizedName"),
        Description = reader.GetNullableString("Description"),
        IsSystem = reader.GetBooleanInt("IsSystem")
    };

    private static AuthProviderRecord ReadProvider(SqliteDataReader reader) => new()
    {
        ProviderName = reader.GetString("ProviderName"),
        DisplayName = reader.GetString("DisplayName"),
        IsEnabled = reader.GetBooleanInt("IsEnabled"),
        SortOrder = reader.GetInt32("SortOrder"),
        UpdatedAtUtc = ParseDate(reader.GetString("UpdatedAtUtc")),
        UpdatedByUserId = reader.GetNullableString("UpdatedByUserId")
    };

    private static ProviderLink ReadProviderLink(SqliteDataReader reader) => new()
    {
        LinkId = reader.GetString("LinkId"),
        UserId = reader.GetString("UserId"),
        ProviderName = reader.GetString("ProviderName"),
        ProviderSubject = reader.GetString("ProviderSubject"),
        ProviderDisplayName = reader.GetNullableString("ProviderDisplayName"),
        ProviderEmail = reader.GetNullableString("ProviderEmail"),
        ProviderEmailVerified = reader.GetNullableBool("ProviderEmailVerified"),
        LinkedAtUtc = ParseDate(reader.GetString("LinkedAtUtc")),
        LastUsedAtUtc = reader.GetNullableDate("LastUsedAtUtc")
    };

    private static LoginHistoryEntry ReadLoginHistory(SqliteDataReader reader) => new()
    {
        LoginId = reader.GetString("LoginId"),
        UserId = reader.GetNullableString("UserId"),
        ProviderName = reader.GetString("ProviderName"),
        ProviderSubject = reader.GetNullableString("ProviderSubject"),
        OccurredAtUtc = ParseDate(reader.GetString("OccurredAtUtc")),
        Succeeded = reader.GetBooleanInt("Succeeded"),
        FailureCode = reader.GetNullableString("FailureCode"),
        IpHash = reader.GetNullableString("IpHash"),
        UserAgentHash = reader.GetNullableString("UserAgentHash"),
        RequestId = reader.GetNullableString("RequestId")
    };

    private static AuditLogEntry ReadAudit(SqliteDataReader reader) => new()
    {
        AuditId = reader.GetString("AuditId"),
        Sequence = reader.GetInt64("Sequence"),
        OccurredAtUtc = ParseDate(reader.GetString("OccurredAtUtc")),
        RecordedAtUtc = ParseDate(reader.GetString("RecordedAtUtc")),
        ActorUserId = reader.GetNullableString("ActorUserId"),
        ActorDisplay = reader.GetNullableString("ActorDisplay"),
        ActorKind = reader.GetString("ActorKind"),
        AuthScheme = reader.GetNullableString("AuthScheme"),
        ProviderName = reader.GetNullableString("ProviderName"),
        RoleSnapshotJson = reader.GetNullableString("RoleSnapshotJson"),
        Action = reader.GetString("Action"),
        TargetKind = reader.GetString("TargetKind"),
        TargetId = reader.GetNullableString("TargetId"),
        Outcome = reader.GetString("Outcome"),
        Reason = reader.GetNullableString("Reason"),
        BeforeHash = reader.GetNullableString("BeforeHash"),
        AfterHash = reader.GetNullableString("AfterHash"),
        DiffRef = reader.GetNullableString("DiffRef"),
        RequestId = reader.GetNullableString("RequestId"),
        CorrelationId = reader.GetNullableString("CorrelationId"),
        IpHash = reader.GetNullableString("IpHash"),
        UserAgentHash = reader.GetNullableString("UserAgentHash"),
        DetailsJson = reader.GetNullableString("DetailsJson"),
        PreviousAuditHash = reader.GetNullableString("PreviousAuditHash"),
        AuditHash = reader.GetString("AuditHash")
    };

    private static AdminSetting ReadSetting(SqliteDataReader reader) => new()
    {
        Key = reader.GetString("Key"),
        ValueJson = reader.GetString("ValueJson"),
        ValueHash = reader.GetString("ValueHash"),
        UpdatedByUserId = reader.GetNullableString("UpdatedByUserId"),
        UpdatedAtUtc = ParseDate(reader.GetString("UpdatedAtUtc"))
    };

    private static VersionHistoryEntry ReadVersion(SqliteDataReader reader) => new()
    {
        VersionId = reader.GetString("VersionId"),
        TargetKind = reader.GetString("TargetKind"),
        TargetId = reader.GetString("TargetId"),
        VersionNumber = reader.GetInt32("VersionNumber"),
        ParentVersionId = reader.GetNullableString("ParentVersionId"),
        Format = reader.GetString("Format"),
        HistoryPath = reader.GetString("HistoryPath"),
        BeforeHash = reader.GetNullableString("BeforeHash"),
        AfterHash = reader.GetString("AfterHash"),
        ByteSize = reader.GetInt64("ByteSize"),
        CreatedAtUtc = ParseDate(reader.GetString("CreatedAtUtc")),
        CreatedByUserId = reader.GetNullableString("CreatedByUserId"),
        AuditId = reader.GetNullableString("AuditId"),
        RestoreSupported = reader.GetBooleanInt("RestoreSupported")
    };

    private static SemanticIndexMetadata ReadSemantic(SqliteDataReader reader) => new()
    {
        MetadataId = reader.GetString("MetadataId"),
        CorpusKind = reader.GetString("CorpusKind"),
        SourceId = reader.GetString("SourceId"),
        ChunkId = reader.GetString("ChunkId"),
        SourceContentHash = reader.GetString("SourceContentHash"),
        EmbeddingModelId = reader.GetString("EmbeddingModelId"),
        TokenizerId = reader.GetString("TokenizerId"),
        VectorDimensions = reader.GetInt32("VectorDimensions"),
        IndexPath = reader.GetNullableString("IndexPath"),
        IndexedAtUtc = ParseDate(reader.GetString("IndexedAtUtc")),
        LastBuildId = reader.GetNullableString("LastBuildId"),
        Status = reader.GetString("Status")
    };

    private static ApiTokenRecord ReadApiToken(SqliteDataReader reader) => new()
    {
        TokenId = reader.GetString("TokenId"),
        Name = reader.GetString("Name"),
        TokenHash = reader.GetString("TokenHash"),
        TokenPrefix = reader.GetString("TokenPrefix"),
        OwnerUserId = reader.GetNullableString("OwnerUserId"),
        ServicePrincipalName = reader.GetNullableString("ServicePrincipalName"),
        ScopesJson = reader.GetString("ScopesJson"),
        IsDisabled = reader.GetBooleanInt("IsDisabled"),
        CreatedAtUtc = ParseDate(reader.GetString("CreatedAtUtc")),
        CreatedByUserId = reader.GetNullableString("CreatedByUserId"),
        LastUsedAtUtc = reader.GetNullableDate("LastUsedAtUtc"),
        ExpiresAtUtc = reader.GetNullableDate("ExpiresAtUtc")
    };

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    private static string FormatDate(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value.ToString("O", CultureInfo.InvariantCulture) : value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? FormatNullableDate(DateTime? value) => value.HasValue ? FormatDate(value.Value) : null;

    private static DateTime ParseDate(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static int ToInt(bool value) => value ? 1 : 0;
    private static int? ToInt(bool? value) => value.HasValue ? ToInt(value.Value) : null;

    private static bool IsInMemoryDatabase(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        return string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveConnectionString(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (!string.IsNullOrWhiteSpace(builder.DataSource) &&
            !string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase) &&
            !Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.GetFullPath(builder.DataSource, AppContext.BaseDirectory);
        }

        return builder.ToString();
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

    private sealed class SqlWhereClause
    {
        private readonly IReadOnlyList<string> _filters;
        private readonly Action<SqliteCommand> _apply;

        public SqlWhereClause(IReadOnlyList<string> filters, Action<SqliteCommand> apply)
        {
            _filters = filters;
            _apply = apply;
        }

        public string Sql => _filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", _filters);
        public void Apply(SqliteCommand command) => _apply(command);
    }

    private static readonly string InitialSchemaSql = """
        CREATE TABLE IF NOT EXISTS SchemaMigrations (
            MigrationId TEXT PRIMARY KEY,
            AppliedAtUtc TEXT NOT NULL,
            ProductVersion TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Settings (
            Key TEXT PRIMARY KEY,
            ValueJson TEXT NOT NULL,
            ValueHash TEXT NOT NULL,
            UpdatedByUserId TEXT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Users (
            UserId TEXT PRIMARY KEY,
            DisplayName TEXT NOT NULL,
            NormalizedDisplayName TEXT NOT NULL,
            Email TEXT NULL,
            NormalizedEmail TEXT NULL,
            IsDisabled INTEGER NOT NULL DEFAULT 0,
            LocalPasswordEnabled INTEGER NOT NULL DEFAULT 0,
            PasswordHash TEXT NULL,
            PasswordHashVersion INTEGER NOT NULL DEFAULT 1,
            SecurityStamp TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL,
            LastLoginAtUtc TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_Users_NormalizedEmail ON Users(NormalizedEmail);
        CREATE UNIQUE INDEX IF NOT EXISTS UX_Users_NormalizedDisplayName ON Users(NormalizedDisplayName);

        CREATE TABLE IF NOT EXISTS Providers (
            ProviderName TEXT PRIMARY KEY,
            DisplayName TEXT NOT NULL,
            IsEnabled INTEGER NOT NULL DEFAULT 0,
            SortOrder INTEGER NOT NULL DEFAULT 0,
            UpdatedAtUtc TEXT NOT NULL,
            UpdatedByUserId TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS Roles (
            RoleId TEXT PRIMARY KEY,
            Name TEXT NOT NULL UNIQUE,
            NormalizedName TEXT NOT NULL UNIQUE,
            Description TEXT NULL,
            IsSystem INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS UserRoles (
            UserId TEXT NOT NULL REFERENCES Users(UserId) ON DELETE CASCADE,
            RoleId TEXT NOT NULL REFERENCES Roles(RoleId) ON DELETE CASCADE,
            AssignedAtUtc TEXT NOT NULL,
            AssignedByUserId TEXT NULL,
            PRIMARY KEY(UserId, RoleId)
        );

        CREATE TABLE IF NOT EXISTS UserProviderLinks (
            LinkId TEXT PRIMARY KEY,
            UserId TEXT NOT NULL REFERENCES Users(UserId) ON DELETE CASCADE,
            ProviderName TEXT NOT NULL REFERENCES Providers(ProviderName),
            ProviderSubject TEXT NOT NULL,
            ProviderDisplayName TEXT NULL,
            ProviderEmail TEXT NULL,
            ProviderEmailVerified INTEGER NULL,
            LinkedAtUtc TEXT NOT NULL,
            LastUsedAtUtc TEXT NULL,
            UNIQUE(ProviderName, ProviderSubject)
        );

        CREATE INDEX IF NOT EXISTS IX_UserProviderLinks_UserId ON UserProviderLinks(UserId);

        CREATE TABLE IF NOT EXISTS LoginHistory (
            LoginId TEXT PRIMARY KEY,
            UserId TEXT NULL REFERENCES Users(UserId) ON DELETE SET NULL,
            ProviderName TEXT NOT NULL,
            ProviderSubject TEXT NULL,
            OccurredAtUtc TEXT NOT NULL,
            Succeeded INTEGER NOT NULL,
            FailureCode TEXT NULL,
            IpHash TEXT NULL,
            UserAgentHash TEXT NULL,
            RequestId TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_LoginHistory_User_Time ON LoginHistory(UserId, OccurredAtUtc);
        CREATE INDEX IF NOT EXISTS IX_LoginHistory_Provider_Time ON LoginHistory(ProviderName, OccurredAtUtc);

        CREATE TABLE IF NOT EXISTS ApiTokens (
            TokenId TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            TokenHash TEXT NOT NULL UNIQUE,
            TokenPrefix TEXT NOT NULL,
            OwnerUserId TEXT NULL REFERENCES Users(UserId) ON DELETE CASCADE,
            ServicePrincipalName TEXT NULL,
            ScopesJson TEXT NOT NULL,
            IsDisabled INTEGER NOT NULL DEFAULT 0,
            CreatedAtUtc TEXT NOT NULL,
            CreatedByUserId TEXT NULL,
            LastUsedAtUtc TEXT NULL,
            ExpiresAtUtc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS AuditMetadata (
            Sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            AuditId TEXT NOT NULL UNIQUE,
            OccurredAtUtc TEXT NOT NULL,
            RecordedAtUtc TEXT NOT NULL,
            ActorUserId TEXT NULL REFERENCES Users(UserId) ON DELETE SET NULL,
            ActorDisplay TEXT NULL,
            ActorKind TEXT NOT NULL,
            AuthScheme TEXT NULL,
            ProviderName TEXT NULL,
            RoleSnapshotJson TEXT NULL,
            Action TEXT NOT NULL,
            TargetKind TEXT NOT NULL,
            TargetId TEXT NULL,
            Outcome TEXT NOT NULL,
            Reason TEXT NULL,
            BeforeHash TEXT NULL,
            AfterHash TEXT NULL,
            DiffRef TEXT NULL,
            RequestId TEXT NULL,
            CorrelationId TEXT NULL,
            IpHash TEXT NULL,
            UserAgentHash TEXT NULL,
            DetailsJson TEXT NULL,
            PreviousAuditHash TEXT NULL,
            AuditHash TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_Audit_Time ON AuditMetadata(OccurredAtUtc);
        CREATE INDEX IF NOT EXISTS IX_Audit_Actor_Time ON AuditMetadata(ActorUserId, OccurredAtUtc);
        CREATE INDEX IF NOT EXISTS IX_Audit_Target ON AuditMetadata(TargetKind, TargetId, OccurredAtUtc);
        CREATE INDEX IF NOT EXISTS IX_Audit_Action_Time ON AuditMetadata(Action, OccurredAtUtc);
        CREATE INDEX IF NOT EXISTS IX_Audit_Correlation ON AuditMetadata(CorrelationId);

        CREATE TABLE IF NOT EXISTS VersionHistory (
            VersionId TEXT PRIMARY KEY,
            TargetKind TEXT NOT NULL,
            TargetId TEXT NOT NULL,
            VersionNumber INTEGER NOT NULL,
            ParentVersionId TEXT NULL REFERENCES VersionHistory(VersionId),
            Format TEXT NOT NULL,
            HistoryPath TEXT NOT NULL,
            BeforeHash TEXT NULL,
            AfterHash TEXT NOT NULL,
            ByteSize INTEGER NOT NULL,
            CreatedAtUtc TEXT NOT NULL,
            CreatedByUserId TEXT NULL REFERENCES Users(UserId) ON DELETE SET NULL,
            AuditId TEXT NULL REFERENCES AuditMetadata(AuditId),
            RestoreSupported INTEGER NOT NULL DEFAULT 1,
            UNIQUE(TargetKind, TargetId, VersionNumber)
        );

        CREATE INDEX IF NOT EXISTS IX_VersionHistory_Target ON VersionHistory(TargetKind, TargetId, VersionNumber);
        CREATE INDEX IF NOT EXISTS IX_VersionHistory_Time ON VersionHistory(CreatedAtUtc);

        CREATE TABLE IF NOT EXISTS SemanticIndexMetadata (
            MetadataId TEXT PRIMARY KEY,
            CorpusKind TEXT NOT NULL,
            SourceId TEXT NOT NULL,
            ChunkId TEXT NOT NULL,
            SourceContentHash TEXT NOT NULL,
            EmbeddingModelId TEXT NOT NULL,
            TokenizerId TEXT NOT NULL,
            VectorDimensions INTEGER NOT NULL,
            IndexPath TEXT NULL,
            IndexedAtUtc TEXT NOT NULL,
            LastBuildId TEXT NULL,
            Status TEXT NOT NULL,
            UNIQUE(CorpusKind, SourceId, ChunkId, EmbeddingModelId, TokenizerId)
        );

        CREATE INDEX IF NOT EXISTS IX_SemanticIndex_Source ON SemanticIndexMetadata(CorpusKind, SourceId);
        CREATE INDEX IF NOT EXISTS IX_SemanticIndex_Status ON SemanticIndexMetadata(Status, IndexedAtUtc);

        CREATE TABLE IF NOT EXISTS IndexBuilds (
            BuildId TEXT PRIMARY KEY,
            StartedAtUtc TEXT NOT NULL,
            CompletedAtUtc TEXT NULL,
            RequestedByUserId TEXT NULL REFERENCES Users(UserId) ON DELETE SET NULL,
            Kind TEXT NOT NULL,
            Status TEXT NOT NULL,
            DetailsJson TEXT NULL,
            AuditId TEXT NULL REFERENCES AuditMetadata(AuditId)
        );
        """;

    private static readonly string SeedSql = """
        INSERT OR IGNORE INTO Roles (RoleId, Name, NormalizedName, Description, IsSystem) VALUES
            ('viewer', 'Viewer', 'VIEWER', 'Read-only MemorySmith user.', 1),
            ('editor', 'Editor', 'EDITOR', 'MemorySmith content editor.', 1),
            ('admin', 'Admin', 'ADMIN', 'MemorySmith system administrator.', 1);

        INSERT OR IGNORE INTO Providers (ProviderName, DisplayName, IsEnabled, SortOrder, UpdatedAtUtc, UpdatedByUserId) VALUES
            ('GitHub', 'GitHub', 0, 10, '2026-05-17T00:00:00.0000000Z', NULL),
            ('Google', 'Google', 0, 20, '2026-05-17T00:00:00.0000000Z', NULL),
            ('Microsoft', 'Microsoft', 0, 30, '2026-05-17T00:00:00.0000000Z', NULL),
            ('LocalPassword', 'Local password', 1, 40, '2026-05-17T00:00:00.0000000Z', NULL),
            ('ApiToken', 'API token', 1, 50, '2026-05-17T00:00:00.0000000Z', NULL),
            ('System', 'System', 1, 60, '2026-05-17T00:00:00.0000000Z', NULL);
        """;
}

/// <summary>
/// Describes a single ordered schema migration that can be auto-applied on startup.
/// </summary>
/// <param name="Id">Unique migration identifier (e.g. date-based like "20260517_auth_rbac_audit_history_v1").</param>
/// <param name="SchemaSql">DDL statements (CREATE TABLE, ALTER TABLE, CREATE INDEX, etc.).</param>
/// <param name="SeedSql">Optional seed data (INSERT OR IGNORE, etc.). May be null or empty.</param>
public sealed record SchemaMigration(string Id, string SchemaSql, string? SeedSql);

file static class SqliteReaderExtensions
{
    public static string GetString(this SqliteDataReader reader, string name) => reader.GetString(reader.GetOrdinal(name));
    public static int GetInt32(this SqliteDataReader reader, string name) => reader.GetInt32(reader.GetOrdinal(name));
    public static long GetInt64(this SqliteDataReader reader, string name) => reader.GetInt64(reader.GetOrdinal(name));

    public static string? GetNullableString(this SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static bool GetBooleanInt(this SqliteDataReader reader, string name) => reader.GetInt64(reader.GetOrdinal(name)) != 0;

    public static bool? GetNullableBool(this SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal) != 0;
    }

    public static DateTime? GetNullableDate(this SqliteDataReader reader, string name)
    {
        var value = reader.GetNullableString(name);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    }
}
