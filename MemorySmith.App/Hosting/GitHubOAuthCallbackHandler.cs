namespace MemorySmith.App.Hosting;

using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;

/// <summary>
/// GitHub external-auth callback logic, extracted from the inline OAuth event lambdas in
/// Program.cs (TSK-0282 / Audit #9 candidate 1) so the behaviour has a real, unit-testable seam:
/// account resolution (existing link / explicit link request / first sign-in), first-admin role
/// bootstrap, durable success/failure evidence via <see cref="ExternalAuthOutcomeRecorder"/>, and
/// claims construction.
///
/// Services are resolved from <c>HttpContext.RequestServices</c> — exactly as the original lambdas
/// did — which is what makes the handler testable with a <c>DefaultHttpContext</c> carrying a
/// purpose-built service provider and a stubbed backchannel <c>HttpClient</c>.
/// </summary>
public sealed class GitHubOAuthCallbackHandler
{
    public async Task OnCreatingTicketAsync(OAuthCreatingTicketContext ctx)
    {
        if (ctx.Identity == null) return;
        // Fetch GitHub user profile
        var req = new HttpRequestMessage(HttpMethod.Get, ctx.Options.UserInformationEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("MemorySmith", "1.0"));
        var res = await ctx.Backchannel.SendAsync(req, ctx.HttpContext.RequestAborted);
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        var root = doc.RootElement;
        var githubSubject = root.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : null;
        var githubLogin = root.TryGetProperty("login", out var loginEl) ? loginEl.GetString() : null;
        var githubEmail = root.TryGetProperty("email", out var emailEl) && emailEl.ValueKind == JsonValueKind.String ? emailEl.GetString() : null;
        var githubDisplayName = root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(nameEl.GetString()) ? nameEl.GetString() : null;
        if (githubSubject == null) return;
        var db = ctx.HttpContext.RequestServices.GetRequiredService<IMemorySmithDatabase>();
        var externalAuthOutcomes = ctx.HttpContext.RequestServices.GetRequiredService<ExternalAuthOutcomeRecorder>();
        var msOpts = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<MemorySmithOptions>>().Value;
        var ct = ctx.HttpContext.RequestAborted;
        var displayName = githubDisplayName ?? githubLogin ?? githubSubject;
        var linkUserId = ctx.Properties?.Items.TryGetValue(MemorySmithAuthProperties.LinkUserId, out var requestedUserId) == true
            ? requestedUserId
            : null;
        var requestedLink = !string.IsNullOrWhiteSpace(linkUserId);
        var link = await db.ProviderLinks.GetByProviderSubjectAsync(MemorySmithProviders.GitHub, githubSubject, ct);
        string internalUserId;
        if (link != null)
        {
            if (!string.IsNullOrWhiteSpace(linkUserId) && !string.Equals(link.UserId, linkUserId, StringComparison.Ordinal))
            {
                const string message = "This GitHub account is already linked to another MemorySmith user.";
                await externalAuthOutcomes.RecordFailureIfNeededAsync(
                    ctx.HttpContext,
                    MemorySmithProviders.GitHub,
                    githubSubject,
                    link.UserId,
                    "link_conflict",
                    message,
                    requestedLink: true,
                    details: new { requestedLinkUserId = linkUserId, existingLinkedUserId = link.UserId },
                    cancellationToken: ct);
                ctx.Fail(message);
                return;
            }

            internalUserId = link.UserId;
        }
        else if (!string.IsNullOrWhiteSpace(linkUserId))
        {
            var linkedUser = await db.Users.GetByIdAsync(linkUserId, ct);
            if (linkedUser is null || linkedUser.IsDisabled)
            {
                const string message = "The MemorySmith account for this link request is not available.";
                await externalAuthOutcomes.RecordFailureIfNeededAsync(
                    ctx.HttpContext,
                    MemorySmithProviders.GitHub,
                    githubSubject,
                    linkUserId,
                    linkedUser is null ? "link_user_missing" : "disabled",
                    message,
                    requestedLink: true,
                    details: new { requestedLinkUserId = linkUserId },
                    cancellationToken: ct);
                ctx.Fail(message);
                return;
            }

            internalUserId = linkedUser.UserId;
            await db.ProviderLinks.LinkAsync(new ProviderLink
            {
                LinkId = Guid.NewGuid().ToString("N"),
                UserId = internalUserId,
                ProviderName = MemorySmithProviders.GitHub,
                ProviderSubject = githubSubject,
                ProviderDisplayName = githubLogin ?? displayName,
                ProviderEmail = githubEmail,
                LinkedAtUtc = DateTime.UtcNow
            }, ct);
        }
        else
        {
            internalUserId = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;
            await db.Users.CreateAsync(new UserAccount
            {
                UserId = internalUserId,
                DisplayName = displayName,
                NormalizedDisplayName = displayName.ToUpperInvariant(),
                Email = githubEmail,
                NormalizedEmail = githubEmail?.ToUpperInvariant(),
                LocalPasswordEnabled = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            }, ct);
            await db.ProviderLinks.LinkAsync(new ProviderLink
            {
                LinkId = Guid.NewGuid().ToString("N"),
                UserId = internalUserId,
                ProviderName = MemorySmithProviders.GitHub,
                ProviderSubject = githubSubject,
                ProviderDisplayName = githubLogin ?? displayName,
                ProviderEmail = githubEmail,
                LinkedAtUtc = now
            }, ct);
            var isFirstAdmin = !await db.Users.HasAnyAdminAsync(ct);
            var assignedRole = isFirstAdmin ? MemorySmithRoles.Admin : MemorySmithPermissionHandler.NormalizeAuthenticatedDefaultRole(msOpts.Auth.AuthenticatedDefaultRole);
            await db.Roles.AssignRoleAsync(internalUserId, assignedRole, null, ct);
        }
        var roles = await db.Roles.GetRolesForUserAsync(internalUserId, ct);
        var user = await db.Users.GetByIdAsync(internalUserId, ct);
        if (user is null || user.IsDisabled)
        {
            const string message = "The MemorySmith account is disabled or no longer exists.";
            await externalAuthOutcomes.RecordFailureIfNeededAsync(
                ctx.HttpContext,
                MemorySmithProviders.GitHub,
                githubSubject,
                user?.UserId ?? internalUserId,
                user is null ? "user_missing" : "disabled",
                message,
                requestedLink,
                cancellationToken: ct);
            ctx.Fail(message);
            return;
        }

        var resolvedUser = user;
        var loginAtUtc = DateTime.UtcNow;
        resolvedUser.LastLoginAtUtc = loginAtUtc;
        resolvedUser.UpdatedAtUtc = loginAtUtc;
        await db.Users.UpdateAsync(resolvedUser, ct);
        // RecordSuccessAsync persists durable login-history AND audit evidence
        // (auth.login.succeeded) with request metadata — richer than a bare
        // LoginHistory.RecordAsync call.
        await externalAuthOutcomes.RecordSuccessAsync(ctx.HttpContext, MemorySmithProviders.GitHub, githubSubject, resolvedUser, roles, requestedLink, ct);
        ctx.Identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, internalUserId, ClaimValueTypes.String, ctx.Options.ClaimsIssuer));
        ctx.Identity.AddClaim(new Claim(ClaimTypes.Name, resolvedUser.DisplayName, ClaimValueTypes.String, ctx.Options.ClaimsIssuer));
        if (resolvedUser.Email is not null)
            ctx.Identity.AddClaim(new Claim(ClaimTypes.Email, resolvedUser.Email, ClaimValueTypes.String, ctx.Options.ClaimsIssuer));
        ctx.Identity.AddClaim(new Claim("provider", MemorySmithProviders.GitHub, ClaimValueTypes.String, ctx.Options.ClaimsIssuer));
        ctx.Identity.AddClaim(new Claim("security_stamp", resolvedUser.SecurityStamp, ClaimValueTypes.String, ctx.Options.ClaimsIssuer));
        foreach (var role in roles)
            ctx.Identity.AddClaim(new Claim(ClaimTypes.Role, role.Name, ClaimValueTypes.String, ctx.Options.ClaimsIssuer));
    }

    public async Task OnRemoteFailureAsync(RemoteFailureContext ctx)
    {
        var linkUserId = ctx.Properties?.Items.TryGetValue(MemorySmithAuthProperties.LinkUserId, out var requestedUserId) == true
            ? requestedUserId
            : null;
        var externalAuthOutcomes = ctx.HttpContext.RequestServices.GetRequiredService<ExternalAuthOutcomeRecorder>();
        await externalAuthOutcomes.RecordFailureIfNeededAsync(
            ctx.HttpContext,
            MemorySmithProviders.GitHub,
            providerSubject: null,
            targetUserId: linkUserId,
            failureCode: "remote_failure",
            message: ctx.Failure?.Message ?? "External sign-in failed.",
            requestedLink: !string.IsNullOrWhiteSpace(linkUserId),
            details: string.IsNullOrWhiteSpace(linkUserId) ? null : new { requestedLinkUserId = linkUserId },
            cancellationToken: ctx.HttpContext.RequestAborted);
        ctx.HandleResponse();
        var returnUri = ctx.Properties?.RedirectUri;
        var target = !string.IsNullOrWhiteSpace(returnUri) && returnUri.StartsWith("/profile", StringComparison.Ordinal)
            ? $"/profile?error={Uri.EscapeDataString(ctx.Failure?.Message ?? "External sign-in failed.")}"
            : "/login?error=1";
        ctx.Response.Redirect(target);
    }
}
