using MemorySmith.App.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly MemorySmithLocalAuthService _auth;
    private readonly ICurrentUserContext _currentUser;

    public AuthController(MemorySmithLocalAuthService auth, ICurrentUserContext currentUser)
    {
        _auth = auth;
        _currentUser = currentUser;
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
    public IActionResult ExternalChallenge([FromQuery] string scheme, [FromQuery] string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(scheme))
            return BadRequest("scheme is required");
        var safeReturn = MemorySmithLocalAuthService.SanitizeReturnUrl(returnUrl);
        var properties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties
        {
            RedirectUri = safeReturn
        };
        return Challenge(properties, scheme);
    }
}

public sealed class LoginFormRequest
{
    public string UserNameOrEmail { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}
