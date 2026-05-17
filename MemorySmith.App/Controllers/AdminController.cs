using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly IMemorySmithDatabase _database;
    private readonly MemorySmithLocalAuthService _auth;
    private readonly ICurrentUserContext _currentUser;
    private readonly AuditLogService _audit;

    public AdminController(
        IMemorySmithDatabase database,
        MemorySmithLocalAuthService auth,
        ICurrentUserContext currentUser,
        AuditLogService audit)
    {
        _database = database;
        _auth = auth;
        _currentUser = currentUser;
        _audit = audit;
    }

    [HttpGet("setup/status")]
    [AllowAnonymous]
    public async Task<ActionResult<object>> SetupStatus(CancellationToken cancellationToken)
    {
        return Ok(new { needsSetup = await _auth.NeedsSetupAsync(cancellationToken) });
    }

    [HttpPost("setup")]
    [AllowAnonymous]
    [Consumes("application/json")]
    public async Task<IActionResult> Setup([FromBody] SetupAdminRequest request, CancellationToken cancellationToken)
    {
        var result = await _auth.CreateFirstAdminAsync(request, cancellationToken);
        return result.Succeeded ? Ok(new { ok = true, userId = result.User!.UserId }) : BadRequest(new { error = result.Error });
    }

    [HttpPost("setup")]
    [AllowAnonymous]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> SetupForm([FromForm] SetupAdminFormRequest request, CancellationToken cancellationToken)
    {
        var result = await _auth.CreateFirstAdminAsync(new SetupAdminRequest(request.DisplayName, request.Email, request.Password, request.BootstrapToken, request.ReturnUrl), cancellationToken);
        return result.Succeeded
            ? LocalRedirect(MemorySmithLocalAuthService.SanitizeReturnUrl(request.ReturnUrl))
            : LocalRedirect($"/admin/setup?error=1&returnUrl={Uri.EscapeDataString(MemorySmithLocalAuthService.SanitizeReturnUrl(request.ReturnUrl))}");
    }

    [HttpGet("users")]
    [Authorize(Policy = MemorySmithPolicies.CanManageUsers)]
    public async Task<ActionResult<PagedResult<UserAccount>>> Users([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        return Ok(await _database.Users.ListAsync(new UserQuery(search, page, pageSize), cancellationToken));
    }

    [HttpGet("users/{userId}/roles")]
    [Authorize(Policy = MemorySmithPolicies.CanManageUsers)]
    public async Task<ActionResult<IReadOnlyList<RoleRecord>>> UserRoles(string userId, CancellationToken cancellationToken)
    {
        return Ok(await _database.Roles.GetRolesForUserAsync(userId, cancellationToken));
    }

    [HttpPost("users/{userId}/roles/{roleName}")]
    [Authorize(Policy = MemorySmithPolicies.CanManageUsers)]
    public async Task<IActionResult> AssignRole(string userId, string roleName, CancellationToken cancellationToken)
    {
        await _database.Roles.AssignRoleAsync(userId, roleName, _currentUser.UserId, cancellationToken);
        await _audit.RecordAsync("role.assigned", "User", userId, MemorySmithAuditOutcomes.Success, details: new { roleName }, cancellationToken: cancellationToken);
        return NoContent();
    }

    [HttpDelete("users/{userId}/roles/{roleName}")]
    [Authorize(Policy = MemorySmithPolicies.CanManageUsers)]
    public async Task<IActionResult> RemoveRole(string userId, string roleName, CancellationToken cancellationToken)
    {
        await _database.Roles.RemoveRoleAsync(userId, roleName, _currentUser.UserId, cancellationToken);
        await _audit.RecordAsync("role.removed", "User", userId, MemorySmithAuditOutcomes.Success, details: new { roleName }, cancellationToken: cancellationToken);
        return NoContent();
    }

    [HttpGet("roles")]
    [Authorize(Policy = MemorySmithPolicies.CanManageUsers)]
    public async Task<ActionResult<IReadOnlyList<RoleRecord>>> Roles(CancellationToken cancellationToken)
    {
        return Ok(await _database.Roles.ListRolesAsync(cancellationToken));
    }

    [HttpGet("providers")]
    [Authorize(Policy = MemorySmithPolicies.CanAdminMemorySmith)]
    public async Task<ActionResult<IReadOnlyList<AuthProviderRecord>>> Providers(CancellationToken cancellationToken)
    {
        return Ok(await _database.ProviderLinks.ListProvidersAsync(cancellationToken));
    }

    [HttpPost("providers/{providerName}/enabled")]
    [Authorize(Policy = MemorySmithPolicies.CanAdminMemorySmith)]
    public async Task<IActionResult> SetProviderEnabled(string providerName, [FromBody] ProviderEnabledRequest request, CancellationToken cancellationToken)
    {
        await _database.ProviderLinks.SetProviderEnabledAsync(providerName, request.Enabled, _currentUser.UserId, cancellationToken);
        await _audit.RecordAsync("provider.enabled.changed", "Provider", providerName, MemorySmithAuditOutcomes.Success, details: request, cancellationToken: cancellationToken);
        return NoContent();
    }

    [HttpGet("audit")]
    [Authorize(Policy = MemorySmithPolicies.CanViewAudit)]
    public async Task<ActionResult<PagedResult<AuditLogEntry>>> Audit([FromQuery] string? action, [FromQuery] string? targetKind, [FromQuery] string? targetId, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken cancellationToken = default)
    {
        return Ok(await _database.AuditLogs.QueryAsync(new AuditLogQuery(Action: action, TargetKind: targetKind, TargetId: targetId, Page: page, PageSize: pageSize), cancellationToken));
    }

    [HttpGet("history/{targetKind}/{targetId}")]
    [Authorize(Policy = MemorySmithPolicies.CanRestoreHistory)]
    public async Task<ActionResult<IReadOnlyList<VersionHistoryEntry>>> History(string targetKind, string targetId, CancellationToken cancellationToken)
    {
        return Ok(await _database.VersionHistory.GetHistoryAsync(targetKind, targetId, cancellationToken));
    }
}

public sealed class SetupAdminFormRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Password { get; set; } = string.Empty;
    public string? BootstrapToken { get; set; }
    public string? ReturnUrl { get; set; }
}

public sealed record ProviderEnabledRequest(bool Enabled);
