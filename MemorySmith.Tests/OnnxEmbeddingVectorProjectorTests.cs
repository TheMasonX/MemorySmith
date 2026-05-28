using MemorySmith.App.Services;

namespace MemorySmith.Tests;

[TestFixture]
public sealed class OnnxEmbeddingVectorProjectorTests
{
    [Test]
    public void ProjectSequenceOutput_UsesMeanPoolingByDefault()
    {
        var projected = OnnxEmbeddingVectorProjector.ProjectSequenceOutput(
            [1f, 2f, 3f, 4f, 99f, 100f],
            tokenCount: 3,
            dimension: 2,
            attentionMask: [1, 1, 0],
            poolingMode: null);

        Assert.That(projected, Is.EqualTo(new[] { 2f, 3f }));
    }

    [Test]
    public void ProjectSequenceOutput_UsesMeanPoolingWhenExplicitlyConfigured()
    {
        var projected = OnnxEmbeddingVectorProjector.ProjectSequenceOutput(
            [1f, 2f, 3f, 4f, 99f, 100f],
            tokenCount: 3,
            dimension: 2,
            attentionMask: [1, 1, 0],
            poolingMode: "Mean");

        Assert.That(projected, Is.EqualTo(new[] { 2f, 3f }));
    }

    [Test]
    public void ProjectSequenceOutput_UsesClsPoolingWhenConfigured()
    {
        var projected = OnnxEmbeddingVectorProjector.ProjectSequenceOutput(
            [1f, 2f, 3f, 4f, 99f, 100f],
            tokenCount: 3,
            dimension: 2,
            attentionMask: [1, 1, 0],
            poolingMode: "Cls");

        Assert.That(projected, Is.EqualTo(new[] { 1f, 2f }));
    }

    [Test]
    public void ProjectSequenceOutput_RejectsUnsupportedPoolingMode()
    {
        var exception = Assert.Throws<NotSupportedException>(() => OnnxEmbeddingVectorProjector.ProjectSequenceOutput(
            [1f, 2f, 3f, 4f],
            tokenCount: 2,
            dimension: 2,
            attentionMask: [1, 1],
            poolingMode: "Max"));

        Assert.That(exception!.Message, Does.Contain("Pooling mode 'Max' is not supported"));
    }
}