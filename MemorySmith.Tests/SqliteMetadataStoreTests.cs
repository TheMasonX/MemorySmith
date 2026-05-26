using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Net;

namespace MemorySmith.Tests;

[TestFixture]
public class SqliteMetadataStoreTests
{
    private string _tempDir = null!;
    private SqliteMemorySmithDatabase _database = null!;

    [SetUp]
    public async Task SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _database = new SqliteMemorySmithDatabase(new DatabaseOptions
        {
            ConnectionString = $"Data Source={Path.Combine(_tempDir, "memorysmith.db")};Pooling=False",
            ApplyMigrationsOnStartup = true,
            UseWal = false
        });
        await _database.InitializeAsync(CancellationToken.None);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public async Task InitializeAsync_SeedsSystemRolesAndProviders()
    {
        var roles = await _database.Roles.ListRolesAsync(CancellationToken.None);
        var providers = await _database.ProviderLinks.ListProvidersAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(roles.Select(role => role.Name), Is.SupersetOf(new[] { MemorySmithRoles.Viewer, MemorySmithRoles.Editor, MemorySmithRoles.Admin }));
            Assert.That(providers.Select(provider => provider.ProviderName), Is.SupersetOf(new[] { MemorySmithProviders.GitHub, MemorySmithProviders.Google, MemorySmithProviders.Microsoft, MemorySmithProviders.LocalPassword, MemorySmithProviders.ApiToken }));
            Assert.That(providers.Single(provider => provider.ProviderName == MemorySmithProviders.LocalPassword).IsEnabled, Is.True);
        });
    }

    [Test]
    public async Task UserRolesAndProviderLinks_RoundTrip()
    {
        var user = new UserAccount
        {
            UserId = "user-1",
            DisplayName = "Admin User",
            NormalizedDisplayName = "ADMIN USER",
            Email = "admin@example.test",
            NormalizedEmail = "ADMIN@EXAMPLE.TEST",
            LocalPasswordEnabled = true,
            SecurityStamp = "stamp",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await _database.Users.CreateAsync(user, CancellationToken.None);
        await _database.Roles.AssignRoleAsync(user.UserId, MemorySmithRoles.Admin, null, CancellationToken.None);
        await _database.ProviderLinks.LinkAsync(new ProviderLink
        {
            LinkId = "link-1",
            UserId = user.UserId,
            ProviderName = MemorySmithProviders.LocalPassword,
            ProviderSubject = user.UserId,
            ProviderDisplayName = user.DisplayName,
            LinkedAtUtc = DateTime.UtcNow
        }, CancellationToken.None);

        var loaded = await _database.Users.GetByNormalizedEmailAsync("ADMIN@EXAMPLE.TEST", CancellationToken.None);
        var roles = await _database.Roles.GetRolesForUserAsync(user.UserId, CancellationToken.None);
        var link = await _database.ProviderLinks.GetByProviderSubjectAsync(MemorySmithProviders.LocalPassword, user.UserId, CancellationToken.None);
        var hasAnyAdmin = await _database.Users.HasAnyAdminAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.Not.Null);
            Assert.That(roles.Select(role => role.Name), Does.Contain(MemorySmithRoles.Admin));
            Assert.That(link, Is.Not.Null);
            Assert.That(hasAnyAdmin, Is.True);
        });
    }

    [Test]
    public async Task AuditAndVersionHistory_AreQueryable()
    {
        var audit = new AuditLogEntry
        {
            AuditId = "audit-1",
            OccurredAtUtc = DateTime.UtcNow,
            RecordedAtUtc = DateTime.UtcNow,
            ActorKind = MemorySmithActorKinds.System,
            Action = "memory.updated",
            TargetKind = "Memory",
            TargetId = "memory-1",
            Outcome = MemorySmithAuditOutcomes.Success,
            AuditHash = AuditLogService.ComputeHash("audit-1")
        };

        await _database.AuditLogs.AppendAsync(audit, CancellationToken.None);
        var version = await _database.VersionHistory.CreateVersionAsync(new VersionCreateRequest
        {
            TargetKind = "Memory",
            TargetId = "memory-1",
            Format = "memorysmith.memory-snapshot.v1",
            HistoryPath = "memories/memory-1/000001.snapshot.json",
            AfterHash = "after",
            ByteSize = 42,
            AuditId = audit.AuditId
        }, CancellationToken.None);

        var auditResults = await _database.AuditLogs.QueryAsync(new AuditLogQuery(TargetKind: "Memory", TargetId: "memory-1"), CancellationToken.None);
        var history = await _database.VersionHistory.GetHistoryAsync("Memory", "memory-1", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(audit.Sequence, Is.GreaterThan(0));
            Assert.That(auditResults.Data.Single().Action, Is.EqualTo("memory.updated"));
            Assert.That(version.VersionNumber, Is.EqualTo(1));
            Assert.That(history.Single().HistoryPath, Is.EqualTo("memories/memory-1/000001.snapshot.json"));
        });
    }

    [Test]
    public async Task AuditedPageService_CapturesAuditMetadataJsonlMirrorAndHistoryArtifact()
    {
        var options = new MemorySmithOptions
        {
            DataProtectionKeysPath = Path.Combine(_tempDir, "Keys"),
            Audit = new AuditOptions
            {
                JsonlPath = Path.Combine(_tempDir, "Events", "audit-{yyyy}-W{week}.jsonl"),
                JsonlEnabled = true
            },
            History = new HistoryOptions
            {
                RootPath = Path.Combine(_tempDir, ".history")
            }
        };
        var monitor = new TestOptionsMonitor<MemorySmithOptions>(options);
        await SeedUserAsync("admin-user", "Admin User", "ADMIN USER");
        var currentUser = new FakeCurrentUserContext();
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var audit = new AuditLogService(_database, currentUser, httpContextAccessor, monitor);
        var history = new VersionHistoryService(_database, currentUser, monitor);
        var pages = new AuditedPageService(new FilePageService(Path.Combine(_tempDir, "Pages")), audit, history);

        var saved = await pages.SaveAsync(new PageSaveRequest("audit-page", "Audit Page", "Audit page body."), CancellationToken.None);
        var audits = await _database.AuditLogs.QueryAsync(new AuditLogQuery(TargetKind: "Page", TargetId: saved.Slug), CancellationToken.None);
        var versions = await _database.VersionHistory.GetHistoryAsync("Page", saved.Slug, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(audits.Data.Single().Action, Is.EqualTo("page.created"));
            Assert.That(versions.Single().Format, Is.EqualTo("memorysmith.page-snapshot.v1"));
            Assert.That(File.Exists(Path.Combine(options.History.RootPath, versions.Single().HistoryPath.Replace('/', Path.DirectorySeparatorChar))), Is.True);
            Assert.That(Directory.EnumerateFiles(Path.Combine(_tempDir, "Events"), "audit-*.jsonl").Single(), Does.Exist);
        });
    }

    [Test]
    public async Task AuditAndLoginServices_PersistRequestMetadataWithoutRawIpOrUserAgent()
    {
        var options = new MemorySmithOptions
        {
            DataProtectionKeysPath = Path.Combine(_tempDir, "Keys"),
            Audit = new AuditOptions
            {
                JsonlEnabled = false
            }
        };
        var monitor = new TestOptionsMonitor<MemorySmithOptions>(options);
        await SeedUserAsync("admin-user", "Admin User", "ADMIN USER");
        var currentUser = new FakeCurrentUserContext();
        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = "request-0173"
        };
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.24");
        httpContext.Request.Headers.UserAgent = "MemorySmith.Tests/0173";
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        var audit = new AuditLogService(_database, currentUser, httpContextAccessor, monitor);
        var auth = new MemorySmithLocalAuthService(_database, httpContextAccessor, audit, monitor);

        using var activity = new Activity("request-metadata-test");
        activity.Start();
        var expectedCorrelationId = activity.TraceId.ToString();

        await auth.SignInAsync(new LoginRequest("missing@example.test", "wrong-password"), CancellationToken.None);
        var loginHistory = await _database.LoginHistory.QueryAsync(new LoginHistoryQuery(ProviderName: MemorySmithProviders.LocalPassword), CancellationToken.None);
        var audits = await _database.AuditLogs.QueryAsync(new AuditLogQuery(Action: "auth.login.failed"), CancellationToken.None);
        var login = loginHistory.Data.Single();
        var auditEntry = audits.Data.Single();

        Assert.Multiple(() =>
        {
            Assert.That(login.RequestId, Is.EqualTo("request-0173"));
            Assert.That(login.IpHash, Does.Match("^[a-f0-9]{64}$"));
            Assert.That(login.UserAgentHash, Does.Match("^[a-f0-9]{64}$"));
            Assert.That(login.IpHash, Is.Not.EqualTo("203.0.113.24"));
            Assert.That(login.UserAgentHash, Is.Not.EqualTo("MemorySmith.Tests/0173"));
            Assert.That(auditEntry.RequestId, Is.EqualTo("request-0173"));
            Assert.That(auditEntry.CorrelationId, Is.EqualTo(expectedCorrelationId));
            Assert.That(auditEntry.IpHash, Is.EqualTo(login.IpHash));
            Assert.That(auditEntry.UserAgentHash, Is.EqualTo(login.UserAgentHash));
        });
    }

    private sealed class FakeCurrentUserContext : ICurrentUserContext
    {
        public string? UserId => "admin-user";
        public string DisplayName => "Admin User";
        public string AuthScheme => "Test";
        public string? Provider => MemorySmithProviders.LocalPassword;
        public bool IsAuthenticated => true;
        public IReadOnlyList<string> Roles => [MemorySmithRoles.Admin];
        public string ActorKind => MemorySmithActorKinds.User;
    }

    private async Task SeedUserAsync(string userId, string displayName, string normalizedDisplayName)
    {
        await _database.Users.CreateAsync(new UserAccount
        {
            UserId = userId,
            DisplayName = displayName,
            NormalizedDisplayName = normalizedDisplayName,
            LocalPasswordEnabled = false,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        }, CancellationToken.None);
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
