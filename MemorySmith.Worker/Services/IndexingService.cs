using MemorySmith.Core.Indexing;
using MemorySmith.Storage;

namespace MemorySmith.Worker.Services;

public class IndexingService : BackgroundService
{
    private const string ServiceName = "IndexingService";
    private const string ServiceInterval = "1h";

    private readonly IMemoryStore _store;
    private readonly MemoryIndex _index;
    private readonly ILogger<IndexingService> _logger;
    private readonly BackgroundServiceTelemetryTracker _telemetryTracker;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);

    public IndexingService(
        IMemoryStore store,
        MemoryIndex index,
        ILogger<IndexingService> logger,
        BackgroundServiceTelemetryTracker telemetryTracker)
    {
        _store = store;
        _index = index;
        _logger = logger;
        _telemetryTracker = telemetryTracker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            _telemetryTracker.RecordRunStart(ServiceName, ServiceInterval);

            try
            {
                RebuildIndex();
                var durationMs = (DateTime.UtcNow - started).TotalMilliseconds;
                _telemetryTracker.RecordRunSuccess(ServiceName, durationMs);
            }
            catch (Exception ex)
            {
                var durationMs = (DateTime.UtcNow - started).TotalMilliseconds;
                _telemetryTracker.RecordRunFailure(ServiceName, durationMs);
                _logger.LogError(ex, "Indexing error");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private void RebuildIndex()
    {
        var records = _store.LoadAll().ToList();
        _index.Rebuild(records);
        _logger.LogInformation("Index rebuilt: {Count} records", records.Count);
    }
}
