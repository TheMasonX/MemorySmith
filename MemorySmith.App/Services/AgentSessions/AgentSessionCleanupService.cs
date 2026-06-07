namespace MemorySmith.App.Services.AgentSessions;

using Microsoft.Extensions.Hosting;

/// <summary>
/// Background service that periodically expires idle agent sessions.
/// Runs every 5 minutes. Sessions whose idle time exceeds their configured
/// <see cref="AgentSession.IdleTimeoutMinutes"/> are marked <see cref="AgentSessionStatus.Expired"/>
/// under the session lock to prevent data races with <see cref="AgentSessionService"/>.
/// </summary>
public sealed class AgentSessionCleanupService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly IAgentSessionStore _store;
    private readonly ILogger<AgentSessionCleanupService> _logger;

    public AgentSessionCleanupService(
        IAgentSessionStore store,
        ILogger<AgentSessionCleanupService> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
                await RunCleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent session cleanup failed.");
            }
        }
    }

    private async Task RunCleanupAsync(CancellationToken ct)
    {
        var candidates = await _store.GetActiveAndIdleAsync(ct);
        var expired = 0;

        foreach (var session in candidates)
        {
            // Evaluate per-session idle timeout rather than a single global cutoff.
            var idleDeadline = DateTimeOffset.UtcNow.AddMinutes(-session.IdleTimeoutMinutes);

            // Quick check without lock — if clearly not expired, skip immediately.
            if (session.LastAccessedAt >= idleDeadline)
                continue;

            // Acquire session lock before mutating. This prevents races with
            // AgentSessionService.InvokeAsync which also holds this lock while updating
            // LastAccessedAt, TurnCount, and Status.
            await session.AcquireAsync(ct);
            try
            {
                // Re-check after acquiring the lock in case a concurrent InvokeAsync
                // updated LastAccessedAt between the optimistic check above and lock acquisition.
                if (session.LastAccessedAt >= idleDeadline)
                    continue;

                if (session.Status is AgentSessionStatus.Active or AgentSessionStatus.Idle)
                {
                    session.SetStatus(AgentSessionStatus.Expired);
                    await _store.SaveAsync(session, ct);
                    // Hard-delete after setting Expired so the store does not grow unboundedly.
                    // A caller who holds a stale session_id will receive "session_expired" from
                    // AgentSessionService.ResumeSessionAsync (GetAsync returns null → NotFound).
                    await _store.DeleteAsync(session.SessionId, ct);
                    expired++;
                }
            }
            finally
            {
                session.Release();
            }
        }

        if (expired > 0)
            _logger.LogDebug("Expired and deleted {Count} idle agent session(s).", expired);
    }
}
