using Microsoft.AspNetCore.SignalR;
using MemorySmith.Core.Models;

namespace MemorySmith.Worker.Hubs;

public interface IDashboardClient
{
    Task ReceiveMemoryUpdate(MemoryUpdateEvent update);
    Task ReceiveStats(StatsSnapshot stats);
}

public class DashboardHub : Hub<IDashboardClient> { }
