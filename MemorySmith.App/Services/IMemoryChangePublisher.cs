using MemorySmith.Core.Models;

namespace MemorySmith.App.Services;

public interface IMemoryChangePublisher
{
    event Func<MemoryUpdateEvent, Task>? MemoryChanged;
    event Func<StatsSnapshot, Task>? StatsChanged;
    Task PublishMemoryChangedAsync(MemoryUpdateEvent update);
    Task PublishStatsChangedAsync(StatsSnapshot stats);
}

public class MemoryChangePublisher : IMemoryChangePublisher
{
    public event Func<MemoryUpdateEvent, Task>? MemoryChanged;
    public event Func<StatsSnapshot, Task>? StatsChanged;

    public async Task PublishMemoryChangedAsync(MemoryUpdateEvent update)
    {
        if (MemoryChanged is not null)
        {
            await MemoryChanged(update);
        }
    }

    public async Task PublishStatsChangedAsync(StatsSnapshot stats)
    {
        if (StatsChanged is not null)
        {
            await StatsChanged(stats);
        }
    }
}