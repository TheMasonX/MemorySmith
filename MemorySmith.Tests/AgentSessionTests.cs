using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json.Nodes;
using MemorySmith.App.Services;
using MemorySmith.App.Services.AgentSessions;
using MemorySmith.App.Services.Training;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
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

    // ── SqliteAgentSessionStore (TSK-0278) ────────────────────────────────────

    [Test]
    public async Task SqliteStore_SaveAndGet_ReturnsSameInstance()
    {
        var store = CreateSqliteStore();
        var session = CreateTestSession("alice");

        await store.SaveAsync(session, CancellationToken.None);
        var retrieved = await store.GetAsync(session.SessionId, CancellationToken.None);

        Assert.That(retrieved, Is.SameAs(session),
            "While the process is alive the store must return the identical instance — " +
            "AgentSession's embedded lock requires instance identity across concurrent callers");
    }

    [Test]
    public async Task SqliteStore_SurvivesRestart_RehydratesAllFields()
    {
        var dbPath = Path.Combine(_tempDir, "agent-sessions.db");
        var store = CreateSqliteStore(dbPath);
        var session = new AgentSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            PrincipalId = "alice",
            RequestedScope = "custom",
            EffectiveToolNames = ["memorysmith_hybrid_search", "memorysmith_page_search"],
            ModelOverride = "qwen3:8b",
            ProviderOverride = "Ollama",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            MaxTurns = 7,
            TimeoutSeconds = 90,
            IdleTimeoutMinutes = 15,
            SystemPromptAddendum = "Prefer terse answers.",
            ParentSessionId = "parent-123",
            NestingDepth = 1,
        };
        session.AppendMessages("first question", "first answer");
        session.AppendMessages("second question", "second answer");
        session.IncrementTurn();
        session.IncrementTurn();
        session.SetStatus(AgentSessionStatus.Idle);
        await store.SaveAsync(session, CancellationToken.None);

        // A brand-new store over the same database file simulates a server restart:
        // the identity map is empty, so the session must be rehydrated from SQLite.
        var restarted = CreateSqliteStore(dbPath);
        var loaded = await restarted.GetAsync(session.SessionId, CancellationToken.None);

        Assert.That(loaded, Is.Not.Null, "session must survive a restart when persisted");
        Assert.That(loaded, Is.Not.SameAs(session), "restart must produce a rehydrated instance");
        Assert.Multiple(() =>
        {
            Assert.That(loaded!.SessionId, Is.EqualTo(session.SessionId));
            Assert.That(loaded.PrincipalId, Is.EqualTo("alice"));
            Assert.That(loaded.RequestedScope, Is.EqualTo("custom"));
            Assert.That(loaded.EffectiveToolNames,
                Is.EqualTo(new[] { "memorysmith_hybrid_search", "memorysmith_page_search" }));
            Assert.That(loaded.ModelOverride, Is.EqualTo("qwen3:8b"));
            Assert.That(loaded.ProviderOverride, Is.EqualTo("Ollama"));
            Assert.That(loaded.CreatedAt, Is.EqualTo(session.CreatedAt));
            Assert.That(loaded.MaxTurns, Is.EqualTo(7));
            Assert.That(loaded.TimeoutSeconds, Is.EqualTo(90));
            Assert.That(loaded.IdleTimeoutMinutes, Is.EqualTo(15));
            Assert.That(loaded.SystemPromptAddendum, Is.EqualTo("Prefer terse answers."));
            Assert.That(loaded.ParentSessionId, Is.EqualTo("parent-123"));
            Assert.That(loaded.NestingDepth, Is.EqualTo(1));
            Assert.That(loaded.TurnCount, Is.EqualTo(2));
            Assert.That(loaded.LastAccessedAt, Is.EqualTo(session.LastAccessedAt));
            Assert.That(loaded.Status, Is.EqualTo(AgentSessionStatus.Idle));
            Assert.That(loaded.History.Select(m => (m.Role, m.Content)), Is.EqualTo(new[]
            {
                ("user", "first question"), ("assistant", "first answer"),
                ("user", "second question"), ("assistant", "second answer"),
            }));
        });
    }

    [Test]
    public async Task SqliteStore_ConcurrentColdGets_ConvergeOnSingleInstance()
    {
        var dbPath = Path.Combine(_tempDir, "agent-sessions.db");
        var store = CreateSqliteStore(dbPath);
        var session = CreateTestSession("alice");
        await store.SaveAsync(session, CancellationToken.None);

        var restarted = CreateSqliteStore(dbPath);
        var gets = await Task.WhenAll(
            Task.Run(() => restarted.GetAsync(session.SessionId, CancellationToken.None)),
            Task.Run(() => restarted.GetAsync(session.SessionId, CancellationToken.None)),
            Task.Run(() => restarted.GetAsync(session.SessionId, CancellationToken.None)));

        Assert.Multiple(() =>
        {
            Assert.That(gets[0], Is.Not.Null);
            Assert.That(gets[1], Is.SameAs(gets[0]),
                "concurrent cold misses must converge on one instance (embedded-lock identity)");
            Assert.That(gets[2], Is.SameAs(gets[0]));
        });
    }

    [Test]
    public async Task SqliteStore_Delete_RemovesDurably()
    {
        var dbPath = Path.Combine(_tempDir, "agent-sessions.db");
        var store = CreateSqliteStore(dbPath);
        var session = CreateTestSession("alice");

        await store.SaveAsync(session, CancellationToken.None);
        await store.DeleteAsync(session.SessionId, CancellationToken.None);

        var sameStore = await store.GetAsync(session.SessionId, CancellationToken.None);
        var restarted = await CreateSqliteStore(dbPath).GetAsync(session.SessionId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(sameStore, Is.Null, "deleted session must not be retrievable");
            Assert.That(restarted, Is.Null, "deletion must be durable across restart");
        });
    }

    [Test]
    public async Task SqliteStore_GetActiveCount_OnlyCountsActiveAndIdle_AcrossRestart()
    {
        var dbPath = Path.Combine(_tempDir, "agent-sessions.db");
        var store = CreateSqliteStore(dbPath);
        var active = CreateTestSession("alice");
        var closed = CreateTestSession("alice");
        closed.SetStatus(AgentSessionStatus.Closed);
        var otherPrincipal = CreateTestSession("bob");
        await store.SaveAsync(active, CancellationToken.None);
        await store.SaveAsync(closed, CancellationToken.None);
        await store.SaveAsync(otherPrincipal, CancellationToken.None);

        var liveCount = await store.GetActiveCountForPrincipalAsync("alice", CancellationToken.None);
        var restartedCount = await CreateSqliteStore(dbPath)
            .GetActiveCountForPrincipalAsync("alice", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(liveCount, Is.EqualTo(1), "closed sessions and other principals must not count");
            Assert.That(restartedCount, Is.EqualTo(1), "the cap must survive a restart");
        });
    }

    [Test]
    public async Task SqliteStore_GetActiveAndIdle_PrefersLiveInstanceState()
    {
        var store = CreateSqliteStore();
        var active = CreateTestSession("alice");
        var idle = CreateTestSession("alice");
        idle.SetStatus(AgentSessionStatus.Idle);
        var closingLater = CreateTestSession("alice");
        await store.SaveAsync(active, CancellationToken.None);
        await store.SaveAsync(idle, CancellationToken.None);
        await store.SaveAsync(closingLater, CancellationToken.None);

        // Mutate the live instance WITHOUT saving: the persisted row still says Active, but the
        // store must trust the fresher in-process instance and exclude it.
        closingLater.SetStatus(AgentSessionStatus.Closed);

        var candidates = await store.GetActiveAndIdleAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(candidates, Has.Count.EqualTo(2));
            Assert.That(candidates, Does.Contain(active).And.Contain(idle));
            Assert.That(candidates, Does.Not.Contain(closingLater),
                "live instance state must win over a stale persisted row");
        });
    }

    [Test]
    public void PersistSessionsTrue_NoLongerThrowsAtServiceConstruction()
    {
        // TSK-0278: the Phase 2 startup guard is gone — PersistSessions=true is now a supported
        // configuration backed by SqliteAgentSessionStore.
        var options = new MemorySmithOptions();
        options.AgentSession.PersistSessions = true;

        Assert.DoesNotThrow(() => CreateService(options: options));
    }

    private SqliteAgentSessionStore CreateSqliteStore(string? dbPath = null)
    {
        var database = new SqliteMemorySmithDatabase(new DatabaseOptions
        {
            ConnectionString = $"Data Source={dbPath ?? Path.Combine(_tempDir, "agent-sessions.db")};Pooling=False",
            ApplyMigrationsOnStartup = true,
            UseWal = false
        });
        return new SqliteAgentSessionStore(database, NullLogger<SqliteAgentSessionStore>.Instance);
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

    // ── Scope computation combinations (TSK-0275 acceptance criteria) ─────────

    [Test]
    public async Task CreateSession_FullScope_MatchesEnabledChatModeMcpTools()
    {
        var service = CreateService();
        var caller = MakePrincipal("alice", canEdit: true);

        var result = await service.CreateSessionAsync(
            "full", null, null, null, 10, 120, caller, CancellationToken.None);

        Assert.That(result.Succeeded, Is.True, result.Error);

        // With default options (no EnabledTools/DisabledTools, default secure-local profile),
        // the effective set must be exactly the chat-mode MCP tools that are MCP-enabled by
        // default. Write tools are default-off in MCP and sensitive-read tools are not
        // AvailableInChat, so they never appear even for "full".
        var catalog = new ChatToolCatalog();
        var expected = catalog.McpTools
            .Where(t => t.AvailableInChat && t.EnabledByDefaultInMcp)
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.That(result.Session!.EffectiveToolNames.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Is.EquivalentTo(expected));
    }

    [Test]
    public async Task CreateSession_CustomScope_IntersectsRequestedToolsCaseInsensitively()
    {
        var service = CreateService();
        var caller = MakePrincipal("alice", canEdit: true);

        var result = await service.CreateSessionAsync(
            "custom",
            customTools: ["MEMORYSMITH_SEARCH", "memorysmith_get", "not_a_real_tool"],
            modelOverride: null,
            providerOverride: null,
            maxTurns: 10,
            timeoutSeconds: 120,
            caller,
            CancellationToken.None);

        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(result.Session!.EffectiveToolNames.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Is.EquivalentTo(new[] { "memorysmith_search", "memorysmith_get" }),
            "custom scope must intersect requested names case-insensitively and drop unknown tools");
    }

    [Test]
    public async Task CreateSession_RemoteHardenedProfile_LimitsToReadOnlyTools()
    {
        var options = new MemorySmithOptions { SecurityProfile = MemorySmithSecurityProfiles.RemoteHardened };
        var service = CreateService(options: options);
        var caller = MakePrincipal("alice", canEdit: true);

        var result = await service.CreateSessionAsync(
            "full", null, null, null, 10, 120, caller, CancellationToken.None);

        Assert.That(result.Succeeded, Is.True, result.Error);

        var catalog = new ChatToolCatalog();
        Assert.Multiple(() =>
        {
            foreach (var name in result.Session!.EffectiveToolNames)
            {
                Assert.That(catalog.TryGet(name, out var tool), Is.True);
                Assert.That(tool.Risk, Is.EqualTo(ChatToolRisk.ReadOnly),
                    $"remote-hardened ceiling must exclude non-ReadOnly tool '{name}'");
            }
        });
    }

    [Test]
    public async Task CreateSession_RemoteHardenedProfile_CapsConcurrentSessionsAtOne()
    {
        var options = new MemorySmithOptions { SecurityProfile = MemorySmithSecurityProfiles.RemoteHardened };
        var service = CreateService(options: options);
        var caller = MakePrincipal("alice", canEdit: true);

        var first = await service.CreateSessionAsync(
            "read_only", null, null, null, 10, 120, caller, CancellationToken.None);
        var second = await service.CreateSessionAsync(
            "read_only", null, null, null, 10, 120, caller, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first.Succeeded, Is.True, first.Error);
            Assert.That(second.Succeeded, Is.False, "remote-hardened caps concurrent sessions at 1");
            Assert.That(second.Error, Does.Contain("Concurrent session limit (1)"));
        });
    }

    // ── Timeout phases (council-approved observability; queue vs inference) ──

    [Test]
    public async Task Invoke_QueueWaitTimeout_ReturnsQueueWaitPhaseWithoutConsumingTurn()
    {
        var service = CreateService(
            gpuSlots: new BlockedGpuSlotScheduler(),
            scopeFactory: new SingleProviderScopeFactory(new HangingChatProvider()));
        // Construct the session directly with a 1-second budget (CreateSessionAsync clamps
        // timeout_seconds to a 10s minimum, which would make this test needlessly slow).
        var session = CreateTestSession("alice", timeoutSeconds: 1);

        var result = await service.InvokeAsync(session, "hello", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.FinishReason, Is.EqualTo("timeout"));
            Assert.That(result.TimeoutPhase, Is.EqualTo(AgentInvokeResult.TimeoutPhaseQueueWait));
            Assert.That(result.Turn, Is.EqualTo(0), "queue timeout must not consume a turn");
            Assert.That(session.History, Is.Empty, "queue timeout must not mutate session history");
            Assert.That(session.Status, Is.EqualTo(AgentSessionStatus.Active), "session stays alive for retry");
        });
    }

    [Test]
    public async Task Invoke_InferenceTimeout_ReturnsInferencePhaseWithoutConsumingTurn()
    {
        var service = CreateService(
            scopeFactory: new SingleProviderScopeFactory(new HangingChatProvider()));
        var session = CreateTestSession("alice", timeoutSeconds: 1);

        var result = await service.InvokeAsync(session, "hello", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.FinishReason, Is.EqualTo("timeout"));
            Assert.That(result.TimeoutPhase, Is.EqualTo(AgentInvokeResult.TimeoutPhaseInference));
            Assert.That(result.Turn, Is.EqualTo(0), "inference timeout must not consume a turn");
            Assert.That(session.History, Is.Empty, "inference timeout must not mutate session history");
            Assert.That(session.Status, Is.EqualTo(AgentSessionStatus.Active), "session stays alive for retry");
        });
    }

    [Test]
    public void SerializeResult_OmitsTimeoutPhaseForNonTimeoutResults()
    {
        var json = AgentSessionService.SerializeResult(
            new AgentInvokeResult("s1", 1, "ok", [], 0, "stop", null));

        Assert.That(json, Does.Not.Contain("timeoutPhase"),
            "non-timeout results must not enlarge the MCP contract with a null timeoutPhase key");
    }

    [Test]
    public void SerializeResult_IncludesTimeoutPhaseOnTimeout()
    {
        var queueJson = AgentSessionService.SerializeResult(
            AgentInvokeResult.Timeout("s1", 0, AgentInvokeResult.TimeoutPhaseQueueWait));
        var inferenceJson = AgentSessionService.SerializeResult(
            AgentInvokeResult.Timeout("s1", 0, AgentInvokeResult.TimeoutPhaseInference));

        Assert.Multiple(() =>
        {
            Assert.That(queueJson, Does.Contain("\"timeoutPhase\":\"queue_wait\""));
            Assert.That(queueJson, Does.Contain("\"finishReason\":\"timeout\""));
            Assert.That(inferenceJson, Does.Contain("\"timeoutPhase\":\"inference\""));
        });
    }

    // ── Cleanup service expiry (TSK-0275 acceptance criteria) ────────────────

    [Test]
    public async Task CleanupService_ExpiresAndDeletesIdleSessions()
    {
        var store = new InMemoryAgentSessionStore();
        var stale = CreateTestSession("alice", idleTimeoutMinutes: 0); // deadline = now
        var fresh = CreateTestSession("alice", idleTimeoutMinutes: 60);
        await store.SaveAsync(stale, CancellationToken.None);
        await store.SaveAsync(fresh, CancellationToken.None);
        await Task.Delay(50); // ensure stale.LastAccessedAt is strictly in the past

        var cleanup = new AgentSessionCleanupService(
            store, NullLogger<AgentSessionCleanupService>.Instance);
        await cleanup.RunCleanupAsync(CancellationToken.None);

        var staleFromStore = await store.GetAsync(stale.SessionId, CancellationToken.None);
        var freshFromStore = await store.GetAsync(fresh.SessionId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(stale.Status, Is.EqualTo(AgentSessionStatus.Expired),
                "idle session past its per-session deadline must be expired");
            Assert.That(staleFromStore, Is.Null,
                "expired session must be hard-deleted from the store");
            Assert.That(fresh.Status, Is.EqualTo(AgentSessionStatus.Active),
                "session within its idle window must be untouched");
            Assert.That(freshFromStore, Is.Not.Null);
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private AgentSessionService CreateService(
        InMemoryAgentSessionStore? store = null,
        MemorySmithOptions? options = null,
        IGpuSlotScheduler? gpuSlots = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        var sessionStore = store ?? new InMemoryAgentSessionStore();
        gpuSlots ??= new NullGpuSlotScheduler();
        var catalog = new ChatToolCatalog();
        var opts = Options.Create(options ?? new MemorySmithOptions());
        var auth = new AlwaysAllowAuthorizationService();
        scopeFactory ??= new StubServiceScopeFactory();
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

    private static AgentSession CreateTestSession(
        string principalId, int timeoutSeconds = 120, int idleTimeoutMinutes = 30) => new()
    {
        SessionId = Guid.NewGuid().ToString("N"),
        PrincipalId = principalId,
        RequestedScope = "standard",
        EffectiveToolNames = ["memorysmith_search"],
        CreatedAt = DateTimeOffset.UtcNow,
        MaxTurns = 10,
        TimeoutSeconds = timeoutSeconds,
        IdleTimeoutMinutes = idleTimeoutMinutes,
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

    /// <summary>
    /// Scope factory that resolves exactly one chat provider — required by invoke-path tests,
    /// since MemoryChatAgent's constructor throws on an empty provider collection.
    /// </summary>
    private sealed class SingleProviderScopeFactory(IChatProvider provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(provider);

        private sealed class Scope(IChatProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new Provider(provider);
            public void Dispose() { }
        }

        private sealed class Provider(IChatProvider provider) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(IEnumerable<IChatProvider>)
                    ? new[] { provider }
                    : null;
        }
    }

    /// <summary>
    /// Chat provider whose completion never finishes — used to force the inference phase
    /// to exceed the per-turn budget. Honors cancellation, like a real provider.
    /// </summary>
    private sealed class HangingChatProvider : IChatProvider
    {
        public string Name => "Ollama";

        public async Task<ChatProviderResponse> CompleteAsync(ChatProviderRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public async IAsyncEnumerable<ChatProviderChunk> StreamAsync(
            ChatProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public Task<IReadOnlyList<ChatModelSummary>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChatModelSummary>>([]);
    }

    /// <summary>
    /// GPU slot scheduler that never grants a slot — used to force the queue-wait phase
    /// to exceed the per-turn budget. Honors cancellation, like the real semaphore wait.
    /// </summary>
    private sealed class BlockedGpuSlotScheduler : IGpuSlotScheduler
    {
        public int WaitingCount => 1;

        public async Task<IAsyncDisposable> AcquireAsync(string reason, CancellationToken ct)
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }
}
