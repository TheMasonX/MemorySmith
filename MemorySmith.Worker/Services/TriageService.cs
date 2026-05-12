using MemorySmith.Core.Models;
using MemorySmith.Core.StateMachine;
using MemorySmith.Storage;

namespace MemorySmith.Worker.Services;

public class TriageService : BackgroundService
{
    private const string ServiceName = "TriageService";
    private const string ServiceInterval = "5 min";

    private readonly IMemoryStore _store;
    private readonly IEventStore _eventStore;
    private readonly ILogger<TriageService> _logger;
    private readonly BackgroundServiceTelemetryTracker _telemetryTracker;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(5);

    public TriageService(
        IMemoryStore store,
        IEventStore eventStore,
        ILogger<TriageService> logger,
        BackgroundServiceTelemetryTracker telemetryTracker)
    {
        _store = store;
        _eventStore = eventStore;
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
                RunTriage();
                var durationMs = (DateTime.UtcNow - started).TotalMilliseconds;
                _telemetryTracker.RecordRunSuccess(ServiceName, durationMs);
            }
            catch (Exception ex)
            {
                var durationMs = (DateTime.UtcNow - started).TotalMilliseconds;
                _telemetryTracker.RecordRunFailure(ServiceName, durationMs);
                _logger.LogError(ex, "Triage error");
            }

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
                
                // Persist state transition event
                if (evt != null)
                {
                    _eventStore.AppendEvent(evt);
                }
            }
        }
    }
}
