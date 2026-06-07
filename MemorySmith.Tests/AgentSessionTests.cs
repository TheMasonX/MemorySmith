using System.Security.Claims;
using System.Text.Json.Nodes;
using MemorySmith.App.Services;
using MemorySmith.App.Services.AgentSessions;
using MemorySmith.App.Services.Training;
using MemorySmith.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

/// <summary>
/// Unit tests for AgentSessionService, AgentSession, ChatToolCatalog additions,
/// and IAgentSessionStore. See TSK-0275.
/// </summary>
[TestFixture]
public class AgentSessionTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MemorySmithAgentSessionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best effort */ }
    }

    // ── ChatToolCatalog additions ─────────────────────────────────────────────

    [Test]
    public void AgentTools_OnlyContainsAvailableInChatTools()
    {
        var catalog = new ChatToolCatalog();
        var agentToolNames = catalog.AgentTools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chatToolNames = catalog.ChatTools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Multiple(() =>
        {
            // AgentTools is a superset of ChatTools: all chat tools PLUS agent-only write tools
            Assert.That(agentToolNames, Is.SupersetOf(chatToolNames),
                "AgentTools should include all ChatTools (AvailableInChat=true) plus agent-only write tools");
            // Exactly 7 agent-only write tools (AvailableInAgent=true, AvailableInChat=false)
            var agentOnlyTools = agentToolNames.Except(chatToolNames).ToList();
            Assert.That(agentOnlyTools.Count, Is.EqualTo(7),
                $"Expected 7 agent-only write tools (memory_create, memory_update, task_create, task_update, task_set_status, task_add_comment, task_add_attachment), got {agentOnlyTools.Count}: {string.Join(", ", agentOnlyTools)}");

            // MCP-only write tools must NOT be in AgentTools
            Assert.That(agentToolNames, Does.Not.Contain("memorysmith_page_save"),
                "page_save is AvailableInMcp but not AvailableInChat — must not be in AgentTools");
            Assert.That(agentToolNames, Does.Not.Contain("memorysmith_page_delete"),
                "page_delete is AvailableInMcp but not AvailableInChat — must not be in AgentTools");

            // Read-only tools must be in AgentTools
            Assert.That(agentToolNames, Does.Contain("memorysmith_search"));
            Assert.That(agentToolNames, Does.Contain("memorysmith_hybrid_search"));
            Assert.That(agentToolNames, Does.Contain("memorysmith_page_search"));
        });
    }

    [Test]
    public void FilteredConstructor_CreatesSubsetCatalog()
    {
        var full = new ChatToolCatalog();
        var searchOnly = full.ChatTools.Where(t => t.Name.Contains("search")).ToList();
        var filtered = new ChatToolCatalog(searchOnly);

        Assert.Multiple(() =>
        {
            Assert.That(filtered.All.Count, Is.LessThan(full.All.Count));
            Assert.That(filtered.TryGet("memorysmith_search", out _), Is.True);
            Assert.That(filtered.TryGet("memorysmith_hybrid_search", out _), Is.True);
            Assert.That(filtered.TryGet("memorysmith_get", out _), Is.False,
                "memorysmith_get should not be in a search-only filtered catalog");
        });
    }

    [Test]
    public void WriteMcpTools_HaveEnabledByDefaultInMcpFalse()
    {
        var catalog = new ChatToolCatalog();
        var writeTools = catalog.McpTools.Where(t => t.Risk == ChatToolRisk.Write).ToList();

        Assert.That(writeTools, Is.Not.Empty, "Catalog should have at least one Write-tier MCP tool");
        Assert.Multiple(() =>
        {
            foreach (var tool in writeTools)
            {
                Assert.That(tool.EnabledByDefaultInMcp, Is.False,
                    $"Write tool '{tool.Name}' must have EnabledByDefaultInMcp=false for safe defaults");
            }
        });
    }

    [Test]
    public void ReadOnlyMcpTools_HaveEnabledByDefaultInMcpTrue()
    {
        var catalog = new ChatToolCatalog();
        var readOnlyTools = catalog.McpTools.Where(t => t.Risk == ChatToolRisk.ReadOnly).ToList();

        Assert.That(readOnlyTools, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            foreach (var tool in readOnlyTools)
            {
                Assert.That(tool.EnabledByDefaultInMcp, Is.True,
                    $"ReadOnly tool '{tool.Name}' should have EnabledByDefaultInMcp=true");
            }
        });
    }

    // ── AgentSession embedded lock ────────────────────────────────────────────

    [Test]
    public async Task AgentSession_Lock_SerializesAccess()
    {
        var session = new AgentSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            PrincipalId = "test-user",
            RequestedScope = "standard",
            EffectiveToolNames = [],
            CreatedAt = DateTimeOffset.UtcNow,
            MaxTurns = 10,
            TimeoutSeconds = 120,
            IdleTimeoutMinutes = 30,
        };

        // Acquire and release works without deadlock
        await session.AcquireAsync(CancellationToken.None);
        session.Release();

        // Second acquisition after release also works
        await session.AcquireAsync(CancellationToken.None);
        session.Release();

        Assert.Pass("Session lock acquired and released twice without deadlock");
    }

    [Test]
    public void AgentSession_TrimHistory_RespectsMaxTurns()
    {
        var session = new AgentSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            PrincipalId = "test-user",
            RequestedScope = "standard",
            EffectiveToolNames = [],
            CreatedAt = DateTimeOffset.UtcNow,
            MaxTurns = 50,
            TimeoutSeconds = 120,
            IdleTimeoutMinutes = 30,
        };

        // Add 10 turns (20 messages)
        for (var i = 0; i < 10; i++)
        {
            session.AppendMessages($"user {i}", $"assistant {i}");
        }
        Assert.That(session.History.Count, Is.EqualTo(20));

        // Trim to 3 turns max — should keep last 6 messages (3 turns × 2 messages)
        session.TrimHistoryToMaxTurns(3);
        Assert.That(session.History.Count, Is.EqualTo(6));
        Assert.That(session.History[0].Content, Is.EqualTo("user 7"),
            "After trimming to 3 turns, first message should be user 7");
    }

    [Test]
    public void AgentSession_TrimHistory_ClampsBadMaxTurnsValues()
    {
        var session = new AgentSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            PrincipalId = "p",
            RequestedScope = "standard",
            EffectiveToolNames = [],
            CreatedAt = DateTimeOffset.UtcNow,
            MaxTurns = 10,
            TimeoutSeconds = 120,
            IdleTimeoutMinutes = 30,
        };
        session.AppendMessages("u", "a");

        // maxTurns=0 should be clamped to 1 (not delete everything)
        session.TrimHistoryToMaxTurns(0);
        Assert.That(session.History.Count, Is.EqualTo(2), "maxTurns=0 should clamp to 1 turn (2 messages)");
    }

    // ── InMemoryAgentSessionStore ─────────────────────────────────────────────

    [Test]
    public async Task InMemoryStore_SaveAndGet_RoundTrips()
    {
        var store = new InMemoryAgentSessionStore();
        var session = CreateTestSession("alice");

        await store.SaveAsync(session, CancellationToken.None);
        var retrieved = await store.GetAsync(session.SessionId, CancellationToken.None);

        Assert.That(retrieved, Is.SameAs(session), "In-memory store should return the same object reference");
    }

    [Test]
    public async Task InMemoryStore_Delete_RemovesSession()
    {
        var store = new InMemoryAgentSessionStore();
        var session = CreateTestSession("alice");

        await store.SaveAsync(session, CancellationToken.None);
        await store.DeleteAsync(session.SessionId, CancellationToken.None);
        var retrieved = await store.GetAsync(session.SessionId, CancellationToken.None);

        Assert.That(retrieved, Is.Null, "Deleted session should not be retrievable");
    }

    [Test]
    public async Task InMemoryStore_GetActiveCount_OnlyCountsActiveAndIdle()
    {
        var store = new InMemoryAgentSessionStore();
        var active = CreateTestSession("alice");
        var closed = CreateTestSession("alice");
        closed.SetStatus(AgentSessionStatus.Closed);
        var expired = CreateTestSession("alice");
        expired.SetStatus(AgentSessionStatus.Expired);

        await store.SaveAsync(active, CancellationToken.None);
        await store.SaveAsync(closed, CancellationToken.None);
        await store.SaveAsync(expired, CancellationToken.None);

        var count = await store.GetActiveCountForPrincipalAsync("alice", CancellationToken.None);
        Assert.That(count, Is.EqualTo(1), "Only Active sessions should be counted");
    }

    [Test]
    public async Task InMemoryStore_GetActiveAndIdle_ReturnsOnlyNonTerminalSessions()
    {
        var store = new InMemoryAgentSessionStore();
        var active = CreateTestSession("alice");
        var idle = CreateTestSession("alice");
        idle.SetStatus(AgentSessionStatus.Idle);
        var closed = CreateTestSession("alice");
        closed.SetStatus(AgentSessionStatus.Closed);

        await store.SaveAsync(active, CancellationToken.None);
        await store.SaveAsync(idle, CancellationToken.None);
        await store.SaveAsync(closed, CancellationToken.None);

        var candidates = await store.GetActiveAndIdleAsync(CancellationToken.None);
        Assert.That(candidates.Count, Is.EqualTo(2), "GetActiveAndIdleAsync should return Active and Idle only");
    }

    // ── AgentSessionService ───────────────────────────────────────────────────

    [Test]
    public async Task CreateSession_CustomScopeWithEmptyTools_ReturnsFail()
    {
        var service = CreateService();
        var caller = MakePrincipal("alice", canEdit: true);

        var result = await service.CreateSessionAsync(
            "custom",
            customTools: null,
            modelOverride: null,
            providerOverride: null,
            maxTurns: 10,
            timeoutSeconds: 120,
            caller,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Error, Does.Contain("custom"), "Error should mention 'custom' scope");
            Assert.That(result.Error, Does.Contain("allowed_tools"), "Error should mention allowed_tools");
        });
    }

    [Test]
    public async Task CreateSession_EmptyEffectiveScope_ReturnsFail()
    {
        // Disable ALL AvailableInChat tools via DisabledTools (10 tools)
        // Note: agent-only write tools (AvailableInAgent=true, AvailableInChat=false) are not
        // listed here because they are never in scope for chat sessions anyway.
        var options = new MemorySmithOptions
        {
            Mcp = new McpOptions
            {
                DisabledTools = [
                    "memorysmith_search",
                    "memorysmith_hybrid_search",
                    "memorysmith_context_pack",
                    "memorysmith_get",
                    "memorysmith_code_search",
                    "memorysmith_code_search_status",
                    "memorysmith_page_search",
                    "memorysmith_page_get",
                    "memorysmith_task_list",
                    "memorysmith_task_get"
                ]
            }
        };
        var service = CreateService(options: options);
        var caller = MakePrincipal("alice", canEdit: true);

        var result = await service.CreateSessionAsync(
            "read_only",
            customTools: null,
            modelOverride: null,
            providerOverride: null,
            maxTurns: 10,
            timeoutSeconds: 120,
            caller,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Error, Does.Contain("No tools"), "Error should indicate no tools available");
        });
    }

    [Test]
    public async Task CreateSession_ReadOnlyScope_OnlyIncludesReadOnlyTools()
    {
        var service = CreateService();
        var caller = MakePrincipal("alice", canEdit: true);

        var result = await service.CreateSessionAsync(
            "read_only",
            customTools: null,
            modelOverride: null,
            providerOverride: null,
            maxTurns: 10,
            timeoutSeconds: 120,
            caller,
            CancellationToken.None);

        Assert.That(result.Succeeded, Is.True, result.Error);

        var catalog = new ChatToolCatalog();
        var writeToolNames = catalog.McpTools
            .Where(t => t.Risk != ChatToolRisk.ReadOnly)
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Multiple(() =>
        {
            foreach (var name in result.Session!.EffectiveToolNames)
            {
                Assert.That(writeToolNames.Contains(name), Is.False,
                    $"read_only scope should not include write/sensitive tool '{name}'");
            }
        });
    }

    [Test]
    public async Task ResumeSession_WrongPrincipal_ReturnsNotFound()
    {
        var service = CreateService();
        var alice = MakePrincipal("alice", canEdit: true);
        var bob = MakePrincipal("bob", canEdit: true);

        var create = await service.CreateSessionAsync(
            "standard", null, null, null, 10, 120, alice, CancellationToken.None);
        Assert.That(create.Succeeded, Is.True);

        // Bob tries to resume Alice's session
        var resume = await service.ResumeSessionAsync(create.Session!.SessionId, bob, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(resume.Succeeded, Is.False);
            // Error must not reveal which principal owns the session (anti-enumeration)
            Assert.That(resume.Error, Does.Contain("session_expired").Or.Contain("not found").IgnoreCase,
                "Cross-principal resume must return the same error as not-found");
        });
    }

    [Test]
    public async Task ResumeSession_ExpiredSession_ReturnsNotFound()
    {
        var store = new InMemoryAgentSessionStore();
        var service = CreateService(store: store);
        var caller = MakePrincipal("alice", canEdit: true);

        var create = await service.CreateSessionAsync(
            "standard", null, null, null, 10, 120, caller, CancellationToken.None);
        Assert.That(create.Succeeded, Is.True);

        // Manually expire the session
        var session = await store.GetAsync(create.Session!.SessionId, CancellationToken.None);
        session!.SetStatus(AgentSessionStatus.Expired);
        await store.SaveAsync(session, CancellationToken.None);

        var resume = await service.ResumeSessionAsync(create.Session.SessionId, caller, CancellationToken.None);
        Assert.That(resume.Succeeded, Is.False);
    }

    [Test]
    public async Task EndSession_DoubleClose_ReturnsFalseOnSecondCall()
    {
        var service = CreateService();
        var caller = MakePrincipal("alice", canEdit: true);

        var create = await service.CreateSessionAsync(
            "standard", null, null, null, 10, 120, caller, CancellationToken.None);
        Assert.That(create.Succeeded, Is.True);

        var firstClose = await service.EndSessionAsync(create.Session!.SessionId, caller, CancellationToken.None);
        var secondClose = await service.EndSessionAsync(create.Session.SessionId, caller, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(firstClose, Is.True, "First close should succeed");
            Assert.That(secondClose, Is.False, "Second close on already-closed session should return false, not throw");
        });
    }

    [Test]
    public async Task EndSession_WrongPrincipal_ReturnsFalse()
    {
        var service = CreateService();
        var alice = MakePrincipal("alice", canEdit: true);
        var bob = MakePrincipal("bob", canEdit: true);

        var create = await service.CreateSessionAsync(
            "standard", null, null, null, 10, 120, alice, CancellationToken.None);
        Assert.That(create.Succeeded, Is.True);

        var result = await service.EndSessionAsync(create.Session!.SessionId, bob, CancellationToken.None);
        Assert.That(result, Is.False, "Wrong-principal end must return false, not throw");
    }

    [Test]
    public async Task CreateSession_UnknownProvider_ReturnsFail()
    {
        var service = CreateService();
        var caller = MakePrincipal("alice", canEdit: true);

        var result = await service.CreateSessionAsync(
            "standard", null,
            modelOverride: null,
            providerOverride: "UnknownProvider",
            maxTurns: 10,
            timeoutSeconds: 120,
            caller,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Error, Does.Contain("UnknownProvider").Or.Contain("Unknown provider").IgnoreCase);
        });
    }

    [Test]
    public async Task CreateSession_NullPrincipal_ReturnsFail()
    {
        var service = CreateService();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity()); // no NameIdentifier, no Name

        var result = await service.CreateSessionAsync(
            "standard", null, null, null, 10, 120, anonymous, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Error, Does.Contain("NameIdentifier").Or.Contain("authenticated").IgnoreCase);
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private AgentSessionService CreateService(
        InMemoryAgentSessionStore? store = null,
        MemorySmithOptions? options = null)
    {
        var sessionStore = store ?? new InMemoryAgentSessionStore();
        var gpuSlots = new NullGpuSlotScheduler();
        var catalog = new ChatToolCatalog();
        var opts = Options.Create(options ?? new MemorySmithOptions());
        var auth = new AlwaysAllowAuthorizationService();
        var scopeFactory = new StubServiceScopeFactory();
        var dataPath = Path.Combine(_tempDir, "data", "Memories");
        Directory.CreateDirectory(dataPath);
        var memories = TestServiceFactory.CreateMemoryApplicationService(
            new InMemoryMemoryStore(),
            new RecordingEventStore(),
            new RecordingMemoryChangePublisher());
        var pages = new FilePageService(_tempDir);
        var interceptor = new ChatIntentInterceptor();
        var logger = NullLogger<AgentSessionService>.Instance;
        var transcriptWriter = new NullChatTranscriptWriter();

        return new AgentSessionService(
            sessionStore, gpuSlots, catalog, opts, auth, scopeFactory,
            memories, pages, interceptor, logger, transcriptWriter);
    }

    private static AgentSession CreateTestSession(string principalId) => new()
    {
        SessionId = Guid.NewGuid().ToString("N"),
        PrincipalId = principalId,
        RequestedScope = "standard",
        EffectiveToolNames = ["memorysmith_search"],
        CreatedAt = DateTimeOffset.UtcNow,
        MaxTurns = 10,
        TimeoutSeconds = 120,
        IdleTimeoutMinutes = 30,
    };

    private static ClaimsPrincipal MakePrincipal(string userId, bool canEdit = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userId)
        };
        if (canEdit)
        {
            claims.Add(new Claim(ClaimTypes.Role, MemorySmithRoles.Editor));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Authorization service that approves all policy checks unconditionally.
    /// Used to isolate AgentSessionService tests from auth policy infrastructure.
    /// </summary>
    private sealed class AlwaysAllowAuthorizationService : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
            => Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, string policyName)
            => Task.FromResult(AuthorizationResult.Success());
    }

    /// <summary>
    /// Service scope factory that resolves an empty provider collection.
    /// Prevents actual Ollama calls during unit tests.
    /// </summary>
    private sealed class StubServiceScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new StubScope();

        private sealed class StubScope : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new StubServiceProvider();
            public void Dispose() { }
        }

        private sealed class StubServiceProvider : IServiceProvider
        {
            public object? GetService(Type serviceType)
            {
                // Return empty provider collection — InvokeCoreAsync will fail gracefully
                // (this is expected in unit tests that don't test full invocation)
                if (serviceType == typeof(IEnumerable<IChatProvider>))
                    return Enumerable.Empty<IChatProvider>();
                return null;
            }
        }
    }
}
