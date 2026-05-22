using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

[TestFixture]
public class MaintenanceAgentWorkflowTests
{
    private string _tempDir = null!;
    private InMemoryMemoryStore _memoryStore = null!;
    private FilePageService _pages = null!;
    private MaintenanceAgentConfigService _config = null!;
    private MaintenanceDiffService _diff = null!;
    private MaintenanceWritePermissionService _permissions = null!;
    private MaintenanceProposalWorkflow _workflow = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-maintenance-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _memoryStore = new InMemoryMemoryStore();
        _pages = new FilePageService(Path.Combine(_tempDir, "Pages"));
        var options = new MemorySmithOptions
        {
            MaintenanceAgent = new MaintenanceAgentOptions
            {
                Read = [Path.Combine(_tempDir, "Memories"), Path.Combine(_tempDir, "Pages")],
                Write = [Path.Combine(_tempDir, "Memories", "Working"), Path.Combine(_tempDir, "Pages")],
                UseLlm = false,
                Storage = new MaintenanceAgentStorageOptions
                {
                    ProposalsPath = Path.Combine(_tempDir, "Proposals"),
                    TopicMapCachePath = Path.Combine(_tempDir, "Graph", "topic-map-cache.json"),
                    LastRunPath = Path.Combine(_tempDir, "Events", "maintenance-agent-last-run.json")
                }
            }
        };
        _config = new MaintenanceAgentConfigService(new StaticOptionsMonitor<MemorySmithOptions>(options));
        _diff = new MaintenanceDiffService();
        _permissions = new MaintenanceWritePermissionService(_config);
        var store = new FileMaintenanceProposalStore(_config, _permissions, _diff);
        _workflow = new MaintenanceProposalWorkflow(store, new TestCurrentUserContext());
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void DiffService_EmitsRemovedAndAddedLines()
    {
        var diff = _diff.BuildUnifiedDiff("note.md", "# Title\nold line", "# Title\nnew line");

        Assert.Multiple(() =>
        {
            Assert.That(diff, Does.Contain("--- note.md"));
            Assert.That(diff, Does.Contain("-old line"));
            Assert.That(diff, Does.Contain("+new line"));
        });
    }

    [Test]
    public async Task ProposalLifecycle_RespondRequiresCommentAndRevisionBeforeApproval()
    {
        var targetPath = Path.Combine(_tempDir, "Pages", "proposal-note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, "# Proposal\n\nBefore");
        var proposal = CreateProposal(targetPath, "# Proposal\n\nBefore", "# Proposal\n\nAfter");

        var submitted = await _workflow.SubmitAsync(proposal, CancellationToken.None);
        var responseError = Assert.ThrowsAsync<ArgumentException>(async () => await _workflow.RespondAsync(submitted.ProposalId, " ", CancellationToken.None));
        var needsRevision = await _workflow.RespondAsync(submitted.ProposalId, "Please cite the source page.", CancellationToken.None);
        var approveError = Assert.ThrowsAsync<InvalidOperationException>(async () => await _workflow.ApproveAsync(submitted.ProposalId, "Looks good.", CancellationToken.None));
        var revised = await _workflow.SubmitRevisionAsync(submitted.ProposalId, CreateProposal(targetPath, "# Proposal\n\nBefore", "# Proposal\n\nAfter"), CancellationToken.None);
        var approved = await _workflow.ApproveAsync(revised.ProposalId, "Looks good.", CancellationToken.None);
        var applied = await File.ReadAllTextAsync(targetPath);

        Assert.Multiple(() =>
        {
            Assert.That(responseError!.Message, Does.Contain("Respond requires"));
            Assert.That(needsRevision.Status, Is.EqualTo(MaintenanceProposalStatuses.NeedsRevision));
            Assert.That(needsRevision.Comments.Single().Comment, Is.EqualTo("Please cite the source page."));
            Assert.That(needsRevision.History.Single(item => item.Action == "respond").Comment, Is.Null);
            Assert.That(approveError!.Message, Does.Contain("Only open"));
            Assert.That(revised.Metadata.Supersedes, Does.Contain(submitted.ProposalId));
            Assert.That(approved.Status, Is.EqualTo(MaintenanceProposalStatuses.Approved));
            Assert.That(approved.History.Select(item => item.Action), Does.Contain("approve"));
            Assert.That(applied, Is.EqualTo("# Proposal\n\nAfter"));
        });
    }

    [Test]
    public async Task RequestAgentReview_PreservesStatusAndRecordsReviewRequest()
    {
        var targetPath = Path.Combine(_tempDir, "Pages", "review-note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, "before");
        var submitted = await _workflow.SubmitAsync(CreateProposal(targetPath, "before", "after"), CancellationToken.None);

        var requested = await _workflow.RequestAgentReviewAsync(submitted.ProposalId, "Double-check the evidence bundle.", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(requested.Status, Is.EqualTo(MaintenanceProposalStatuses.Open));
            Assert.That(requested.History.Select(item => item.Action), Does.Contain("agent_review_requested"));
            Assert.That(requested.Comments.Single().Comment, Is.EqualTo("Double-check the evidence bundle."));
            Assert.That(requested.UpdatedAtUtc, Is.GreaterThanOrEqualTo(submitted.UpdatedAtUtc));
        });
    }

    [Test]
    public async Task ApproveRejectsWhenCurrentFileNoLongerMatchesBeforeText()
    {
        var targetPath = Path.Combine(_tempDir, "Pages", "changed-note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, "before");
        var submitted = await _workflow.SubmitAsync(CreateProposal(targetPath, "before", "after"), CancellationToken.None);
        await File.WriteAllTextAsync(targetPath, "changed elsewhere");

        var error = Assert.ThrowsAsync<InvalidOperationException>(async () => await _workflow.ApproveAsync(submitted.ProposalId, null, CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("no longer matches"));
    }

    [Test]
    public void PermissionGuardRejectsOutsideDirectoriesAndConfigFiles()
    {
        var outside = Path.Combine(_tempDir, "Outside", "note.md");
        var configPath = Path.Combine(_tempDir, "Pages", "maintenance_agent.yaml");

        Assert.Multiple(() =>
        {
            Assert.That(() => _permissions.ValidateWritablePath(outside), Throws.InvalidOperationException.With.Message.Contains("outside"));
            Assert.That(() => _permissions.ValidateWritablePath(configPath), Throws.InvalidOperationException.With.Message.Contains("schema or configuration"));
        });
    }

    [Test]
    public void ConfigService_UsesStandardMaintenanceAgentOptions()
    {
        var readRoots = new[] { Path.Combine(_tempDir, "Memories"), Path.Combine(_tempDir, "Pages") };
        var writeRoots = new[] { Path.Combine(_tempDir, "Memories", "Working"), Path.Combine(_tempDir, "Pages") };
        var options = new MemorySmithOptions
        {
            Chat = new ChatOptions { OllamaEndpoint = "http://localhost:2345", OllamaModel = "chat-default" },
            MaintenanceAgent = new MaintenanceAgentOptions
            {
                Read = readRoots.ToList(),
                Write = writeRoots.ToList(),
                DirectWrite = true,
                UseLlm = false,
                Provider = "GitHub",
                OllamaEndpoint = "http://localhost:11434",
                Model = "gpt-5-mini",
                AgentVersion = "maintenance-agent.v2",
                MaxFindingsPerTask = 7,
                Tasks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["spot_checks"] = false,
                    ["staleness_scan"] = true,
                    ["synthesis"] = true
                },
                Schedule = new MaintenanceAgentScheduleOptions
                {
                    Enabled = true,
                    WeeklyDay = "Monday",
                    WeeklyHourLocal = 4,
                    MinimumHoursBetweenRuns = 48
                },
                ResourceProbe = new MaintenanceAgentResourceProbeOptions
                {
                    Enabled = false,
                    SkipWhenBusy = false,
                    BusyProcessNames = ["steam"]
                },
                Storage = new MaintenanceAgentStorageOptions
                {
                    ProposalsPath = Path.Combine(_tempDir, "YamlProposals")
                }
            }
        };
        var service = new MaintenanceAgentConfigService(new StaticOptionsMonitor<MemorySmithOptions>(options));

        var loaded = service.GetCurrent();

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Read, Is.EquivalentTo(readRoots));
            Assert.That(loaded.Write, Is.EquivalentTo(writeRoots));
            Assert.That(loaded.DirectWrite, Is.True);
            Assert.That(loaded.UseLlm, Is.False);
            Assert.That(loaded.Provider, Is.EqualTo("GitHub"));
            Assert.That(loaded.Model, Is.EqualTo("gpt-5-mini"));
            Assert.That(loaded.MaxFindingsPerTask, Is.EqualTo(7));
            Assert.That(loaded.Tasks["spot_checks"], Is.False);
            Assert.That(loaded.Tasks["staleness_scan"], Is.True);
            Assert.That(loaded.Tasks["synthesis"], Is.True);
            Assert.That(loaded.Schedule.Enabled, Is.True);
            Assert.That(loaded.Schedule.WeeklyDay, Is.EqualTo("Monday"));
            Assert.That(loaded.Schedule.MinimumHoursBetweenRuns, Is.EqualTo(48));
            Assert.That(loaded.ResourceProbe.Enabled, Is.False);
            Assert.That(loaded.ResourceProbe.SkipWhenBusy, Is.False);
            Assert.That(loaded.Storage.ProposalsPath, Is.EqualTo(Path.Combine(_tempDir, "YamlProposals")));
        });
    }

    [Test]
    public void ConfigService_NormalizesDefaultsFromStandardConfig()
    {
        var dataPath = Path.Combine(_tempDir, "Memories");
        var pagesPath = Path.Combine(_tempDir, "Pages");
        var options = new MemorySmithOptions
        {
            DataPath = dataPath,
            PagesPath = pagesPath,
            Chat = new ChatOptions
            {
                OllamaEndpoint = "http://localhost:6789",
                OllamaModel = "fallback-model"
            },
            MaintenanceAgent = new MaintenanceAgentOptions
            {
                Read = [],
                Write = [],
                Provider = string.Empty,
                OllamaEndpoint = string.Empty,
                Model = string.Empty,
                AgentVersion = string.Empty
            }
        };
        var service = new MaintenanceAgentConfigService(new StaticOptionsMonitor<MemorySmithOptions>(options));

        var loaded = service.GetCurrent();

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Read, Is.EqualTo(new[] { dataPath, pagesPath }));
            Assert.That(loaded.Write, Is.EqualTo(new[] { Path.Combine(dataPath, "Working"), pagesPath }));
            Assert.That(loaded.Provider, Is.EqualTo("Ollama"));
            Assert.That(loaded.OllamaEndpoint, Is.EqualTo("http://localhost:6789"));
            Assert.That(loaded.Model, Is.EqualTo("fallback-model"));
            Assert.That(loaded.AgentVersion, Is.EqualTo("maintenance-agent.v1"));
        });
    }

    [Test]
    public async Task TopicMap_ExtractsHeadingsRelationshipsCyclesAndStaleness()
    {
        _memoryStore.Save(new MemoryRecord
        {
            Id = "old-record",
            Title = "Old Record",
            Content = "## Rule\nOld guidance.",
            Tags = ["project-wiki", "expires:2000-01", "depends-on:new-record"],
            LastUpdated = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        _memoryStore.Save(new MemoryRecord
        {
            Id = "new-record",
            Title = "New Record",
            Content = "## Decision\nNew guidance.",
            Tags = ["project-wiki", "supersedes:old-record", "depends-on:old-record"],
            LastUpdated = DateTime.UtcNow
        });
        await _pages.SaveAsync(new PageSaveRequest("topic-page", "Topic Page", "# Topic Page\n\nSee [Old](old.md) and new-record."), CancellationToken.None);
        var service = new MaintenanceTopicMapService(_memoryStore, _pages, _config);

        var topicMap = await service.BuildAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(topicMap.Nodes.Single(node => node.Id == "old-record").Headings, Does.Contain("Rule"));
            Assert.That(topicMap.Nodes.Single(node => node.Id == "old-record").StalenessStatus, Is.EqualTo("expired"));
            Assert.That(topicMap.Edges.Any(edge => edge.SourceId == "new-record" && edge.TargetId == "old-record" && edge.Type == "Supersedes"), Is.True);
            Assert.That(topicMap.Edges.Any(edge => edge.SourceId == "page:topic-page" && edge.TargetId == "new-record" && edge.Type == "Mentions"), Is.True);
            Assert.That(MaintenanceTopicMapService.GenerateMermaid(topicMap), Does.Contain("Supersedes"));
            Assert.That(topicMap.DependencyCycles, Is.Not.Empty);
            Assert.That(topicMap.Clusters.Single(cluster => cluster.Key == "project-wiki").NodeIds, Is.EquivalentTo(new[] { "old-record", "new-record" }));
            Assert.That(File.Exists(Path.Combine(_tempDir, "Graph", "topic-map-cache.json")), Is.True);
        });
    }

    [Test]
    public async Task TopicMap_LoadCachedReturnsNullForCorruptCache()
    {
        var cachePath = Path.Combine(_tempDir, "Graph", "topic-map-cache.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllTextAsync(cachePath, "not json");
        var service = new MaintenanceTopicMapService(_memoryStore, _pages, _config);

        var cached = await service.LoadCachedAsync(CancellationToken.None);

        Assert.That(cached, Is.Null);
    }

    [Test]
    public async Task AgentRun_CreatesReviewProposalForDeterministicFindings()
    {
        _memoryStore.Save(new MemoryRecord
        {
            Id = "expired-record",
            Title = "Expired Record",
            Content = "Old guidance.",
            Tags = ["project-wiki", "expires:2000-01"],
            LastUpdated = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        var topicMap = new MaintenanceTopicMapService(_memoryStore, _pages, _config);
        var agent = new MaintenanceAgentService(
            _config,
            new MaintenanceResourceProbe(),
            topicMap,
            _workflow,
            [],
            NullLogger<MaintenanceAgentService>.Instance);

        var result = await agent.RunMaintenanceOnDemandAsync("staleness_scan", CancellationToken.None);
        var proposals = await _workflow.ListAsync(CancellationToken.None);
        var proposal = proposals.Single();
        await _workflow.ApproveAsync(proposal.ProposalId, "Create review page.", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outputs.Single().Findings, Has.Count.EqualTo(1));
            Assert.That(result.Outputs.Single().Proposals, Has.Count.EqualTo(1));
            Assert.That(proposal.Changes.Single().Path, Does.Contain(Path.Combine("Pages", "maintenance-agent")));
            Assert.That(proposal.Changes.Single().After, Does.Contain("expired-record"));
            Assert.That(File.Exists(proposal.Changes.Single().Path), Is.True);
        });
    }

    [Test]
    public void SchedulerTiming_RespectsWeeklyWindowAndMinimumInterval()
    {
        var schedule = new MaintenanceAgentScheduleOptions
        {
            WeeklyDay = "Monday",
            WeeklyHourLocal = 4,
            MinimumHoursBetweenRuns = 24
        };
        var mondayWindow = new DateTimeOffset(2026, 5, 18, 4, 15, 0, TimeSpan.Zero);
        var utcNow = new DateTimeOffset(2026, 5, 19, 4, 15, 0, TimeSpan.Zero);

        Assert.Multiple(() =>
        {
            Assert.That(MaintenanceAgentSchedulerService.IsWeeklyWindow(schedule, mondayWindow), Is.True);
            Assert.That(MaintenanceAgentSchedulerService.IsWeeklyWindow(schedule, mondayWindow.AddHours(1)), Is.False);
            Assert.That(MaintenanceAgentSchedulerService.ShouldRun(null, schedule, utcNow), Is.True);
            Assert.That(MaintenanceAgentSchedulerService.ShouldRun(utcNow.AddHours(-23), schedule, utcNow), Is.False);
            Assert.That(MaintenanceAgentSchedulerService.ShouldRun(utcNow.AddHours(-24), schedule, utcNow), Is.True);
        });
    }

    [Test]
    public void SchedulerTiming_ParsesPersistedLastRunState()
    {
        var started = new DateTimeOffset(2026, 5, 18, 4, 0, 0, TimeSpan.Zero);
        var finished = started.AddMinutes(2);
        var payload = $$"""
        {
          "startedAtUtc": "{{started:O}}",
          "finishedAtUtc": "{{finished:O}}"
        }
        """;

        Assert.Multiple(() =>
        {
            Assert.That(MaintenanceAgentSchedulerService.ParsePersistedLastRun(payload), Is.EqualTo(finished));
            Assert.That(MaintenanceAgentSchedulerService.ParsePersistedLastRun(" "), Is.Null);
            Assert.That(MaintenanceAgentSchedulerService.MostRecent(started, finished), Is.EqualTo(finished));
        });
    }

    private static MaintenanceWriteProposal CreateProposal(string path, string before, string after) =>
        new()
        {
            ProposalId = Guid.NewGuid().ToString("D"),
            Changes = [new MaintenanceProposalChange(path, before, after)],
            Evidence = [new MaintenanceEvidenceItem("memory", "test-memory", Reference: "test-memory")],
            RelatedRecords = ["test-memory"],
            RiskLevel = MaintenanceProposalRiskLevels.Low,
            Confidence = 0.9,
            Metadata = new MaintenanceProposalMetadata("staleness_scan", 0.9, MaintenanceProposalRiskLevels.Low, ["test-memory"], [], [], "test-agent")
        };

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public string? UserId => "tester";
        public string DisplayName => "Test User";
        public string AuthScheme => "Test";
        public string? Provider => "Test";
        public bool IsAuthenticated => true;
        public IReadOnlyList<string> Roles => [MemorySmithRoles.Admin];
        public string ActorKind => MemorySmithActorKinds.User;
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T currentValue)
        {
            CurrentValue = currentValue;
        }

        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}