using MemorySmith.App.Services;
using MemorySmith.Core.Models;
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
                ConfigPath = Path.Combine(_tempDir, "missing-maintenance-agent.yaml"),
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
            Assert.That(approveError!.Message, Does.Contain("Only open"));
            Assert.That(revised.Metadata.Supersedes, Does.Contain(submitted.ProposalId));
            Assert.That(approved.Status, Is.EqualTo(MaintenanceProposalStatuses.Approved));
            Assert.That(approved.History.Select(item => item.Action), Does.Contain("approve"));
            Assert.That(applied, Is.EqualTo("# Proposal\n\nAfter"));
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
        public void ConfigService_LoadsYamlDotNetMaintenanceAgentConfig()
        {
                var configPath = Path.Combine(_tempDir, "maintenance_agent.yaml");
                File.WriteAllText(configPath, $$"""
                read:
                    - '{{Path.Combine(_tempDir, "Memories")}}'
                write:
                    - '{{Path.Combine(_tempDir, "Pages")}}'
                direct_write: true
                use_llm: false
                provider: Ollama
                model: llama3.1:8b
                tasks:
                    staleness_scan: true
                    synthesis: false
                schedule:
                    enabled: true
                    weekly_day: Monday
                    weekly_hour_local: 4
                resource_probe:
                    skip_when_busy: false
                storage:
                    proposals_path: '{{Path.Combine(_tempDir, "YamlProposals")}}'
                """);
                var options = new MemorySmithOptions
                {
                        MaintenanceAgent = new MaintenanceAgentOptions { ConfigPath = configPath }
                };
                var service = new MaintenanceAgentConfigService(new StaticOptionsMonitor<MemorySmithOptions>(options));

                var loaded = service.GetCurrent();

                Assert.Multiple(() =>
                {
                        Assert.That(loaded.DirectWrite, Is.True);
                        Assert.That(loaded.UseLlm, Is.False);
                        Assert.That(loaded.Model, Is.EqualTo("llama3.1:8b"));
                        Assert.That(loaded.Tasks["staleness_scan"], Is.True);
                        Assert.That(loaded.Tasks["synthesis"], Is.False);
                        Assert.That(loaded.Schedule.Enabled, Is.True);
                        Assert.That(loaded.Schedule.WeeklyDay, Is.EqualTo("Monday"));
                        Assert.That(loaded.Schedule.WeeklyHourLocal, Is.EqualTo(4));
                        Assert.That(loaded.ResourceProbe.SkipWhenBusy, Is.False);
                        Assert.That(loaded.Storage.ProposalsPath, Is.EqualTo(Path.Combine(_tempDir, "YamlProposals")));
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
            Assert.That(topicMap.DependencyCycles, Is.Not.Empty);
            Assert.That(topicMap.Clusters.Single(cluster => cluster.Key == "project-wiki").NodeIds, Is.EquivalentTo(new[] { "old-record", "new-record" }));
            Assert.That(File.Exists(Path.Combine(_tempDir, "Graph", "topic-map-cache.json")), Is.True);
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