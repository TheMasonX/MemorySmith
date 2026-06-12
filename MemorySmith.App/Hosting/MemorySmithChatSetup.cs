namespace MemorySmith.App.Hosting;

using MemorySmith.App.Services;
using MemorySmith.App.Services.AgentSessions;
using MemorySmith.App.Services.Training;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

/// <summary>
/// Chat and agent wiring: model profiles, feedback, the chat agent, both chat providers, the
/// tool catalog and intent interceptor, and the agent-session services behind
/// <c>memorysmith_agent_invoke</c>. Extracted from Program.cs (TSK-0282).
/// </summary>
public static class MemorySmithChatSetup
{
    public static WebApplicationBuilder AddMemorySmithChat(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ChatModelProfileService>();
        builder.Services.AddSingleton<IChatFeedbackStore, SqliteChatFeedbackStore>();
        builder.Services.AddScoped<IChatAgent, MemoryChatAgent>();
        builder.Services.AddHttpClient<OllamaChatProvider>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<MemorySmithOptions>>().Value;
            var timeoutSeconds = Math.Clamp(options.Chat.RequestTimeoutSeconds, 10, 3600);
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        });
        builder.Services.AddScoped<GitHubCopilotChatProvider>();
        builder.Services.AddScoped<IChatProvider>(sp => sp.GetRequiredService<OllamaChatProvider>());
        builder.Services.AddScoped<IChatProvider>(sp => sp.GetRequiredService<GitHubCopilotChatProvider>());
        // ChatToolCatalog has two constructors: the parameterless one (full BuildTools catalog) and a
        // filtered one taking IEnumerable<ChatToolDescriptor> (used by AgentSessionService for scoped
        // sub-agent catalogs). MS.DI prefers the constructor with the most resolvable parameters, and
        // IEnumerable<T> always resolves (empty when nothing is registered) — so a bare
        // AddSingleton<ChatToolCatalog>() would construct an EMPTY catalog. Use an explicit factory.
        builder.Services.AddSingleton<ChatToolCatalog>(_ => new ChatToolCatalog());
        builder.Services.AddSingleton<ChatIntentInterceptor>();

        // ── Agent session services (memorysmith_agent_invoke) ─────────────────────
        builder.Services.AddSingleton<IGpuSlotScheduler, OllamaGpuSlotScheduler>();
        // IAgentSessionStore: in-memory by default; SQLite-backed persistence (TSK-0278) is opt-in
        // via MemorySmith:AgentSession:PersistSessions=true. Explicit factory so the choice is made
        // from bound options at resolution time and tests can flip it with configuration alone.
        builder.Services.AddSingleton<IAgentSessionStore>(sp =>
            sp.GetRequiredService<IOptions<MemorySmithOptions>>().Value.AgentSession.PersistSessions
                ? new SqliteAgentSessionStore(
                    sp.GetRequiredService<MemorySmith.Storage.IMemorySmithDatabase>(),
                    sp.GetRequiredService<ILogger<SqliteAgentSessionStore>>())
                : new InMemoryAgentSessionStore());
        // IChatTranscriptWriter: register ChatTranscriptWriter as the default implementation.
        // ChatTranscriptWriter.WriteAsync already no-ops when Training:ChatTranscriptEnabled=false,
        // so this is safe to register unconditionally. TryAddSingleton allows tests to override
        // with NullChatTranscriptWriter (or any other implementation) by registering before this.
        builder.Services.TryAddSingleton<IChatTranscriptWriter, ChatTranscriptWriter>();
        builder.Services.AddSingleton<AgentSessionService>();
        builder.Services.AddSingleton<McpAgentToolHandler>();
        builder.Services.AddHostedService<AgentSessionCleanupService>();
        // ─────────────────────────────────────────────────────────────────────────
        return builder;
    }
}
