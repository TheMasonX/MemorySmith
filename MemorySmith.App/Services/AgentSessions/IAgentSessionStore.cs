namespace MemorySmith.App.Services.AgentSessions;

/// <summary>
/// Persistence contract for agent sessions. Default implementation is in-memory;
/// opt in to SQLite-backed persistence via <c>AgentSession:PersistSessions=true</c> (Phase 2).
/// </summary>
public interface IAgentSessionStore
{
    Task<AgentSession?> GetAsync(string sessionId, CancellationToken ct);
    Task SaveAsync(AgentSession session, CancellationToken ct);
    Task DeleteAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// Returns sessions whose <see cref="AgentSession.LastAccessedAt"/> is before
    /// <paramref name="expiryBefore"/>. Used by the cleanup service.
    /// </summary>
    Task<IReadOnlyList<AgentSession>> GetIdleOrExpiredAsync(DateTimeOffset expiryBefore, CancellationToken ct);

    /// <summary>
    /// Returns the count of Active or Idle sessions for a principal.
    /// Used to enforce <c>AgentSession:MaxConcurrentSessionsPerUser</c>.
    /// </summary>
    Task<int> GetActiveCountForPrincipalAsync(string principalId, CancellationToken ct);
}
