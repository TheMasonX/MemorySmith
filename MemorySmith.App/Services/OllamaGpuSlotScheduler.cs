namespace MemorySmith.App.Services;

using Microsoft.Extensions.Options;

/// <summary>
/// Serializes access to the local Ollama inference backend using a semaphore.
/// Default capacity is 1 (serial) to prevent VRAM exhaustion on single-GPU hosts (e.g. RTX 5060 8 GB).
/// Configurable via <c>Chat:MaxParallelOllamaRequests</c> for multi-GPU or high-VRAM setups.
/// </summary>
public sealed class OllamaGpuSlotScheduler : IGpuSlotScheduler
{
    private readonly SemaphoreSlim _semaphore;
    private int _waiting;

    public OllamaGpuSlotScheduler(IOptionsMonitor<MemorySmithOptions> options)
    {
        // MaxParallelOllamaRequests defaults to 1 (serial). Override only for high-VRAM multi-GPU setups.
        var maxParallel = Math.Max(1, options.CurrentValue.Chat.MaxParallelOllamaRequests);
        _semaphore = new SemaphoreSlim(maxParallel, maxParallel);
    }

    /// <inheritdoc/>
    public int WaitingCount => Volatile.Read(ref _waiting);

    /// <inheritdoc/>
    public async Task<IAsyncDisposable> AcquireAsync(string reason, CancellationToken ct)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            await _semaphore.WaitAsync(ct);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
        return new SlotHandle(_semaphore);
    }

    private sealed class SlotHandle(SemaphoreSlim sem) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                sem.Release();
            return ValueTask.CompletedTask;
        }
    }
}
