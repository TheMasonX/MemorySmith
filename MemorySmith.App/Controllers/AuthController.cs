using MemorySmith.App.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly MemorySmithLocalAuthService _auth;
    private readonly ICurrentUserContext _currentUser;
    private readonly IAuthenticationSchemeProvider _schemeProvider;
    private readonly IOptionsMonitor<MemorySmithOptions> _options;

    public AuthController(
        MemorySmithLocalAuthService auth,
        ICurrentUserContext currentUser,
        IAuthenticationSchemeProvider schemeProvider,
        IOptionsMonitor<MemorySmithOptions> options)
    {
        _auth = auth;
        _currentUser = currentUser;
        _schemeProvider = schemeProvider;
        _options = options;
    }

    [HttpGet("me")]
    [AllowAnonymous]
    public async Task<ActionResult<CurrentUserResponse>> Me(CancellationToken cancellationToken)
    {
        return Ok(new CurrentUserResponse(
            _currentUser.IsAuthenticated,
            _currentUser.UserId,
            _currentUser.DisplayName,
            _currentUser.Roles,
            await _auth.NeedsSetupAsync(cancellationToken)));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [Consumes("application/json")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await _auth.SignInAsync(request, cancellationToken);
        return result.Succeeded ? Ok(new { ok = true }) : Unauthorized(new { error = result.Error });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [Consumes("application/x-www-form-urlencoded")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> LoginForm([FromForm] LoginFormRequest request, CancellationToken cancellationToken)
    {
        var result = await _auth.SignInAsync(new LoginRequest(request.UserNameOrEmail, request.Password, request.RememberMe, request.ReturnUrl), cancellationToken);
        return result.Succeeded
            ? LocalRedirect(MemorySmithLocalAuthService.SanitizeReturnUrl(request.ReturnUrl))
            : LocalRedirect($"/login?error=1&returnUrl={Uri.EscapeDataString(MemorySmithLocalAuthService.SanitizeReturnUrl(request.ReturnUrl))}");
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromQuery] string? returnUrl = null)
    {
        await _auth.SignOutAsync();
        return LocalRedirect(MemorySmithLocalAuthService.SanitizeReturnUrl(returnUrl));
    }

    [HttpGet("challenge")]
    [AllowAnonymous]
    public async Task<IActionResult> ExternalChallenge([FromQuery] string scheme, [FromQuery] string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(scheme))
            return BadRequest("scheme is required");
        if (!IsAllowedExternalScheme(scheme) || await _schemeProvider.GetSchemeAsync(scheme) is null)
            return BadRequest("External sign-in provider is not configured.");
        if (_options.CurrentValue.Auth.RequireHttpsForRemoteAuth && !Request.IsHttps && !MemorySmithRequestGuardMiddleware.IsLoopback(HttpContext.Connection.RemoteIpAddress))
            return BadRequest("External sign-in requires HTTPS for remote requests.");

        var safeReturn = MemorySmithLocalAuthService.SanitizeReturnUrl(returnUrl);
        var properties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties
        {
            RedirectUri = safeReturn
        };
        if (_currentUser.IsAuthenticated && !string.IsNullOrWhiteSpace(_currentUser.UserId) && safeReturn.StartsWith("/profile", StringComparison.OrdinalIgnoreCase))
        {
            properties.Items[MemorySmithAuthProperties.LinkUserId] = _currentUser.UserId;
        }

        return Challenge(properties, scheme);
    }

    private bool IsAllowedExternalScheme(string scheme)
    {
        var providers = _options.CurrentValue.Auth.Providers;
        return string.Equals(scheme, "GitHub", StringComparison.OrdinalIgnoreCase)
            && providers.GitHub.Enabled
            && !string.IsNullOrWhiteSpace(providers.GitHub.ClientId);
    }
}

public sealed class LoginFormRequest
{
    public string UserNameOrEmail { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}
