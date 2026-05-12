using Microsoft.AspNetCore.SignalR;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using MemorySmith.Worker.Hubs;
using Microsoft.Extensions.Configuration;

namespace MemorySmith.Worker.Services;

public class StatsBroadcastService : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly IMemoryStore _store;
    private readonly IHubContext<DashboardHub, IDashboardClient> _hub;
    private readonly ILogger<StatsBroadcastService> _logger;
    private readonly TimeSpan _interval;

    public StatsBroadcastService(
        IMemoryStore store,
        IHubContext<DashboardHub, IDashboardClient> hub,
        ILogger<StatsBroadcastService> logger,
        IConfiguration configuration)
    {
        _store = store;
        _hub = hub;
        _logger = logger;

        var configuredSeconds = configuration.GetValue<int?>("StatsBroadcastSeconds");
        _interval = configuredSeconds.HasValue && configuredSeconds.Value > 0
            ? TimeSpan.FromSeconds(configuredSeconds.Value)
            : DefaultInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = StatsSnapshotFactory.Build(_store.LoadAll());
                await _hub.Clients.All.ReceiveStats(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to broadcast stats snapshot");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
