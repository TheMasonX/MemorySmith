using System.Net.Http.Json;
using System.Text.RegularExpressions;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MemorySmith.Tests;

[TestFixture]
public class ProjectWikiTestbaseTests
{
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-project-wiki-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Test]
    public void RepositoryDataMemories_LoadAsProjectWikiFixture()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        var diagnostics = new StorageDiagnostics();
        var store = new FileMemoryStore(dataPath, diagnostics);

        var records = store.LoadAll()
            .Where(record => record.Tags.Contains("project-wiki"))
            .OrderBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(records, Has.Count.GreaterThanOrEqualTo(5));
            Assert.That(records.Select(record => record.Id), Does.Contain("project-wiki-data-folder-policy"));
            Assert.That(records.Any(record => record.Status == MemoryStatus.Core), Is.True);
            Assert.That(records.All(record => record.Status is MemoryStatus.Core or MemoryStatus.Working), Is.True);
            Assert.That(records.All(record => ProjectWikiFixture.SafeIdPattern.IsMatch(record.Id)), Is.True);
            Assert.That(records.All(record => !string.IsNullOrWhiteSpace(record.Title)), Is.True);
            Assert.That(records.All(record => !string.IsNullOrWhiteSpace(record.Content)), Is.True);
            Assert.That(diagnostics.GetSnapshot().CorruptFiles, Is.Empty);
        });
    }

    [Test]
    public async Task ProjectWikiSearch_FindsSingleHostDecisionThroughApplicationService()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        var store = new FileMemoryStore(dataPath, new StorageDiagnostics());
        var service = TestServiceFactory.CreateMemoryApplicationService(
            store,
            new RecordingEventStore(),
            new RecordingMemoryChangePublisher());

        var results = await service.SearchAsync(
            new MemorySearchQuery(Query: "single deployable host", Tags: "project-wiki", Limit: 10),
            CancellationToken.None);

        Assert.That(results.Select(record => record.Id), Does.Contain("project-wiki-active-architecture"));
    }

    [Test]
    public async Task ProjectWikiFixture_IncludesDeterministicSearchAndGraphFixtures()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        var store = new FileMemoryStore(dataPath, new StorageDiagnostics());
        var service = TestServiceFactory.CreateMemoryApplicationService(
            store,
            new RecordingEventStore(),
            new RecordingMemoryChangePublisher());

        var fixtureRecords = store.LoadAll()
            .Where(record => record.Tags.Contains("test-fixture"))
            .ToList();
        var searchResults = await service.HybridSearchAsync(
            new HybridMemorySearchQuery(Query: "quartzwave nimbusvector context fixture", Tags: "test-fixture", Limit: 10),
            CancellationToken.None);
        var pack = await service.BuildContextPackAsync(
            new MemoryContextPackQuery(
                Ids: "project-wiki-test-fixture-context-root",
                ReferenceDepth: 1,
                IncludeBacklinks: true,
                MaxRecords: 10,
                MaxContentChars: 500),
            CancellationToken.None);
        var relationships = pack.Records.ToDictionary(record => record.Id, record => record.Relationship, StringComparer.OrdinalIgnoreCase);

        Assert.Multiple(() =>
        {
            Assert.That(fixtureRecords.Select(record => record.Id), Does.Contain("project-wiki-test-fixture-overview"));
            Assert.That(fixtureRecords.Select(record => record.Id), Does.Contain("project-wiki-test-fixture-context-root"));
            Assert.That(searchResults.Select(record => record.Id), Does.Contain("project-wiki-test-fixture-context-root"));
            Assert.That(searchResults.Select(record => record.Id), Does.Contain("project-wiki-test-fixture-reference-child"));
            Assert.That(relationships["project-wiki-test-fixture-reference-child"], Is.EqualTo("reference of project-wiki-test-fixture-context-root"));
            Assert.That(relationships["project-wiki-test-fixture-conflict-note"], Is.EqualTo("conflict of project-wiki-test-fixture-context-root"));
            Assert.That(relationships["project-wiki-test-fixture-backlink-source"], Is.EqualTo("references project-wiki-test-fixture-context-root"));
            // Fixture records have source links with %MemorySmithRepo% prefix (unresolved variable).
            // Since the test environment has no VarResolver configured with that variable,
            // source-link diagnostics (source.missing_variable, source.unresolved) may appear
            // in pack.Warnings. Don't assert on exact warning count — just verify the core
            // structure (fixture IDs and relationship strings) is deterministic.
        });
    }

    [Test]
    public async Task AppApi_UsesCopiedProjectWikiFixtureWithoutMutatingSourceWiki()
    {
        var sourceBefore = ProjectWikiFixture.ReadSourceSnapshot();
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MemorySmith:DataPath"] = dataPath,
                        ["MemorySmith:EventLogPath"] = Path.Combine(_tempRoot, "Events", "audit.log"),
                        ["MemorySmith:Maintenance:Enabled"] = "false",
                        // Isolate the SQLite database to the temp directory to prevent
                        // conflicts with the shared default path (../Data/memorysmith.db).
                        ["MemorySmith:Database:ConnectionString"] = $"Data Source={Path.Combine(_tempRoot, "memorysmith.db")};Pooling=False",
                        ["MemorySmith:DataProtectionKeysPath"] = Path.Combine(_tempRoot, "Keys"),
                        ["MemorySmith:ApiKey"] = string.Empty
                        // Auth:Enabled stays true so IAuthorizationService is registered.
                    });
                });
            });

        // Bootstrap admin so the setup guard allows API requests on the fresh DB.
        using var setupBootstrapClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await setupBootstrapClient.PostAsJsonAsync("/api/admin/setup",
            new SetupAdminRequest("Test Admin", "admin@memorysmith.test", "T3stAdmin@2026!"),
            JsonSerializerOptions.Web);

        using var client = factory.CreateClient();

        var page = await client.GetFromJsonAsync<PagedResult<MemoryMetadata>>("/api/memories?tags=project-wiki&pageSize=100");
        var searchResponse = await client.PostAsJsonAsync("/api/memories/search", new
        {
            Query = "Data/Memories folder is the project memory wiki",
            Tags = "project-wiki",
            Limit = 10
        });
        searchResponse.EnsureSuccessStatusCode();
        var searchResults = await searchResponse.Content.ReadFromJsonAsync<List<MemoryRecord>>();
        var sourceAfter = ProjectWikiFixture.ReadSourceSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(page, Is.Not.Null);
            Assert.That(page!.TotalCount, Is.GreaterThanOrEqualTo(5));
            Assert.That(page.Data.Select(record => record.Id), Does.Contain("project-wiki-data-folder-policy"));
            Assert.That(searchResults, Is.Not.Null);
            Assert.That(searchResults!.Select(record => record.Id), Does.Contain("project-wiki-data-folder-policy"));
            Assert.That(sourceAfter, Is.EqualTo(sourceBefore));
        });
    }
}

internal static class ProjectWikiFixture
{
    public static readonly Regex SafeIdPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    public static string CopyToTemp(string tempRoot)
    {
        var source = SourcePath;
        var target = Path.Combine(tempRoot, "Memories");
        CopyDirectory(source, target);
        return target;
    }

    public static IReadOnlyDictionary<string, string> ReadSourceSnapshot() =>
        Directory.EnumerateFiles(SourcePath, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                path => Path.GetRelativePath(SourcePath, path),
                File.ReadAllText,
                StringComparer.OrdinalIgnoreCase);

    private static string SourcePath => Path.Combine(FindRepositoryRoot(), "Data", "Memories");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MemorySmith.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate MemorySmith.slnx from the test output directory.");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var targetFile = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: true);
        }
    }
}
