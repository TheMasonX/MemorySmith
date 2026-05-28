using System.Diagnostics;
using MemorySmith.App.Services;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

[TestFixture]
[Category("Benchmark")]
[NonParallelizable]
public class CudaEmbeddingBatchBenchmarkTests
{
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-cuda-batch-{Guid.NewGuid():N}");
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
    public void CudaBatchEmbedding_MatchesScalarEmbeddingsOrSkipsWhenCudaIsUnavailable()
    {
        using var provider = CreateCudaProvider();
        var status = provider.GetStatus();
        if (!string.Equals(status.ActiveExecutionProvider, "Cuda", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Ignore($"CUDA embedding benchmark requires active Cuda. Status: {status.Reason}");
        }

        var texts = BuildBenchmarkTexts(12);
        Assert.That(provider.TryEmbedBatch(texts, EmbeddingInputKind.Document, out var batchEmbeddings, out var batchReason), Is.True, batchReason);

        Assert.That(batchEmbeddings.Count, Is.EqualTo(texts.Length));
        for (var index = 0; index < texts.Length; index++)
        {
            Assert.That(provider.TryEmbed(texts[index], EmbeddingInputKind.Document, out var scalarEmbedding, out var scalarReason), Is.True, scalarReason);
            Assert.That(batchEmbeddings[index].Length, Is.EqualTo(scalarEmbedding.Length));
            Assert.That(CosineSimilarity(batchEmbeddings[index], scalarEmbedding), Is.GreaterThan(0.9999), $"Batch embedding drifted for sample {index}.");
        }
    }

    [Test]
    public void CudaBatchEmbedding_MedianBatchLatencyBeatsScalarLatencyOrSkipsWhenCudaIsUnavailable()
    {
        using var provider = CreateCudaProvider();
        var status = provider.GetStatus();
        if (!string.Equals(status.ActiveExecutionProvider, "Cuda", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Ignore($"CUDA embedding benchmark requires active Cuda. Status: {status.Reason}");
        }

        var texts = BuildBenchmarkTexts(48);
        Assert.That(provider.TryEmbedBatch(texts, EmbeddingInputKind.Document, out _, out var warmBatchReason), Is.True, warmBatchReason);
        Assert.That(provider.TryEmbed(texts[0], EmbeddingInputKind.Document, out _, out var warmScalarReason), Is.True, warmScalarReason);

        var scalarSamples = new List<long>();
        var batchSamples = new List<long>();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var scalarStopwatch = Stopwatch.StartNew();
            foreach (var text in texts)
            {
                Assert.That(provider.TryEmbed(text, EmbeddingInputKind.Document, out _, out var scalarReason), Is.True, scalarReason);
            }

            scalarStopwatch.Stop();
            scalarSamples.Add(scalarStopwatch.ElapsedMilliseconds);

            var batchStopwatch = Stopwatch.StartNew();
            Assert.That(provider.TryEmbedBatch(texts, EmbeddingInputKind.Document, out var batchEmbeddings, out var batchReason), Is.True, batchReason);
            batchStopwatch.Stop();
            Assert.That(batchEmbeddings.Count, Is.EqualTo(texts.Length));
            batchSamples.Add(batchStopwatch.ElapsedMilliseconds);
        }

        var scalarMedian = Median(scalarSamples);
        var batchMedian = Median(batchSamples);
        TestContext.Out.WriteLine($"CUDA scalar median: {scalarMedian} ms; batch median: {batchMedian} ms; samples scalar=[{string.Join(", ", scalarSamples)}] batch=[{string.Join(", ", batchSamples)}]");
        Assert.That(batchMedian, Is.LessThanOrEqualTo(scalarMedian), "CUDA batch inference should not be slower than the scalar path once the provider is warm.");
    }

    private OnnxTextEmbeddingProvider CreateCudaProvider()
    {
        var dataPath = ProjectWikiFixture.CopyToTemp(_tempRoot);
        var dataRoot = Path.GetDirectoryName(dataPath) ?? _tempRoot;
        Directory.CreateDirectory(Path.Combine(dataRoot, "Models"));

        var modelSource = Path.Combine(FindRepositoryRoot(), "Data", "Models");
        if (Directory.Exists(modelSource))
        {
            CopyDirectory(modelSource, Path.Combine(dataRoot, "Models"));
        }

        return new OnnxTextEmbeddingProvider(Options.Create(new MemorySmithOptions
        {
            DataPath = dataPath,
            SemanticSearch = new SemanticSearchOptions
            {
                EmbeddingsEnabled = true,
                ModelPath = Path.Combine("Models", "embedding-model.onnx"),
                VocabularyPath = Path.Combine("Models", "vocab.txt"),
                ExecutionProvider = "Cuda",
                CpuFallbackEnabled = true,
                CudaDeviceId = 0,
                MaxInputTokens = 512,
                MaxIndexedTextCharacters = 6000,
                QueryPrefix = "query: ",
                DocumentPrefix = "passage: "
            }
        }));
    }

    private static string[] BuildBenchmarkTexts(int count)
    {
        return Enumerable.Range(0, count)
            .Select(index => $"Path: MemorySmith.App/Services/CodeSearchService.cs\nBatch benchmark sample {index}. This paragraph exercises CUDA batch document inference with repeated code-search style text, semantic prefixes, and enough tokens to make the GPU launch overhead visible.")
            .ToArray();
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

    private static double CosineSimilarity(float[] left, float[] right)
    {
        var dot = 0d;
        var leftMagnitude = 0d;
        var rightMagnitude = 0d;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        if (leftMagnitude <= 0 || rightMagnitude <= 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    private static long Median(IReadOnlyList<long> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        return ordered[ordered.Length / 2];
    }
}