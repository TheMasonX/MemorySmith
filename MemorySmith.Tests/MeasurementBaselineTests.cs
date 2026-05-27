using MemorySmith.App.Services;
using MemorySmith.Core.Indexing;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

[TestFixture]
public class MeasurementBaselineTests
{
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-measurements-{Guid.NewGuid():N}");
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
    public async Task GetSnapshotAsync_MeasuresCopiedProjectWikiWithoutMutatingSourceWiki()
    {
        var sourceMemoriesBefore = ProjectWikiFixture.ReadSourceSnapshot();
        var sourcePagesBefore = ReadDirectorySnapshot(Path.Combine(FindRepositoryRoot(), "Data", "Pages"));
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        var pagesPath = CopyPagesToTemp(_tempRoot);
        var service = CreateService(new FileMemoryStore(dataPath, new StorageDiagnostics()), new FilePageService(pagesPath));

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        var sourceMemoriesAfter = ProjectWikiFixture.ReadSourceSnapshot();
        var sourcePagesAfter = ReadDirectorySnapshot(Path.Combine(FindRepositoryRoot(), "Data", "Pages"));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Search.Modes.Select(mode => mode.Mode), Is.EqualTo(new[] { "lexical", "semantic", "hybrid", "unified", "context-pack" }));
            Assert.That(snapshot.Search.Modes, Has.All.Matches<SearchModeMeasurement>(mode => mode.ProbeCount > 0));
            Assert.That(snapshot.Search.Modes, Has.All.Matches<SearchModeMeasurement>(mode => mode.MeanReciprocalRank is >= 0 and <= 1));
            Assert.That(snapshot.Search.Modes, Has.All.Matches<SearchModeMeasurement>(mode => mode.RecallAt5 is >= 0 and <= 1));
            Assert.That(snapshot.SemanticSearchMode, Is.EqualTo("token-fallback"));
            Assert.That(snapshot.Pages.PageCount, Is.GreaterThan(0));
            Assert.That(snapshot.Pages.LongestPages, Is.Not.Empty);
            Assert.That(snapshot.Tags.RecordCount, Is.GreaterThan(0));
            Assert.That(snapshot.Tags.DistinctTagCount, Is.GreaterThan(0));
            Assert.That(snapshot.SourceLinks.TotalSourceLinks, Is.GreaterThan(0));
            Assert.That(snapshot.Thresholds.SearchPromotionMinimumMrr, Is.EqualTo(0.75));
            Assert.That(sourceMemoriesAfter, Is.EqualTo(sourceMemoriesBefore));
            Assert.That(sourcePagesAfter, Is.EqualTo(sourcePagesBefore));
        });
    }

    [Test]
    public async Task GetSnapshotAsync_ReportsTagPolicyAndSourceLinkHealth()
    {
        var sourcePath = Path.Combine(_tempRoot, "source.txt");
        await File.WriteAllTextAsync(sourcePath, "one" + Environment.NewLine + "two" + Environment.NewLine);
        var store = new InMemoryMemoryStore();
        store.Save(new MemoryRecord
        {
            Id = "problem-record",
            Title = "Problem Record",
            Content = "Problem content for measurement baseline.",
            Tags = ["working", "retrieval", "unknown-topic", "kind:rule", "kind:guide"],
            SourceLinks =
            [
                new SourceLink { Uri = "%MissingRoot%missing.cs" },
                new SourceLink { Uri = "%AllowedRoot%missing.cs" },
                new SourceLink { Uri = "%AllowedRoot%source.txt", StartLine = 3, EndLine = 2 }
            ]
        });
        var pagesPath = Path.Combine(_tempRoot, "Pages");
        Directory.CreateDirectory(pagesPath);
        await File.WriteAllTextAsync(Path.Combine(pagesPath, "baseline.md"), "# Baseline\n\nMeasurement page.");
        var policyPath = Path.Combine(_tempRoot, "Policies", "tag-policy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);
        await File.WriteAllTextAsync(policyPath, """
            {
              "schemaVersion": 1,
              "mode": "warn",
              "namespaces": [
                { "name": "kind", "cardinality": "single", "valueKind": "enum", "allowedValues": ["rule"] },
                { "name": "kind", "cardinality": "many", "valueKind": "tag" }
              ],
              "plainTags": {
                "mode": "allowWithSuggestions",
                "allowlist": ["project-wiki"],
                "blocklist": ["working"],
                "aliases": { "retrieval": "search" }
              }
            }
            """);
        var options = Options.Create(new MemorySmithOptions
        {
            Governance = new GovernanceOptions { TagPolicyPath = policyPath },
            SourceLinks = new SourceLinkOptions { AllowedFileRootVariables = ["AllowedRoot"] }
        });
        var vars = new MutableVarStore(new Dictionary<string, string>
        {
            ["AllowedRoot"] = _tempRoot + Path.DirectorySeparatorChar
        });
        var service = CreateService(store, new FilePageService(pagesPath), vars, options);

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Tags.BlockedTagUseCount, Is.EqualTo(1));
            Assert.That(snapshot.Tags.AliasCandidateUseCount, Is.EqualTo(1));
            Assert.That(snapshot.Tags.UnknownPlainTagCount, Is.EqualTo(1));
            Assert.That(snapshot.Tags.DuplicatePolicyNamespaceWarningCount, Is.EqualTo(1));
            Assert.That(snapshot.SourceLinks.MissingVariableCount, Is.EqualTo(1));
            Assert.That(snapshot.SourceLinks.MissingFileCount, Is.EqualTo(1));
            Assert.That(snapshot.SourceLinks.InvalidLineRangeCount, Is.EqualTo(1));
            Assert.That(snapshot.SourceLinks.LineOutOfRangeCount, Is.EqualTo(1));
            Assert.That(snapshot.SourceLinks.SourceWarningCount, Is.GreaterThanOrEqualTo(4));
            Assert.That(snapshot.SourceLinks.BrokenSourceLinkRate, Is.GreaterThan(0));
        });
    }

    private MeasurementBaselineService CreateService(
        IMemoryStore store,
        IPageService pages,
        IVarStore? vars = null,
        IOptions<MemorySmithOptions>? options = null)
    {
        options ??= Options.Create(new MemorySmithOptions
        {
            Governance = new GovernanceOptions
            {
                TagPolicyPath = Path.Combine(FindRepositoryRoot(), "Data", "Policies", "tag-policy.json")
            }
        });
        vars ??= new MutableVarStore(new Dictionary<string, string>
        {
            ["MemorySmithRepo"] = FindRepositoryRoot() + Path.DirectorySeparatorChar
        });
        var tagPolicy = new TagPolicyService(options);
        var diagnostics = new MemoryDiagnosticsService(tagPolicy, new VarResolver(vars, options), store, options);
        var memoryService = new MemoryApplicationService(
            store,
            new RecordingEventStore(),
            new MemoryIndex(),
            new BackgroundServiceTelemetryTracker(),
            new RecordingMemoryChangePublisher(),
            options,
            diagnostics: diagnostics);

        return new MeasurementBaselineService(
            memoryService,
            pages,
            store,
            diagnostics,
            tagPolicy,
            new UnavailableEmbeddingProvider());
    }

    private static string CopyPagesToTemp(string tempRoot)
    {
        var source = Path.Combine(FindRepositoryRoot(), "Data", "Pages");
        var target = Path.Combine(tempRoot, "Pages");
        CopyDirectory(source, target);
        return target;
    }

    private static IReadOnlyDictionary<string, string> ReadDirectorySnapshot(string source) =>
        Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                path => Path.GetRelativePath(source, path),
                File.ReadAllText,
                StringComparer.OrdinalIgnoreCase);

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

    private sealed class MutableVarStore : IVarStore
    {
        private readonly IReadOnlyDictionary<string, string> _vars;

        public MutableVarStore(IReadOnlyDictionary<string, string> vars)
        {
            _vars = vars;
        }

        public IReadOnlyDictionary<string, string> Load() => _vars;

        public void Save(IReadOnlyDictionary<string, string> vars)
        {
        }
    }

    private sealed class UnavailableEmbeddingProvider : ITextEmbeddingProvider
    {
        public EmbeddingProviderStatus GetStatus() => new(false, "Test provider unavailable; semantic search uses token fallback.", null, null, null, "Cpu", "None", null, null);

        public bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason)
        {
            embedding = [];
            reason = "Test provider unavailable.";
            return false;
        }
    }
}