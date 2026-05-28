using System.Text.RegularExpressions;
using MemorySmith.App.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

[TestFixture]
public class CodeSearchServiceTests
{
    private string _repoRoot = null!;
    private string _dataPath = null!;

    [SetUp]
    public void SetUp()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-code-search-{Guid.NewGuid():N}");
        _dataPath = Path.Combine(_repoRoot, "Data", "Memories");
        Directory.CreateDirectory(_dataPath);
        Directory.CreateDirectory(Path.Combine(_repoRoot, "MemorySmith.App", "Services"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, "MemorySmith.Core", "Services"));
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_repoRoot))
        {
            Directory.Delete(_repoRoot, recursive: true);
        }
    }

    [Test]
    public async Task SearchAsync_BuildsSQLiteIndexAndRanksRelevantChunk()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), "obj/\n");
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "WidgetParser.cs"),
            "namespace MemorySmith.App.Services;\npublic static class WidgetParser\n{\n    public static string ParseWidgetTokens(string input) => input.Trim().ToUpperInvariant();\n}\n");
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.Core", "Services", "ArchivePlanner.cs"),
            "namespace MemorySmith.Core.Services;\npublic static class ArchivePlanner\n{\n    public static string BuildArchive(string input) => input + \" archive\";\n}\n");

        var service = CreateService(new HashEmbeddingProvider());

        var results = await service.SearchAsync(new CodeSearchQuery("ParseWidgetTokens ToUpperInvariant widget parser", Limit: 5), CancellationToken.None);

        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Not.Empty);
            Assert.That(results[0].DocumentPath, Is.EqualTo("MemorySmith.App/Services/WidgetParser.cs"));
            Assert.That(results[0].StartLine, Is.EqualTo(1));
            Assert.That(results[0].MatchReason, Does.Contain("cosine similarity"));
            Assert.That(File.Exists(Path.Combine(_repoRoot, "Data", "Graph", "code-search", "code-search.db")), Is.True);
            Assert.That(status.IndexedFileCount, Is.EqualTo(2));
            Assert.That(status.Build.Timings.EmbeddingCallCount, Is.GreaterThan(0));
            Assert.That(status.Build.Timings.EmbeddedChunkCount, Is.GreaterThan(0));
            Assert.That(status.Build.Timings.DatabaseWriteMilliseconds, Is.GreaterThanOrEqualTo(0));
        });
    }

    [Test]
    public async Task SearchAsync_HonorsGitIgnoreAndIncludeOverrides()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "MemorySmith.App", "obj"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, "MemorySmith.App", "Generated"));
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), "obj/\nMemorySmith.App/Generated/\n");
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "obj", "IgnoredChunk.cs"),
            "public static class IgnoredChunk { public static string Leak() => \"should never appear\"; }");
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Generated", "IncludedChunk.cs"),
            "public static class IncludedChunk { public static string BuildGeneratedPipeline() => \"generated pipeline\"; }");

        var service = CreateService(
            new HashEmbeddingProvider(),
            options => options.CodeSearch.IncludePatterns = ["MemorySmith.App/Generated/IncludedChunk.cs"]);

        var included = await service.SearchAsync(new CodeSearchQuery("generated pipeline", Limit: 5), CancellationToken.None);
        var ignored = await service.SearchAsync(new CodeSearchQuery("should never appear", Limit: 5), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(included.Select(result => result.DocumentPath), Does.Contain("MemorySmith.App/Generated/IncludedChunk.cs"));
            Assert.That(ignored, Is.Empty);
        });
    }

    [Test]
    public async Task SearchAsync_ReusesPersistedIndexWithoutReembeddingUnchangedFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "Planner.cs"),
            "namespace MemorySmith.App.Services;\npublic static class Planner\n{\n    public static string BuildPlan(string input) => input + \" plan\";\n}\n");

        var firstProvider = new CountingHashEmbeddingProvider();
        var firstService = CreateService(firstProvider);
        var firstResults = await firstService.SearchAsync(new CodeSearchQuery("build plan", Limit: 5), CancellationToken.None);

        var secondProvider = new CountingHashEmbeddingProvider();
        var secondService = CreateService(secondProvider);
        var secondResults = await secondService.SearchAsync(new CodeSearchQuery("build plan", Limit: 5), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(firstResults, Is.Not.Empty);
            Assert.That(secondResults, Is.Not.Empty);
            Assert.That(firstProvider.DocumentEmbeddingsRequested, Is.GreaterThan(0));
            Assert.That(secondProvider.DocumentEmbeddingsRequested, Is.EqualTo(0), "Unchanged files should reuse the persisted SQLite-backed index.");
            Assert.That(secondProvider.QueryEmbeddingsRequested, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SearchAsync_ReusesCachedRepeatedQueryWithinSameService()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "Planner.cs"),
            "namespace MemorySmith.App.Services;\npublic static class Planner\n{\n    public static string BuildPlan(string input) => input + \" plan\";\n}\n");

        var provider = new CountingHashEmbeddingProvider();
        var service = CreateService(provider);

        var firstResults = await service.SearchAsync(new CodeSearchQuery("build plan", Limit: 5), CancellationToken.None);
        var secondResults = await service.SearchAsync(new CodeSearchQuery("build plan", Limit: 5), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(firstResults, Is.Not.Empty);
            Assert.That(secondResults, Is.Not.Empty);
            Assert.That(provider.QueryEmbeddingsRequested, Is.EqualTo(1), "Identical warm queries should reuse the cached ranking response instead of re-embedding the query.");
        });
    }

    [Test]
    public async Task SearchAsync_ForceRebuildClearsWarmQueryCacheAndReembedsDocuments()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "Planner.cs"),
            "namespace MemorySmith.App.Services;\npublic static class Planner\n{\n    public static string BuildPlan(string input) => input + \" plan\";\n}\n");

        var provider = new CountingHashEmbeddingProvider();
        var service = CreateService(provider);

        await service.SearchAsync(new CodeSearchQuery("build plan", Limit: 5), CancellationToken.None);
        await service.SearchAsync(new CodeSearchQuery("build plan", Limit: 5), CancellationToken.None);
        await service.SearchAsync(new CodeSearchQuery("build plan", Limit: 5, ForceRebuild: true), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(provider.QueryEmbeddingsRequested, Is.EqualTo(2), "forceRebuild should clear the cached query embedding before rerunning the search.");
            Assert.That(provider.DocumentEmbeddingsRequested, Is.EqualTo(2), "forceRebuild should bypass warm document reuse and re-embed indexed documents.");
        });
    }

    [Test]
    public async Task GetStatusAsync_ReportsInProgressBuildWhileIndexing()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "Planner.cs"),
            "namespace MemorySmith.App.Services;\npublic static class Planner\n{\n    public static string BuildPlan(string input) => input + \" plan\";\n}\n");

        var provider = new BlockingHashEmbeddingProvider();
        var service = CreateService(provider, options =>
        {
            options.CodeSearch.IndexWriteBatchSize = 1;
            options.CodeSearch.StatusUpdateIntervalDocuments = 1;
        });

        var searchTask = Task.Run(async () => await service.SearchAsync(new CodeSearchQuery("build plan", Limit: 5), CancellationToken.None));
        Assert.That(provider.DocumentEmbeddingStarted.Wait(TimeSpan.FromSeconds(5)), Is.True, "The test provider never reached the document embedding gate.");

        var status = await service.GetStatusAsync(CancellationToken.None);

        provider.ReleaseDocumentEmbedding.Set();
        var results = await searchTask;

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Not.Empty);
            Assert.That(status.Build.IsRunning, Is.True);
            Assert.That(status.Build.State, Is.EqualTo("indexing"));
            Assert.That(status.Build.TotalFileCount, Is.EqualTo(1));
            Assert.That(status.Build.CurrentDocumentPath, Is.EqualTo("MemorySmith.App/Services/Planner.cs"));
        });
    }

    [Test]
    public async Task GetStatusAsync_ReportsWarmIncrementalReuseCountsAfterCompletedBuild()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "Planner.cs"),
            "namespace MemorySmith.App.Services;\npublic static class Planner\n{\n    public static string BuildPlan(string input) => input + \" plan\";\n}\n");

        var firstService = CreateService(new CountingHashEmbeddingProvider());
        await firstService.SearchAsync(new CodeSearchQuery("build plan", Limit: 5), CancellationToken.None);

        var secondService = CreateService(new CountingHashEmbeddingProvider());
        await secondService.SearchAsync(new CodeSearchQuery("build plan", Limit: 5), CancellationToken.None);
        var status = await secondService.GetStatusAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(status.Build.State, Is.EqualTo("completed"));
            Assert.That(status.Build.ProcessedFileCount, Is.EqualTo(1));
            Assert.That(status.Build.ReusedFileCount, Is.EqualTo(1));
            Assert.That(status.Build.UpdatedFileCount, Is.EqualTo(0));
            Assert.That(status.Build.ProgressPercentage, Is.EqualTo(100));
            Assert.That(status.Build.Timings.EmbeddingCallCount, Is.EqualTo(0));
        });
    }

    private CodeSearchService CreateService(ITextEmbeddingProvider provider, Action<MemorySmithOptions>? configure = null)
    {
        var options = new MemorySmithOptions
        {
            DataPath = _dataPath,
            CodeSearch = new CodeSearchOptions
            {
                RepositoryRootPath = "..",
                TargetDirectories = ["MemorySmith.App", "MemorySmith.Core"],
                IncludedFileExtensions = [".cs"],
                MaxResults = 10
            }
        };
        configure?.Invoke(options);
        return new CodeSearchService(provider, Options.Create(options), NullLogger<CodeSearchService>.Instance);
    }

    private class HashEmbeddingProvider : ITextEmbeddingProvider
    {
        public EmbeddingProviderStatus GetStatus() => new(true, "Hash embedding provider available.", null, null, 512, "Cpu", "Cpu", null, null);

        public virtual bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason)
        {
            embedding = BuildEmbedding(text);
            reason = null;
            return true;
        }

        protected static float[] BuildEmbedding(string text)
        {
            var vector = new float[512];
            foreach (Match match in Regex.Matches(text ?? string.Empty, "[A-Za-z0-9_]+"))
            {
                var token = match.Value.ToLowerInvariant();
                var slot = Math.Abs(token.GetHashCode()) % vector.Length;
                vector[slot] += 1f;
            }

            var magnitude = MathF.Sqrt(vector.Sum(value => value * value));
            if (magnitude > 0)
            {
                for (var index = 0; index < vector.Length; index++)
                {
                    vector[index] /= magnitude;
                }
            }

            return vector;
        }
    }

    private sealed class CountingHashEmbeddingProvider : HashEmbeddingProvider
    {
        public int QueryEmbeddingsRequested { get; private set; }

        public int DocumentEmbeddingsRequested { get; private set; }

        public override bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason)
        {
            if (kind == EmbeddingInputKind.Query)
            {
                QueryEmbeddingsRequested++;
            }
            else
            {
                DocumentEmbeddingsRequested++;
            }

            return base.TryEmbed(text, kind, out embedding, out reason);
        }
    }

    private sealed class BlockingHashEmbeddingProvider : HashEmbeddingProvider
    {
        public ManualResetEventSlim DocumentEmbeddingStarted { get; } = new(false);

        public ManualResetEventSlim ReleaseDocumentEmbedding { get; } = new(false);

        public override bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason)
        {
            if (kind == EmbeddingInputKind.Document)
            {
                DocumentEmbeddingStarted.Set();
                if (!ReleaseDocumentEmbedding.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("The test did not release the blocked document embedding in time.");
                }
            }

            return base.TryEmbed(text, kind, out embedding, out reason);
        }
    }
}