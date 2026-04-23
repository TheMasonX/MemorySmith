using MemorySmith.Core.Models;
using MemorySmith.Storage;

namespace MemorySmith.Worker.Services;

public class ConsolidationService : BackgroundService
{
    private readonly IMemoryStore _store;
    private readonly ILogger<ConsolidationService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(24);

    public ConsolidationService(IMemoryStore store, ILogger<ConsolidationService> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { RunConsolidation(); }
            catch (Exception ex) { _logger.LogError(ex, "Consolidation error"); }
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private void RunConsolidation()
    {
        var records = _store.LoadAll().ToList();
        _logger.LogInformation("Consolidation: processing {Count} records", records.Count);
        // Future: merge duplicates, rewrite, promote Working→Core if stable
    }
}
