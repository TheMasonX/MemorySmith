using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

[TestFixture]
public class SecurityAndSourceLinkTests
{
    private const string ValidPassword = "ThisIsAValidPassword123!";
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-security-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Test]
    public async Task ApiKey_WhenConfigured_IsRequiredForApiAndMcpRequests()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["MemorySmith:ApiKey"] = "secret"
        });
        using var client = factory.CreateClient();

        var missing = await client.GetAsync("/api/health/live");
        var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/health/live");
        wrong.Headers.Add(MemorySmithRequestGuardMiddleware.ApiKeyHeaderName, "wrong");
        var wrongResponse = await client.SendAsync(wrong);
        var correct = new HttpRequestMessage(HttpMethod.Get, "/api/health/live");
        correct.Headers.Add(MemorySmithRequestGuardMiddleware.ApiKeyHeaderName, "secret");
        var correctResponse = await client.SendAsync(correct);

        Assert.Multiple(() =>
        {
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(wrongResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(correctResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });

        var mcpMissing = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = 1,
            Method = "tools/list"
        }, JsonSerializerOptions.Web);
        var mcpCorrect = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                JsonRpc = "2.0",
                Id = 1,
                Method = "tools/list"
            }, options: JsonSerializerOptions.Web)
        };
        mcpCorrect.Headers.Add(MemorySmithRequestGuardMiddleware.ApiKeyHeaderName, "secret");
        var mcpCorrectResponse = await client.SendAsync(mcpCorrect);

        Assert.Multiple(() =>
        {
            Assert.That(mcpMissing.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(mcpCorrectResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
    }

    [Test]
    public void RequestGuard_TreatsOnlyLoopbackAsLocal()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MemorySmithRequestGuardMiddleware.IsLoopback(IPAddress.Loopback), Is.True);
            Assert.That(MemorySmithRequestGuardMiddleware.IsLoopback(IPAddress.IPv6Loopback), Is.True);
            Assert.That(MemorySmithRequestGuardMiddleware.IsLoopback(IPAddress.Parse("::ffff:127.0.0.1")), Is.True);
            Assert.That(MemorySmithRequestGuardMiddleware.IsLoopback(IPAddress.Parse("192.168.1.10")), Is.False);
            Assert.That(MemorySmithRequestGuardMiddleware.IsLoopback(null), Is.False);
        });
    }

    [Test]
    public async Task RequestGuard_DeniesNullRemoteAddressWhenRemoteApiIsDisabled()
    {
        var context = new DefaultHttpContext();
        var nextCalls = 0;
        var middleware = new MemorySmithRequestGuardMiddleware(_ =>
        {
            nextCalls++;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, Options.Create(new MemorySmithOptions { AllowRemoteApi = false }));

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
            Assert.That(nextCalls, Is.Zero);
        });
    }

    [TestCase("/api/health/live")]
    [TestCase("/mcp")]
    public async Task RequestGuard_BlocksRemoteApiWhenRemoteApiIsAllowedWithoutConfiguredApiKey(string path)
    {
        var blocked = CreateRemoteApiContext(path);
        var allowed = CreateRemoteApiContext(path);
        allowed.Request.Headers[MemorySmithRequestGuardMiddleware.ApiKeyHeaderName] = "secret";
        var nextCalls = 0;
        var middleware = new MemorySmithRequestGuardMiddleware(_ =>
        {
            nextCalls++;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(blocked, Options.Create(new MemorySmithOptions { AllowRemoteApi = true }));
        await middleware.InvokeAsync(allowed, Options.Create(new MemorySmithOptions { AllowRemoteApi = true, ApiKey = "secret" }));

        Assert.Multiple(() =>
        {
            Assert.That(blocked.Response.StatusCode, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
            Assert.That(ReadResponseBody(blocked), Does.Contain("MemorySmith:ApiKey"));
            Assert.That(allowed.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(nextCalls, Is.EqualTo(1));
        });
    }

    [TestCase("/api/auth/me")]
    [TestCase("/api/auth/login")]
    [TestCase("/api/auth/logout")]
    [TestCase("/api/auth/challenge")]
    [TestCase("/api/admin/setup")]
    [TestCase("/api/admin/setup/status")]
    public async Task RequestGuard_AllowsRemoteBrowserAuthAndSetupEndpointsWithoutApiKey(string path)
    {
        var context = CreateRemoteApiContext(path);
        var nextCalls = 0;
        var middleware = new MemorySmithRequestGuardMiddleware(_ =>
        {
            nextCalls++;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, Options.Create(new MemorySmithOptions { AllowRemoteApi = true, ApiKey = "secret" }));

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(ReadResponseBody(context), Is.Empty);
            Assert.That(nextCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task McpInitializedNotification_DoesNotReturnJsonRpcError()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Method = "initialized"
        }, JsonSerializerOptions.Web);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
    }

    [Test]
    public async Task AdminSetup_RejectsJsonPostWithoutAntiforgeryToken()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsJsonAsync(
            "/api/admin/setup",
            new SetupAdminRequest("Admin User", "admin@example.test", ValidPassword));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task McpSensitiveRead_DeniesAnonymousAndAuthenticatedViewerCallers()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["MemorySmith:Auth:AnonymousAccess"] = MemorySmithRoles.Viewer,
            ["MemorySmith:Auth:AuthenticatedDefaultRole"] = MemorySmithRoles.Viewer,
            // Disable OpenLocalEditorCompatibility so anonymous callers don't get
            // implicit Editor access (which would bypass the ReadSourceBundle check).
            ["MemorySmith:Auth:OpenLocalEditorCompatibility"] = "false",
            // Enable sensitive-read tools so the auth check actually runs.
            // Without this, the MCP endpoint returns "disabled by MCP tool configuration"
            // instead of "not authorized to read source bundles".
            ["MemorySmith:Mcp:EnabledTools:0"] = "memorysmith_find_by_source"
        });

        using var setupClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var setupResponse = await setupClient.PostAsJsonWithAntiforgeryAsync(factory.Services, "/api/admin/setup", new SetupAdminRequest("Admin User", "admin@example.test", ValidPassword));
        setupResponse.EnsureSuccessStatusCode();

        using var anonymousClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var anonymousText = await CallFindBySourceTextAsync(anonymousClient);

        await CreateLocalUserAsync(factory, "Viewer User", "viewer@example.test", ValidPassword, MemorySmithRoles.Viewer);
        using var viewerClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var loginResponse = await viewerClient.PostAsJsonAsync("/api/auth/login", new LoginRequest("viewer@example.test", ValidPassword));
        loginResponse.EnsureSuccessStatusCode();
        var viewerText = await CallFindBySourceTextAsync(viewerClient);

        Assert.Multiple(() =>
        {
            Assert.That(anonymousText, Does.Contain("not authorized to read source bundles"));
            Assert.That(viewerText, Does.Contain("not authorized to read source bundles"));
        });
    }

    [Test]
    public async Task McpSensitiveRead_AllowsEditorAdminApiKeyAndAuthDisabledCallers()
    {
        await using var roleFactory = CreateFactory(new Dictionary<string, string?>
        {
            ["MemorySmith:Auth:AnonymousAccess"] = MemorySmithRoles.Viewer,
            ["MemorySmith:Auth:AuthenticatedDefaultRole"] = MemorySmithRoles.Viewer
        });

        using var adminClient = roleFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var setupResponse = await adminClient.PostAsJsonWithAntiforgeryAsync(roleFactory.Services, "/api/admin/setup", new SetupAdminRequest("Admin User", "admin@example.test", ValidPassword));
        setupResponse.EnsureSuccessStatusCode();
        var adminText = await CallFindBySourceTextAsync(adminClient);

        await CreateLocalUserAsync(roleFactory, "Editor User", "editor@example.test", ValidPassword, MemorySmithRoles.Editor);
        using var editorClient = roleFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var loginResponse = await editorClient.PostAsJsonAsync("/api/auth/login", new LoginRequest("editor@example.test", ValidPassword));
        loginResponse.EnsureSuccessStatusCode();
        var editorText = await CallFindBySourceTextAsync(editorClient);

        await using var apiKeyFactory = CreateFactory(new Dictionary<string, string?>
        {
            ["MemorySmith:ApiKey"] = "secret"
        });
        using var apiKeyClient = apiKeyFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        apiKeyClient.DefaultRequestHeaders.Add(MemorySmithRequestGuardMiddleware.ApiKeyHeaderName, "secret");
        var apiKeyText = await CallFindBySourceTextAsync(apiKeyClient);

        await using var authDisabledFactory = CreateFactory(new Dictionary<string, string?>
        {
            ["MemorySmith:Auth:Enabled"] = "false"
        });
        using var authDisabledClient = authDisabledFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var authDisabledText = await CallFindBySourceTextAsync(authDisabledClient);

        Assert.Multiple(() =>
        {
            Assert.That(adminText, Does.Not.Contain("not authorized to read source bundles"));
            Assert.That(editorText, Does.Not.Contain("not authorized to read source bundles"));
            Assert.That(apiKeyText, Does.Not.Contain("not authorized to read source bundles"));
            Assert.That(authDisabledText, Does.Not.Contain("not authorized to read source bundles"));
        });
    }

    [Test]
    public async Task Diagnostics_WarnsWhenRemoteApiIsAllowedWithoutApiKey()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["MemorySmith:AllowRemoteApi"] = "true"
        });
        using var client = factory.CreateClient();

        var setupResponse = await client.PostAsJsonWithAntiforgeryAsync(factory.Services, "/api/admin/setup", new SetupAdminRequest("Admin User", "admin@example.test", "ThisIsAValidPassword123!"));
        setupResponse.EnsureSuccessStatusCode();

        var body = await client.GetStringAsync("/api/diagnostics");

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("remote-api-without-api-key"));
            Assert.That(body, Does.Contain("blocked until MemorySmith:ApiKey"));
        });
    }

    [Test]
    public void SecurityProfile_RemoteHardenedAppliesRemoteSafeDefaults()
    {
        var options = new MemorySmithOptions
        {
            SecurityProfile = MemorySmithSecurityProfiles.RemoteHardened,
            AllowRemoteApi = false,
            Auth = new AuthOptions
            {
                Enabled = false,
                AnonymousAccess = MemorySmithRoles.Viewer,
                AutoEditorForAuthenticatedUsers = true,
                RequireHttpsForRemoteAuth = false,
                OpenLocalEditorCompatibility = true,
                Setup = new AuthSetupOptions { AllowLoopbackBootstrap = true }
            }
        };
        var postConfigure = CreatePostConfigure("Production");

        postConfigure.PostConfigure(null, options);

        Assert.Multiple(() =>
        {
            Assert.That(options.SecurityProfile, Is.EqualTo(MemorySmithSecurityProfiles.RemoteHardened));
            Assert.That(options.AllowRemoteApi, Is.True);
            Assert.That(options.Auth.Enabled, Is.True);
            Assert.That(options.Auth.RequireHttpsForRemoteAuth, Is.True);
            Assert.That(options.Auth.AnonymousAccess, Is.EqualTo("None"));
            Assert.That(options.Auth.AutoEditorForAuthenticatedUsers, Is.False);
            Assert.That(options.Auth.OpenLocalEditorCompatibility, Is.False);
            Assert.That(options.Auth.Setup.AllowLoopbackBootstrap, Is.False);
        });
    }

    [Test]
    public void SecurityProfile_LocalDevelopmentEnvironmentPreservesDogfoodOverrides()
    {
        var options = new MemorySmithOptions();
        var postConfigure = CreatePostConfigure("LocalDevelopment");

        postConfigure.PostConfigure(null, options);

        Assert.Multiple(() =>
        {
            Assert.That(options.AllowRemoteApi, Is.True);
            Assert.That(options.Auth.RequireHttpsForRemoteAuth, Is.False);
            Assert.That(options.Chat.AgentWritesEnabled, Is.True);
            Assert.That(options.SourceLinks.MaxReadBytes, Is.EqualTo(262144));
        });
    }

    [Test]
    public async Task SourceBundle_ClampsHugeMaxFileBytes()
    {
        var allowedRoot = Path.Combine(_tempRoot, "allowed");
        Directory.CreateDirectory(allowedRoot);
        var sourceFile = Path.Combine(allowedRoot, "source.txt");
        await File.WriteAllTextAsync(sourceFile, new string('a', 200));

        var varsPath = Path.Combine(_tempRoot, "vars.json");
        var dataPath = Path.Combine(_tempRoot, "Memories");
        var store = new FileMemoryStore(dataPath);
        store.Save(new MemoryRecord
        {
            Id = "source-test",
            Title = "Source Test",
            Content = "Source bundle test",
            SourceLinks = [new SourceLink { Label = "source", Uri = "%AllowedRoot%source.txt" }]
        });
        new FileVarStore(varsPath).Save(new Dictionary<string, string> { ["AllowedRoot"] = allowedRoot + Path.DirectorySeparatorChar });

        await using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["MemorySmith:DataPath"] = dataPath,
            ["MemorySmith:VarsPath"] = varsPath,
            ["MemorySmith:SourceLinks:MaxReadBytes"] = "32",
            ["MemorySmith:SourceLinks:AllowedFileRootVariables:0"] = "AllowedRoot",
            ["MemorySmith:Mcp:EnabledTools:0"] = "memorysmith_source_bundle"
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "bundle",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_source_bundle",
                Arguments = new
                {
                    Ids = "source-test",
                    MaxFileBytes = int.MaxValue,
                    Format = "json"
                }
            }
        }, JsonSerializerOptions.Web);

        var text = await ExtractFirstToolTextAsync(response);
        using var document = JsonDocument.Parse(text);
        var content = document.RootElement.GetProperty("entries")[0].GetProperty("content").GetString();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(content, Does.Contain("truncated"));
            Assert.That(content!, Has.Length.LessThan(120));
        });
    }

    [Test]
    public async Task VarResolver_ReadSourceAsync_ExpandsConfiguredContextAroundRequestedLines()
    {
        var allowedRoot = Path.Combine(_tempRoot, "allowed");
        Directory.CreateDirectory(allowedRoot);
        var sourceFile = Path.Combine(allowedRoot, "source.txt");
        await File.WriteAllTextAsync(sourceFile, string.Join(Environment.NewLine, Enumerable.Range(1, 200).Select(index => $"LINE-{index:000}")));

        var varsPath = Path.Combine(_tempRoot, "vars.json");
        new FileVarStore(varsPath).Save(new Dictionary<string, string> { ["AllowedRoot"] = allowedRoot + Path.DirectorySeparatorChar });
        var resolver = new VarResolver(
            new FileVarStore(varsPath),
            Options.Create(new MemorySmithOptions
            {
                SourceLinks = new SourceLinkOptions
                {
                    AllowedFileRootVariables = ["AllowedRoot"],
                    ReadContextLinesBefore = 20,
                    ReadContextLinesAfter = 20
                }
            }));

        var content = await resolver.ReadSourceAsync(new SourceLink
        {
            Uri = sourceFile,
            StartLine = 100,
            EndLine = 110
        });

        Assert.Multiple(() =>
        {
            Assert.That(content.Exists, Is.True);
            Assert.That(content.Content, Does.Contain("LINE-080"));
            Assert.That(content.Content, Does.Contain("LINE-130"));
            Assert.That(content.Content, Does.Not.Contain("LINE-079"));
            Assert.That(content.Content, Does.Not.Contain("LINE-131"));
        });
    }

    [Test]
    public async Task VarResolver_ReadSourceAsync_AllowsBroadReadsWhenOptInIsEnabled()
    {
        var broadRoot = Path.Combine(_tempRoot, "broad");
        Directory.CreateDirectory(broadRoot);
        var sourceFile = Path.Combine(broadRoot, "source.txt");
        await File.WriteAllTextAsync(sourceFile, string.Join(Environment.NewLine, Enumerable.Range(1, 120).Select(index => $"LINE-{index:000}")));

        var boundedResolver = new VarResolver(
            new FileVarStore(Path.Combine(_tempRoot, "vars-bounded.json")),
            Options.Create(new MemorySmithOptions
            {
                SourceLinks = new SourceLinkOptions
                {
                    AllowedFileRoots = [broadRoot]
                }
            }));

        var boundedContent = await boundedResolver.ReadSourceAsync(new SourceLink { Uri = sourceFile });

        var resolver = new VarResolver(
            new FileVarStore(Path.Combine(_tempRoot, "vars.json")),
            Options.Create(new MemorySmithOptions
            {
                SourceLinks = new SourceLinkOptions
                {
                    AllowUnrestrictedSourceReads = true
                }
            }));

        var content = await resolver.ReadSourceAsync(new SourceLink { Uri = sourceFile });

        Assert.Multiple(() =>
        {
            Assert.That(boundedContent.Exists, Is.True);
            Assert.That(boundedContent.Content, Does.Contain("LINE-001"));
            Assert.That(boundedContent.Content, Does.Contain("LINE-050"));
            Assert.That(boundedContent.Content, Does.Not.Contain("LINE-051"));
            Assert.That(content.Exists, Is.True);
            Assert.That(content.Content, Does.Contain("LINE-001"));
            Assert.That(content.Content, Does.Contain("LINE-120"));
        });
    }

    [Test]
    public async Task VarResolver_ReadSourceAsync_DeniedRootsOverrideBroadReads()
    {
        var blockedRoot = Path.Combine(_tempRoot, "blocked");
        Directory.CreateDirectory(blockedRoot);
        var blockedFile = Path.Combine(blockedRoot, "secret.txt");
        await File.WriteAllTextAsync(blockedFile, "blocked content");

        var resolver = new VarResolver(
            new FileVarStore(Path.Combine(_tempRoot, "vars.json")),
            Options.Create(new MemorySmithOptions
            {
                SourceLinks = new SourceLinkOptions
                {
                    AllowUnrestrictedSourceReads = true,
                    DeniedFileRoots = [blockedRoot]
                }
            }));

        var content = await resolver.ReadSourceAsync(new SourceLink { Uri = blockedFile });

        Assert.Multiple(() =>
        {
            Assert.That(content.Exists, Is.False);
            Assert.That(content.Content, Does.Contain("blocked by the configured denied source roots"));
        });
    }

    [Test]
    public async Task VarResolver_BlocksLocalFilesOutsideAllowedRoots()
    {
        var allowedRoot = Path.Combine(_tempRoot, "allowed");
        var blockedRoot = Path.Combine(_tempRoot, "blocked");
        Directory.CreateDirectory(allowedRoot);
        Directory.CreateDirectory(blockedRoot);
        var blockedFile = Path.Combine(blockedRoot, "secret.txt");
        await File.WriteAllTextAsync(blockedFile, "secret");

        var varsPath = Path.Combine(_tempRoot, "vars.json");
        new FileVarStore(varsPath).Save(new Dictionary<string, string> { ["AllowedRoot"] = allowedRoot + Path.DirectorySeparatorChar });
        var resolver = new VarResolver(
            new FileVarStore(varsPath),
            Options.Create(new MemorySmithOptions
            {
                SourceLinks = new SourceLinkOptions
                {
                    AllowedFileRootVariables = ["AllowedRoot"]
                }
            }));

        var content = await resolver.ReadSourceAsync(new SourceLink { Uri = blockedFile });

        Assert.Multiple(() =>
        {
            Assert.That(content.Exists, Is.False);
            Assert.That(content.Content, Does.Contain("outside the configured allowed source roots"));
        });
    }

    [Test]
    public async Task VarResolver_OpenWithDefaultApp_UsesPlatformDefaultAppCommand()
    {
        var allowedRoot = Path.Combine(_tempRoot, "allowed");
        Directory.CreateDirectory(allowedRoot);
        var sourceFile = Path.Combine(allowedRoot, "source file.cs");
        await File.WriteAllTextAsync(sourceFile, "Console.WriteLine();");

        var varsPath = Path.Combine(_tempRoot, "vars.json");
        new FileVarStore(varsPath).Save(new Dictionary<string, string> { ["AllowedRoot"] = allowedRoot + Path.DirectorySeparatorChar });
        var resolver = new CapturingVarResolver(
            new FileVarStore(varsPath),
            Options.Create(new MemorySmithOptions
            {
                SourceLinks = new SourceLinkOptions
                {
                    AllowOpenWithDefaultApp = true,
                    AllowedFileRootVariables = ["AllowedRoot"]
                }
            }));

        var result = await resolver.OpenWithDefaultAppAsync(new SourceLink { Uri = "%AllowedRoot%source file.cs" });
        var startInfo = resolver.CapturedStartInfo;
        var encodedCommandIndex = startInfo?.ArgumentList.IndexOf("-EncodedCommand") ?? -1;
        var decodedWindowsCommand = encodedCommandIndex >= 0
            ? Encoding.Unicode.GetString(Convert.FromBase64String(startInfo!.ArgumentList[encodedCommandIndex + 1]))
            : string.Empty;

        Assert.Multiple(() =>
        {
            Assert.That(result.Opened, Is.True);
            Assert.That(startInfo, Is.Not.Null);
            Assert.That(startInfo!.FileName, Is.Not.EqualTo(sourceFile));
            Assert.That(startInfo.UseShellExecute, Is.False);

            if (OperatingSystem.IsWindows())
            {
                Assert.That(startInfo.FileName, Is.EqualTo("powershell.exe"));
                Assert.That(startInfo.ArgumentList, Does.Contain("-NonInteractive"));
                Assert.That(startInfo.ArgumentList, Does.Contain("-EncodedCommand"));
                Assert.That(decodedWindowsCommand, Is.EqualTo($"Invoke-Item -LiteralPath '{sourceFile}'"));
            }
            else
            {
                Assert.That(startInfo.FileName, Is.EqualTo(OperatingSystem.IsMacOS() ? "open" : "xdg-open"));
                Assert.That(startInfo.ArgumentList, Does.Contain(sourceFile));
            }
        });
    }

    [Test]
    public async Task VarResolver_OpenWithDefaultApp_AllowsAdditionalRootsWithoutChangingGlobalPolicy()
    {
        var blockedRoot = Path.Combine(_tempRoot, "training-runs");
        Directory.CreateDirectory(blockedRoot);
        var artifactPath = Path.Combine(blockedRoot, "status.json");
        await File.WriteAllTextAsync(artifactPath, "{}");

        var varsPath = Path.Combine(_tempRoot, "vars.json");
        new FileVarStore(varsPath).Save(new Dictionary<string, string> { ["AllowedRoot"] = Path.Combine(_tempRoot, "allowed") + Path.DirectorySeparatorChar });
        var resolver = new CapturingVarResolver(
            new FileVarStore(varsPath),
            Options.Create(new MemorySmithOptions
            {
                SourceLinks = new SourceLinkOptions
                {
                    AllowOpenWithDefaultApp = true,
                    AllowedFileRootVariables = ["AllowedRoot"]
                }
            }));

        var blockedResult = await resolver.OpenWithDefaultAppAsync(new SourceLink { Uri = artifactPath });
        var allowedResult = await resolver.OpenWithDefaultAppAsync(new SourceLink { Uri = artifactPath }, [blockedRoot]);

        Assert.Multiple(() =>
        {
            Assert.That(blockedResult.Opened, Is.False);
            Assert.That(blockedResult.Message, Does.Contain("outside the configured allowed source roots"));
            Assert.That(allowedResult.Opened, Is.True);
            Assert.That(resolver.CapturedStartInfo, Is.Not.Null);
        });
    }

    [Test]
    public void FileVarStore_Load_RecordsDiagnosticsForCorruptVarsFile()
    {
        var varsPath = Path.Combine(_tempRoot, "vars.json");
        File.WriteAllText(varsPath, "{ not json");
        var diagnostics = new StorageDiagnostics();
        var store = new FileVarStore(varsPath, diagnostics);

        var vars = store.Load();

        Assert.Multiple(() =>
        {
            Assert.That(vars, Is.Empty);
            Assert.That(diagnostics.GetSnapshot().CorruptFiles.Single().Path, Is.EqualTo(varsPath));
        });
    }

    private WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?>? overrides = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.Configure(app => app.Use(async (HttpContext context, Func<Task> next) =>
            {
                context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                await next();
            }));
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["MemorySmith:DataPath"] = Path.Combine(_tempRoot, "Memories"),
                    ["MemorySmith:PagesPath"] = Path.Combine(_tempRoot, "Pages"),
                    ["MemorySmith:EventLogPath"] = Path.Combine(_tempRoot, "Events", "audit.log"),
                    ["MemorySmith:VarsPath"] = Path.Combine(_tempRoot, "vars.json"),
                    ["MemorySmith:DataProtectionKeysPath"] = Path.Combine(_tempRoot, "Keys"),
                    ["MemorySmith:Database:ConnectionString"] = $"Data Source={Path.Combine(_tempRoot, "memorysmith.db")};Pooling=False",
                    ["MemorySmith:Audit:JsonlPath"] = Path.Combine(_tempRoot, "Events", "audit-{yyyy}-W{week}.jsonl"),
                    ["MemorySmith:History:RootPath"] = Path.Combine(_tempRoot, ".history"),
                    ["MemorySmith:Maintenance:Enabled"] = "false",
                    ["MemorySmith:ApiKey"] = string.Empty
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

    private MemorySmithLocalDevelopmentPostConfigure CreatePostConfigure(string environmentName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MemorySmith:SettingsOverridePath"] = Path.Combine(_tempRoot, "missing-overrides.json")
            })
            .Build();

        return new MemorySmithLocalDevelopmentPostConfigure(new TestHostEnvironment(environmentName), configuration);
    }

    private static DefaultHttpContext CreateRemoteApiContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadResponseBody(DefaultHttpContext context) =>
        Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "MemorySmith.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task CreateLocalUserAsync(WebApplicationFactory<Program> factory, string displayName, string email, string password, string role)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IMemorySmithDatabase>();
        var now = DateTime.UtcNow;
        var user = new UserAccount
        {
            UserId = Guid.NewGuid().ToString("N"),
            DisplayName = displayName,
            NormalizedDisplayName = displayName.Trim().ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.Trim().ToUpperInvariant(),
            LocalPasswordEnabled = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        user.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(user, password);

        await database.Users.CreateAsync(user, CancellationToken.None);
        await database.Roles.AssignRoleAsync(user.UserId, role, null, CancellationToken.None);
    }

    private static async Task<string> CallFindBySourceTextAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/mcp", new
        {
            JsonRpc = "2.0",
            Id = "source-auth",
            Method = "tools/call",
            Params = new
            {
                Name = "memorysmith_find_by_source",
                Arguments = new
                {
                    Pattern = "no-match-source-auth-probe"
                }
            }
        }, JsonSerializerOptions.Web);
        response.EnsureSuccessStatusCode();
        return await ExtractFirstToolTextAsync(response);
    }

    private static async Task<string> ExtractFirstToolTextAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;
    }

    private sealed class CapturingVarResolver : VarResolver
    {
        public CapturingVarResolver(IVarStore varStore, IOptions<MemorySmithOptions> options)
            : base(varStore, options)
        {
        }

        public ProcessStartInfo? CapturedStartInfo { get; private set; }

        protected override Process? StartDefaultAppProcess(ProcessStartInfo startInfo)
        {
            CapturedStartInfo = startInfo;
            return null;
        }
    }
}
