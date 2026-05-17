using System.Data.Common;
using MemorySmith.Core.Models;

namespace MemorySmith.Storage;

public interface IMemorySmithDatabase
{
    string ProviderName { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);
    IMemorySmithUserStore Users { get; }
    IMemorySmithRoleStore Roles { get; }
    IProviderLinkStore ProviderLinks { get; }
    ILoginHistoryStore LoginHistory { get; }
    IAuditLogStore AuditLogs { get; }
    ISettingsStore Settings { get; }
    IVersionHistoryStore VersionHistory { get; }
    ISemanticIndexMetadataStore SemanticIndexMetadata { get; }
    IApiTokenStore ApiTokens { get; }
}

public interface IDatabaseProviderFactory
{
    IMemorySmithDatabase Create(DatabaseOptions options);
}

public interface IMemorySmithUserStore
{
    Task<UserAccount?> GetByIdAsync(string userId, CancellationToken cancellationToken);
    Task<UserAccount?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken);
    Task<UserAccount?> GetByNormalizedDisplayNameAsync(string normalizedDisplayName, CancellationToken cancellationToken);
    Task<PagedResult<UserAccount>> ListAsync(UserQuery query, CancellationToken cancellationToken);
    Task CreateAsync(UserAccount user, CancellationToken cancellationToken);
    Task UpdateAsync(UserAccount user, CancellationToken cancellationToken);
    Task DisableAsync(string userId, string disabledByUserId, CancellationToken cancellationToken);
    Task<bool> HasAnyAdminAsync(CancellationToken cancellationToken);
}

public interface IMemorySmithRoleStore
{
    Task<IReadOnlyList<RoleRecord>> ListRolesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RoleRecord>> GetRolesForUserAsync(string userId, CancellationToken cancellationToken);
    Task AssignRoleAsync(string userId, string roleName, string? assignedByUserId, CancellationToken cancellationToken);
    Task RemoveRoleAsync(string userId, string roleName, string? removedByUserId, CancellationToken cancellationToken);
}

public interface IProviderLinkStore
{
    Task<IReadOnlyList<ProviderLink>> GetLinksForUserAsync(string userId, CancellationToken cancellationToken);
    Task<ProviderLink?> GetByProviderSubjectAsync(string providerName, string providerSubject, CancellationToken cancellationToken);
    Task LinkAsync(ProviderLink link, CancellationToken cancellationToken);
    Task UnlinkAsync(string linkId, CancellationToken cancellationToken);
    Task SetProviderEnabledAsync(string providerName, bool enabled, string? updatedByUserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuthProviderRecord>> ListProvidersAsync(CancellationToken cancellationToken);
}

public interface ILoginHistoryStore
{
    Task RecordAsync(LoginHistoryEntry entry, CancellationToken cancellationToken);
    Task<PagedResult<LoginHistoryEntry>> QueryAsync(LoginHistoryQuery query, CancellationToken cancellationToken);
}

public interface IAuditLogStore
{
    Task AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken);
    Task<PagedResult<AuditLogEntry>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken);
    Task<AuditLogEntry?> GetAsync(string auditId, CancellationToken cancellationToken);
    Task<AuditLogEntry?> GetLatestAsync(CancellationToken cancellationToken);
}

public interface ISettingsStore
{
    Task<AdminSetting?> GetAsync(string key, CancellationToken cancellationToken);
    Task<IReadOnlyList<AdminSetting>> ListAsync(CancellationToken cancellationToken);
    Task SetAsync(AdminSetting setting, CancellationToken cancellationToken);
}

public interface IVersionHistoryStore
{
    Task<VersionHistoryEntry> CreateVersionAsync(VersionCreateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<VersionHistoryEntry>> GetHistoryAsync(string targetKind, string targetId, CancellationToken cancellationToken);
    Task<VersionHistoryEntry?> GetVersionAsync(string versionId, CancellationToken cancellationToken);
}

public interface ISemanticIndexMetadataStore
{
    Task UpsertChunkAsync(SemanticIndexMetadata metadata, CancellationToken cancellationToken);
    Task<IReadOnlyList<SemanticIndexMetadata>> GetBySourceAsync(string corpusKind, string sourceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SemanticIndexMetadata>> GetStaleAsync(string modelId, string tokenizerId, CancellationToken cancellationToken);
    Task RecordBuildAsync(IndexBuildRecord build, CancellationToken cancellationToken);
}

public interface IApiTokenStore
{
    Task<ApiTokenRecord?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);
    Task<PagedResult<ApiTokenRecord>> ListAsync(ApiTokenQuery query, CancellationToken cancellationToken);
    Task CreateAsync(ApiTokenRecord token, CancellationToken cancellationToken);
    Task RevokeAsync(string tokenId, string? revokedByUserId, CancellationToken cancellationToken);
    Task RecordUseAsync(string tokenId, DateTime usedAtUtc, CancellationToken cancellationToken);
}
