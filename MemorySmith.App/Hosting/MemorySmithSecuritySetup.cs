namespace MemorySmith.App.Hosting;

using MemorySmith.App.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

/// <summary>
/// Authentication (cookie + optional GitHub OAuth), authorization policies, login rate limiting,
/// and data-protection key persistence. Extracted from Program.cs (TSK-0282) — the OAuth event
/// bodies live in <see cref="GitHubOAuthCallbackHandler"/> so they are unit-testable.
/// </summary>
public static class MemorySmithSecuritySetup
{
    public static WebApplicationBuilder AddMemorySmithSecurity(this WebApplicationBuilder builder)
    {
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddCascadingAuthenticationState();
        var authProviders = builder.Configuration.GetSection("MemorySmith:Auth:Providers").Get<AuthProviderOptions>() ?? new AuthProviderOptions();
        var auth = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/login";
                options.AccessDeniedPath = "/login";
                options.Cookie.Name = "MemorySmith.Auth";
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.SlidingExpiration = true;
            });
        if (authProviders.GitHub.Enabled && !string.IsNullOrEmpty(authProviders.GitHub.ClientId))
        {
            auth.AddOAuth("GitHub", options =>
            {
                options.ClientId = authProviders.GitHub.ClientId!;
                options.ClientSecret = authProviders.GitHub.ClientSecret ?? "";
                options.CallbackPath = new PathString("/signin-github");
                options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
                options.TokenEndpoint = "https://github.com/login/oauth/access_token";
                options.UserInformationEndpoint = "https://api.github.com/user";
                options.Scope.Add("read:user");
                options.Scope.Add("user:email");
                options.SaveTokens = true;
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                // Durable success/failure evidence recording and account resolution live in
                // GitHubOAuthCallbackHandler (real seam, unit-tested) — these events only route.
                options.Events = new OAuthEvents
                {
                    OnCreatingTicket = ctx => ctx.HttpContext.RequestServices
                        .GetRequiredService<GitHubOAuthCallbackHandler>()
                        .OnCreatingTicketAsync(ctx),
                    OnRemoteFailure = ctx => ctx.HttpContext.RequestServices
                        .GetRequiredService<GitHubOAuthCallbackHandler>()
                        .OnRemoteFailureAsync(ctx)
                };
            });
        }
        builder.Services.AddSingleton<GitHubOAuthCallbackHandler>();
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthorization(options =>
        {
            AddPermissionPolicy(options, MemorySmithPolicies.CanViewMemorySmith, MemorySmithPermission.View);
            AddPermissionPolicy(options, MemorySmithPolicies.CanEditMemorySmith, MemorySmithPermission.Edit);
            AddPermissionPolicy(options, MemorySmithPolicies.CanAdminMemorySmith, MemorySmithPermission.Admin);
            AddPermissionPolicy(options, MemorySmithPolicies.CanManageUsers, MemorySmithPermission.ManageUsers);
            AddPermissionPolicy(options, MemorySmithPolicies.CanManageSettings, MemorySmithPermission.ManageSettings);
            AddPermissionPolicy(options, MemorySmithPolicies.CanViewAudit, MemorySmithPermission.ViewAudit);
            AddPermissionPolicy(options, MemorySmithPolicies.CanRestoreHistory, MemorySmithPermission.RestoreHistory);
            AddPermissionPolicy(options, MemorySmithPolicies.CanReadSourceBundle, MemorySmithPermission.ReadSourceBundle);
            AddPermissionPolicy(options, MemorySmithPolicies.CanUseChat, MemorySmithPermission.UseChat);
            AddPermissionPolicy(options, MemorySmithPolicies.CanApproveAgentWrites, MemorySmithPermission.ApproveAgentWrites);
        });
        builder.Services.AddSingleton<IAuthorizationHandler, MemorySmithPermissionHandler>();
        builder.Services.AddSingleton<ExternalAuthOutcomeRecorder>();
        builder.Services.AddRateLimiter(options =>
        {
            var authLimits = builder.Configuration.GetSection("MemorySmith:Auth:RateLimits").Get<AuthRateLimitOptions>() ?? new AuthRateLimitOptions();
            options.AddPolicy("login", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, authLimits.LoginPermitLimit),
                        Window = TimeSpan.FromMinutes(Math.Max(1, authLimits.LoginWindowMinutes)),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));
        });

        var dataProtectionKeysPath = builder.Configuration["MemorySmith:DataProtectionKeysPath"] ?? Path.Combine("..", "Data", "Keys");
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(dataProtectionKeysPath)));

        return builder;
    }

    private static void AddPermissionPolicy(AuthorizationOptions options, string name, MemorySmithPermission permission) =>
        options.AddPolicy(name, policy => policy.AddRequirements(new MemorySmithPermissionRequirement(permission)));
}
