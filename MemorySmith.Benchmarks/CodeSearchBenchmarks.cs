using BenchmarkDotNet.Attributes;
using MemorySmith.App.Services;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text.Json;

namespace MemorySmith.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class CodeSearchBenchmarks
{
    private string _tempRoot = string.Empty;
    private CodeSearchService _service = null!;
    private readonly CodeSearchQuery _toolQuery = new("tool utility harness", Limit: 10);
    private readonly CodeSearchQuery _screwdriverQuery = new("screwdriver", Limit: 10);

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-code-search-bench-{Guid.NewGuid():N}");
        var dataPath = Path.Combine(_tempRoot, "Data", "Memories");
        Directory.CreateDirectory(dataPath);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "MemorySmith.App", "Services"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "MemorySmith.Core", "Services"));

        await File.WriteAllTextAsync(Path.Combine(_tempRoot, ".gitignore"), string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_tempRoot, "MemorySmith.App", "Services", "ToolCatalog.cs"),
            "namespace MemorySmith.App.Services;\npublic static class ToolCatalog\n{\n    public static string RegisterTool(string input) => input + \" utility harness\";\n}\n");
        await File.WriteAllTextAsync(
            Path.Combine(_tempRoot, "MemorySmith.App", "Services", "CliRunner.cs"),
            "namespace MemorySmith.App.Services;\npublic static class CliRunner\n{\n    public static string RunCliTool(string input) => input + \" cli command runner\";\n}\n");
        await File.WriteAllTextAsync(
            Path.Combine(_tempRoot, "MemorySmith.Core", "Services", "ProposalTaskBoard.cs"),
            "namespace MemorySmith.Core.Services;\npublic static class ProposalTaskBoard\n{\n    public static string ReviewProposalTask(string input) => input + \" proposal review task workflow\";\n}\n");
        await File.WriteAllTextAsync(
            Path.Combine(_tempRoot, "MemorySmith.Core", "Services", "UnrelatedPipeline.cs"),
            "namespace MemorySmith.Core.Services;\npublic static class UnrelatedPipeline\n{\n    public static string BuildOpaquePipeline(string input) => input + \" opaque\";\n}\n");

        var options = new MemorySmithOptions
        {
            DataPath = dataPath,
            CodeSearch = new CodeSearchOptions
            {
                RepositoryRootPath = "..",
                TargetDirectories = ["MemorySmith.App", "MemorySmith.Core"],
                IncludedFileExtensions = [".cs"],
                MaxResults = 10
            }
        };

        _service = new CodeSearchService(new HashEmbeddingProvider(), new TreeSitterChunkingService(), Options.Create(options));
        _ = await _service.SearchAsync(new CodeSearchQuery("bootstrap", Limit: 3), CancellationToken.None);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _service.Dispose();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Benchmark]
    public async Task<int> SearchToolQuery()
    {
        var results = await _service.SearchAsync(_toolQuery, CancellationToken.None);
        return results.Count;
    }

    [Benchmark]
    public async Task<int> SearchScrewdriverQuery()
    {
        var results = await _service.SearchAsync(_screwdriverQuery, CancellationToken.None);
        return results.Count;
    }

    public static async Task RunSmokeAsync()
    {
        var bench = new CodeSearchBenchmarks();
        await bench.SetupAsync();
        try
        {
            var toolResults = await bench._service.SearchAsync(bench._toolQuery, CancellationToken.None);
            var screwdriverResults = await bench._service.SearchAsync(bench._screwdriverQuery, CancellationToken.None);

            var toolLatencyMs = await MeasureWarmLatencyAsync(bench._service, bench._toolQuery, iterations: 20);
            var screwdriverLatencyMs = await MeasureWarmLatencyAsync(bench._service, bench._screwdriverQuery, iterations: 20);
            var scorecard = await RunScorecardAsync(bench._service);

            Console.WriteLine($"CodeSearch Tool Query: {toolResults.Count} result(s), top={toolResults.FirstOrDefault()?.DocumentPath ?? "<none>"}");
            Console.WriteLine($"CodeSearch Screwdriver Query: {screwdriverResults.Count} result(s), top={screwdriverResults.FirstOrDefault()?.DocumentPath ?? "<none>"}");
            Console.WriteLine($"CodeSearch Tool Query Warm Avg: {toolLatencyMs:0.###} ms over 20 iterations");
            Console.WriteLine($"CodeSearch Screwdriver Query Warm Avg: {screwdriverLatencyMs:0.###} ms over 20 iterations");
            Console.WriteLine($"CodeSearch Relevance Scorecard: {scorecard.passed}/{scorecard.total} passed");
            foreach (var line in scorecard.lines)
            {
                Console.WriteLine($"  {line}");
            }
        }
        finally
        {
            bench.Cleanup();
        }
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

    private static async Task<(int passed, int total, List<string> lines)> RunScorecardAsync(CodeSearchService service)
    {
        var scorecard = LoadScorecardProbes();

        var lines = new List<string>(scorecard.Length);
        var passed = 0;
        foreach (var entry in scorecard)
        {
            var results = await service.SearchAsync(new CodeSearchQuery(entry.Query, Limit: 5), CancellationToken.None);
            var top = results.FirstOrDefault()?.DocumentPath ?? "<none>";
            var ok = string.Equals(top, entry.ExpectedTopDocument, StringComparison.OrdinalIgnoreCase);
            if (ok)
            {
                passed++;
            }

            lines.Add($"[{(ok ? "PASS" : "FAIL")}] query='{entry.Query}' expected='{entry.ExpectedTopDocument}' actual='{top}'");
        }

        return (passed, scorecard.Length, lines);
    }

    private static CodeSearchScorecardProbe[] LoadScorecardProbes()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "Data", "Benchmarks", "code-search-scorecard.json");
        var payload = JsonSerializer.Deserialize<CodeSearchScorecardDocument>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return payload?.Probes?.ToArray() ?? [];
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

        throw new DirectoryNotFoundException("Could not locate MemorySmith.slnx from benchmark output directory.");
    }

    private sealed record CodeSearchScorecardDocument(CodeSearchScorecardProbe[] Probes);

    private sealed record CodeSearchScorecardProbe(string Query, string ExpectedTopDocument, int MaxRank, int MaxLatencyMs);

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
