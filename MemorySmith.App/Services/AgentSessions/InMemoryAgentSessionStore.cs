namespace MemorySmith.App.Services.AgentSessions;

using System.Collections.Concurrent;

/// <summary>
/// Default in-memory session store. Sessions are lost on server restart.
/// Enable <c>AgentSession:PersistSessions=true</c> in Phase 2 for SQLite-backed persistence.
/// </summary>
public sealed class InMemoryAgentSessionStore : IAgentSessionStore
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions =
        new(StringComparer.Ordinal);

    public Task<AgentSession?> GetAsync(string sessionId, CancellationToken ct)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public Task SaveAsync(AgentSession session, CancellationToken ct)
    {
        _sessions[session.SessionId] = session;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        _sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentSession>> GetActiveAndIdleAsync(CancellationToken ct)
    {
        var result = _sessions.Values
            .Where(s => s.Status is AgentSessionStatus.Active or AgentSessionStatus.Idle)
            .ToList();
        return Task.FromResult<IReadOnlyList<AgentSession>>(result);
    }

    public Task<int> GetActiveCountForPrincipalAsync(string principalId, CancellationToken ct)
    {
        var count = _sessions.Values.Count(s =>
            s.PrincipalId == principalId &&
            s.Status is AgentSessionStatus.Active or AgentSessionStatus.Idle);
        return Task.FromResult(count);
    }
}
