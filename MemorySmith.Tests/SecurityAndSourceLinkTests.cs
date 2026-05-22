using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

[TestFixture]
public class SecurityAndSourceLinkTests
{
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
            Assert.That(MemorySmithRequestGuardMiddleware.IsLoopback(IPAddress.Parse("192.168.1.10")), Is.False);
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
    public async Task Diagnostics_WarnsWhenRemoteApiIsAllowedWithoutApiKey()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["MemorySmith:AllowRemoteApi"] = "true"
        });
        using var client = factory.CreateClient();

        var setupResponse = await client.PostAsJsonAsync("/api/admin/setup", new SetupAdminRequest("Admin User", "admin@example.test", "ThisIsAValidPassword123!"));
        setupResponse.EnsureSuccessStatusCode();

        var body = await client.GetStringAsync("/api/diagnostics");

        Assert.That(body, Does.Contain("remote-api-without-api-key"));
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
            ["MemorySmith:SourceLinks:AllowedFileRootVariables:0"] = "AllowedRoot"
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