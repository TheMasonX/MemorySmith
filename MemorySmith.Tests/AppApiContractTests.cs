using System.Net;
using System.Net.Http.Json;
using MemorySmith.App.Controllers;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;

namespace MemorySmith.Tests;

[TestFixture]
public class AppApiContractTests
{
    private string _tempDir = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-api-{Guid.NewGuid():N}");
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MemorySmith:DataPath"] = Path.Combine(_tempDir, "Memories"),
                        ["MemorySmith:PagesPath"] = Path.Combine(_tempDir, "Pages"),
                        ["MemorySmith:EventLogPath"] = Path.Combine(_tempDir, "Events", "audit.log"),
                        ["MemorySmith:DataProtectionKeysPath"] = Path.Combine(_tempDir, "Keys"),
                        ["MemorySmith:Database:ConnectionString"] = $"Data Source={Path.Combine(_tempDir, "memorysmith.db")};Pooling=False",
                        ["MemorySmith:Audit:JsonlPath"] = Path.Combine(_tempDir, "Events", "audit-{yyyy}-W{week}.jsonl"),
                        ["MemorySmith:History:RootPath"] = Path.Combine(_tempDir, ".history"),
                        ["MemorySmith:Maintenance:Enabled"] = "false"
                    });
                });
            });
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public async Task GetMemories_ClampsPageSizeAndKeepsRouteContract()
    {
        var response = await _client.GetFromJsonAsync<PagedResult<MemoryMetadata>>("/api/memories?page=-5&pageSize=500");

        Assert.Multiple(() =>
        {
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.Page, Is.EqualTo(1));
            Assert.That(response.PageSize, Is.EqualTo(100));
        });
    }

    [Test]
    public async Task PostMemory_WithInvalidBody_ReturnsValidationProblem()
    {
        var response = await _client.PostAsJsonAsync("/api/memories", new MemoryRecord
        {
            Id = "invalid-content",
            Title = "Invalid",
            Content = ""
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("Content"));
    }

    [Test]
    public async Task CreateGetIncrementDelete_FullApiWorkflow_PersistsRealFiles()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/memories", new MemoryRecord
        {
            Id = "workflow",
            Title = "Workflow",
            Content = "Real file-backed workflow",
            Tags = [" api ", "api"]
        });
        createResponse.EnsureSuccessStatusCode();

        var created = await createResponse.Content.ReadFromJsonAsync<MemoryRecord>();
        var loaded = await _client.GetFromJsonAsync<MemoryRecord>($"/api/memories/{created!.Id}");
        var usageResponse = await _client.PostAsync($"/api/memories/{created.Id}/usage", null);
        usageResponse.EnsureSuccessStatusCode();
        var deleteResponse = await _client.DeleteAsync($"/api/memories/{created.Id}");

        Assert.Multiple(() =>
        {
            Assert.That(createResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(loaded!.Tags, Is.EqualTo(new[] { "api" }));
            Assert.That(deleteResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(Directory.EnumerateFiles(Path.Combine(_tempDir, "Memories"), "*.json", SearchOption.AllDirectories), Is.Empty);
            Assert.That(Directory.EnumerateFiles(Path.Combine(_tempDir, ".history"), "*.snapshot.json", SearchOption.AllDirectories).Count(), Is.GreaterThanOrEqualTo(2));
        });
    }

    [Test]
    public async Task SharedApiKey_CanWriteAfterFirstAdminExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-api-key-{Guid.NewGuid():N}");
        const string apiKey = "contract-secret";
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MemorySmith:DataPath"] = Path.Combine(tempDir, "Memories"),
                        ["MemorySmith:PagesPath"] = Path.Combine(tempDir, "Pages"),
                        ["MemorySmith:EventLogPath"] = Path.Combine(tempDir, "Events", "audit.log"),
                        ["MemorySmith:DataProtectionKeysPath"] = Path.Combine(tempDir, "Keys"),
                        ["MemorySmith:Database:ConnectionString"] = $"Data Source={Path.Combine(tempDir, "memorysmith.db")};Pooling=False",
                        ["MemorySmith:Audit:JsonlPath"] = Path.Combine(tempDir, "Events", "audit-{yyyy}-W{week}.jsonl"),
                        ["MemorySmith:History:RootPath"] = Path.Combine(tempDir, ".history"),
                        ["MemorySmith:Maintenance:Enabled"] = "false",
                        ["MemorySmith:ApiKey"] = apiKey
                    });
                });
            });

        try
        {
            using var setupClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            setupClient.DefaultRequestHeaders.Add(MemorySmithRequestGuardMiddleware.ApiKeyHeaderName, apiKey);
            var setupResponse = await setupClient.PostAsJsonAsync("/api/admin/setup", new SetupAdminRequest("Admin User", "admin@example.test", "ThisIsAValidPassword123!"));
            setupResponse.EnsureSuccessStatusCode();

            using var apiClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            apiClient.DefaultRequestHeaders.Add(MemorySmithRequestGuardMiddleware.ApiKeyHeaderName, apiKey);
            var createResponse = await apiClient.PostAsJsonAsync("/api/memories", new MemoryRecord
            {
                Id = "api-key-write",
                Title = "API key write",
                Content = "Compatibility write path"
            });

            Assert.That(createResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task AdminPage_WithAnonymousAdminConfig_DoesNotRenderAdminWorkbenchForSignedOutUser()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-admin-page-{Guid.NewGuid():N}");
        var factory = CreateIsolatedFactory(tempDir, new Dictionary<string, string?>
        {
            ["MemorySmith:Auth:AnonymousAccess"] = MemorySmithRoles.Admin,
            ["MemorySmith:Auth:AuthenticatedDefaultRole"] = MemorySmithRoles.Admin,
            ["MemorySmith:Auth:AutoEditorForAuthenticatedUsers"] = "true"
        });

        try
        {
            using var setupClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var setupResponse = await setupClient.PostAsJsonAsync("/api/admin/setup", new SetupAdminRequest("Admin User", "admin@example.test", "ThisIsAValidPassword123!"));
            setupResponse.EnsureSuccessStatusCode();

            using var anonymousClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var pageResponse = await anonymousClient.GetAsync("/admin");
            var body = await pageResponse.Content.ReadAsStringAsync();

            Assert.Multiple(() =>
            {
                Assert.That(pageResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK).Or.EqualTo(HttpStatusCode.Redirect).Or.EqualTo(HttpStatusCode.Unauthorized).Or.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(body, Does.Not.Contain("Users, providers, settings, audit, history"));
                if (pageResponse.StatusCode == HttpStatusCode.OK)
                {
                    Assert.That(body, Does.Contain("Sign In"));
                }
            });
        }
        finally
        {
            await DisposeFactoryTempDirAsync(factory, tempDir);
        }
    }

    [Test]
    public async Task AdminRoleApi_WithAnonymousAdminConfig_RejectsSignedOutRoleChanges()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-admin-api-{Guid.NewGuid():N}");
        var factory = CreateIsolatedFactory(tempDir, new Dictionary<string, string?>
        {
            ["MemorySmith:Auth:AnonymousAccess"] = MemorySmithRoles.Admin,
            ["MemorySmith:Auth:AuthenticatedDefaultRole"] = MemorySmithRoles.Admin,
            ["MemorySmith:Auth:AutoEditorForAuthenticatedUsers"] = "true"
        });

        try
        {
            using var setupClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var setupResponse = await setupClient.PostAsJsonAsync("/api/admin/setup", new SetupAdminRequest("Admin User", "admin@example.test", "ThisIsAValidPassword123!"));
            setupResponse.EnsureSuccessStatusCode();

            using var anonymousClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var roleResponse = await anonymousClient.PostAsync($"/api/admin/users/{Guid.NewGuid():N}/roles/{MemorySmithRoles.Editor}", null);

            Assert.That(IsAuthChallenge(roleResponse.StatusCode), Is.True);
        }
        finally
        {
            await DisposeFactoryTempDirAsync(factory, tempDir);
        }
    }

    [Test]
    public async Task AdminApi_WithAuthDisabled_StillRejectsSignedOutAdminAccess()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-admin-auth-disabled-{Guid.NewGuid():N}");
        var factory = CreateIsolatedFactory(tempDir, new Dictionary<string, string?>
        {
            ["MemorySmith:Auth:Enabled"] = "false"
        });

        try
        {
            using var anonymousClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var usersResponse = await anonymousClient.GetAsync("/api/admin/users");
            var settingsResponse = await anonymousClient.GetAsync("/api/admin/settings");

            Assert.Multiple(() =>
            {
                Assert.That(IsAuthChallenge(usersResponse.StatusCode), Is.True);
                Assert.That(IsAuthChallenge(settingsResponse.StatusCode), Is.True);
            });
        }
        finally
        {
            await DisposeFactoryTempDirAsync(factory, tempDir);
        }
    }

    [Test]
    public async Task AdminSettings_UpdateRequiresAdminAndPersistsAllowedSetting()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-admin-settings-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(tempDir, "appsettings.LocalDevelopment.json");
        var factory = CreateIsolatedFactory(tempDir, new Dictionary<string, string?>
        {
            ["MemorySmith:SettingsOverridePath"] = settingsPath
        });

        try
        {
            using var anonymousClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var anonymousResponse = await anonymousClient.PutAsJsonAsync("/api/admin/settings", new AdminSettingUpdateRequest("MemorySmith:Chat:MaxToolIterations", "3"));

            using var adminClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var setupResponse = await adminClient.PostAsJsonAsync("/api/admin/setup", new SetupAdminRequest("Admin User", "admin@example.test", "ThisIsAValidPassword123!"));
            setupResponse.EnsureSuccessStatusCode();
            var updateResponse = await adminClient.PutAsJsonAsync("/api/admin/settings", new AdminSettingUpdateRequest("MemorySmith:Chat:MaxToolIterations", "3"));

            Assert.Multiple(() =>
            {
                Assert.That(IsAuthChallenge(anonymousResponse.StatusCode), Is.True);
                Assert.That(updateResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(File.Exists(settingsPath), Is.True);
            });

            var json = await File.ReadAllTextAsync(settingsPath);
            Assert.That(json, Does.Contain("\"MaxToolIterations\": 3"));
        }
        finally
        {
            await DisposeFactoryTempDirAsync(factory, tempDir);
        }
    }

    [Test]
    public async Task HealthLiveAndReady_ReturnSuccessWithoutStartingWorker()
    {
        var live = await _client.GetAsync("/api/health/live");
        var ready = await _client.GetAsync("/api/health/ready");

        Assert.Multiple(() =>
        {
            Assert.That(live.IsSuccessStatusCode, Is.True);
            Assert.That(ready.IsSuccessStatusCode, Is.True);
        });
    }

    [Test]
    public async Task Diagnostics_ReturnsRedactedConfigurationAndPathStatus()
    {
        var setupResponse = await _client.PostAsJsonAsync("/api/admin/setup", new SetupAdminRequest("Admin User", "admin@example.test", "ThisIsAValidPassword123!"));
        setupResponse.EnsureSuccessStatusCode();

        var response = await _client.GetAsync("/api/diagnostics");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("configuration"));
            Assert.That(body, Does.Contain("dataPath"));
            Assert.That(body, Does.Contain("pagesPath"));
            Assert.That(body, Does.Contain("apiKeyConfigured"));
            Assert.That(body, Does.Contain("warnings"));
            Assert.That(body, Does.Contain("paths"));
            Assert.That(body, Does.Contain("storageDiagnostics"));
            Assert.That(body, Does.Not.Contain("apiKey\""));
        });
    }

    [Test]
    public async Task PagesApi_SavesSearchesRendersAndDeletesMarkdownPages()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/pages", new PageSaveRequest(
            "contract-page",
            "Contract Page",
            "Body with ![image](assets/example.png)"));
        createResponse.EnsureSuccessStatusCode();

        var created = await createResponse.Content.ReadFromJsonAsync<PageDocument>();
        var search = await _client.GetFromJsonAsync<PageSummary[]>("/api/pages?query=contract");
        var html = await _client.GetStringAsync("/api/pages/contract-page/html");
        var deleteResponse = await _client.DeleteAsync("/api/pages/contract-page");

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.Not.Null);
            Assert.That(created!.Slug, Is.EqualTo("contract-page"));
            Assert.That(search!.Select(page => page.Slug), Does.Contain("contract-page"));
            Assert.That(html, Does.Contain(">Contract Page</h1>"));
            Assert.That(html, Does.Contain("/page-assets/example.png"));
            Assert.That(deleteResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        });
    }

    [Test]
    public async Task CombinedSearch_ReturnsMemoryAndPageResults()
    {
        await _client.PostAsJsonAsync("/api/memories", new MemoryRecord
        {
            Id = "combined-memory",
            Title = "Combined Search Memory",
            Content = "shared discovery phrase",
            Tags = ["combined"]
        });
        await _client.PostAsJsonAsync("/api/pages", new PageSaveRequest(
            "combined-page",
            "Combined Search Page",
            "shared discovery phrase"));

        var results = await _client.GetFromJsonAsync<UnifiedSearchResult[]>("/api/search?query=shared%20discovery&limit=10");
        Assert.That(results, Is.Not.Null);
        var nonNullResults = results!;

        Assert.Multiple(() =>
        {
            Assert.That(nonNullResults.Select(result => result.Kind), Does.Contain("memory"));
            Assert.That(nonNullResults.Select(result => result.Kind), Does.Contain("page"));
            Assert.That(nonNullResults.Single(result => result.Id == "combined-page").Url, Is.EqualTo("/pages/combined-page"));
        });
    }

    [Test]
    public async Task ChatApi_WithEmptyMessage_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/chat", new { message = "", mode = "Chat" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    private static WebApplicationFactory<Program> CreateIsolatedFactory(string tempDir, Dictionary<string, string?>? overrides = null) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    var values = new Dictionary<string, string?>
                    {
                        ["MemorySmith:DataPath"] = Path.Combine(tempDir, "Memories"),
                        ["MemorySmith:PagesPath"] = Path.Combine(tempDir, "Pages"),
                        ["MemorySmith:EventLogPath"] = Path.Combine(tempDir, "Events", "audit.log"),
                        ["MemorySmith:DataProtectionKeysPath"] = Path.Combine(tempDir, "Keys"),
                        ["MemorySmith:Database:ConnectionString"] = $"Data Source={Path.Combine(tempDir, "memorysmith.db")};Pooling=False",
                        ["MemorySmith:Audit:JsonlPath"] = Path.Combine(tempDir, "Events", "audit-{yyyy}-W{week}.jsonl"),
                        ["MemorySmith:History:RootPath"] = Path.Combine(tempDir, ".history"),
                        ["MemorySmith:Maintenance:Enabled"] = "false"
                    };

                    if (overrides is not null)
                    {
                        foreach (var pair in overrides)
                        {
                            values[pair.Key] = pair.Value;
                        }
                    }

                    config.AddInMemoryCollection(values);
                });
            });

    private static bool IsAuthChallenge(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static async Task DisposeFactoryTempDirAsync(WebApplicationFactory<Program> factory, string tempDir)
    {
        await factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}