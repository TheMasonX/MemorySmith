namespace MemorySmith.Core.Models;

public static class MemorySmithRoles
{
    public const string Viewer = "Viewer";
    public const string Editor = "Editor";
    public const string Admin = "Admin";
}

public static class MemorySmithProviders
{
    public const string GitHub = "GitHub";
    public const string Google = "Google";
    public const string Microsoft = "Microsoft";
    public const string LocalPassword = "LocalPassword";
    public const string ApiToken = "ApiToken";
    public const string System = "System";
}

public static class MemorySmithActorKinds
{
    public const string Anonymous = "Anonymous";
    public const string User = "User";
    public const string ServiceToken = "ServiceToken";
    public const string System = "System";
}

public static class MemorySmithAuditOutcomes
{
    public const string Success = "Success";
    public const string Failure = "Failure";
    public const string Denied = "Denied";
    public const string Pending = "Pending";
}

public sealed class UserAccount
{
    public string UserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string NormalizedDisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }
    public bool IsDisabled { get; set; }
    public bool LocalPasswordEnabled { get; set; }
    public string? PasswordHash { get; set; }
    public int PasswordHashVersion { get; set; } = 1;
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAtUtc { get; set; }
}

public sealed record UserQuery(string? Search = null, int Page = 1, int PageSize = 50);

public sealed class RoleRecord
{
    public string RoleId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystem { get; set; } = true;
}

public sealed class AuthProviderRecord
{
    public string ProviderName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public int SortOrder { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedByUserId { get; set; }
}

public sealed class ProviderLink
{
    public string LinkId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ProviderSubject { get; set; } = string.Empty;
    public string? ProviderDisplayName { get; set; }
    public string? ProviderEmail { get; set; }
    public bool? ProviderEmailVerified { get; set; }
    public DateTime LinkedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAtUtc { get; set; }
}

public sealed class LoginHistoryEntry
{
    public string LoginId { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string? ProviderSubject { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public bool Succeeded { get; set; }
    public string? FailureCode { get; set; }
    public string? IpHash { get; set; }
    public string? UserAgentHash { get; set; }
    public string? RequestId { get; set; }
}

public sealed record LoginHistoryQuery(string? UserId = null, string? ProviderName = null, DateTime? SinceUtc = null, int Page = 1, int PageSize = 100);

public sealed class ApiTokenRecord
{
    public string TokenId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string TokenPrefix { get; set; } = string.Empty;
    public string? OwnerUserId { get; set; }
    public string? ServicePrincipalName { get; set; }
    public string ScopesJson { get; set; } = "[]";
    public bool IsDisabled { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

public sealed record ApiTokenQuery(string? OwnerUserId = null, bool IncludeDisabled = false, int Page = 1, int PageSize = 100);

public sealed class AuditLogEntry
{
    public string AuditId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
    public string? ActorUserId { get; set; }
    public string? ActorDisplay { get; set; }
    public string ActorKind { get; set; } = MemorySmithActorKinds.Anonymous;
    public string? AuthScheme { get; set; }
    public string? ProviderName { get; set; }
    public string? RoleSnapshotJson { get; set; }
    public string Action { get; set; } = string.Empty;
    public string TargetKind { get; set; } = string.Empty;
    public string? TargetId { get; set; }
    public string Outcome { get; set; } = MemorySmithAuditOutcomes.Success;
    public string? Reason { get; set; }
    public string? BeforeHash { get; set; }
    public string? AfterHash { get; set; }
    public string? DiffRef { get; set; }
    public string? RequestId { get; set; }
    public string? CorrelationId { get; set; }
    public string? IpHash { get; set; }
    public string? UserAgentHash { get; set; }
    public string? DetailsJson { get; set; }
    public string? PreviousAuditHash { get; set; }
    public string AuditHash { get; set; } = string.Empty;
}

public sealed record AuditLogQuery(
    string? ActorUserId = null,
    string? Action = null,
    string? TargetKind = null,
    string? TargetId = null,
    string? Outcome = null,
    DateTime? SinceUtc = null,
    DateTime? UntilUtc = null,
    int Page = 1,
    int PageSize = 100);

public sealed class VersionHistoryEntry
{
    public string VersionId { get; set; } = string.Empty;
    public string TargetKind { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public string? ParentVersionId { get; set; }
    public string Format { get; set; } = string.Empty;
    public string HistoryPath { get; set; } = string.Empty;
    public string? BeforeHash { get; set; }
    public string AfterHash { get; set; } = string.Empty;
    public long ByteSize { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }
    public string? AuditId { get; set; }
    public bool RestoreSupported { get; set; } = true;
}

public sealed class VersionCreateRequest
{
    public string TargetKind { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string HistoryPath { get; set; } = string.Empty;
    public string? BeforeHash { get; set; }
    public string AfterHash { get; set; } = string.Empty;
    public long ByteSize { get; set; }
    public string? CreatedByUserId { get; set; }
    public string? AuditId { get; set; }
    public bool RestoreSupported { get; set; } = true;
}

public sealed record VersionHistoryQuery(string TargetKind, string TargetId, int Page = 1, int PageSize = 100);

public sealed class SemanticIndexMetadata
{
    public string MetadataId { get; set; } = string.Empty;
    public string CorpusKind { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string ChunkId { get; set; } = string.Empty;
    public string SourceContentHash { get; set; } = string.Empty;
    public string EmbeddingModelId { get; set; } = string.Empty;
    public string TokenizerId { get; set; } = string.Empty;
    public int VectorDimensions { get; set; }
    public string? IndexPath { get; set; }
    public DateTime IndexedAtUtc { get; set; } = DateTime.UtcNow;
    public string? LastBuildId { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class IndexBuildRecord
{
    public string BuildId { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public string? RequestedByUserId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? DetailsJson { get; set; }
    public string? AuditId { get; set; }
}

public sealed class AdminSetting
{
    public string Key { get; set; } = string.Empty;
    public string ValueJson { get; set; } = string.Empty;
    public string ValueHash { get; set; } = string.Empty;
    public string? UpdatedByUserId { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
