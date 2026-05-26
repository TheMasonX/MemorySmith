using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using System.Text.Json;

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
                    LastRunPath = Path.Combine(_tempDir, "Events", "maintenance-agent-last-run.json"),
                    ActivityLogPath = Path.Combine(_tempDir, "Events", "maintenance-agent-runs.jsonl"),
                    TranscriptLogPath = Path.Combine(_tempDir, "Events", "maintenance-agent-transcript.jsonl")
                }
            }
        };
        _config = new MaintenanceAgentConfigService(new StaticOptionsMonitor<MemorySmithOptions>(options));
        _diff = new MaintenanceDiffService();
        _permissions = new MaintenanceWritePermissionService(_config);
        var store = new FileMaintenanceProposalStore(_config, _permissions, _diff);
        _workflow = new MaintenanceProposalWorkflow(store, new TestCurrentUserContext(), options: Options.Create(options));
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
    public void ActiveRunStore_TracksCurrentRunUntilEnded()
    {
        var store = new MaintenanceActiveRunStore();
        var started = DateTimeOffset.UtcNow;

        var active = store.Begin("run_maintenance_on_demand", "staleness_scan", started);
        var current = store.GetCurrent();
        store.End("not-the-current-run");
        var stillCurrent = store.GetCurrent();
        store.End(active.RunId);

        Assert.Multiple(() =>
        {
            Assert.That(current, Is.Not.Null);
            Assert.That(current!.Trigger, Is.EqualTo("run_maintenance_on_demand"));
            Assert.That(current.Task, Is.EqualTo("staleness_scan"));
            Assert.That(current.StartedAtUtc, Is.EqualTo(started));
            Assert.That(stillCurrent, Is.Not.Null);
            Assert.That(store.GetCurrent(), Is.Null);
        });
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
            Assert.That(submitted.Metadata.BatchId, Is.EqualTo(submitted.ProposalId));
            Assert.That(submitted.Metadata.ParentProposalId, Is.Null);
            Assert.That(submitted.Metadata.Attempt, Is.EqualTo(1));
            Assert.That(submitted.History.Single(item => item.Action == "open").Comment, Does.Contain("batchId="));
            Assert.That(revised.Metadata.Supersedes, Does.Contain(submitted.ProposalId));
            Assert.That(revised.Metadata.BatchId, Is.EqualTo(submitted.Metadata.BatchId));
            Assert.That(revised.Metadata.ParentProposalId, Is.EqualTo(submitted.ProposalId));
            Assert.That(revised.Metadata.Attempt, Is.EqualTo(2));
            Assert.That(revised.History.Single(item => item.Action == "open").Comment, Does.Contain($"parentProposalId={submitted.ProposalId}"));
            Assert.That(approved.Status, Is.EqualTo(MaintenanceProposalStatuses.Approved));
            Assert.That(approved.History.Select(item => item.Action), Does.Contain("approve"));
            Assert.That(applied, Is.EqualTo("# Proposal\n\nAfter"));
        });
    }

    [Test]
    public async Task ProposalLifecycle_ActionUxCanAcceptNeedsRevisionProposalWithoutReplacement()
    {
        var workflow = CreateWorkflow(new MaintenanceAgentActionUxOptions
        {
            RevisionRequired = false
        });
        var targetPath = Path.Combine(_tempDir, "Pages", "proposal-note-accept-without-revision.md");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, "# Proposal\n\nBefore");
        var proposal = CreateProposal(targetPath, "# Proposal\n\nBefore", "# Proposal\n\nAfter");

        var submitted = await workflow.SubmitAsync(proposal, CancellationToken.None);
        var needsRevision = await workflow.RespondAsync(submitted.ProposalId, "Keep the diff, but adjust the note later.", CancellationToken.None);
        var accepted = await workflow.ApproveAsync(needsRevision.ProposalId, "Accepting without a replacement proposal.", CancellationToken.None);
        var applied = await File.ReadAllTextAsync(targetPath);

        Assert.Multiple(() =>
        {
            Assert.That(needsRevision.Status, Is.EqualTo(MaintenanceProposalStatuses.NeedsRevision));
            Assert.That(accepted.Status, Is.EqualTo(MaintenanceProposalStatuses.Approved));
            Assert.That(accepted.History.Select(item => item.Action), Does.Contain("approve"));
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

    private MaintenanceProposalWorkflow CreateWorkflow(MaintenanceAgentActionUxOptions? actionUx = null)
    {
        var options = new MemorySmithOptions
        {
            MaintenanceAgent = new MaintenanceAgentOptions
            {
                Read = [Path.Combine(_tempDir, "Memories"), Path.Combine(_tempDir, "Pages")],
                Write = [Path.Combine(_tempDir, "Memories", "Working"), Path.Combine(_tempDir, "Pages")],
                UseLlm = false,
                ActionUx = actionUx ?? new MaintenanceAgentActionUxOptions(),
                Storage = new MaintenanceAgentStorageOptions
                {
                    ProposalsPath = Path.Combine(_tempDir, "Proposals"),
                    TopicMapCachePath = Path.Combine(_tempDir, "Graph", "topic-map-cache.json"),
                    LastRunPath = Path.Combine(_tempDir, "Events", "maintenance-agent-last-run.json"),
                    ActivityLogPath = Path.Combine(_tempDir, "Events", "maintenance-agent-runs.jsonl"),
                    TranscriptLogPath = Path.Combine(_tempDir, "Events", "maintenance-agent-transcript.jsonl")
                }
            }
        };

        var config = new MaintenanceAgentConfigService(new StaticOptionsMonitor<MemorySmithOptions>(options));
        var permissions = new MaintenanceWritePermissionService(config);
        var store = new FileMaintenanceProposalStore(config, permissions, _diff);
        return new MaintenanceProposalWorkflow(store, new TestCurrentUserContext(), options: Options.Create(options));
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
    public void ConfigService_RebasesStandardRootEntriesAgainstOverriddenWikiPaths()
    {
        var dataPath = Path.Combine(_tempDir, "PublishedWiki", "Memories");
        var pagesPath = Path.Combine(_tempDir, "PublishedWiki", "Pages");
        var options = new MemorySmithOptions
        {
            DataPath = dataPath,
            PagesPath = pagesPath,
            MaintenanceAgent = new MaintenanceAgentOptions
            {
                Read = [Path.Combine("..", "Data", "Memories"), Path.Combine("..", "Data", "Pages")],
                Write = [Path.Combine("..", "Data", "Memories", "Working"), Path.Combine("..", "Data", "Pages")]
            }
        };
        var service = new MaintenanceAgentConfigService(new StaticOptionsMonitor<MemorySmithOptions>(options));

        var loaded = service.GetCurrent();

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Read, Is.EqualTo(new[] { dataPath, pagesPath }));
            Assert.That(loaded.Write, Is.EqualTo(new[] { Path.Combine(dataPath, "Working"), pagesPath }));
        });
    }

    [Test]
    public void ConfigService_UsesAssignedModelProfilesForAgentPurposes()
    {
        var options = new MemorySmithOptions
        {
            Chat = new ChatOptions
            {
                OllamaEndpoint = "http://localhost:4321",
                ModelProfiles =
                [
                    new ChatModelProfileOptions
                    {
                        Id = "maintenance-runner",
                        Provider = "Ollama",
                        Model = "runner-model",
                        Enabled = true
                    },
                    new ChatModelProfileOptions
                    {
                        Id = "proposal-reviewer",
                        Provider = "GitHub",
                        Model = "review-model",
                        Enabled = true
                    },
                    new ChatModelProfileOptions
                    {
                        Id = "admin-maintenance-chat",
                        Provider = "Ollama",
                        Model = "admin-chat-model",
                        Enabled = true
                    }
                ]
            },
            MaintenanceAgent = new MaintenanceAgentOptions
            {
                Provider = "GitHub",
                Model = "legacy-model",
                OllamaEndpoint = string.Empty,
                ModelProfileId = "maintenance-runner",
                ProposalReviewModelProfileId = "proposal-reviewer",
                AdminChatModelProfileId = "admin-maintenance-chat"
            }
        };
        var service = new MaintenanceAgentConfigService(new StaticOptionsMonitor<MemorySmithOptions>(options));

        var runConfig = service.GetCurrent();
        var reviewConfig = service.GetCurrent(MaintenanceAgentModelPurpose.ProposalReview);
        var adminChatConfig = service.GetCurrent(MaintenanceAgentModelPurpose.AdminChat);

        Assert.Multiple(() =>
        {
            Assert.That(runConfig.Provider, Is.EqualTo("Ollama"));
            Assert.That(runConfig.Model, Is.EqualTo("runner-model"));
            Assert.That(runConfig.OllamaEndpoint, Is.EqualTo("http://localhost:4321"));
            Assert.That(reviewConfig.Provider, Is.EqualTo("GitHub"));
            Assert.That(reviewConfig.Model, Is.EqualTo("review-model"));
            Assert.That(adminChatConfig.Provider, Is.EqualTo("Ollama"));
            Assert.That(adminChatConfig.Model, Is.EqualTo("admin-chat-model"));
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
    public async Task AgentRun_AppendsReadableMaintenanceActivity()
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

        await agent.RunMaintenanceOnDemandAsync("staleness_scan", CancellationToken.None);
        var activity = await agent.ListRecentActivityAsync(10, CancellationToken.None);
        var proposal = (await _workflow.ListAsync(CancellationToken.None)).Single();

        Assert.Multiple(() =>
        {
            Assert.That(activity, Has.Count.EqualTo(1));
            Assert.That(activity.Single().Trigger, Is.EqualTo("run_maintenance_on_demand"));
            Assert.That(activity.Single().Tasks, Is.EqualTo(new[] { "staleness_scan" }));
            Assert.That(activity.Single().FindingCount, Is.EqualTo(1));
            Assert.That(activity.Single().ProposalCount, Is.EqualTo(1));
            Assert.That(activity.Single().ProposalIds, Is.EqualTo(new[] { proposal.ProposalId }));
            Assert.That(File.Exists(Path.Combine(_tempDir, "Events", "maintenance-agent-runs.jsonl")), Is.True);
        });
    }

    [Test]
    public async Task AdminChat_WithProvider_AppendsReadableTranscript()
    {
        var provider = new FakeChatProvider("The last maintenance run found stale records.");
        var agent = CreateReviewAgent(provider);

        var entry = await agent.SendAdminMessageAsync("What did maintenance find?", CancellationToken.None);
        var transcripts = await agent.ListRecentTranscriptsAsync(10, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(entry.UserMessage, Is.EqualTo("What did maintenance find?"));
            Assert.That(entry.AssistantMessage, Is.EqualTo("The last maintenance run found stale records."));
            Assert.That(entry.Provider, Is.EqualTo(provider.Name));
            Assert.That(entry.Model, Is.EqualTo("review-model"));
            Assert.That(provider.LastRequest?.Messages.First().Content, Does.Contain("non-mutating maintenance agent"));
            Assert.That(provider.LastRequest?.Messages.Last().Content, Is.EqualTo("What did maintenance find?"));
            Assert.That(transcripts, Has.Count.EqualTo(1));
            Assert.That(transcripts.Single().Id, Is.EqualTo(entry.Id));
            Assert.That(File.Exists(Path.Combine(_tempDir, "Events", "maintenance-agent-transcript.jsonl")), Is.True);
        });
    }

    [Test]
    public async Task AdminChat_WithChatAgent_UsesToolBackedChatPipeline()
    {
        var provider = new FakeChatProvider("raw provider response");
        var chatAgent = new FakeMaintenanceChatAgent(new MemoryChatResponse(
            "Tool-backed maintenance answer.",
            provider.Name,
            "review-model",
            null,
            [new ChatContextItem("page", "tasks", "Tasks", "Pending maintenance work", ChatContextOrigins.Tool)],
            [],
            []));
        var agent = CreateReviewAgent(provider, chatAgent: chatAgent);

        var entry = await agent.SendAdminMessageAsync("What wiki tasks are pending?", CancellationToken.None);
        var transcripts = await agent.ListRecentTranscriptsAsync(10, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(entry.AssistantMessage, Is.EqualTo("Tool-backed maintenance answer."));
            Assert.That(entry.Provider, Is.EqualTo(provider.Name));
            Assert.That(entry.Model, Is.EqualTo("review-model"));
            Assert.That(chatAgent.LastRequest?.Mode, Is.EqualTo(MemoryChatMode.Chat));
            Assert.That(chatAgent.LastRequest?.Provider, Is.EqualTo(provider.Name));
            Assert.That(chatAgent.LastRequest?.Model, Is.EqualTo("review-model"));
            Assert.That(chatAgent.LastRequest?.History?.Single().Role, Is.EqualTo("system"));
            Assert.That(chatAgent.LastRequest?.History?.Single().Content, Does.Contain("non-mutating maintenance agent"));
            Assert.That(provider.LastRequest, Is.Null);
            Assert.That(transcripts.Single().Id, Is.EqualTo(entry.Id));
        });
    }

    [Test]
    public async Task AdminChat_WithDisabledLlm_RecordsWarningTranscript()
    {
        var topicMap = new MaintenanceTopicMapService(_memoryStore, _pages, _config);
        var agent = new MaintenanceAgentService(
            _config,
            new MaintenanceResourceProbe(),
            topicMap,
            _workflow,
            [],
            NullLogger<MaintenanceAgentService>.Instance);

        var entry = await agent.SendAdminMessageAsync("Can you review the wiki?", CancellationToken.None);
        var transcripts = await agent.ListRecentTranscriptsAsync(10, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(entry.Warnings.Single(), Does.Contain("disabled"));
            Assert.That(entry.AssistantMessage, Does.Contain("disabled"));
            Assert.That(entry.Provider, Is.Null);
            Assert.That(entry.Model, Is.Null);
            Assert.That(transcripts, Has.Count.EqualTo(1));
            Assert.That(transcripts.Single().Warnings, Is.EqualTo(entry.Warnings));
        });
    }

    [Test]
    public async Task AdminChat_RedactsSearchesAndRetainsTranscriptEntries()
    {
        var provider = new FakeChatProvider("Agent response token=agent-secret");
        var agent = CreateReviewAgent(provider, options =>
        {
            options.Storage.TranscriptRetentionEntries = 1;
            options.Storage.TranscriptRedactionEnabled = true;
        });

        await agent.SendAdminMessageAsync("first password=one", CancellationToken.None);
        await agent.SendAdminMessageAsync("second api_key=user-secret", CancellationToken.None);
        var transcripts = await agent.ListRecentTranscriptsAsync(10, CancellationToken.None);
        var filtered = await agent.ListRecentTranscriptsAsync(10, CancellationToken.None, "second");

        Assert.Multiple(() =>
        {
            Assert.That(transcripts, Has.Count.EqualTo(1));
            Assert.That(transcripts.Single().UserMessage, Does.Contain("api_key=[redacted]"));
            Assert.That(transcripts.Single().UserMessage, Does.Not.Contain("user-secret"));
            Assert.That(transcripts.Single().AssistantMessage, Does.Contain("token=[redacted]"));
            Assert.That(transcripts.Single().AssistantMessage, Does.Not.Contain("agent-secret"));
            Assert.That(filtered.Single().Id, Is.EqualTo(transcripts.Single().Id));
        });
    }

    [Test]
    public async Task AgentRun_AcceptsFencedLlmReviewJson()
    {
        var provider = new FakeChatProvider("""
        ```json
        {
          "task": "llm_review",
          "findings": [],
          "proposals": [],
          "warnings": [],
          "confidence": 0.66,
          "metadata": { "source": "test" }
        }
        ```
        """);
        var agent = CreateReviewAgent(provider);

        var result = await agent.RunMaintenanceOnDemandAsync("staleness_scan", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Warnings, Has.None.Contains("LLM review skipped"));
            Assert.That(result.Outputs.Select(output => output.Task), Does.Contain("llm_review"));
            Assert.That(result.Outputs.Single(output => output.Task == "llm_review").Confidence, Is.EqualTo(0.66));
        });
    }

    [Test]
    public async Task AgentReview_AddsProviderFeedbackWithoutChangingProposalStatus()
    {
        var targetPath = Path.Combine(_tempDir, "Pages", "agent-review-note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, "before");
        var submitted = await _workflow.SubmitAsync(CreateProposal(targetPath, "before", "after"), CancellationToken.None);
        var provider = new FakeChatProvider("```json" + Environment.NewLine + JsonSerializer.Serialize(new
        {
            recommendation = "approve",
            comments = new[] { "The evidence and diff are consistent." },
            confidence = 0.84
        }) + Environment.NewLine + "```");
        var agent = CreateReviewAgent(provider);

        var result = await agent.ReviewProposalAsync(submitted.ProposalId, "Please review before approval.", CancellationToken.None);
        var refreshed = await _workflow.GetAsync(submitted.ProposalId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Warnings, Is.Empty);
            Assert.That(result.RevisedProposal, Is.Null);
            Assert.That(refreshed!.Status, Is.EqualTo(MaintenanceProposalStatuses.Open));
            Assert.That(refreshed.History.Select(item => item.Action), Does.Contain("agent_review_requested"));
            Assert.That(refreshed.History.Select(item => item.Action), Does.Contain("agent_review_completed"));
            Assert.That(refreshed.Comments.Select(item => item.Comment), Does.Contain("Please review before approval."));
            Assert.That(refreshed.Comments.Last().Comment, Does.Contain("The evidence and diff are consistent."));
            Assert.That(provider.LastRequest?.Model, Is.EqualTo("review-model"));
        });
    }

    [Test]
    public async Task AgentReview_WithValidRevisedProposal_PreservesOriginalAndSubmitsRevision()
    {
        var targetPath = Path.Combine(_tempDir, "Pages", "agent-revision-note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, "before");
        var submitted = await _workflow.SubmitAsync(CreateProposal(targetPath, "before", "after"), CancellationToken.None);
        var provider = new FakeChatProvider(JsonSerializer.Serialize(new
        {
            recommendation = "revise",
            comments = new[] { "Narrow the proposed text before approval." },
            confidence = 0.72,
            revisedProposal = new
            {
                proposal_id = "agent-revision-1",
                changes = new[] { new { path = targetPath, before = "before", after = "after revised" } },
                evidence = new[] { new { kind = "review", citation = "agent-review", reference = "test-memory", excerpt = "Narrowed wording." } },
                related_records = new[] { "test-memory" },
                risk_level = MaintenanceProposalRiskLevels.Low,
                confidence = 0.72,
                metadata = new
                {
                    task = "proposal_review",
                    confidence = 0.72,
                    risk_level = MaintenanceProposalRiskLevels.Low,
                    related_records = new[] { "test-memory" },
                    supersedes = Array.Empty<string>(),
                    superseded_by = Array.Empty<string>(),
                    agent_version = "test-agent"
                }
            }
        }));
        var agent = CreateReviewAgent(provider);

        var result = await agent.ReviewProposalAsync(submitted.ProposalId, null, CancellationToken.None);
        var proposals = await _workflow.ListAsync(CancellationToken.None);
        var original = proposals.Single(item => item.ProposalId == submitted.ProposalId);

        Assert.Multiple(() =>
        {
            Assert.That(result.RevisedProposal, Is.Not.Null);
            Assert.That(original.Status, Is.EqualTo(MaintenanceProposalStatuses.Open));
            Assert.That(original.Metadata.SupersededBy, Does.Contain(result.RevisedProposal!.ProposalId));
            Assert.That(original.History.Select(item => item.Action), Does.Contain("agent_revision_proposed"));
            Assert.That(result.RevisedProposal.Metadata.Supersedes, Does.Contain(original.ProposalId));
            Assert.That(result.RevisedProposal.Metadata.BatchId, Is.EqualTo(original.Metadata.BatchId));
            Assert.That(result.RevisedProposal.Metadata.ParentProposalId, Is.EqualTo(original.ProposalId));
            Assert.That(result.RevisedProposal.Metadata.Attempt, Is.EqualTo(2));
            Assert.That(result.RevisedProposal.History.Single(item => item.Action == "open").Comment, Does.Contain("Lineage: batchId="));
            Assert.That(result.RevisedProposal.Changes.Single().After, Is.EqualTo("after revised"));
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

    private MaintenanceAgentService CreateReviewAgent(FakeChatProvider provider, Action<MaintenanceAgentOptions>? configure = null, IChatAgent? chatAgent = null)
    {
        var maintenanceOptions = new MaintenanceAgentOptions
        {
            Read = [Path.Combine(_tempDir, "Memories"), Path.Combine(_tempDir, "Pages")],
            Write = [Path.Combine(_tempDir, "Memories", "Working"), Path.Combine(_tempDir, "Pages")],
            UseLlm = true,
            Provider = provider.Name,
            Model = "review-model",
            Storage = new MaintenanceAgentStorageOptions
            {
                ProposalsPath = Path.Combine(_tempDir, "Proposals"),
                TopicMapCachePath = Path.Combine(_tempDir, "Graph", "topic-map-cache.json"),
                LastRunPath = Path.Combine(_tempDir, "Events", "maintenance-agent-last-run.json"),
                ActivityLogPath = Path.Combine(_tempDir, "Events", "maintenance-agent-runs.jsonl"),
                TranscriptLogPath = Path.Combine(_tempDir, "Events", "maintenance-agent-transcript.jsonl")
            }
        };
        configure?.Invoke(maintenanceOptions);
        var config = new MaintenanceAgentConfigService(new StaticOptionsMonitor<MemorySmithOptions>(new MemorySmithOptions
        {
            MaintenanceAgent = maintenanceOptions
        }));
        return new MaintenanceAgentService(
            config,
            new MaintenanceResourceProbe(),
            new MaintenanceTopicMapService(_memoryStore, _pages, config),
            _workflow,
            [provider],
            NullLogger<MaintenanceAgentService>.Instance,
            chatAgent: chatAgent);
    }

    private sealed class FakeMaintenanceChatAgent(MemoryChatResponse response) : IChatAgent
    {
        public MemoryChatRequest? LastRequest { get; private set; }

        public Task<MemoryChatResponse> SendAsync(MemoryChatRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<MemoryChatStreamUpdate> StreamAsync(MemoryChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<AgentWriteApplyResult> ApplyAgentWritesAsync(
            IReadOnlyList<AgentMemoryWriteProposal> memoryWrites,
            IReadOnlyList<AgentPageWriteProposal> pageWrites,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AgentWriteApplyResult([], [], []));
    }

    private sealed class FakeChatProvider(string response) : IChatProvider
    {
        public string Name => "Ollama";
        public ChatProviderRequest? LastRequest { get; private set; }

        public Task<ChatProviderResponse> CompleteAsync(ChatProviderRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new ChatProviderResponse(response, Name, request.Model ?? "test-model"));
        }

        public async IAsyncEnumerable<ChatProviderChunk> StreamAsync(ChatProviderRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<ChatModelSummary>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChatModelSummary>>([]);
    }

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