namespace MemorySmith.App.Services.AgentSessions;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>
/// Background service that periodically expires idle agent sessions and tombstones them.
/// Runs every 5 minutes. Sessions that have been idle longer than their configured
/// <see cref="AgentSession.IdleTimeoutMinutes"/> are marked <see cref="AgentSessionStatus.Expired"/>.
/// </summary>
public sealed class AgentSessionCleanupService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly IAgentSessionStore _store;
    private readonly IOptionsMonitor<MemorySmithOptions> _options;
    private readonly ILogger<AgentSessionCleanupService> _logger;

    public AgentSessionCleanupService(
        IAgentSessionStore store,
        IOptionsMonitor<MemorySmithOptions> options,
        ILogger<AgentSessionCleanupService> logger)
    {
        _store = store;
        _options = options;
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
        // Use a fixed idle timeout derived from the most permissive profile
        // (LocalDev = 30 min). Sessions store their own IdleTimeoutMinutes.
        var globalExpiryBefore = DateTimeOffset.UtcNow.AddMinutes(-30);
        var expired = await _store.GetIdleOrExpiredAsync(globalExpiryBefore, ct);

        var count = 0;
        foreach (var session in expired)
        {
            var idleTimeout = DateTimeOffset.UtcNow.AddMinutes(-session.IdleTimeoutMinutes);
            if (session.LastAccessedAt >= idleTimeout)
                continue;

            session.SetStatus(AgentSessionStatus.Expired);
            await _store.SaveAsync(session, ct);
            count++;
        }

        if (count > 0)
            _logger.LogDebug("Expired {Count} idle agent session(s).", count);
    }
}
