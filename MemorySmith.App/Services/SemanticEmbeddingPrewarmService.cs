using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public sealed class SemanticEmbeddingPrewarmService : BackgroundService
{
    private const string QueryWarmupText = "MemorySmith semantic prewarm query";
    private const string DocumentWarmupText = "Path: MemorySmith.App/Program.cs\nMemorySmith semantic prewarm document";

    private readonly ITextEmbeddingProvider _embeddingProvider;
    private readonly SemanticSearchOptions _options;
    private readonly ILogger<SemanticEmbeddingPrewarmService> _logger;

    public SemanticEmbeddingPrewarmService(
        ITextEmbeddingProvider embeddingProvider,
        IOptions<MemorySmithOptions> options,
        ILogger<SemanticEmbeddingPrewarmService> logger)
    {
        _embeddingProvider = embeddingProvider;
        _options = options.Value.SemanticSearch;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        if (!_options.PrewarmOnStartupEnabled)
        {
            _logger.LogInformation("Semantic embedding startup prewarm is disabled by configuration.");
            return;
        }

        if (!_options.EmbeddingsEnabled)
        {
            _logger.LogInformation("Skipping semantic embedding startup prewarm because embeddings are disabled.");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var status = _embeddingProvider.GetStatus();
            if (!status.Available)
            {
                stopwatch.Stop();
                _logger.LogInformation(
                    "Skipping semantic embedding startup prewarm because the provider is unavailable after {ElapsedMilliseconds} ms: {Reason}",
                    stopwatch.ElapsedMilliseconds,
                    status.Reason);
                return;
            }

            if (!_embeddingProvider.TryEmbed(QueryWarmupText, EmbeddingInputKind.Query, out var queryEmbedding, out var queryReason))
            {
                stopwatch.Stop();
                _logger.LogWarning(
                    "Semantic embedding startup prewarm failed during the query probe after {ElapsedMilliseconds} ms: {Reason}",
                    stopwatch.ElapsedMilliseconds,
                    queryReason ?? "unknown reason");
                return;
            }

            if (!_embeddingProvider.TryEmbed(DocumentWarmupText, EmbeddingInputKind.Document, out var documentEmbedding, out var documentReason))
            {
                stopwatch.Stop();
                _logger.LogWarning(
                    "Semantic embedding startup prewarm failed during the document probe after {ElapsedMilliseconds} ms: {Reason}",
                    stopwatch.ElapsedMilliseconds,
                    documentReason ?? "unknown reason");
                return;
            }

            stopwatch.Stop();
            _logger.LogInformation(
                "Completed semantic embedding startup prewarm in {ElapsedMilliseconds} ms using {ActiveProvider}. Query dimensions {QueryDimensions}; document dimensions {DocumentDimensions}.",
                stopwatch.ElapsedMilliseconds,
                FormatActiveProvider(status),
                queryEmbedding.Length,
                documentEmbedding.Length);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Semantic embedding startup prewarm failed after {ElapsedMilliseconds} ms.", stopwatch.ElapsedMilliseconds);
        }
    }

    private static string FormatActiveProvider(EmbeddingProviderStatus status)
    {
        return string.IsNullOrWhiteSpace(status.ActiveExecutionDevice)
            ? status.ActiveExecutionProvider
            : $"{status.ActiveExecutionProvider} ({status.ActiveExecutionDevice})";
    }
}