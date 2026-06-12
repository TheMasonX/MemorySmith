using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MemorySmith.App.Hosting;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

/// <summary>
/// Behavioral coverage for the GitHub external-auth callback module (TSK-0282 / Audit #9).
/// These tests replace the old source-inspection assertion that merely grepped Program.cs for
/// symbol names: they drive <see cref="GitHubOAuthCallbackHandler"/> with a real (temp-file)
/// metadata database, a stubbed GitHub backchannel, and a DefaultHttpContext, then assert the
/// DURABLE evidence — login-history rows and hash-chained audit events — not source text.
/// </summary>
[TestFixture]
public class GitHubOAuthCallbackHandlerTests
{
    private string _tempDir = null!;
    private SqliteMemorySmithDatabase _database = null!;
    private ServiceProvider _services = null!;

    [SetUp]
    public async Task SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-oauth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _database = new SqliteMemorySmithDatabase(new DatabaseOptions
        {
            ConnectionString = $"Data Source={Path.Combine(_tempDir, "memorysmith.db")};Pooling=False",
            ApplyMigrationsOnStartup = true,
            UseWal = false
        });
        await _database.InitializeAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddSingleton<IMemorySmithDatabase>(_database);
        services.AddHttpContextAccessor();
        services.AddSingleton<ICurrentUserContext, HttpCurrentUserContext>();
        services.AddSingleton<IOptionsMonitor<MemorySmithOptions>>(new StaticOptionsMonitor<MemorySmithOptions>(new MemorySmithOptions()));
        services.AddSingleton(Options.Create(new MemorySmithOptions()));
        services.AddSingleton<AuditLogService>();
        services.AddSingleton<ExternalAuthOutcomeRecorder>();
        _services = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public async Task FirstSignIn_CreatesAccount_AssignsFirstAdmin_AndRecordsDurableSuccess()
    {
        var handler = new GitHubOAuthCallbackHandler();
        var ctx = CreateTicketContext("""{"id":12345,"login":"octo","email":"octo@example.com","name":"Octo Cat"}""");

        await handler.OnCreatingTicketAsync(ctx);

        var link = await _database.ProviderLinks.GetByProviderSubjectAsync(MemorySmithProviders.GitHub, "12345", CancellationToken.None);
        Assert.That(link, Is.Not.Null, "first sign-in must create a provider link");
        var user = await _database.Users.GetByIdAsync(link!.UserId, CancellationToken.None);
        var roles = await _database.Roles.GetRolesForUserAsync(link.UserId, CancellationToken.None);
        var logins = await _database.LoginHistory.QueryAsync(new LoginHistoryQuery(ProviderName: MemorySmithProviders.GitHub), CancellationToken.None);
        var audits = await _database.AuditLogs.QueryAsync(new AuditLogQuery(Action: "auth.login.succeeded"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(user, Is.Not.Null);
            Assert.That(user!.DisplayName, Is.EqualTo("Octo Cat"));
            Assert.That(roles.Select(r => r.Name), Does.Contain(MemorySmithRoles.Admin),
                "the first GitHub sign-in on an empty database must bootstrap the first admin");
            Assert.That(logins.Data.Count(entry => entry.Succeeded), Is.EqualTo(1),
                "success must persist a durable login-history row");
            Assert.That(audits.Data, Has.Count.EqualTo(1),
                "success must persist the auth.login.succeeded audit event");
            Assert.That(ctx.Identity!.Claims.Select(c => c.Type), Does.Contain(ClaimTypes.NameIdentifier));
            Assert.That(ctx.Identity.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value),
                Does.Contain(MemorySmithRoles.Admin));
            Assert.That(ctx.Identity.Claims.Select(c => c.Type), Does.Contain("security_stamp"));
        });
    }

    [Test]
    public async Task LinkConflict_RecordsDurableFailure_AndFailsTicket()
    {
        var existingUserId = await SeedUserAsync("Existing Owner");
        await SeedLinkAsync(existingUserId, "999");
        var otherUserId = await SeedUserAsync("Other User");

        var handler = new GitHubOAuthCallbackHandler();
        var properties = new AuthenticationProperties();
        properties.Items[MemorySmithAuthProperties.LinkUserId] = otherUserId;
        var ctx = CreateTicketContext("""{"id":999,"login":"taken"}""", properties);

        await handler.OnCreatingTicketAsync(ctx);

        var logins = await _database.LoginHistory.QueryAsync(new LoginHistoryQuery(ProviderName: MemorySmithProviders.GitHub), CancellationToken.None);
        var audits = await _database.AuditLogs.QueryAsync(new AuditLogQuery(Action: "auth.login.failed"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Result?.Failure, Is.Not.Null, "a link conflict must fail the ticket");
            Assert.That(ctx.Result!.Failure!.Message, Does.Contain("already linked"));
            Assert.That(logins.Data.Single().Succeeded, Is.False);
            Assert.That(logins.Data.Single().FailureCode, Is.EqualTo("link_conflict"),
                "the failure must persist durably with its failure code");
            Assert.That(audits.Data, Has.Count.EqualTo(1),
                "the failure must persist the auth.login.failed audit event");
        });
    }

    [Test]
    public async Task DisabledLinkedUser_RecordsDurableFailure_AndFailsTicket()
    {
        var userId = await SeedUserAsync("Disabled Owner", disabled: true);
        await SeedLinkAsync(userId, "777");

        var handler = new GitHubOAuthCallbackHandler();
        var ctx = CreateTicketContext("""{"id":777,"login":"ghost"}""");

        await handler.OnCreatingTicketAsync(ctx);

        var logins = await _database.LoginHistory.QueryAsync(new LoginHistoryQuery(ProviderName: MemorySmithProviders.GitHub), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Result?.Failure, Is.Not.Null, "a disabled account must fail the ticket");
            Assert.That(logins.Data.Single().Succeeded, Is.False);
            Assert.That(logins.Data.Single().FailureCode, Is.EqualTo("disabled"));
        });
    }

    [Test]
    public async Task RemoteFailure_RecordsDurableFailure_AndRedirectsToLogin()
    {
        var handler = new GitHubOAuthCallbackHandler();
        var httpContext = new DefaultHttpContext { RequestServices = _services };
        var scheme = new AuthenticationScheme("GitHub", "GitHub", typeof(OAuthHandler<OAuthOptions>));
        var ctx = new RemoteFailureContext(httpContext, scheme, new OAuthOptions(), new InvalidOperationException("access denied"))
        {
            Properties = new AuthenticationProperties()
        };

        await handler.OnRemoteFailureAsync(ctx);

        var logins = await _database.LoginHistory.QueryAsync(new LoginHistoryQuery(ProviderName: MemorySmithProviders.GitHub), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(logins.Data.Single().Succeeded, Is.False);
            Assert.That(logins.Data.Single().FailureCode, Is.EqualTo("remote_failure"),
                "remote OAuth failures must persist durable evidence");
            Assert.That(httpContext.Response.StatusCode, Is.EqualTo((int)HttpStatusCode.Found));
            Assert.That(httpContext.Response.Headers.Location.ToString(), Is.EqualTo("/login?error=1"));
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> SeedUserAsync(string displayName, bool disabled = false)
    {
        var userId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        await _database.Users.CreateAsync(new UserAccount
        {
            UserId = userId,
            DisplayName = displayName,
            NormalizedDisplayName = displayName.ToUpperInvariant(),
            LocalPasswordEnabled = false,
            IsDisabled = disabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        }, CancellationToken.None);
        return userId;
    }

    private async Task SeedLinkAsync(string userId, string providerSubject) =>
        await _database.ProviderLinks.LinkAsync(new ProviderLink
        {
            LinkId = Guid.NewGuid().ToString("N"),
            UserId = userId,
            ProviderName = MemorySmithProviders.GitHub,
            ProviderSubject = providerSubject,
            ProviderDisplayName = "linked",
            LinkedAtUtc = DateTime.UtcNow
        }, CancellationToken.None);

    private OAuthCreatingTicketContext CreateTicketContext(string githubUserJson, AuthenticationProperties? properties = null)
    {
        var httpContext = new DefaultHttpContext { RequestServices = _services };
        var principal = new ClaimsPrincipal(new ClaimsIdentity("GitHub"));
        var options = new OAuthOptions
        {
            UserInformationEndpoint = "https://api.github.com/user",
            ClaimsIssuer = "GitHub"
        };
        var backchannel = new HttpClient(new StubGitHubProfileHandler(githubUserJson));
        var tokens = OAuthTokenResponse.Success(JsonDocument.Parse("""{"access_token":"test-token"}"""));
        var scheme = new AuthenticationScheme("GitHub", "GitHub", typeof(OAuthHandler<OAuthOptions>));
        using var emptyUser = JsonDocument.Parse("{}");
        return new OAuthCreatingTicketContext(
            principal,
            properties ?? new AuthenticationProperties(),
            httpContext,
            scheme,
            options,
            backchannel,
            tokens,
            emptyUser.RootElement.Clone());
    }

    /// <summary>Backchannel stub: answers the GitHub /user profile fetch with canned JSON.</summary>
    private sealed class StubGitHubProfileHandler(string profileJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(profileJson, Encoding.UTF8, "application/json")
            });
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
