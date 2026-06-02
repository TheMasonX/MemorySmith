using System.Diagnostics;
using MemorySmith.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace MemorySmith.Tests;

[TestFixture]
[Category("Benchmark")]
[NonParallelizable]
public sealed class CodeSearchBenchmarkTests
{
    private string _repoRoot = null!;
    private string _dataPath = null!;
    private CodeSearchService _service = null!;
    private List<CodeSearchScorecardProbe> _relevanceProbes = [];

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

        _relevanceProbes = LoadScorecardProbes();

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

        _service = new CodeSearchService(new HashEmbeddingProvider(), null!, Options.Create(options), NullLogger<CodeSearchService>.Instance);

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

        foreach (var probe in _relevanceProbes)
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
                Assert.That(sw.ElapsedMilliseconds, Is.LessThanOrEqualTo(probe.MaxLatencyMs), $"Query '{probe.Query}' took {sw.ElapsedMilliseconds} ms; expected <= {probe.MaxLatencyMs} ms.");
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

    [Test]
    public async Task WarmLatencyDistribution_100QueriesP95Under10Ms()
    {
        var queries = _relevanceProbes.Select(probe => probe.Query).ToArray();
        Assert.That(queries, Is.Not.Empty, "Scorecard probes should be available for latency distribution checks.");

        foreach (var query in queries)
        {
            _ = await _service.SearchAsync(new CodeSearchQuery(query, Limit: 5), CancellationToken.None);
        }

        var samples = new List<double>(100);
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var query = queries[iteration % queries.Length];
            var sw = Stopwatch.StartNew();
            _ = await _service.SearchAsync(new CodeSearchQuery(query, Limit: 5), CancellationToken.None);
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var percentileIndex = Math.Max(0, (int)Math.Ceiling(samples.Count * 0.95) - 1);
        var p95 = samples[percentileIndex];
        var max = samples[^1];

        Assert.Multiple(() =>
        {
            Assert.That(p95, Is.LessThanOrEqualTo(10), $"Warm query p95 latency was {p95:0.###} ms; expected <= 10 ms.");
            Assert.That(max, Is.LessThanOrEqualTo(50), $"Warm query max latency was {max:0.###} ms; expected <= 50 ms.");
        });

        TestContext.Out.WriteLine($"100 warm code-search queries: p95={p95:0.###} ms, max={max:0.###} ms");
    }

    [Test]
    public async Task SparsePrefilterThreshold_AB_ImprovesRecallWithBoundedLatencyCost()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-code-search-bench-ab-{Guid.NewGuid():N}");
        var dataPath = Path.Combine(tempRoot, "Data", "Memories");
        Directory.CreateDirectory(dataPath);
        Directory.CreateDirectory(Path.Combine(tempRoot, "MemorySmith.App", "Services"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "MemorySmith.Core", "Services"));

        await File.WriteAllTextAsync(Path.Combine(tempRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(tempRoot, "MemorySmith.App", "Services", "LexicalAnchor.cs"),
            "namespace MemorySmith.App.Services;\npublic static class LexicalAnchor\n{\n    public static string Anchor(string input) => input + \" semantic alias retrieval \";\n}\n");
        await File.WriteAllTextAsync(
            Path.Combine(tempRoot, "MemorySmith.Core", "Services", "HiddenTrueTop.cs"),
            "namespace MemorySmith.Core.Services;\npublic static class HiddenTrueTop\n{\n    public static string Resolve(string input) => input + \" latent concept bridge \";\n}\n");

        CodeSearchService? disabledService = null;
        CodeSearchService? enabledService = null;
        try
        {
            disabledService = CreateSparsePrefilterBenchService(tempRoot, dataPath, threshold: 0);
            enabledService = CreateSparsePrefilterBenchService(tempRoot, dataPath, threshold: 5);

            var query = new CodeSearchQuery("semantic alias retrieval", Limit: 5);

            var disabledTop = (await disabledService.SearchAsync(query, CancellationToken.None)).FirstOrDefault()?.DocumentPath ?? "<none>";
            var enabledTop = (await enabledService.SearchAsync(query, CancellationToken.None)).FirstOrDefault()?.DocumentPath ?? "<none>";

            var disabledWarmAvg = await MeasureWarmLatencyAsync(disabledService, query, iterations: 50);
            var enabledWarmAvg = await MeasureWarmLatencyAsync(enabledService, query, iterations: 50);

            Assert.Multiple(() =>
            {
                Assert.That(disabledTop, Is.EqualTo("MemorySmith.App/Services/LexicalAnchor.cs"), "With sparse fallback disabled, lexical prefilter should anchor the top result.");
                Assert.That(enabledTop, Is.EqualTo("MemorySmith.Core/Services/HiddenTrueTop.cs"), "With sparse fallback enabled, semantic top document should be recovered.");
                Assert.That(enabledWarmAvg, Is.LessThanOrEqualTo(Math.Max(10, disabledWarmAvg * 3.0)), "Sparse fallback should keep warm latency within a bounded envelope.");
            });

            TestContext.Out.WriteLine($"Sparse prefilter A/B: disabledAvg={disabledWarmAvg:0.###} ms, enabledAvg={enabledWarmAvg:0.###} ms, disabledTop={disabledTop}, enabledTop={enabledTop}");
        }
        finally
        {
            disabledService?.Dispose();
            enabledService?.Dispose();
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static CodeSearchService CreateSparsePrefilterBenchService(string repoRoot, string dataPath, int threshold)
    {
        var options = new MemorySmithOptions
        {
            DataPath = dataPath,
            CodeSearch = new CodeSearchOptions
            {
                RepositoryRootPath = "..",
                TargetDirectories = ["MemorySmith.App", "MemorySmith.Core"],
                IncludedFileExtensions = [".cs"],
                MaxResults = 10,
                VectorPrefilterFullScanFallbackCandidateCount = threshold
            }
        };

        return new CodeSearchService(new SparsePrefilterSemanticProvider(), null!, Options.Create(options), NullLogger<CodeSearchService>.Instance);
    }

    private static async Task<double> MeasureWarmLatencyAsync(CodeSearchService service, CodeSearchQuery query, int iterations)
    {
        var sw = Stopwatch.StartNew();
        for (var index = 0; index < iterations; index++)
        {
            _ = await service.SearchAsync(query, CancellationToken.None);
        }

        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / Math.Max(1, iterations);
    }

    private static List<CodeSearchScorecardProbe> LoadScorecardProbes()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "Data", "Benchmarks", "code-search-scorecard.json");
        var payload = JsonSerializer.Deserialize<CodeSearchScorecardDocument>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.That(payload, Is.Not.Null, "Scorecard fixture could not be parsed.");
        Assert.That(payload!.Probes, Is.Not.Null.And.Not.Empty, "Scorecard fixture contains no probes.");
        return payload.Probes;
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

    private sealed record CodeSearchScorecardDocument(List<CodeSearchScorecardProbe> Probes);

    private sealed record CodeSearchScorecardProbe(
        string Query,
        string ExpectedTopDocument,
        int MaxRank,
        int MaxLatencyMs);

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
                var slot = (int)(ComputeStableHash(token) % (uint)vector.Length);
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

        private static uint ComputeStableHash(string value)
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            uint hash = offset;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= prime;
            }

            return hash;
        }
    }

    private sealed class SparsePrefilterSemanticProvider : ITextEmbeddingProvider
    {
        public EmbeddingProviderStatus GetStatus() => new(true, "Sparse prefilter semantic provider available.", null, null, 2, "Cpu", "Cpu", null, null);

        public bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason)
        {
            reason = null;
            if (kind == EmbeddingInputKind.Query)
            {
                embedding = [1f, 0f];
                return true;
            }

            if (text.Contains("LexicalAnchor.cs", StringComparison.OrdinalIgnoreCase))
            {
                embedding = [0.75f, 0f];
                return true;
            }

            if (text.Contains("HiddenTrueTop.cs", StringComparison.OrdinalIgnoreCase))
            {
                embedding = [3f, 0f];
                return true;
            }

            embedding = [0.15f, 0f];
            return true;
        }
    }
}
