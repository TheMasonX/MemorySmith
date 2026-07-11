using MemorySmith.Core.Models;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<MemoryChangePublisher>? _logger;

    public MemoryChangePublisher(ILogger<MemoryChangePublisher>? logger = null)
    {
        _logger = logger;
    }

    public event Func<MemoryUpdateEvent, Task>? MemoryChanged;
    public event Func<StatsSnapshot, Task>? StatsChanged;

    public async Task PublishMemoryChangedAsync(MemoryUpdateEvent update)
    {
        await PublishAsync(MemoryChanged, update);
    }

    public async Task PublishStatsChangedAsync(StatsSnapshot stats)
    {
        await PublishAsync(StatsChanged, stats);
    }

    private async Task PublishAsync<T>(Func<T, Task>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }

        var delegates = handlers.GetInvocationList()
            .Cast<Func<T, Task>>()
            .ToArray();

        foreach (var handler in delegates)
        {
            try
            {
                await handler(value);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Subscriber failed in MemoryChangePublisher for {EventType}: {Message}", typeof(T).Name, ex.Message);
            }
        }
    }
}