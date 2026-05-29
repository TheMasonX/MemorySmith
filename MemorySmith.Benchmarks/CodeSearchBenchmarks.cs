using BenchmarkDotNet.Attributes;
using MemorySmith.App.Services;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

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

        _service = new CodeSearchService(new HashEmbeddingProvider(), Options.Create(options));
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

            Console.WriteLine($"CodeSearch Tool Query: {toolResults.Count} result(s), top={toolResults.FirstOrDefault()?.DocumentPath ?? "<none>"}");
            Console.WriteLine($"CodeSearch Screwdriver Query: {screwdriverResults.Count} result(s), top={screwdriverResults.FirstOrDefault()?.DocumentPath ?? "<none>"}");
        }
        finally
        {
            bench.Cleanup();
        }
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
