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
        await PublishAsync(MemoryChanged, update);
    }

    public async Task PublishStatsChangedAsync(StatsSnapshot stats)
    {
        await PublishAsync(StatsChanged, stats);
    }

    private static async Task PublishAsync<T>(Func<T, Task>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }

        var tasks = handlers.GetInvocationList()
            .Cast<Func<T, Task>>()
            .Select(handler => InvokeHandler(handler, value))
            .ToArray();

        await Task.WhenAll(tasks);
    }

    private static Task InvokeHandler<T>(Func<T, Task> handler, T value)
    {
        try
        {
            return handler(value);
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }
}