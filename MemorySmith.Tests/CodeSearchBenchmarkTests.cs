using System.Diagnostics;
using MemorySmith.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace MemorySmith.Tests;

[TestFixture]
[Category("Benchmark")]
[NonParallelizable]
public sealed class CodeSearchBenchmarkTests
{
    private string _repoRoot = null!;
    private string _dataPath = null!;
    private CodeSearchService _service = null!;

    private static readonly (string Query, string ExpectedTopDocument, int MaxRank, int MaxMs)[] RelevanceProbes =
    [
        ("screwdriver", "MemorySmith.App/Services/ToolCatalog.cs", 1, 200),
        ("cli command runner", "MemorySmith.App/Services/CliRunner.cs", 1, 200),
        ("proposal review task", "MemorySmith.Core/Services/ProposalTaskBoard.cs", 1, 200),
        ("opaque pipeline", "MemorySmith.Core/Services/UnrelatedPipeline.cs", 1, 200)
    ];

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-code-search-bench-tests-{Guid.NewGuid():N}");
        _dataPath = Path.Combine(_repoRoot, "Data", "Memories");
        Directory.CreateDirectory(_dataPath);
        Directory.CreateDirectory(Path.Combine(_repoRoot, "MemorySmith.App", "Services"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, "MemorySmith.Core", "Services"));

        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "ToolCatalog.cs"),
            "namespace MemorySmith.App.Services;\npublic static class ToolCatalog\n{\n    public static string RegisterTool(string input) => input + \" tool utility harness\";\n}\n");
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.App", "Services", "CliRunner.cs"),
            "namespace MemorySmith.App.Services;\npublic static class CliRunner\n{\n    public static string RunCliTool(string input) => input + \" cli command runner\";\n}\n");
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.Core", "Services", "ProposalTaskBoard.cs"),
            "namespace MemorySmith.Core.Services;\npublic static class ProposalTaskBoard\n{\n    public static string ReviewProposalTask(string input) => input + \" proposal review task workflow\";\n}\n");
        await File.WriteAllTextAsync(
            Path.Combine(_repoRoot, "MemorySmith.Core", "Services", "UnrelatedPipeline.cs"),
            "namespace MemorySmith.Core.Services;\npublic static class UnrelatedPipeline\n{\n    public static string BuildOpaquePipeline(string input) => input + \" opaque pipeline\";\n}\n");

        var options = new MemorySmithOptions
        {
            DataPath = _dataPath,
            CodeSearch = new CodeSearchOptions
            {
                RepositoryRootPath = "..",
                TargetDirectories = ["MemorySmith.App", "MemorySmith.Core"],
                IncludedFileExtensions = [".cs"],
                MaxResults = 10,
                MaxResultsPerDocument = 2
            }
        };

        _service = new CodeSearchService(new HashEmbeddingProvider(), Options.Create(options), NullLogger<CodeSearchService>.Instance);

        _ = await _service.SearchAsync(new CodeSearchQuery("warmup", Limit: 3), CancellationToken.None);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _service.Dispose();
        if (Directory.Exists(_repoRoot))
        {
            Directory.Delete(_repoRoot, recursive: true);
        }
    }

    [Test]
    public async Task RelevanceScorecard_MeetsTopRankAndLatencyTargets()
    {
        var reciprocalRanks = new List<double>();

        foreach (var probe in RelevanceProbes)
        {
            var sw = Stopwatch.StartNew();
            var results = await _service.SearchAsync(new CodeSearchQuery(probe.Query, Limit: 5), CancellationToken.None);
            sw.Stop();

            var paths = results.Select(result => result.DocumentPath).ToList();
            var rank = paths.FindIndex(path => string.Equals(path, probe.ExpectedTopDocument, StringComparison.OrdinalIgnoreCase)) + 1;
            reciprocalRanks.Add(rank == 0 ? 0 : 1.0 / rank);

            Assert.Multiple(() =>
            {
                Assert.That(rank, Is.GreaterThan(0), $"Query '{probe.Query}' did not return expected document '{probe.ExpectedTopDocument}'. Results: {string.Join(", ", paths)}");
                Assert.That(rank, Is.LessThanOrEqualTo(probe.MaxRank), $"Query '{probe.Query}' ranked expected document at {rank}; expected <= {probe.MaxRank}.");
                Assert.That(sw.ElapsedMilliseconds, Is.LessThanOrEqualTo(probe.MaxMs), $"Query '{probe.Query}' took {sw.ElapsedMilliseconds} ms; expected <= {probe.MaxMs} ms.");
            });
        }

        Assert.That(reciprocalRanks.Average(), Is.GreaterThanOrEqualTo(1.0), "Code-search relevance MRR should remain at 1.0 on benchmark fixture probes.");
    }

    [Test]
    public async Task WarmThroughputBaseline_50QueriesUnder1000Ms()
    {
        var queries = new[]
        {
            "tool utility", "screwdriver", "cli command runner", "proposal review task", "opaque pipeline"
        };

        foreach (var query in queries)
        {
            _ = await _service.SearchAsync(new CodeSearchQuery(query, Limit: 5), CancellationToken.None);
        }

        var sw = Stopwatch.StartNew();
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var query = queries[iteration % queries.Length];
            _ = await _service.SearchAsync(new CodeSearchQuery(query, Limit: 5), CancellationToken.None);
        }

        sw.Stop();

        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(1000), $"50 warm code-search queries took {sw.ElapsedMilliseconds} ms; expected under 1,000 ms.");
        TestContext.Out.WriteLine($"50 warm code-search queries: {sw.ElapsedMilliseconds} ms ({sw.ElapsedMilliseconds / 50.0:0.###} ms avg)");
    }

    private sealed class HashEmbeddingProvider : ITextEmbeddingProvider
    {
        public EmbeddingProviderStatus GetStatus() => new(true, "Hash embedding provider available.", null, null, 512, "Cpu", "Cpu", null, null);

        public bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason)
        {
            embedding = BuildEmbedding(text);
            reason = null;
            return true;
        }

        private static float[] BuildEmbedding(string text)
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
}
