namespace MemorySmith.App.Services.AgentSessions;

/// <summary>
/// Persistence contract for agent sessions. Default implementation is
/// <see cref="InMemoryAgentSessionStore"/>; opt in to SQLite-backed persistence
/// (<see cref="SqliteAgentSessionStore"/>, TSK-0278) via
/// <c>MemorySmith:AgentSession:PersistSessions=true</c>.
/// </summary>
public interface IAgentSessionStore
{
    Task<AgentSession?> GetAsync(string sessionId, CancellationToken ct);
    Task SaveAsync(AgentSession session, CancellationToken ct);
    Task DeleteAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// Returns all Active or Idle sessions for evaluation by the cleanup service.
    /// The cleanup service applies per-session idle timeout checks rather than a global cutoff,
    /// because individual sessions may have different <see cref="AgentSession.IdleTimeoutMinutes"/> values.
    /// </summary>
    Task<IReadOnlyList<AgentSession>> GetActiveAndIdleAsync(CancellationToken ct);

    /// <summary>
    /// Returns the count of Active or Idle sessions for a principal.
    /// Used to enforce the concurrent session cap, which is configured via
    /// <c>MemorySmith:Mcp:MaxConcurrentSessionsPerUser</c> (nullable; defaults to profile-based
    /// values: LocalDev=10, SecureLocal=3, RemoteHardened=1).
    /// </summary>
    Task<int> GetActiveCountForPrincipalAsync(string principalId, CancellationToken ct);
}
