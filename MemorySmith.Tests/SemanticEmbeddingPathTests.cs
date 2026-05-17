using MemorySmith.App.Services;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

[TestFixture]
[NonParallelizable]
public class SemanticEmbeddingPathTests
{
    private string _tempRoot = null!;
    private string _originalCurrentDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-embedding-paths-{Guid.NewGuid():N}");
        _originalCurrentDirectory = Directory.GetCurrentDirectory();
    }

    [TearDown]
    public void TearDown()
    {
        Directory.SetCurrentDirectory(_originalCurrentDirectory);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Test]
    public void GetStatus_ResolvesRelativeSemanticPathsFromDataDeploymentRoot()
    {
        var dataRoot = CreateDataDeploymentRoot("Data");
        var unrelatedWorkingDirectory = Path.Combine(_tempRoot, "windows-service-cwd");
        Directory.CreateDirectory(unrelatedWorkingDirectory);
        Directory.SetCurrentDirectory(unrelatedWorkingDirectory);

        using var provider = new OnnxTextEmbeddingProvider(Options.Create(new MemorySmithOptions
        {
            DataPath = Path.Combine(dataRoot, "Memories"),
            SemanticSearch = new SemanticSearchOptions
            {
                ModelPath = Path.Combine("Models", "embedding-model.onnx"),
                VocabularyPath = Path.Combine("Models", "vocab.txt")
            }
        }));

        var status = provider.GetStatus();

        Assert.Multiple(() =>
        {
            Assert.That(status.ModelPath, Is.EqualTo(Path.Combine(dataRoot, "Models", "embedding-model.onnx")));
            Assert.That(status.VocabularyPath, Is.EqualTo(Path.Combine(dataRoot, "Models", "vocab.txt")));
            Assert.That(status.Reason, Does.Contain(Path.Combine(dataRoot, "Models", "embedding-model.onnx")));
        });
    }

    [Test]
    public void GetStatus_TreatsLegacyDataPrefixedSemanticPathsAsDataRootRelative()
    {
        var dataRoot = CreateDataDeploymentRoot("DeploymentData");
        var unrelatedWorkingDirectory = Path.Combine(_tempRoot, "service32");
        Directory.CreateDirectory(unrelatedWorkingDirectory);
        Directory.SetCurrentDirectory(unrelatedWorkingDirectory);

        using var provider = new OnnxTextEmbeddingProvider(Options.Create(new MemorySmithOptions
        {
            DataPath = Path.Combine(dataRoot, "Memories"),
            SemanticSearch = new SemanticSearchOptions
            {
                ModelPath = Path.Combine("..", "Data", "Models", "embedding-model.onnx"),
                VocabularyPath = Path.Combine("..", "Data", "Models", "vocab.txt")
            }
        }));

        var status = provider.GetStatus();

        Assert.Multiple(() =>
        {
            Assert.That(status.ModelPath, Is.EqualTo(Path.Combine(dataRoot, "Models", "embedding-model.onnx")));
            Assert.That(status.VocabularyPath, Is.EqualTo(Path.Combine(dataRoot, "Models", "vocab.txt")));
        });
    }

    private string CreateDataDeploymentRoot(string folderName)
    {
        var dataRoot = Path.Combine(_tempRoot, folderName);
        Directory.CreateDirectory(Path.Combine(dataRoot, "Events"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "Graph"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "Memories"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "Models"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "Pages"));
        return dataRoot;
    }
}