using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemorySmith.Core.Security;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public static class MemorySmithPolicies
{
    public const string CanViewMemorySmith = "CanViewMemorySmith";
    public const string CanEditMemorySmith = "CanEditMemorySmith";
    public const string CanAdminMemorySmith = "CanAdminMemorySmith";
    public const string CanManageUsers = "CanManageUsers";
    public const string CanManageSettings = "CanManageSettings";
    public const string CanViewAudit = "CanViewAudit";
    public const string CanRestoreHistory = "CanRestoreHistory";
    public const string CanReadSourceBundle = "CanReadSourceBundle";
    public const string CanUseChat = "CanUseChat";
    public const string CanApproveAgentWrites = "CanApproveAgentWrites";
}

public static class MemorySmithAuthProperties
{
    public const string LinkUserId = "MemorySmith.LinkUserId";
}

public static class MemorySmithExternalAuthSupport
{
    public static async Task<HashSet<string>> GetSupportedExternalProvidersAsync(
        IAuthenticationSchemeProvider schemeProvider,
        AuthOptions auth,
        CancellationToken cancellationToken)
    {
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scheme in await schemeProvider.GetAllSchemesAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsConfiguredExternalProvider(scheme.Name, auth))
            {
                supported.Add(scheme.Name);
            }
        }

        return supported;
    }

    public static bool IsConfiguredExternalProvider(string providerName, AuthOptions auth) => providerName switch
    {
        MemorySmithProviders.GitHub => auth.Providers.GitHub.Enabled && !string.IsNullOrWhiteSpace(auth.Providers.GitHub.ClientId),
        MemorySmithProviders.Google => auth.Providers.Google.Enabled && !string.IsNullOrWhiteSpace(auth.Providers.Google.ClientId),
        MemorySmithProviders.Microsoft => auth.Providers.Microsoft.Enabled && !string.IsNullOrWhiteSpace(auth.Providers.Microsoft.ClientId),
        _ => false
    };

    public static bool IsRuntimeSupportedExternalProvider(string providerName, ISet<string> supportedExternalProviders) =>
        supportedExternalProviders.Contains(providerName);
}

public sealed record AuditActorContext(
    string? UserId,
    string DisplayName,
    string AuthScheme,
    string? ProviderName,
    string ActorKind,
    IReadOnlyList<string> Roles);

public sealed class ExternalAuthOutcomeRecorder
{
    private const string FailurePersistedKey = "MemorySmith.ExternalAuthFailurePersisted";
    private readonly IMemorySmithDatabase _database;
    private readonly AuditLogService _audit;
    private readonly IOptionsMonitor<MemorySmithOptions> _options;

    public ExternalAuthOutcomeRecorder(
        IMemorySmithDatabase database,
        AuditLogService audit,
        IOptionsMonitor<MemorySmithOptions> options)
    {
        _database = database;
        _audit = audit;
        _options = options;
    }

    public async Task RecordSuccessAsync(
        HttpContext httpContext,
        string providerName,
        string? providerSubject,
        UserAccount user,
        IReadOnlyList<RoleRecord> roles,
        bool requestedLink,
        CancellationToken cancellationToken)
    {
        var occurredAtUtc = DateTime.UtcNow;
        var requestMetadata = RequestMetadata.Capture(httpContext, _options.CurrentValue);
        await _database.LoginHistory.RecordAsync(new LoginHistoryEntry
        {
            LoginId = Guid.NewGuid().ToString("N"),
            UserId = user.UserId,
            ProviderName = providerName,
            ProviderSubject = providerSubject,
            OccurredAtUtc = occurredAtUtc,
            Succeeded = true,
            IpHash = requestMetadata.IpHash,
            UserAgentHash = requestMetadata.UserAgentHash,
            RequestId = requestMetadata.RequestId
        }, cancellationToken);

        await _audit.RecordWithActorAsync(
            new AuditActorContext(
                user.UserId,
                user.DisplayName,
                providerName,
                providerName,
                MemorySmithActorKinds.User,
                roles.Select(role => role.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList()),
            "auth.login.succeeded",
            "User",
            user.UserId,
            MemorySmithAuditOutcomes.Success,
            details: new { providerName, requestedLink },
            httpContext: httpContext,
            cancellationToken: cancellationToken);
    }

    public async Task RecordFailureIfNeededAsync(
        HttpContext httpContext,
        string providerName,
        string? providerSubject,
        string? targetUserId,
        string failureCode,
        string message,
        bool requestedLink,
        object? details = null,
        CancellationToken cancellationToken = default)
    {
        if (FailureAlreadyPersisted(httpContext))
        {
            return;
        }

        MarkFailurePersisted(httpContext);
        var occurredAtUtc = DateTime.UtcNow;
        var requestMetadata = RequestMetadata.Capture(httpContext, _options.CurrentValue);
        await _database.LoginHistory.RecordAsync(new LoginHistoryEntry
        {
            LoginId = Guid.NewGuid().ToString("N"),
            UserId = targetUserId,
            ProviderName = providerName,
            ProviderSubject = providerSubject,
            OccurredAtUtc = occurredAtUtc,
            Succeeded = false,
            FailureCode = failureCode,
            IpHash = requestMetadata.IpHash,
            UserAgentHash = requestMetadata.UserAgentHash,
            RequestId = requestMetadata.RequestId
        }, cancellationToken);

        await _audit.RecordWithActorAsync(
            ResolveAttemptActor(httpContext, providerName),
            "auth.login.failed",
            targetUserId is null ? "Provider" : "User",
            targetUserId ?? providerName,
            MemorySmithAuditOutcomes.Failure,
            reason: failureCode,
            details: details is null
                ? new { providerName, requestedLink, message }
                : new { providerName, requestedLink, message, details },
            httpContext: httpContext,
            cancellationToken: cancellationToken);
    }

    private static AuditActorContext ResolveAttemptActor(HttpContext httpContext, string providerName)
    {
        var principal = httpContext.User;
        if (principal.Identity?.IsAuthenticated == true)
        {
            return new AuditActorContext(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                principal.FindFirstValue(ClaimTypes.Name) ?? "Authenticated user",
                principal.Identity.AuthenticationType ?? providerName,
                principal.FindFirstValue("provider") ?? providerName,
                MemorySmithActorKinds.User,
                principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }

        return new AuditActorContext(
            null,
            "Anonymous",
            providerName,
            providerName,
            MemorySmithActorKinds.Anonymous,
            []);
    }

    private static bool FailureAlreadyPersisted(HttpContext httpContext) =>
        httpContext.Items.TryGetValue(FailurePersistedKey, out var value) && value is true;

    private static void MarkFailurePersisted(HttpContext httpContext) =>
        httpContext.Items[FailurePersistedKey] = true;
}

public enum MemorySmithPermission
{
    View,
    Edit,
    Admin,
    ReadSourceBundle,
    UseChat,
    ApproveAgentWrites,
    ViewAudit,
    RestoreHistory,
    ManageUsers,
    ManageSettings
}

public sealed class MemorySmithPermissionRequirement : IAuthorizationRequirement
{
    public MemorySmithPermissionRequirement(MemorySmithPermission permission)
    {
        Permission = permission;
    }

    public MemorySmithPermission Permission { get; }
}

public sealed class MemorySmithPermissionHandler : AuthorizationHandler<MemorySmithPermissionRequirement>
{
    private readonly IOptionsMonitor<MemorySmithOptions> _options;
    private readonly IMemorySmithDatabase _database;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public MemorySmithPermissionHandler(
        IOptionsMonitor<MemorySmithOptions> options,
        IMemorySmithDatabase database,
        IHttpContextAccessor httpContextAccessor)
    {
        _options = options;
        _database = database;
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, MemorySmithPermissionRequirement requirement)
    {
        var auth = _options.CurrentValue.Auth;
        var roles = context.User.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isAuthenticated = context.User.Identity?.IsAuthenticated == true;
        if (RequiresAuthenticatedAdmin(requirement.Permission))
        {
            if (isAuthenticated && roles.Contains(MemorySmithRoles.Admin))
            {
                context.Succeed(requirement);
            }

            return;
        }

        if (!auth.Enabled)
        {
            context.Succeed(requirement);
            return;
        }

        if (HasConfiguredApiKeyAccess())
        {
            context.Succeed(requirement);
            return;
        }

        var hasAdmin = await _database.Users.HasAnyAdminAsync(CancellationToken.None);
        if (!hasAdmin && auth.OpenLocalEditorCompatibility && IsLoopbackRequest())
        {
            context.Succeed(requirement);
            return;
        }

        if (isAuthenticated)
        {
            if (auth.AutoEditorForAuthenticatedUsers)
            {
                roles.Add(MemorySmithRoles.Editor);
            }
            else if (roles.Count == 0 && !string.IsNullOrWhiteSpace(auth.AuthenticatedDefaultRole))
            {
                roles.Add(NormalizeAuthenticatedDefaultRole(auth.AuthenticatedDefaultRole));
            }
        }
        else
        {
            AddAnonymousRole(auth.AnonymousAccess, roles);
        }

        if (Allows(requirement.Permission, roles))
        {
            context.Succeed(requirement);
        }
    }

    public static string NormalizeAuthenticatedDefaultRole(string? roleName) =>
        string.Equals(roleName, MemorySmithRoles.Editor, StringComparison.OrdinalIgnoreCase)
            ? MemorySmithRoles.Editor
            : MemorySmithRoles.Viewer;

    private static bool RequiresAuthenticatedAdmin(MemorySmithPermission permission) =>
        permission is MemorySmithPermission.Admin
            or MemorySmithPermission.ManageUsers
            or MemorySmithPermission.ManageSettings
            or MemorySmithPermission.ViewAudit
            or MemorySmithPermission.RestoreHistory;

    private bool IsLoopbackRequest()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        return httpContext is null || MemorySmithRequestGuardMiddleware.IsLoopback(httpContext.Connection.RemoteIpAddress);
    }

    private bool HasConfiguredApiKeyAccess()
    {
        var apiKey = _options.CurrentValue.ApiKey;
        var httpContext = _httpContextAccessor.HttpContext;
        if (string.IsNullOrWhiteSpace(apiKey) || httpContext is null || !MemorySmithRequestGuardMiddleware.RequiresApiKey(httpContext.Request.Path))
        {
            return false;
        }

        return httpContext.Request.Headers.TryGetValue(MemorySmithRequestGuardMiddleware.ApiKeyHeaderName, out var values) &&
            values.Any(value => SecurityCompare.FixedTimeEquals(value, apiKey));
    }

    private static void AddAnonymousRole(string anonymousAccess, HashSet<string> roles)
    {
        if (string.Equals(anonymousAccess, MemorySmithRoles.Viewer, StringComparison.OrdinalIgnoreCase))
        {
            roles.Add(MemorySmithRoles.Viewer);
        }
    }

    private static bool Allows(MemorySmithPermission permission, HashSet<string> roles)
    {
        if (roles.Contains(MemorySmithRoles.Admin))
        {
            return true;
        }

        if (permission is MemorySmithPermission.Admin or MemorySmithPermission.ManageUsers or MemorySmithPermission.ManageSettings or MemorySmithPermission.ViewAudit or MemorySmithPermission.RestoreHistory)
        {
            return false;
        }

        if (roles.Contains(MemorySmithRoles.Editor))
        {
            return true;
        }

        return roles.Contains(MemorySmithRoles.Viewer) && permission is MemorySmithPermission.View or MemorySmithPermission.UseChat;
    }
}

public interface ICurrentUserContext
{
    string? UserId { get; }
    string DisplayName { get; }
    string AuthScheme { get; }
    string? Provider { get; }
    bool IsAuthenticated { get; }
    IReadOnlyList<string> Roles { get; }
    string ActorKind { get; }
}

public sealed class HttpCurrentUserContext : ICurrentUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCurrentUserContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal User => _httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

    public string? UserId => User.FindFirstValue(ClaimTypes.NameIdentifier);
    public string DisplayName => User.FindFirstValue(ClaimTypes.Name) ?? (IsAuthenticated ? "Authenticated user" : "Anonymous");
    public string AuthScheme => User.Identity?.AuthenticationType ?? "Anonymous";
    public string? Provider => User.FindFirstValue("provider");
    public bool IsAuthenticated => User.Identity?.IsAuthenticated == true;
    public IReadOnlyList<string> Roles => User.FindAll(ClaimTypes.Role).Select(claim => claim.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public string ActorKind => IsAuthenticated ? MemorySmithActorKinds.User : MemorySmithActorKinds.Anonymous;
}

public sealed record AuthResult(bool Succeeded, string? Error, UserAccount? User = null);

public sealed record LoginRequest(string UserNameOrEmail, string Password, bool RememberMe = false, string? ReturnUrl = null);

public sealed record SetupAdminRequest(string DisplayName, string? Email, string Password, string? BootstrapToken = null, string? ReturnUrl = null);

public sealed record CurrentUserResponse(bool IsAuthenticated, string? UserId, string DisplayName, IReadOnlyList<string> Roles, bool NeedsSetup);

public sealed class MemorySmithLocalAuthService
{
    private static readonly PasswordHasher<UserAccount> PasswordHasher = new();
    private readonly IMemorySmithDatabase _database;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AuditLogService _audit;
    private readonly IOptionsMonitor<MemorySmithOptions> _options;
    private readonly IAuthenticationSchemeProvider _schemeProvider;

    public MemorySmithLocalAuthService(
        IMemorySmithDatabase database,
        IHttpContextAccessor httpContextAccessor,
        AuditLogService audit,
        IOptionsMonitor<MemorySmithOptions> options,
        IAuthenticationSchemeProvider schemeProvider)
    {
        _database = database;
        _httpContextAccessor = httpContextAccessor;
        _audit = audit;
        _options = options;
        _schemeProvider = schemeProvider;
    }

    public async Task<bool> NeedsSetupAsync(CancellationToken cancellationToken) =>
        !await _database.Users.HasAnyAdminAsync(cancellationToken);

    public async Task<AuthResult> CreateFirstAdminAsync(SetupAdminRequest request, CancellationToken cancellationToken)
    {
        var auth = _options.CurrentValue.Auth;
        if (!auth.LocalPasswordEnabled)
        {
            return new AuthResult(false, "Local password sign-in is disabled.");
        }

        if (await _database.Users.HasAnyAdminAsync(cancellationToken))
        {
            return new AuthResult(false, "Setup has already been completed.");
        }

        var (isAuthorized, error) = BootstrapGate.Authorize(
            _httpContextAccessor.HttpContext,
            auth.Setup,
            request.BootstrapToken);
        if (!isAuthorized)
        {
            return new AuthResult(false, error ?? "Bootstrap authorization denied.");
        }

        var displayName = request.DisplayName.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return new AuthResult(false, "Display name is required.");
        }

        if (request.Password.Length < 15)
        {
            return new AuthResult(false, "Password must be at least 15 characters.");
        }

        var now = DateTime.UtcNow;
        var user = new UserAccount
        {
            UserId = Guid.NewGuid().ToString("N"),
            DisplayName = displayName,
            NormalizedDisplayName = Normalize(displayName),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            NormalizedEmail = string.IsNullOrWhiteSpace(request.Email) ? null : Normalize(request.Email),
            LocalPasswordEnabled = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        user.PasswordHash = PasswordHasher.HashPassword(user, request.Password);

        await _database.Users.CreateAsync(user, cancellationToken);
        await _database.Roles.AssignRoleAsync(user.UserId, MemorySmithRoles.Admin, null, cancellationToken);
        await _database.ProviderLinks.LinkAsync(new ProviderLink
        {
            LinkId = Guid.NewGuid().ToString("N"),
            UserId = user.UserId,
            ProviderName = MemorySmithProviders.LocalPassword,
            ProviderSubject = user.UserId,
            ProviderDisplayName = user.DisplayName,
            ProviderEmail = user.Email,
            ProviderEmailVerified = null,
            LinkedAtUtc = now
        }, cancellationToken);

        await _audit.RecordAsync("auth.setup.completed", "User", user.UserId, MemorySmithAuditOutcomes.Success, details: new { user.DisplayName }, cancellationToken: cancellationToken);
        await SignInUserAsync(user, [MemorySmithRoles.Admin], request.ReturnUrl, rememberMe: true, cancellationToken);
        return new AuthResult(true, null, user);
    }

    public async Task<AuthResult> SignInAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Auth.LocalPasswordEnabled)
        {
            return new AuthResult(false, "Local password sign-in is disabled.");
        }

        var normalized = Normalize(request.UserNameOrEmail);
        var user = await _database.Users.GetByNormalizedEmailAsync(normalized, cancellationToken)
            ?? await _database.Users.GetByNormalizedDisplayNameAsync(normalized, cancellationToken);
        var success = false;
        var failureCode = "invalid_credentials";

        if (user is not null && !user.IsDisabled && user.LocalPasswordEnabled && !string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            var verification = PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
            success = verification is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
            failureCode = user.IsDisabled ? "disabled" : "invalid_credentials";
            if (success && verification == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = PasswordHasher.HashPassword(user, request.Password);
            }
        }

        var requestMetadata = RequestMetadata.Capture(_httpContextAccessor.HttpContext, _options.CurrentValue);
        await _database.LoginHistory.RecordAsync(new LoginHistoryEntry
        {
            LoginId = Guid.NewGuid().ToString("N"),
            UserId = user?.UserId,
            ProviderName = MemorySmithProviders.LocalPassword,
            ProviderSubject = user?.UserId,
            OccurredAtUtc = DateTime.UtcNow,
            Succeeded = success,
            FailureCode = success ? null : failureCode,
            IpHash = requestMetadata.IpHash,
            UserAgentHash = requestMetadata.UserAgentHash,
            RequestId = requestMetadata.RequestId
        }, cancellationToken);

        if (!success || user is null)
        {
            await _audit.RecordAsync("auth.login.failed", "User", user?.UserId ?? normalized, MemorySmithAuditOutcomes.Failure, reason: failureCode, cancellationToken: cancellationToken);
            return new AuthResult(false, "Invalid username or password.");
        }

        var roles = await _database.Roles.GetRolesForUserAsync(user.UserId, cancellationToken);
        user.LastLoginAtUtc = DateTime.UtcNow;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _database.Users.UpdateAsync(user, cancellationToken);
        await SignInUserAsync(user, roles.Select(role => role.Name), request.ReturnUrl, request.RememberMe, cancellationToken);
        await _audit.RecordAsync("auth.login.succeeded", "User", user.UserId, MemorySmithAuditOutcomes.Success, cancellationToken: cancellationToken);
        return new AuthResult(true, null, user);
    }

    public async Task<AuthResult> UpdateProfileAsync(string userId, string displayName, string? email, CancellationToken cancellationToken)
    {
        var user = await _database.Users.GetByIdAsync(userId, cancellationToken);
        if (user is null || user.IsDisabled)
        {
            return new AuthResult(false, "The account could not be found.");
        }

        displayName = displayName.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return new AuthResult(false, "Display name is required.");
        }

        var normalizedDisplayName = Normalize(displayName);
        var existingName = await _database.Users.GetByNormalizedDisplayNameAsync(normalizedDisplayName, cancellationToken);
        if (existingName is not null && !string.Equals(existingName.UserId, user.UserId, StringComparison.Ordinal))
        {
            return new AuthResult(false, "That display name is already in use.");
        }

        var normalizedEmail = string.IsNullOrWhiteSpace(email) ? null : Normalize(email);
        if (!string.IsNullOrWhiteSpace(normalizedEmail))
        {
            var existingEmail = await _database.Users.GetByNormalizedEmailAsync(normalizedEmail, cancellationToken);
            if (existingEmail is not null && !string.Equals(existingEmail.UserId, user.UserId, StringComparison.Ordinal))
            {
                return new AuthResult(false, "That email address is already in use.");
            }
        }

        user.DisplayName = displayName;
        user.NormalizedDisplayName = normalizedDisplayName;
        user.Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        user.NormalizedEmail = normalizedEmail;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _database.Users.UpdateAsync(user, cancellationToken);
        await _audit.RecordAsync("account.profile.updated", "User", user.UserId, MemorySmithAuditOutcomes.Success, details: new { user.DisplayName, user.Email }, cancellationToken: cancellationToken);
        return new AuthResult(true, null, user);
    }

    public async Task<AuthResult> CreateLocalUserAsync(string displayName, string? email, string password, string roleName, string? actorUserId, CancellationToken cancellationToken)
    {
        var auth = _options.CurrentValue.Auth;
        if (!auth.LocalPasswordEnabled)
        {
            return new AuthResult(false, "Local password sign-in is disabled.");
        }

        if (!auth.AllowAdminCreateLocalUsers)
        {
            return new AuthResult(false, "Admin-created local users are disabled.");
        }

        displayName = displayName.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return new AuthResult(false, "Display name is required.");
        }

        if (password.Length < 15)
        {
            return new AuthResult(false, "Password must be at least 15 characters.");
        }

        var normalizedDisplayName = Normalize(displayName);
        var existingName = await _database.Users.GetByNormalizedDisplayNameAsync(normalizedDisplayName, cancellationToken);
        if (existingName is not null)
        {
            return new AuthResult(false, "That display name is already in use.");
        }

        var normalizedEmail = string.IsNullOrWhiteSpace(email) ? null : Normalize(email);
        if (!string.IsNullOrWhiteSpace(normalizedEmail))
        {
            var existingEmail = await _database.Users.GetByNormalizedEmailAsync(normalizedEmail, cancellationToken);
            if (existingEmail is not null)
            {
                return new AuthResult(false, "That email address is already in use.");
            }
        }

        roleName = string.IsNullOrWhiteSpace(roleName) ? MemorySmithRoles.Viewer : roleName.Trim();
        if (string.Equals(roleName, MemorySmithRoles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            return new AuthResult(false, "Admin accounts are not created from this flow.");
        }

        var availableRoles = await _database.Roles.ListRolesAsync(cancellationToken);
        if (!availableRoles.Any(role => string.Equals(role.Name, roleName, StringComparison.OrdinalIgnoreCase)))
        {
            return new AuthResult(false, "The selected role is not valid.");
        }

        var now = DateTime.UtcNow;
        var user = new UserAccount
        {
            UserId = Guid.NewGuid().ToString("N"),
            DisplayName = displayName,
            NormalizedDisplayName = normalizedDisplayName,
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            NormalizedEmail = normalizedEmail,
            LocalPasswordEnabled = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            PasswordHash = null
        };
        user.PasswordHash = PasswordHasher.HashPassword(user, password);

        await _database.Users.CreateAsync(user, cancellationToken);
        await _database.Roles.AssignRoleAsync(user.UserId, roleName, actorUserId, cancellationToken);
        await EnsureLocalPasswordLinkAsync(user, cancellationToken);
        await _audit.RecordAsync("account.local_user.created", "User", user.UserId, MemorySmithAuditOutcomes.Success, details: new { user.DisplayName, user.Email, roleName }, cancellationToken: cancellationToken);
        return new AuthResult(true, null, user);
    }

    public async Task<AuthResult> SetLocalPasswordAsync(string userId, string? currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Auth.LocalPasswordEnabled)
        {
            return new AuthResult(false, "Local password sign-in is disabled.");
        }

        if (newPassword.Length < 15)
        {
            return new AuthResult(false, "Password must be at least 15 characters.");
        }

        var user = await _database.Users.GetByIdAsync(userId, cancellationToken);
        if (user is null || user.IsDisabled)
        {
            return new AuthResult(false, "The account could not be found.");
        }

        var hasPassword = user.LocalPasswordEnabled && !string.IsNullOrWhiteSpace(user.PasswordHash);
        if (hasPassword)
        {
            var verification = PasswordHasher.VerifyHashedPassword(user, user.PasswordHash!, currentPassword ?? string.Empty);
            if (verification is not PasswordVerificationResult.Success and not PasswordVerificationResult.SuccessRehashNeeded)
            {
                return new AuthResult(false, "Current password is incorrect.");
            }
        }

        user.LocalPasswordEnabled = true;
        user.PasswordHash = PasswordHasher.HashPassword(user, newPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _database.Users.UpdateAsync(user, cancellationToken);
        await EnsureLocalPasswordLinkAsync(user, cancellationToken);
        await _audit.RecordAsync(hasPassword ? "account.local_password.changed" : "account.local_password.added", "User", user.UserId, MemorySmithAuditOutcomes.Success, cancellationToken: cancellationToken);
        return new AuthResult(true, null, user);
    }

    public async Task<AuthResult> RemoveLocalPasswordAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await _database.Users.GetByIdAsync(userId, cancellationToken);
        if (user is null || user.IsDisabled)
        {
            return new AuthResult(false, "The account could not be found.");
        }

        var links = await _database.ProviderLinks.GetLinksForUserAsync(user.UserId, cancellationToken);
        var localLinks = links.Where(link => string.Equals(link.ProviderName, MemorySmithProviders.LocalPassword, StringComparison.OrdinalIgnoreCase)).ToList();
        var removedLinkIds = localLinks.Select(link => link.LinkId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!await HasUsableSignInMethodAfterRemovingAsync(user, links, removedLinkIds, cancellationToken))
        {
            return new AuthResult(false, "Add another working sign-in method before removing local password sign-in.");
        }

        user.LocalPasswordEnabled = false;
        user.PasswordHash = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _database.Users.UpdateAsync(user, cancellationToken);
        foreach (var link in localLinks)
        {
            await _database.ProviderLinks.UnlinkAsync(link.LinkId, cancellationToken);
        }

        await _audit.RecordAsync("account.local_password.removed", "User", user.UserId, MemorySmithAuditOutcomes.Success, cancellationToken: cancellationToken);
        return new AuthResult(true, null, user);
    }

    public async Task<AuthResult> UnlinkProviderAsync(string userId, string linkId, CancellationToken cancellationToken)
    {
        var user = await _database.Users.GetByIdAsync(userId, cancellationToken);
        if (user is null || user.IsDisabled)
        {
            return new AuthResult(false, "The account could not be found.");
        }

        var links = await _database.ProviderLinks.GetLinksForUserAsync(user.UserId, cancellationToken);
        var link = links.FirstOrDefault(item => string.Equals(item.LinkId, linkId, StringComparison.OrdinalIgnoreCase));
        if (link is null)
        {
            return new AuthResult(false, "The sign-in method could not be found.");
        }

        if (string.Equals(link.ProviderName, MemorySmithProviders.LocalPassword, StringComparison.OrdinalIgnoreCase))
        {
            return await RemoveLocalPasswordAsync(userId, cancellationToken);
        }

        var removedLinkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { link.LinkId };
        if (!await HasUsableSignInMethodAfterRemovingAsync(user, links, removedLinkIds, cancellationToken))
        {
            return new AuthResult(false, "Add another working sign-in method before removing this provider.");
        }

        await _database.ProviderLinks.UnlinkAsync(link.LinkId, cancellationToken);
        await _audit.RecordAsync("account.provider.unlinked", "User", user.UserId, MemorySmithAuditOutcomes.Success, details: new { link.ProviderName }, cancellationToken: cancellationToken);
        return new AuthResult(true, null, user);
    }

    public async Task SignOutAsync() =>
        await (_httpContextAccessor.HttpContext?.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme) ?? Task.CompletedTask);

    private async Task EnsureLocalPasswordLinkAsync(UserAccount user, CancellationToken cancellationToken)
    {
        var links = await _database.ProviderLinks.GetLinksForUserAsync(user.UserId, cancellationToken);
        if (links.Any(link => string.Equals(link.ProviderName, MemorySmithProviders.LocalPassword, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        await _database.ProviderLinks.LinkAsync(new ProviderLink
        {
            LinkId = Guid.NewGuid().ToString("N"),
            UserId = user.UserId,
            ProviderName = MemorySmithProviders.LocalPassword,
            ProviderSubject = user.UserId,
            ProviderDisplayName = user.DisplayName,
            ProviderEmail = user.Email,
            LinkedAtUtc = DateTime.UtcNow
        }, cancellationToken);
    }

    private async Task<bool> HasUsableSignInMethodAfterRemovingAsync(UserAccount user, IReadOnlyList<ProviderLink> links, ISet<string> removedLinkIds, CancellationToken cancellationToken)
    {
        var providers = await _database.ProviderLinks.ListProvidersAsync(cancellationToken);
        var supportedExternalProviders = await MemorySmithExternalAuthSupport.GetSupportedExternalProvidersAsync(_schemeProvider, _options.CurrentValue.Auth, cancellationToken);
        return links
            .Where(link => !removedLinkIds.Contains(link.LinkId))
            .Any(link => IsUsableSignInMethod(user, link, providers, supportedExternalProviders));
    }

    private bool IsUsableSignInMethod(UserAccount user, ProviderLink link, IReadOnlyList<AuthProviderRecord> providers, ISet<string> supportedExternalProviders)
    {
        var providerRecord = providers.FirstOrDefault(provider => string.Equals(provider.ProviderName, link.ProviderName, StringComparison.OrdinalIgnoreCase));
        if (providerRecord?.IsEnabled != true)
        {
            return false;
        }

        var auth = _options.CurrentValue.Auth;
        return link.ProviderName switch
        {
            MemorySmithProviders.LocalPassword => auth.LocalPasswordEnabled && user.LocalPasswordEnabled && !string.IsNullOrWhiteSpace(user.PasswordHash),
            MemorySmithProviders.GitHub or MemorySmithProviders.Google or MemorySmithProviders.Microsoft =>
                MemorySmithExternalAuthSupport.IsConfiguredExternalProvider(link.ProviderName, auth)
                && MemorySmithExternalAuthSupport.IsRuntimeSupportedExternalProvider(link.ProviderName, supportedExternalProviders),
            _ => false
        };
    }

    private async Task SignInUserAsync(UserAccount user, IEnumerable<string> roles, string? returnUrl, bool rememberMe, CancellationToken cancellationToken)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId),
            new(ClaimTypes.Name, user.DisplayName),
            new("provider", MemorySmithProviders.LocalPassword),
            new("security_stamp", user.SecurityStamp)
        };
        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var properties = new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            RedirectUri = SanitizeReturnUrl(returnUrl)
        };
        await (_httpContextAccessor.HttpContext?.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties) ?? Task.CompletedTask);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    public static string SanitizeReturnUrl(string? returnUrl) =>
        string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith("/", StringComparison.Ordinal) || returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? "/"
            : returnUrl;
}

public sealed class AuditLogService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly IMemorySmithDatabase _database;
    private readonly ICurrentUserContext _currentUser;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOptionsMonitor<MemorySmithOptions> _options;
    private readonly object _jsonlLock = new();

    public AuditLogService(
        IMemorySmithDatabase database,
        ICurrentUserContext currentUser,
        IHttpContextAccessor httpContextAccessor,
        IOptionsMonitor<MemorySmithOptions> options)
    {
        _database = database;
        _currentUser = currentUser;
        _httpContextAccessor = httpContextAccessor;
        _options = options;
    }

    public async Task<AuditLogEntry> RecordAsync(
        string action,
        string targetKind,
        string? targetId,
        string outcome,
        string? beforeHash = null,
        string? afterHash = null,
        string? diffRef = null,
        string? reason = null,
        object? details = null,
        CancellationToken cancellationToken = default)
    {
        return await RecordWithActorAsync(
            new AuditActorContext(
                _currentUser.UserId,
                _currentUser.DisplayName,
                _currentUser.AuthScheme,
                _currentUser.Provider,
                _currentUser.ActorKind,
                _currentUser.Roles),
            action,
            targetKind,
            targetId,
            outcome,
            beforeHash,
            afterHash,
            diffRef,
            reason,
            details,
            _httpContextAccessor.HttpContext,
            cancellationToken);
    }

    public async Task<AuditLogEntry> RecordWithActorAsync(
        AuditActorContext actor,
        string action,
        string targetKind,
        string? targetId,
        string outcome,
        string? beforeHash = null,
        string? afterHash = null,
        string? diffRef = null,
        string? reason = null,
        object? details = null,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var latest = _options.CurrentValue.Audit.HashChainEnabled
            ? await _database.AuditLogs.GetLatestAsync(cancellationToken)
            : null;
        var requestMetadata = RequestMetadata.Capture(httpContext ?? _httpContextAccessor.HttpContext, _options.CurrentValue);
        var entry = new AuditLogEntry
        {
            AuditId = Guid.NewGuid().ToString("N"),
            OccurredAtUtc = DateTime.UtcNow,
            RecordedAtUtc = DateTime.UtcNow,
            ActorUserId = actor.UserId,
            ActorDisplay = actor.DisplayName,
            ActorKind = actor.ActorKind,
            AuthScheme = actor.AuthScheme,
            ProviderName = actor.ProviderName,
            RoleSnapshotJson = JsonSerializer.Serialize(actor.Roles, JsonOptions),
            Action = action,
            TargetKind = targetKind,
            TargetId = targetId,
            Outcome = outcome,
            Reason = reason,
            BeforeHash = beforeHash,
            AfterHash = afterHash,
            DiffRef = diffRef,
            RequestId = requestMetadata.RequestId,
            CorrelationId = requestMetadata.CorrelationId,
            IpHash = requestMetadata.IpHash,
            UserAgentHash = requestMetadata.UserAgentHash,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details, JsonOptions),
            PreviousAuditHash = latest?.AuditHash
        };
        entry.AuditHash = ComputeHash(JsonSerializer.Serialize(new
        {
            entry.AuditId,
            entry.OccurredAtUtc,
            entry.ActorUserId,
            entry.ActorKind,
            entry.Action,
            entry.TargetKind,
            entry.TargetId,
            entry.Outcome,
            entry.BeforeHash,
            entry.AfterHash,
            entry.DiffRef,
            entry.PreviousAuditHash
        }, JsonOptions));

        await _database.AuditLogs.AppendAsync(entry, cancellationToken);
        AppendJsonlMirror(entry);
        return entry;
    }

    public static string ComputeHash(string? value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string ComputeJsonHash<T>(T value) => ComputeHash(JsonSerializer.Serialize(value, JsonOptions));

    private void AppendJsonlMirror(AuditLogEntry entry)
    {
        var auditOptions = _options.CurrentValue.Audit;
        if (!auditOptions.JsonlEnabled)
        {
            return;
        }

        try
        {
            var path = ResolveAuditPath(auditOptions.JsonlPath, entry.OccurredAtUtc);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (_jsonlLock)
            {
                File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
            }
        }
        catch
        {
            // Audit metadata in SQLite remains the query source of truth; mirror write failures surface through future diagnostics.
        }
    }

    private static string ResolveAuditPath(string pattern, DateTime utc)
    {
        var week = ISOWeek.GetWeekOfYear(utc);
        var resolved = pattern
            .Replace("{yyyy}", utc.Year.ToString("D4", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{MM}", utc.Month.ToString("D2", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{dd}", utc.Day.ToString("D2", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{week}", week.ToString("D2", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return Path.GetFullPath(resolved);
    }
}

public sealed class VersionHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IMemorySmithDatabase _database;
    private readonly ICurrentUserContext _currentUser;
    private readonly IOptionsMonitor<MemorySmithOptions> _options;

    public VersionHistoryService(IMemorySmithDatabase database, ICurrentUserContext currentUser, IOptionsMonitor<MemorySmithOptions> options)
    {
        _database = database;
        _currentUser = currentUser;
        _options = options;
    }

    public async Task<VersionHistoryEntry?> RecordMemoryAsync(string action, MemoryRecord? before, MemoryRecord? after, string? auditId, CancellationToken cancellationToken)
    {
        var targetId = after?.Id ?? before?.Id;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return null;
        }

        var existing = await _database.VersionHistory.GetHistoryAsync("Memory", targetId, cancellationToken);
        var nextVersion = existing.Count + 1;
        var checkpointEvery = Math.Max(1, _options.CurrentValue.History.MemoryCheckpointEveryVersions);
        var useSnapshot = before is null || after is null || nextVersion % checkpointEvery == 0;
        var payload = useSnapshot
            ? JsonSerializer.Serialize(after ?? before, JsonOptions)
            : JsonSerializer.Serialize(BuildMemoryDiff(before!, after!), JsonOptions);
        var format = useSnapshot ? "memorysmith.memory-snapshot.v1" : "memorysmith.memory-diff.v1";
        var extension = useSnapshot ? "snapshot.json" : "patch.json";
        var relativePath = Path.Combine("memories", SafeSegment(targetId), $"{nextVersion:D6}.{extension}");
        var fullPath = WriteArtifact(relativePath, payload);
        return await _database.VersionHistory.CreateVersionAsync(new VersionCreateRequest
        {
            TargetKind = "Memory",
            TargetId = targetId,
            Format = format,
            HistoryPath = relativePath.Replace(Path.DirectorySeparatorChar, '/'),
            BeforeHash = before is null ? null : AuditLogService.ComputeJsonHash(before),
            AfterHash = AuditLogService.ComputeJsonHash(after ?? before),
            ByteSize = new FileInfo(fullPath).Length,
            CreatedByUserId = _currentUser.UserId,
            AuditId = auditId
        }, cancellationToken);
    }

    public async Task<VersionHistoryEntry?> RecordPageAsync(string slug, string? beforeMarkdown, string? afterMarkdown, string? auditId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        var existing = await _database.VersionHistory.GetHistoryAsync("Page", slug, cancellationToken);
        var nextVersion = existing.Count + 1;
        var markdown = afterMarkdown ?? beforeMarkdown ?? string.Empty;
        var relativePath = Path.Combine("pages", SafeSegment(slug), $"{nextVersion:D6}.md");
        var fullPath = WriteArtifact(relativePath, markdown);
        return await _database.VersionHistory.CreateVersionAsync(new VersionCreateRequest
        {
            TargetKind = "Page",
            TargetId = slug,
            Format = "memorysmith.page-snapshot.v1",
            HistoryPath = relativePath.Replace(Path.DirectorySeparatorChar, '/'),
            BeforeHash = beforeMarkdown is null ? null : AuditLogService.ComputeHash(beforeMarkdown),
            AfterHash = AuditLogService.ComputeHash(markdown),
            ByteSize = new FileInfo(fullPath).Length,
            CreatedByUserId = _currentUser.UserId,
            AuditId = auditId
        }, cancellationToken);
    }

    private string WriteArtifact(string relativePath, string content)
    {
        var root = Path.GetFullPath(_options.CurrentValue.History.RootPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("History artifact path resolves outside the configured history root.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, fullPath, overwrite: true);
        return fullPath;
    }

    private static object BuildMemoryDiff(MemoryRecord before, MemoryRecord after) => new
    {
        Format = "memorysmith.memory-diff.v1",
        BeforeHash = AuditLogService.ComputeJsonHash(before),
        AfterHash = AuditLogService.ComputeJsonHash(after),
        Changes = BuildChanges(before, after)
    };

    private static List<object> BuildChanges(MemoryRecord before, MemoryRecord after)
    {
        var changes = new List<object>();
        AddChange(changes, "/Title", before.Title, after.Title);
        AddChange(changes, "/Content", before.Content, after.Content);
        AddChange(changes, "/Status", before.Status.ToString(), after.Status.ToString());
        AddChange(changes, "/Confidence", before.Confidence, after.Confidence);
        AddChange(changes, "/Tags", before.Tags, after.Tags);
        AddChange(changes, "/References", before.References, after.References);
        AddChange(changes, "/Conflicts", before.Conflicts, after.Conflicts);
        AddChange(changes, "/SourceLinks", before.SourceLinks, after.SourceLinks);
        AddChange(changes, "/UsageCount", before.UsageCount, after.UsageCount);
        return changes;
    }

    private static void AddChange<T>(List<object> changes, string path, T before, T after)
    {
        var beforeJson = JsonSerializer.Serialize(before, JsonOptions);
        var afterJson = JsonSerializer.Serialize(after, JsonOptions);
        if (!string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
        {
            changes.Add(new { Path = path, Kind = "replace", Before = before, After = after });
        }
    }

    private static string SafeSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Replace('\\', '/'))
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '/' ? ch : '-');
        }

        return string.Join('/', builder.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}

public sealed class AuditedPageService : IPageService
{
    private readonly IPageService _inner;
    private readonly AuditLogService _audit;
    private readonly VersionHistoryService _history;

    public AuditedPageService(IPageService inner, AuditLogService audit, VersionHistoryService history)
    {
        _inner = inner;
        _audit = audit;
        _history = history;
    }

    public Task<IReadOnlyList<PageSummary>> ListAsync(CancellationToken cancellationToken) => _inner.ListAsync(cancellationToken);
    public Task<IReadOnlyList<PageSummary>> SearchAsync(PageSearchQuery query, CancellationToken cancellationToken) => _inner.SearchAsync(query, cancellationToken);
    public Task<PageDocument?> GetAsync(string slug, CancellationToken cancellationToken) => _inner.GetAsync(slug, cancellationToken);
    public string RenderHtml(string markdown) => _inner.RenderHtml(markdown);

    public async Task<PageDocument> SaveAsync(PageSaveRequest request, CancellationToken cancellationToken)
    {
        PageDocument? before = null;
        if (!string.IsNullOrWhiteSpace(request.Slug))
        {
            before = await _inner.GetAsync(request.Slug, cancellationToken);
        }

        var saved = await _inner.SaveAsync(request, cancellationToken);
        var version = await _history.RecordPageAsync(saved.Slug, before?.Markdown, saved.Markdown, null, cancellationToken);
        await _audit.RecordAsync(
            before is null ? "page.created" : "page.updated",
            "Page",
            saved.Slug,
            MemorySmithAuditOutcomes.Success,
            beforeHash: before is null ? null : AuditLogService.ComputeHash(before.Markdown),
            afterHash: AuditLogService.ComputeHash(saved.Markdown),
            diffRef: version?.HistoryPath,
            details: new { saved.Title, saved.RelativePath },
            cancellationToken: cancellationToken);
        return saved;
    }

    public async Task<PageAsset> SaveAssetAsync(string fileName, Stream content, CancellationToken cancellationToken)
    {
        var asset = await _inner.SaveAssetAsync(fileName, content, cancellationToken);
        await _audit.RecordAsync("page.asset.saved", "PageAsset", asset.FileName, MemorySmithAuditOutcomes.Success, details: new { asset.MarkdownPath, asset.Size }, cancellationToken: cancellationToken);
        return asset;
    }

    public async Task<bool> DeleteAsync(string slug, CancellationToken cancellationToken)
    {
        var before = await _inner.GetAsync(slug, cancellationToken);
        var deleted = await _inner.DeleteAsync(slug, cancellationToken);
        if (deleted && before is not null)
        {
            var version = await _history.RecordPageAsync(before.Slug, before.Markdown, null, null, cancellationToken);
            await _audit.RecordAsync(
                "page.deleted",
                "Page",
                before.Slug,
                MemorySmithAuditOutcomes.Success,
                beforeHash: AuditLogService.ComputeHash(before.Markdown),
                diffRef: version?.HistoryPath,
                details: new { before.Title, before.RelativePath },
                cancellationToken: cancellationToken);
        }

        return deleted;
    }
}
