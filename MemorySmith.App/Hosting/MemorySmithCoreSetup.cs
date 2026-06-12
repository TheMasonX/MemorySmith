namespace MemorySmith.App.Hosting;

using MemorySmith.App.Services;
using MemorySmith.App.Services.Training;

/// <summary>
/// Memory-domain and operational services: tag governance, semantic embeddings, code search,
/// the memory application service, tasks, diagnostics, and the embedding prewarm hosted service.
/// Extracted from Program.cs (TSK-0282) — MeasurementBaselineService was among the registrations
/// the June 4 reconstruction silently dropped (DiagnosticsController activation 500s).
/// </summary>
public static class MemorySmithCoreSetup
{
    public static WebApplicationBuilder AddMemorySmithCore(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<TagPolicyService>();
        builder.Services.AddSingleton<MemoryDiagnosticsService>();
        builder.Services.AddSingleton<TagGovernanceService>();
        builder.Services.AddSingleton<ITextEmbeddingProvider, OnnxTextEmbeddingProvider>();
        builder.Services.AddSingleton<SemanticEmbeddingSearchService>();
        builder.Services.AddSingleton<CodeSearchService>();
        builder.Services.AddSingleton<TreeSitterChunkingService>();
        builder.Services.AddSingleton<BackgroundServiceTelemetryTracker>();
        builder.Services.AddSingleton<IMemoryChangePublisher, MemoryChangePublisher>();
        builder.Services.AddSingleton<MemoryApplicationService>();
        builder.Services.AddSingleton<ITaskService, FileTaskService>();
        builder.Services.AddSingleton<LoggingObservabilityService>();
        builder.Services.AddSingleton<TrainingHarnessRunnerService>();
        builder.Services.AddSingleton<MemoryMaintenanceTasks>();
        builder.Services.AddSingleton<MeasurementBaselineService>();
        builder.Services.AddSingleton<OperationalDiagnosticsService>();
        builder.Services.AddHostedService<SemanticEmbeddingPrewarmService>();
        return builder;
    }
}
