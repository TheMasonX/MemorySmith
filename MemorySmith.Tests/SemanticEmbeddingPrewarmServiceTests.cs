using MemorySmith.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

[TestFixture]
public class SemanticEmbeddingPrewarmServiceTests
{
    [Test]
    public void SemanticSearchOptions_DefaultsToStartupPrewarmEnabled()
    {
        Assert.That(new SemanticSearchOptions().PrewarmOnStartupEnabled, Is.True);
    }

    [Test]
    public async Task StartAsync_PrewarmsQueryAndDocumentEmbeddingsWhenEnabled()
    {
        var provider = new RecordingEmbeddingProvider();
        using var service = new SemanticEmbeddingPrewarmService(
            provider,
            Options.Create(new MemorySmithOptions
            {
                SemanticSearch = new SemanticSearchOptions
                {
                    EmbeddingsEnabled = true,
                    PrewarmOnStartupEnabled = true
                }
            }),
            NullLogger<SemanticEmbeddingPrewarmService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.That(provider.DocumentEmbeddingCompleted.Wait(TimeSpan.FromSeconds(5)), Is.True, "The startup prewarm never reached the document embedding probe.");
        await service.StopAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(provider.StatusRequests, Is.EqualTo(1));
            Assert.That(provider.QueryEmbeddingsRequested, Is.EqualTo(1));
            Assert.That(provider.DocumentEmbeddingsRequested, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task StartAsync_SkipsPrewarmWhenDisabled()
    {
        var provider = new RecordingEmbeddingProvider();
        using var service = new SemanticEmbeddingPrewarmService(
            provider,
            Options.Create(new MemorySmithOptions
            {
                SemanticSearch = new SemanticSearchOptions
                {
                    EmbeddingsEnabled = true,
                    PrewarmOnStartupEnabled = false
                }
            }),
            NullLogger<SemanticEmbeddingPrewarmService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await service.StopAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(provider.StatusRequests, Is.EqualTo(0));
            Assert.That(provider.QueryEmbeddingsRequested, Is.EqualTo(0));
            Assert.That(provider.DocumentEmbeddingsRequested, Is.EqualTo(0));
        });
    }

    private sealed class RecordingEmbeddingProvider : ITextEmbeddingProvider
    {
        public int StatusRequests { get; private set; }

        public int QueryEmbeddingsRequested { get; private set; }

        public int DocumentEmbeddingsRequested { get; private set; }

        public ManualResetEventSlim DocumentEmbeddingCompleted { get; } = new(false);

        public EmbeddingProviderStatus GetStatus()
        {
            StatusRequests++;
            return new EmbeddingProviderStatus(
                true,
                "ONNX embedding provider is available via Cpu.",
                null,
                null,
                3,
                "Cpu",
                "Cpu",
                null,
                null);
        }

        public bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason)
        {
            if (kind == EmbeddingInputKind.Query)
            {
                QueryEmbeddingsRequested++;
            }
            else
            {
                DocumentEmbeddingsRequested++;
                DocumentEmbeddingCompleted.Set();
            }

            embedding = [1f, 0f, 0f];
            reason = null;
            return true;
        }
    }
}