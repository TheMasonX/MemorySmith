namespace MemorySmith.App.Services;

/// <summary>
/// Controls access to a local GPU inference backend (Ollama).
/// Prevents concurrent inference sessions from exhausting device VRAM.
/// Use <see cref="OllamaGpuSlotScheduler"/> for single-GPU Ollama hosts and
/// <see cref="NullGpuSlotScheduler"/> for cloud providers that need no scheduling.
/// </summary>
public interface IGpuSlotScheduler
{
    /// <summary>
    /// Acquires an inference slot. Blocks until a slot is available or <paramref name="ct"/> is cancelled.
    /// Dispose the returned handle to release the slot.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(string reason, CancellationToken ct);

    /// <summary>Current number of callers waiting for a slot. Exposed for health metrics.</summary>
    int WaitingCount { get; }
}

/// <summary>
/// No-op scheduler for providers that do not consume local GPU resources (e.g. GitHub Copilot).
/// </summary>
public sealed class NullGpuSlotScheduler : IGpuSlotScheduler
{
    public static readonly NullGpuSlotScheduler Instance = new();
    public int WaitingCount => 0;

    public Task<IAsyncDisposable> AcquireAsync(string reason, CancellationToken ct)
        => Task.FromResult<IAsyncDisposable>(NullAsyncDisposable.Instance);
}

internal sealed class NullAsyncDisposable : IAsyncDisposable
{
    public static readonly NullAsyncDisposable Instance = new();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
