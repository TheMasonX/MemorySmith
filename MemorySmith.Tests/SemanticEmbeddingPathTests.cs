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
            Assert.That(status.RequestedExecutionProvider, Is.EqualTo("Cpu"));
            Assert.That(status.ActiveExecutionProvider, Is.EqualTo("None"));
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
            Assert.That(status.RequestedExecutionProvider, Is.EqualTo("Cpu"));
            Assert.That(status.ActiveExecutionProvider, Is.EqualTo("None"));
        });
    }

    [Test]
    public void GetStatus_RejectsUnsupportedTokenizerKindBeforeOpeningOnnxSession()
    {
        var dataRoot = CreateDataDeploymentRoot("TokenizerKind");
        var unrelatedWorkingDirectory = Path.Combine(_tempRoot, "unsupported-tokenizer");
        Directory.CreateDirectory(unrelatedWorkingDirectory);
        Directory.SetCurrentDirectory(unrelatedWorkingDirectory);

        File.WriteAllBytes(Path.Combine(dataRoot, "Models", "embedding-model.onnx"), new byte[] { 0x00 });
        File.WriteAllLines(Path.Combine(dataRoot, "Models", "vocab.txt"), ["[UNK]", "[CLS]", "[SEP]"]);

        using var provider = new OnnxTextEmbeddingProvider(Options.Create(new MemorySmithOptions
        {
            DataPath = Path.Combine(dataRoot, "Memories"),
            SemanticSearch = new SemanticSearchOptions
            {
                ModelPath = Path.Combine("Models", "embedding-model.onnx"),
                VocabularyPath = Path.Combine("Models", "vocab.txt"),
                TokenizerKind = "SentencePiece"
            }
        }));

        var status = provider.GetStatus();

        Assert.Multiple(() =>
        {
            Assert.That(status.Available, Is.False);
            Assert.That(status.RequestedExecutionProvider, Is.EqualTo("Cpu"));
            Assert.That(status.ActiveExecutionProvider, Is.EqualTo("None"));
            Assert.That(status.Reason, Does.Contain("Tokenizer kind 'SentencePiece' is not supported"));
            Assert.That(status.Reason, Does.Contain("WordPiece"));
        });
    }

    [Test]
    public void GetStatus_RejectsUnsupportedExecutionProviderBeforeOpeningOnnxSession()
    {
        var dataRoot = CreateDataDeploymentRoot("ExecutionProvider");
        var unrelatedWorkingDirectory = Path.Combine(_tempRoot, "unsupported-provider");
        Directory.CreateDirectory(unrelatedWorkingDirectory);
        Directory.SetCurrentDirectory(unrelatedWorkingDirectory);

        File.WriteAllBytes(Path.Combine(dataRoot, "Models", "embedding-model.onnx"), new byte[] { 0x00 });
        File.WriteAllLines(Path.Combine(dataRoot, "Models", "vocab.txt"), ["[UNK]", "[CLS]", "[SEP]"]);

        using var provider = new OnnxTextEmbeddingProvider(Options.Create(new MemorySmithOptions
        {
            DataPath = Path.Combine(dataRoot, "Memories"),
            SemanticSearch = new SemanticSearchOptions
            {
                ModelPath = Path.Combine("Models", "embedding-model.onnx"),
                VocabularyPath = Path.Combine("Models", "vocab.txt"),
                ExecutionProvider = "TensorRt"
            }
        }));

        var status = provider.GetStatus();

        Assert.Multiple(() =>
        {
            Assert.That(status.Available, Is.False);
            Assert.That(status.RequestedExecutionProvider, Is.EqualTo("TensorRt"));
            Assert.That(status.ActiveExecutionProvider, Is.EqualTo("None"));
            Assert.That(status.Reason, Does.Contain("Execution provider 'TensorRt' is not supported"));
            Assert.That(status.Reason, Does.Contain("Cpu, Cuda, OpenVino"));
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