using System.Text.RegularExpressions;
using System.Text;
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
    public async Task SearchAsync_DefaultExcludePatternsSkipProjectDocsNoise()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "MemorySmith.Core", "Docs"));
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.Core", "Docs", "NoiseGuide.md"),
            "# Noise Guide\nThis document repeats BuildNoiseCollector and telemetry chatter.");
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.Core", "Services", "NoiseCollector.cs"),
            "namespace MemorySmith.Core.Services;\npublic static class NoiseCollector\n{\n    public static string BuildNoiseCollector(string input) => input + \" collector\";\n}\n");

        var service = CreateService(new HashEmbeddingProvider(), options =>
        {
            options.CodeSearch.IncludedFileExtensions = [".cs", ".md"];
        });

        var results = await service.SearchAsync(new CodeSearchQuery("BuildNoiseCollector telemetry chatter", Limit: 5), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Not.Empty);
            Assert.That(results.Select(result => result.DocumentPath), Does.Contain("MemorySmith.Core/Services/NoiseCollector.cs"));
            Assert.That(results.Select(result => result.DocumentPath), Does.Not.Contain("MemorySmith.Core/Docs/NoiseGuide.md"));
        });
    }

    [Test]
    public async Task SearchAsync_DemotesTestTargetsForImplementationQueries()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        Directory.CreateDirectory(Path.Combine(_repoRoot, "MemorySmith.Tests"));
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "WidgetRenderer.cs"),
            "namespace MemorySmith.App.Services;\npublic static class WidgetRenderer\n{\n    public static string BuildWidgetRenderer(string input) => input + \" implementation\";\n}\n");
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.Tests", "WidgetRendererTests.cs"),
            "namespace MemorySmith.Tests;\npublic static class WidgetRendererTests\n{\n    public static string BuildWidgetRenderer(string input) => input + \" implementation\";\n}\n");

        var service = CreateService(new RankingBiasEmbeddingProvider());

        var results = await service.SearchAsync(new CodeSearchQuery("BuildWidgetRenderer implementation", Limit: 5), CancellationToken.None);
        var testResultIndex = results
            .Select((result, index) => new { result.DocumentPath, index })
            .Where(item => item.DocumentPath == "MemorySmith.Tests/WidgetRendererTests.cs")
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .Single();

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Not.Empty);
            Assert.That(results[0].DocumentPath, Is.EqualTo("MemorySmith.App/Services/WidgetRenderer.cs"));
            Assert.That(testResultIndex, Is.Not.EqualTo(0));
        });
    }

    [Test]
    public async Task SearchAsync_LexicalFallbackMatchesSnakeCaseQueryAgainstCamelCaseIdentifier()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "VectorSearchTelemetryReporter.cs"),
            "namespace MemorySmith.App.Services;\npublic static class VectorSearchTelemetryReporter\n{\n    public static string EmitHealthTrace(string input) => input + \" telemetry\";\n}\n");
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.Core", "Services", "Noise.cs"),
            "namespace MemorySmith.Core.Services;\npublic static class Noise\n{\n    public static string EmitUnrelatedTrace(string input) => input + \" noise\";\n}\n");

        var service = CreateService(new QueryFailureEmbeddingProvider());

        var results = await service.SearchAsync(new CodeSearchQuery("vector_search", Limit: 5), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Not.Empty);
            Assert.That(results[0].DocumentPath, Is.EqualTo("MemorySmith.App/Services/VectorSearchTelemetryReporter.cs"));
            Assert.That(results[0].MatchReason, Does.Contain("Lexical fallback matched"));
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
    public async Task SearchAsync_SkipsRedundantStalenessReindexWithinCooldownWindow()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "Planner.cs"),
            "namespace MemorySmith.App.Services;\npublic static class Planner\n{\n    public static string BuildPlan(string input) => input + \" plan\";\n}\n");

        var provider = new CountingHashEmbeddingProvider();
        var service = CreateService(provider, options =>
        {
            options.CodeSearch.IndexStalenessCheckCooldownSeconds = 300;
        });

        await service.SearchAsync(new CodeSearchQuery("build plan", Limit: 5), CancellationToken.None);
        var firstStatus = await service.GetStatusAsync(CancellationToken.None);

        await service.SearchAsync(new CodeSearchQuery("planner implementation", Limit: 5), CancellationToken.None);
        var secondStatus = await service.GetStatusAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(firstStatus.Build.State, Is.EqualTo("completed"));
            Assert.That(firstStatus.Build.UpdatedAtUtc, Is.Not.Null);
            Assert.That(secondStatus.Build.UpdatedAtUtc, Is.EqualTo(firstStatus.Build.UpdatedAtUtc), "The second query should reuse the completed build within the staleness cooldown window.");
            Assert.That(provider.QueryEmbeddingsRequested, Is.EqualTo(2));
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
    public async Task SearchAsync_UsesBatchDocumentEmbeddingsWhenProviderSupportsIt()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "BatchPlanner.cs"),
            BuildLargeCodeFile("BatchPlanner", 60));

        var provider = new BatchCountingHashEmbeddingProvider();
        var service = CreateService(provider, options =>
        {
            options.CodeSearch.ChunkLineCount = 5;
            options.CodeSearch.ChunkOverlapLineCount = 0;
            options.CodeSearch.EmbeddingBatchSize = 4;
        });

        var results = await service.SearchAsync(new CodeSearchQuery("BatchPlanner step", Limit: 5), CancellationToken.None);
        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Not.Empty);
            Assert.That(provider.BatchDocumentEmbeddingsRequested, Is.GreaterThan(0));
            Assert.That(provider.DocumentEmbeddingsRequested, Is.EqualTo(0));
            Assert.That(status.Build.Timings.EmbeddedChunkCount, Is.GreaterThan(1));
            Assert.That(status.Build.Timings.EmbeddingCallCount, Is.LessThan(status.Build.Timings.EmbeddedChunkCount));
        });
    }

    [Test]
    public async Task SearchAsync_FallsBackToScalarDocumentEmbeddingsWhenBatchPathFails()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "FallbackPlanner.cs"),
            BuildLargeCodeFile("FallbackPlanner", 40));

        var provider = new BatchCountingHashEmbeddingProvider { FailBatchDocumentEmbeddings = true };
        var service = CreateService(provider, options =>
        {
            options.CodeSearch.ChunkLineCount = 5;
            options.CodeSearch.ChunkOverlapLineCount = 0;
            options.CodeSearch.EmbeddingBatchSize = 4;
        });

        var results = await service.SearchAsync(new CodeSearchQuery("FallbackPlanner step", Limit: 5), CancellationToken.None);
        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Not.Empty);
            Assert.That(provider.BatchDocumentEmbeddingsRequested, Is.GreaterThan(0));
            Assert.That(provider.DocumentEmbeddingsRequested, Is.GreaterThan(0));
            Assert.That(status.Build.Timings.EmbeddedChunkCount, Is.GreaterThan(1));
            Assert.That(status.Build.Timings.EmbeddingCallCount, Is.GreaterThanOrEqualTo(status.Build.Timings.EmbeddedChunkCount));
        });
    }

    [Test]
    public async Task SearchAsync_MarksDocumentFailedWhenScalarDocumentEmbeddingFails()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "BrokenPlanner.cs"),
            "namespace MemorySmith.App.Services;\npublic static class BrokenPlanner\n{\n    public static string BuildBrokenPlan(string input) => input + \" broken\";\n}\n");

        var provider = new CountingHashEmbeddingProvider { FailDocumentEmbeddings = true };
        var service = CreateService(provider);

        var results = await service.SearchAsync(new CodeSearchQuery("broken plan", Limit: 5), CancellationToken.None);
        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Empty);
            Assert.That(status.IndexedFileCount, Is.EqualTo(0));
            Assert.That(status.Build.FailedFileCount, Is.EqualTo(1));
            Assert.That(status.Build.UpdatedFileCount, Is.EqualTo(0));
            Assert.That(status.Build.LastError, Does.Contain("BrokenPlanner.cs"));
            Assert.That(status.Build.LastError, Does.Contain("Simulated document embedding failure."));
        });
    }

    [Test]
    public async Task SearchAsync_MarksDocumentFailedWhenBatchFallbackScalarEmbeddingFails()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "BrokenBatchPlanner.cs"),
            BuildLargeCodeFile("BrokenBatchPlanner", 40));

        var provider = new BatchCountingHashEmbeddingProvider
        {
            FailBatchDocumentEmbeddings = true,
            FailDocumentEmbeddings = true
        };

        var service = CreateService(provider, options =>
        {
            options.CodeSearch.ChunkLineCount = 5;
            options.CodeSearch.ChunkOverlapLineCount = 0;
            options.CodeSearch.EmbeddingBatchSize = 4;
        });

        var results = await service.SearchAsync(new CodeSearchQuery("BrokenBatchPlanner step", Limit: 5), CancellationToken.None);
        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Empty);
            Assert.That(provider.BatchDocumentEmbeddingsRequested, Is.GreaterThan(0));
            Assert.That(provider.DocumentEmbeddingsRequested, Is.GreaterThan(0));
            Assert.That(status.IndexedFileCount, Is.EqualTo(0));
            Assert.That(status.Build.FailedFileCount, Is.EqualTo(1));
            Assert.That(status.Build.LastError, Does.Contain("BrokenBatchPlanner.cs"));
            Assert.That(status.Build.LastError, Does.Contain("Simulated document embedding failure."));
        });
    }

    [Test]
    public async Task SearchAsync_FallsBackToFullVectorScanWhenLexicalPrefilterMissesSemanticHit()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "SemanticAliasTarget.cs"),
            "namespace MemorySmith.App.Services;\npublic static class SemanticAliasTarget\n{\n    public static string BuildOpaquePipeline(string input) => input + \" latent vector\";\n}\n");
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.Core", "Services", "Distractor.cs"),
            "namespace MemorySmith.Core.Services;\npublic static class Distractor\n{\n    public static string BuildDifferentPath(string input) => input + \" distractor\";\n}\n");

        var provider = new SemanticAliasEmbeddingProvider();
        var service = CreateService(provider);

        var results = await service.SearchAsync(new CodeSearchQuery("semantic alias retrieval", Limit: 5), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Not.Empty);
            Assert.That(results[0].DocumentPath, Is.EqualTo("MemorySmith.App/Services/SemanticAliasTarget.cs"));
            Assert.That(provider.QueryEmbeddingsRequested, Is.EqualTo(1));
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

    [Test]
    public async Task BuildIndex_ResumesInterruptedBuildAndSkipsAlreadyEmbeddedDocuments()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "FileA.cs"),
            "namespace MemorySmith.App.Services;\npublic static class FileA\n{\n    public static string MethodA(string input) => input + \" A\";\n}\n");
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.Core", "Services", "FileB.cs"),
            "namespace MemorySmith.Core.Services;\npublic static class FileB\n{\n    public static string MethodB(string input) => input + \" B\";\n}\n");

        // Step 1: build a complete index so both files are embedded and in the DB
        var firstProvider = new CountingHashEmbeddingProvider();
        var firstService = CreateService(firstProvider, options =>
        {
            options.CodeSearch.ResumableBuildsEnabled = true;
        });
        await firstService.SearchAsync(new CodeSearchQuery("MethodA MethodB", Limit: 5), CancellationToken.None);

        var indexDbPath = Path.Combine(_repoRoot, "Data", "Graph", "code-search", "code-search.db");
        Assert.That(File.Exists(indexDbPath), Is.True, "Index DB should exist after first build.");
        Assert.That(firstProvider.DocumentEmbeddingsRequested, Is.EqualTo(2));

        // Step 2: manipulate the build log to simulate an interrupted build:
        //   - rewrite the completed entry as in-progress
        //   - remove FileA from the document log, keeping FileB (so FileA looks already-embedded)
        await using (var editConn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = indexDbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString()))
        {
            await editConn.OpenAsync();
            await using var tx = editConn.BeginTransaction();
            await using (var updateLog = editConn.CreateCommand())
            {
                updateLog.Transaction = tx;
                updateLog.CommandText = "UPDATE CodeSearchBuildLog SET State = 'in-progress', CompletedAtUtc = NULL WHERE State = 'completed';";
                await updateLog.ExecuteNonQueryAsync();
            }

            await using (var deleteDoc = editConn.CreateCommand())
            {
                deleteDoc.Transaction = tx;
                // Keep only FileB in the log; FileA will be treated as not-yet-embedded
                deleteDoc.CommandText = "DELETE FROM CodeSearchBuildLogDocument WHERE DocumentPath NOT LIKE '%FileB.cs';";
                await deleteDoc.ExecuteNonQueryAsync();
            }

            // Also remove FileA's chunks from the index so warm-reuse cannot skip it:
            // this isolates the resume-log skip from the warm-reuse path.
            await using (var deleteChunks = editConn.CreateCommand())
            {
                deleteChunks.Transaction = tx;
                deleteChunks.CommandText = "DELETE FROM CodeSearchChunks WHERE DocumentPath LIKE '%FileA.cs';";
                await deleteChunks.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }

        SqliteConnection.ClearAllPools();

        // Step 3: new service instance picks up the interrupted build and resumes
        var resumeProvider = new CountingHashEmbeddingProvider();
        var resumeService = CreateService(resumeProvider, options =>
        {
            options.CodeSearch.ResumableBuildsEnabled = true;
        });
        await resumeService.SearchAsync(new CodeSearchQuery("MethodA MethodB", Limit: 5), CancellationToken.None);
        var resumeStatus = await resumeService.GetStatusAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            // FileA was not in the build log, so it must be re-embedded (1 embedding)
            // FileB was already in the build log, so it should be reused (0 embeddings for B)
            Assert.That(resumeProvider.DocumentEmbeddingsRequested, Is.EqualTo(1), "Only the non-logged file should be re-embedded on resume.");
            Assert.That(resumeStatus.Build.State, Is.EqualTo("completed"));
        });
    }

    [Test]
    public async Task MergeShardAsync_ThrowsWhenShardFileDoesNotExist()
    {
        var service = CreateService(new HashEmbeddingProvider());
        var nonExistentPath = Path.Combine(_repoRoot, "nonexistent-shard.db");

        Assert.ThrowsAsync<FileNotFoundException>(
            async () => await service.MergeShardAsync(nonExistentPath, preferNewer: true, CancellationToken.None));
    }

    [Test]
    public async Task MergeShardAsync_InsertsNewChunksFromShard()
    {
        // Build main index with FileA only
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "MainFile.cs"),
            "namespace MemorySmith.App.Services;\npublic static class MainFile\n{\n    public static string MainMethod(string input) => input + \" main\";\n}\n");
        var mainService = CreateService(new HashEmbeddingProvider());
        await mainService.SearchAsync(new CodeSearchQuery("MainMethod main file", Limit: 5), CancellationToken.None);

        // Build shard index with FileB only (separate data dir)
        var shardDataPath = Path.Combine(Path.GetTempPath(), $"memorysmith-shard-{Guid.NewGuid():N}", "Data", "Memories");
        Directory.CreateDirectory(shardDataPath);
        var shardRepoRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-shard-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(shardRepoRoot, "MemorySmith.App", "Services"));
        Directory.CreateDirectory(Path.Combine(shardRepoRoot, "MemorySmith.Core", "Services"));
        await File.WriteAllTextAsync(Path.Combine(shardRepoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(shardRepoRoot, "MemorySmith.App", "Services", "ShardFile.cs"),
            "namespace MemorySmith.App.Services;\npublic static class ShardFile\n{\n    public static string ShardMethod(string input) => input + \" shard\";\n}\n");

        var shardOptions = new MemorySmithOptions
        {
            DataPath = shardDataPath,
            CodeSearch = new CodeSearchOptions
            {
                RepositoryRootPath = "..",
                TargetDirectories = ["MemorySmith.App", "MemorySmith.Core"],
                IncludedFileExtensions = [".cs"],
                MaxResults = 10
            }
        };
        // Adjust RepositoryRootPath to point to shardRepoRoot
        shardOptions.CodeSearch.RepositoryRootPath = shardRepoRoot;
        var shardService = new CodeSearchService(new HashEmbeddingProvider(), Options.Create(shardOptions), NullLogger<CodeSearchService>.Instance);
        await shardService.SearchAsync(new CodeSearchQuery("ShardMethod shard file", Limit: 5), CancellationToken.None);

        var shardDbPath = Path.Combine(Path.GetDirectoryName(shardDataPath)!.Replace("Memories", string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), "Graph", "code-search", "code-search.db");
        // Compute the actual shard DB path from shardDataPath
        var shardDataRoot = Directory.GetParent(shardDataPath)!.FullName;
        var actualShardDbPath = Path.Combine(shardDataRoot, "Graph", "code-search", "code-search.db");
        SqliteConnection.ClearAllPools();

        Assert.That(File.Exists(actualShardDbPath), Is.True, "Shard DB should exist after shard build.");

        var result = await mainService.MergeShardAsync(actualShardDbPath, preferNewer: true, CancellationToken.None);

        // Note: we intentionally do NOT search for shard content here because the main service's staleness
        // checker will prune chunks whose source files don't exist in the main repo root.
        // The counter assertions are the meaningful signal that the merge operated correctly.
        Assert.Multiple(() =>
        {
            Assert.That(result.InsertedChunkCount, Is.GreaterThan(0), "Chunks from the shard should be inserted into the main index.");
            Assert.That(result.UpdatedChunkCount, Is.EqualTo(0));
            Assert.That(result.TotalShardChunkCount, Is.GreaterThan(0));
            Assert.That(result.SkippedChunkCount, Is.EqualTo(0), "No chunks should be skipped when they are all new.");
        });

        // Cleanup shard temp dirs
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Directory.GetParent(shardDataRoot)!.FullName, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task MergeShardAsync_SkipsAllChunksWhenPreferNewerFalseAndChunksAlreadyExist()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "SharedFile.cs"),
            "namespace MemorySmith.App.Services;\npublic static class SharedFile\n{\n    public static string SharedMethod(string input) => input + \" shared\";\n}\n");
        var mainService = CreateService(new HashEmbeddingProvider());
        await mainService.SearchAsync(new CodeSearchQuery("SharedMethod shared", Limit: 5), CancellationToken.None);

        // Build a shard with the same file (same DocumentPath, so it already exists in main)
        var shardDataPath = Path.Combine(Path.GetTempPath(), $"memorysmith-shard2-{Guid.NewGuid():N}", "Data", "Memories");
        Directory.CreateDirectory(shardDataPath);
        var shardRepoRoot = _repoRoot; // Same source files → same DocumentPath keys
        var shardOptions = new MemorySmithOptions
        {
            DataPath = shardDataPath,
            CodeSearch = new CodeSearchOptions
            {
                RepositoryRootPath = shardRepoRoot,
                TargetDirectories = ["MemorySmith.App", "MemorySmith.Core"],
                IncludedFileExtensions = [".cs"],
                MaxResults = 10
            }
        };
        var shardService = new CodeSearchService(new HashEmbeddingProvider(), Options.Create(shardOptions), NullLogger<CodeSearchService>.Instance);
        await shardService.SearchAsync(new CodeSearchQuery("SharedMethod shared", Limit: 5), CancellationToken.None);

        var shardDataRoot = Directory.GetParent(shardDataPath)!.FullName;
        var actualShardDbPath = Path.Combine(shardDataRoot, "Graph", "code-search", "code-search.db");
        SqliteConnection.ClearAllPools();

        // Merge with preferNewer = false: existing chunks should not be overwritten
        var result = await mainService.MergeShardAsync(actualShardDbPath, preferNewer: false, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.InsertedChunkCount, Is.EqualTo(0), "Chunks already in the main index should not be inserted again.");
            Assert.That(result.UpdatedChunkCount, Is.EqualTo(0), "Chunks already in the main index should not be updated when preferNewer=false.");
            Assert.That(result.SkippedChunkCount, Is.EqualTo(result.TotalShardChunkCount), "All shard chunks should be skipped when preferNewer=false and the chunks already exist.");
        });

        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Directory.GetParent(shardDataRoot)!.FullName, recursive: true); } catch { /* best effort */ }
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

    private static string BuildLargeCodeFile(string className, int methodCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine("namespace MemorySmith.App.Services;");
        builder.Append("public static class ").Append(className).AppendLine();
        builder.AppendLine("{");
        for (var index = 0; index < methodCount; index++)
        {
            builder.Append("    public static string Step")
                .Append(index)
                .Append("(string input) => input + \" step-")
                .Append(index)
                .AppendLine("\";");
        }

        builder.AppendLine("}");
        return builder.ToString();
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

    private class CountingHashEmbeddingProvider : HashEmbeddingProvider
    {
        public int QueryEmbeddingsRequested { get; private set; }

        public int DocumentEmbeddingsRequested { get; private set; }

        public bool FailDocumentEmbeddings { get; init; }

        public override bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason)
        {
            if (kind == EmbeddingInputKind.Query)
            {
                QueryEmbeddingsRequested++;
            }
            else
            {
                DocumentEmbeddingsRequested++;
                if (FailDocumentEmbeddings)
                {
                    embedding = [];
                    reason = "Simulated document embedding failure.";
                    return false;
                }
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

    private sealed class BatchCountingHashEmbeddingProvider : CountingHashEmbeddingProvider, IBatchTextEmbeddingProvider
    {
        public int BatchDocumentEmbeddingsRequested { get; private set; }

        public bool FailBatchDocumentEmbeddings { get; init; }

        public bool TryEmbedBatch(IReadOnlyList<string> texts, EmbeddingInputKind kind, out IReadOnlyList<float[]> embeddings, out string? reason)
        {
            embeddings = [];
            reason = null;

            if (kind != EmbeddingInputKind.Document)
            {
                var results = new List<float[]>(texts.Count);
                foreach (var text in texts)
                {
                    TryEmbed(text, kind, out var embedding, out _);
                    results.Add(embedding);
                }

                embeddings = results;
                return true;
            }

            BatchDocumentEmbeddingsRequested++;
            if (FailBatchDocumentEmbeddings)
            {
                reason = "Simulated batch failure.";
                return false;
            }

            embeddings = texts.Select(BuildEmbedding).ToArray();
            return true;
        }
    }

    private sealed class SemanticAliasEmbeddingProvider : ITextEmbeddingProvider
    {
        public int QueryEmbeddingsRequested { get; private set; }

        public EmbeddingProviderStatus GetStatus() => new(true, "Semantic alias embedding provider available.", null, null, 2, "Cpu", "Cpu", null, null);

        public bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason)
        {
            if (kind == EmbeddingInputKind.Query)
            {
                QueryEmbeddingsRequested++;
            }

            reason = null;
            embedding = text.Contains("semantic alias retrieval", StringComparison.OrdinalIgnoreCase)
                || text.Contains("BuildOpaquePipeline", StringComparison.OrdinalIgnoreCase)
                ? [1f, 0f]
                : [0f, 1f];
            return true;
        }
    }

    private sealed class QueryFailureEmbeddingProvider : HashEmbeddingProvider
    {
        public override bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason)
        {
            if (kind == EmbeddingInputKind.Query)
            {
                embedding = [];
                reason = "Simulated query embedding failure.";
                return false;
            }

            return base.TryEmbed(text, kind, out embedding, out reason);
        }
    }

    private sealed class RankingBiasEmbeddingProvider : ITextEmbeddingProvider
    {
        public EmbeddingProviderStatus GetStatus() => new(true, "Ranking bias provider available.", null, null, 2, "Cpu", "Cpu", null, null);

        public bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason)
        {
            reason = null;
            if (kind == EmbeddingInputKind.Query)
            {
                embedding = [1f, 0f];
                return true;
            }

            if (text.Contains("MemorySmith.Tests/WidgetRendererTests.cs", StringComparison.OrdinalIgnoreCase))
            {
                embedding = [0.98f, 0f];
                return true;
            }

            if (text.Contains("MemorySmith.App/Services/WidgetRenderer.cs", StringComparison.OrdinalIgnoreCase))
            {
                embedding = [0.95f, 0f];
                return true;
            }

            embedding = [0.25f, 0f];
            return true;
        }
    }
}