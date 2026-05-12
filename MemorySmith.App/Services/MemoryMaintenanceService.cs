using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public class MemoryMaintenanceService : BackgroundService
{
    private readonly MemoryMaintenanceTasks _tasks;
    private readonly BackgroundServiceTelemetryTracker _telemetry;
    private readonly ILogger<MemoryMaintenanceService> _logger;
    private readonly MemorySmithOptions _options;

    public MemoryMaintenanceService(
        MemoryMaintenanceTasks tasks,
        BackgroundServiceTelemetryTracker telemetry,
        ILogger<MemoryMaintenanceService> logger,
        IOptions<MemorySmithOptions> options)
    {
        _tasks = tasks;
        _telemetry = telemetry;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var grace = TimeSpan.FromSeconds(Math.Max(0, _options.Maintenance.StartupGraceSeconds));
        if (grace > TimeSpan.Zero)
        {
            await Task.Delay(grace, stoppingToken);
        }

        var triageInterval = TimeSpan.FromMinutes(Math.Max(1, _options.Maintenance.TriageMinutes));
        var indexInterval = TimeSpan.FromMinutes(Math.Max(1, _options.Maintenance.IndexingMinutes));
        var consolidationInterval = TimeSpan.FromHours(Math.Max(1, _options.Maintenance.ConsolidationHours));

        var nextTriage = DateTimeOffset.MinValue;
        var nextIndex = DateTimeOffset.MinValue;
        var nextConsolidation = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            if (now >= nextTriage)
            {
                await RunTrackedAsync("TriageService", "5 min", () => _tasks.RunTriageAsync(stoppingToken), stoppingToken);
                nextTriage = now.Add(triageInterval);
            }

            if (now >= nextIndex)
            {
                await RunTrackedAsync("IndexingService", "1h", () => _tasks.RunIndexRebuildAsync(stoppingToken), stoppingToken);
                nextIndex = now.Add(indexInterval);
            }

            if (now >= nextConsolidation)
            {
                await RunTrackedAsync("ConsolidationService", "24h", () => _tasks.RunConsolidationAsync(stoppingToken), stoppingToken);
                nextConsolidation = now.Add(consolidationInterval);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task RunTrackedAsync(string serviceName, string interval, Func<Task> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        _telemetry.RecordRunStart(serviceName, interval);

        try
        {
            await action();
            stopwatch.Stop();
            _telemetry.RecordRunSuccess(serviceName, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _telemetry.RecordRunFailure(serviceName, stopwatch.Elapsed.TotalMilliseconds);
            _logger.LogError(ex, "Maintenance task {ServiceName} failed", serviceName);
        }
    }
}