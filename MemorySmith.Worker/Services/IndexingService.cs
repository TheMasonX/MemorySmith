using MemorySmith.Core.Indexing;
using MemorySmith.Storage;

namespace MemorySmith.Worker.Services;

public class IndexingService : BackgroundService
{
    private readonly IMemoryStore _store;
    private readonly MemoryIndex _index;
    private readonly ILogger<IndexingService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);

    public IndexingService(IMemoryStore store, MemoryIndex index, ILogger<IndexingService> logger)
    {
        _store = store;
        _index = index;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { RebuildIndex(); }
            catch (Exception ex) { _logger.LogError(ex, "Indexing error"); }
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
