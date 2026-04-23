using MemorySmith.Core.Models;
using MemorySmith.Core.StateMachine;
using MemorySmith.Storage;

namespace MemorySmith.Worker.Services;

public class TriageService : BackgroundService
{
    private readonly IMemoryStore _store;
    private readonly ILogger<TriageService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(5);

    public TriageService(IMemoryStore store, ILogger<TriageService> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { RunTriage(); }
            catch (Exception ex) { _logger.LogError(ex, "Triage error"); }
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private void RunTriage()
    {
        var stateMachine = new MemoryStateMachine();
        foreach (var record in _store.LoadAll())
        {
            var (newStatus, evt) = stateMachine.Evaluate(record);
            if (newStatus != record.Status)
            {
                _logger.LogInformation("Triage: {Id} {Old}→{New}", record.Id, record.Status, newStatus);
                record.Status = newStatus;
                record.LastUpdated = DateTime.UtcNow;
                _store.Save(record);
            }
        }
    }
}
