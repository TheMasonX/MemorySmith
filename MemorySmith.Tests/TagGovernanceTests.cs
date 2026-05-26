using System.Text.Json;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

[TestFixture]
public class TagGovernanceTests
{
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "MemorySmithTagGovernanceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Test]
    public void CreateDefault_MatchesRepositoryTagPolicy()
    {
        var root = FindRepositoryRoot();
        var expected = JsonSerializer.Deserialize<TagPolicy>(
            File.ReadAllText(Path.Combine(root, "Data", "Policies", "tag-policy.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var actual = TagPolicy.CreateDefault();

        Assert.That(expected, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(actual.Mode, Is.EqualTo(expected!.Mode));
            Assert.That(
                actual.Namespaces.Select(item => $"{item.Name}|{item.Cardinality}|{item.ValueKind}|{string.Join(',', item.AllowedValues)}"),
                Is.EqualTo(expected.Namespaces.Select(item => $"{item.Name}|{item.Cardinality}|{item.ValueKind}|{string.Join(',', item.AllowedValues)}")));
            Assert.That(actual.PlainTags.Mode, Is.EqualTo(expected.PlainTags.Mode));
            Assert.That(actual.PlainTags.Allowlist, Is.EquivalentTo(expected.PlainTags.Allowlist));
            Assert.That(actual.PlainTags.Blocklist, Is.EquivalentTo(expected.PlainTags.Blocklist));
            Assert.That(actual.PlainTags.Aliases.Count, Is.EqualTo(expected.PlainTags.Aliases.Count));
            foreach (var pair in expected.PlainTags.Aliases)
            {
                Assert.That(actual.PlainTags.Aliases.TryGetValue(pair.Key, out var value), Is.True, $"Missing alias '{pair.Key}'.");
                Assert.That(value, Is.EqualTo(pair.Value), $"Alias '{pair.Key}' points to the wrong canonical tag.");
            }
        });
    }

    [Test]
    public void GetSnapshot_ReturnsUsageCountsAndApprovalOnlyLexicalSuggestions()
    {
        var store = new InMemoryMemoryStore();
        store.Save(new MemoryRecord { Id = "one", Title = "One", Content = "Content", Tags = ["search", "general", "kind_rule:fact"] });
        store.Save(new MemoryRecord { Id = "two", Title = "Two", Content = "Content", Tags = ["Search", "semantic-search"] });
        store.Save(new MemoryRecord { Id = "three", Title = "Three", Content = "Content", Tags = ["sematic-search", "unknown-tag"] });
        var governance = CreateTagGovernanceService(store, new TagPolicy
        {
            Mode = "warn",
            Namespaces = [new TagNamespacePolicy { Name = "kind", Cardinality = "single", ValueKind = "enum", AllowedValues = ["fact"] }],
            PlainTags = new PlainTagPolicy
            {
                Mode = "allowWithSuggestions",
                Allowlist = ["search", "semantic-search"],
                Blocklist = ["general"],
                Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            }
        });

        var snapshot = governance.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Tags.Single(tag => tag.Tag.Equals("search", StringComparison.OrdinalIgnoreCase)).Count, Is.EqualTo(2));
            Assert.That(snapshot.Suggestions.Select(suggestion => suggestion.Kind), Does.Contain("casing-variant"));
            Assert.That(snapshot.Suggestions.Select(suggestion => suggestion.Kind), Does.Contain("near-duplicate"));
            Assert.That(snapshot.Suggestions.Select(suggestion => suggestion.Kind), Does.Contain("blocklist-candidate"));
            Assert.That(snapshot.Suggestions.Select(suggestion => suggestion.Kind), Does.Contain("namespace-candidate"));
            Assert.That(snapshot.Suggestions.Select(suggestion => suggestion.Kind), Does.Contain("allowlist-candidate"));
        });
    }

    [TestCase("observe", "Info")]
    [TestCase("warn", "Warning")]
    [TestCase("blockUnknown", "Error")]
    public void AnalyzeDraft_UsesPlainTagPolicyModeForUnknownTags(string mode, string expectedSeverity)
    {
        var governance = CreateTagGovernanceService(new InMemoryMemoryStore(), new TagPolicy
        {
            PlainTags = new PlainTagPolicy
            {
                Mode = mode,
                Allowlist = ["allowed"],
                Blocklist = [],
                Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            }
        });
        var record = new MemoryRecord { Id = "draft", Title = "Draft", Content = "Content", Tags = ["outside"] };

        var diagnostic = governance.AnalyzeDraft(record).Single(item => item.Code == "tag.unknown_plain");

        Assert.That(diagnostic.Severity, Is.EqualTo(expectedSeverity));
    }

    [Test]
    public void CreateAsync_WithBlockUnknownPlainTag_ThrowsValidationAndDoesNotPersist()
    {
        var store = new InMemoryMemoryStore();
        var eventStore = new RecordingEventStore();
        var publisher = new RecordingMemoryChangePublisher();
        var diagnostics = CreateDiagnosticsService(store, new TagPolicy
        {
            PlainTags = new PlainTagPolicy
            {
                Mode = "blockUnknown",
                Allowlist = ["allowed"],
                Blocklist = [],
                Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            }
        });
        var service = TestServiceFactory.CreateMemoryApplicationService(store, eventStore, publisher, diagnostics: diagnostics);
        var record = new MemoryRecord { Id = "blocked", Title = "Blocked", Content = "Content", Tags = ["outside"] };

        var exception = Assert.ThrowsAsync<MemoryValidationException>(async () =>
            await service.CreateAsync(record, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Errors.Keys, Does.Contain(nameof(MemoryRecord.Tags)));
            Assert.That(exception.Errors[nameof(MemoryRecord.Tags)].Single(), Does.Contain("not in the active allowlist"));
            Assert.That(store.LoadAll(), Is.Empty);
            Assert.That(eventStore.Events, Is.Empty);
        });
    }

    [Test]
    public void MemoryEditorMarkup_UsesTagChipsAutocompleteAndDraftDiagnostics()
    {
        var markup = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Components", "Pages", "MemoryViewer.razor"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("wiki-tag-editor"));
            Assert.That(markup, Does.Contain("_tagSuggestions"));
            Assert.That(markup, Does.Contain("RefreshDraftDiagnosticsAsync"));
            Assert.That(markup, Does.Contain("RemoveEditTagAsync"));
        });
    }

    [Test]
    public void TagManagerMarkup_ExposesPolicyEditingUsageAndSuggestionReview()
    {
        var markup = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Components", "Pages", "TagManager.razor"));
        var codeBehind = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Components", "Pages", "TagManager.razor.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("Overall Mode"));
            Assert.That(markup, Does.Contain("Plain Tag Mode"));
            Assert.That(markup, Does.Contain("Namespaces"));
            Assert.That(markup, Does.Contain("Usage"));
            Assert.That(markup, Does.Contain("Suggestions"));
            Assert.That(markup, Does.Contain("ApproveSuggestionAsync"));
            Assert.That(markup, Does.Contain("RejectSuggestionAsync"));
            Assert.That(markup, Does.Contain("tag-suggestion-actions"));
            Assert.That(markup, Does.Contain("<MudIconButton"));
            Assert.That(markup, Does.Contain("SuggestionDecisionTooltip"));
            Assert.That(codeBehind, Does.Contain("Reject suggestion and add"));
            Assert.That(codeBehind, Does.Contain("Approve suggestion and add"));
        });
    }

    [Test]
    public void AdminMarkup_LeavesTagManagerInMainNav_UsesIconActions_AndMovesVariablesIntoAdmin()
    {
        var markup = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Components", "Pages", "Admin.razor"));
        var navMarkup = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Components", "Layout", "NavMenu.razor"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Not.Contain("<MudTabPanel Text=\"Tags\""));
            Assert.That(markup, Does.Contain("<MudTabPanel Text=\"OAuth\""));
            Assert.That(markup, Does.Contain("<MudTabPanel Text=\"Models\""));
            Assert.That(markup, Does.Contain("ChatModelProfileService ModelProfiles"));
            Assert.That(markup, Does.Contain("IEnumerable<IChatProvider> ChatProviders"));
            Assert.That(markup, Does.Contain("OnModelProfileProviderChangedAsync"));
            Assert.That(markup, Does.Contain("_modelProfileModelOptions"));
            Assert.That(markup, Does.Contain("admin-model-select-field"));
            Assert.That(markup, Does.Contain("admin-row-actions"));
            Assert.That(markup, Does.Contain("DuplicateModelProfileAsync"));
            Assert.That(markup, Does.Contain("Icons.Material.Filled.ContentCopy"));
            Assert.That(markup, Does.Contain("Icons.Material.Filled.Edit"));
            Assert.That(markup, Does.Contain("Icons.Material.Filled.Delete"));
            Assert.That(markup, Does.Contain("Maintenance runs"));
            Assert.That(markup, Does.Contain("Proposal reviews"));
            Assert.That(markup, Does.Contain("Admin maintenance chat"));
            Assert.That(markup, Does.Contain("admin-setting-key"));
            Assert.That(markup, Does.Contain("Icons.Material.Filled.Info"));
            Assert.That(markup, Does.Contain("@context.Item.HelpText"));
            Assert.That(markup, Does.Contain("<MudTabPanel Text=\"Variables\""));
            Assert.That(markup, Does.Contain("SaveVariablesAsync"));
            Assert.That(markup, Does.Not.Contain("<MudTabPanel Text=\"Maintenance\""));
            Assert.That(markup, Does.Not.Contain("MaintenanceAgentService Agent"));
            Assert.That(markup, Does.Not.Contain("SendMaintenanceMessageAsync"));
            Assert.That(markup, Does.Not.Contain("admin-maintenance-layout"));
            Assert.That(markup, Does.Not.Contain("Search transcripts"));
            Assert.That(markup, Does.Not.Contain("_maintenanceTranscriptSearch"));
            Assert.That(markup, Does.Not.Contain("title=\"@context.Item.HelpText\""));
            Assert.That(navMarkup, Does.Contain("Href=\"/tags\""));
            Assert.That(navMarkup, Does.Contain("Tags"));
            Assert.That(navMarkup, Does.Contain("Href=\"/maintenance\""));
            Assert.That(navMarkup, Does.Contain("Maintenance"));
            Assert.That(navMarkup, Does.Not.Contain("Href=\"/variables\""));
        });
    }

    [Test]
    public void MaintenanceMarkup_ExposesStandaloneTraceChatAndActionHistoryPage()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "Maintenance.razor"));
        var css = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "wwwroot", "app.css"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("@page \"/maintenance\""));
            Assert.That(markup, Does.Contain("Task trace history"));
            Assert.That(markup, Does.Contain("Maintenance agent chat"));
            Assert.That(markup, Does.Contain("Chat history"));
            Assert.That(markup, Does.Contain("Proposal action history"));
            Assert.That(markup, Does.Contain("ListRecentActivityAsync(50"));
            Assert.That(markup, Does.Contain("ListRecentTranscriptsAsync(50"));
            Assert.That(markup, Does.Contain("ProposalActionRows"));
            Assert.That(css, Does.Contain(".maintenance-body"));
            Assert.That(css, Does.Contain(".maintenance-action-row"));
            Assert.That(css, Does.Contain(".maintenance-active-run"));
        });
    }

    [Test]
    public void AdminMarkup_UsesSortableAuditAndHistoryTablesWithoutRevealIcons()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "Admin.razor"));
        var css = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "wwwroot", "app.css"));

        var auditStart = markup.IndexOf("<MudTabPanel Text=\"Audit\"", StringComparison.Ordinal);
        var historyStart = markup.IndexOf("<MudTabPanel Text=\"History\"", StringComparison.Ordinal);

        Assert.That(auditStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(historyStart, Is.GreaterThan(auditStart));

        var auditMarkup = markup[auditStart..historyStart];
        var historyMarkup = markup[historyStart..];

        Assert.Multiple(() =>
        {
            Assert.That(auditMarkup, Does.Contain("Label=\"Sort audit by\""));
            Assert.That(historyMarkup, Does.Contain("Label=\"Sort history by\""));
            Assert.That(auditMarkup, Does.Contain("RowsPerPage=\"25\""));
            Assert.That(historyMarkup, Does.Contain("RowsPerPage=\"25\""));
            Assert.That(auditMarkup, Does.Contain("MudTablePager"));
            Assert.That(historyMarkup, Does.Contain("MudTablePager"));
            Assert.That(auditMarkup, Does.Not.Contain("<SensitiveValue"));
            Assert.That(historyMarkup, Does.Not.Contain("<SensitiveValue"));
            Assert.That(markup, Does.Contain("CopyAdminValueAsync"));
            Assert.That(css, Does.Contain(".admin-grid-table .mud-table-cell"));
            Assert.That(css, Does.Contain(".admin-copy-cell"));
        });
    }

    [Test]
    public void AdminMarkup_ExposesExplicitSensitiveSettingClearFlow()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "Admin.razor"));
        var css = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "wwwroot", "app.css"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("Clear secret"));
            Assert.That(markup, Does.Contain("ClearSensitiveSettingAsync"));
            Assert.That(markup, Does.Contain("CanClearSensitiveSetting"));
            Assert.That(markup, Does.Contain("ConfirmDestructiveActionAsync"));
            Assert.That(css, Does.Contain(".admin-setting-actions"));
        });
    }

    [Test]
    public void ChatMarkup_UsesAdminDefinedModelProfilesForSelection()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "Chat.razor"));
        var css = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "wwwroot", "app.css"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("ChatModelProfileService ModelProfiles"));
            Assert.That(markup, Does.Contain("chat-model-profile-select"));
            Assert.That(markup, Does.Contain("ModelSetupMessage"));
            Assert.That(markup, Does.Contain("CanSendChat"));
            Assert.That(markup, Does.Contain("ModelProfileId"));
            Assert.That(markup, Does.Not.Contain("chat-provider-select"));
            Assert.That(markup, Does.Not.Contain("chat-model-select"));
            Assert.That(css, Does.Contain(".chat-model-profile-select"));
        });
    }

    [Test]
    public void AdminSettings_ExposeSeparateChatAgentWriteRoots()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Services", "AdminSettingsService.cs"));
        var appsettings = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "appsettings.json"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("MemorySmith:Chat:AgentWriteRoots"));
            Assert.That(source, Does.Contain("Chat proposal write roots"));
            Assert.That(source, Does.Contain("intentionally separate from MaintenanceAgent:Write"));
            Assert.That(appsettings, Does.Contain("\"AgentWriteRoots\""));
            Assert.That(appsettings, Does.Contain("../Data/Memories/Working"));
            Assert.That(appsettings, Does.Contain("../Data/Pages"));
        });
    }

    [Test]
    public void AdminSettings_ExposeSecurityProfilePreset()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Services", "AdminSettingsService.cs"));
        var appsettings = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "appsettings.json"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("MemorySmith:SecurityProfile"));
            Assert.That(source, Does.Contain("Security profile"));
            Assert.That(source, Does.Contain("remote-hardened"));
            Assert.That(source, Does.Contain("secure-local for dogfood"));
            Assert.That(appsettings, Does.Contain("\"SecurityProfile\": null"));
        });
    }

    [Test]
    public void AdminSettings_ExposeProposalActionUxSettings()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Services", "AdminSettingsService.cs"));
        var appsettings = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "appsettings.json"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("MemorySmith:MaintenanceAgent:ActionUx:ShowAccept"));
            Assert.That(source, Does.Contain("MemorySmith:MaintenanceAgent:ActionUx:ShowRespond"));
            Assert.That(source, Does.Contain("MemorySmith:MaintenanceAgent:ActionUx:ShowReject"));
            Assert.That(source, Does.Contain("MemorySmith:MaintenanceAgent:ActionUx:DefaultAction"));
            Assert.That(source, Does.Contain("MemorySmith:MaintenanceAgent:ActionUx:RevisionRequired"));
            Assert.That(source, Does.Contain("Proposal default action"));
            Assert.That(source, Does.Contain("Revision required before accept"));
            Assert.That(appsettings, Does.Contain("\"ActionUx\""));
            Assert.That(appsettings, Does.Contain("\"DefaultAction\": \"accept\""));
            Assert.That(appsettings, Does.Contain("\"RevisionRequired\": true"));
        });
    }

    [Test]
    public void ChatMarkup_ReconcilesApproveAllBatchOutcomesAndPendingState()
    {
        var markup = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Components", "Pages", "Chat.razor"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("Approve all results"));
            Assert.That(markup, Does.Contain("RemoveAttemptedProposals(turn, memories, pages);"));
            Assert.That(markup, Does.Contain("-> approved (submitted"));
            Assert.That(markup, Does.Contain("lineage: batchId="));
            Assert.That(markup, Does.Contain("-> rejected (no changes needed)"));
            Assert.That(markup, Does.Contain("IsBlockedApprovalException(ex) ? \"blocked\" : \"failed\""));
            Assert.That(markup, Does.Contain("PendingWriteCount(ChatSessionState session)"));
            Assert.That(markup, Does.Contain("UpdatePendingWriteStatus(ActiveSession"));
            Assert.That(markup, Does.Contain("UpdatePendingWriteStatus(active, \"Ready\")"));
            Assert.That(markup, Does.Contain("UpdatePendingWriteStatus(session, _mode == MemoryChatMode.Agent ? \"Agent ready\" : \"Chat ready\")"));
            Assert.That(markup, Does.Contain("var pendingWriteCount = PendingWriteCount(ActiveSession);"));
        });
    }

    [Test]
    public void ProposalsMarkup_ShowsActiveRunAndKeepsActionBarHorizontalAtDesktopScale()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "Proposals.razor"));
        var css = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "wwwroot", "app.css"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("proposal-active-run"));
            Assert.That(markup, Does.Contain("BeginRunAsync"));
            Assert.That(markup, Does.Contain("GetActiveRun"));
            Assert.That(markup, Does.Contain("ActiveRunDetail"));
            Assert.That(markup, Does.Contain("Task.Yield"));
            Assert.That(markup, Does.Contain("Request Agent Review"));
            Assert.That(markup, Does.Contain("RequestAgentReviewAsync"));
            Assert.That(markup, Does.Contain("Quick summary"));
            Assert.That(markup, Does.Contain("ProposalQuickSummary"));
            Assert.That(markup, Does.Contain("SummarizeChange"));
            Assert.That(markup, Does.Contain("LineageValue(_selectedProposal.Metadata.BatchId)"));
            Assert.That(markup, Does.Contain("LineageValue(_selectedProposal.Metadata.ParentProposalId)"));
            Assert.That(markup, Does.Contain("LineageAttempt(_selectedProposal.Metadata.Attempt)"));
            Assert.That(markup, Does.Not.Contain("Approval applies the diff to disk; use Respond"));
            Assert.That(markup, Does.Contain("proposal-comment-row"));
            Assert.That(markup, Does.Contain("proposal-action-row"));
            Assert.That(markup, Does.Contain("IOptionsMonitor<MemorySmithOptions> Options"));
            Assert.That(markup, Does.Contain("MaintenanceProposalActionUx.Accept"));
            Assert.That(markup, Does.Contain("CanAccept(_selectedProposal)"));
            Assert.That(markup, Does.Contain(">Accept</MudButton>"));
            Assert.That(markup, Does.Contain("Recent task activity"));
            Assert.That(markup, Does.Contain("ListRecentActivityAsync"));
            Assert.That(markup, Does.Contain("SelectProposalByIdAsync"));
            Assert.That(markup, Does.Contain("maintenance-activity-proposals"));
            Assert.That(css, Does.Contain(".proposal-active-run"));
            Assert.That(css, Does.Contain(".proposal-human-summary"));
            Assert.That(css, Does.Contain(".proposal-action-row"));
            Assert.That(css, Does.Contain(".maintenance-activity-panel"));
            Assert.That(css, Does.Contain(".maintenance-activity-proposals"));
            Assert.That(css, Does.Contain("grid-template-columns: minmax(260px, 31%) minmax(0, 1fr);"));
            Assert.That(css, Does.Contain("@media (max-width: 700px)"));
        });
    }

    private TagGovernanceService CreateTagGovernanceService(IMemoryStore store, TagPolicy policy)
    {
        var diagnostics = CreateDiagnosticsService(store, policy, out var policyService);
        return new TagGovernanceService(policyService, diagnostics, store);
    }

    private MemoryDiagnosticsService CreateDiagnosticsService(IMemoryStore store, TagPolicy policy) =>
        CreateDiagnosticsService(store, policy, out _);

    private MemoryDiagnosticsService CreateDiagnosticsService(IMemoryStore store, TagPolicy policy, out TagPolicyService policyService)
    {
        var options = CreateOptions();
        policyService = new TagPolicyService(options);
        policyService.SavePolicy(policy);
        return new MemoryDiagnosticsService(policyService, new VarResolver(new EmptyVarStore(), options), store, options);
    }

    private IOptions<MemorySmithOptions> CreateOptions() => Options.Create(new MemorySmithOptions
    {
        Governance = new GovernanceOptions
        {
            TagPolicyPath = Path.Combine(_tempRoot, "Policies", "tag-policy.json")
        }
    });

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MemorySmith.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate MemorySmith.slnx from the test output directory.");
    }
}