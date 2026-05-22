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

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("Overall Mode"));
            Assert.That(markup, Does.Contain("Plain Tag Mode"));
            Assert.That(markup, Does.Contain("Namespaces"));
            Assert.That(markup, Does.Contain("Usage"));
            Assert.That(markup, Does.Contain("Suggestions"));
        });
    }

    [Test]
    public void AdminMarkup_LeavesTagManagerInMainNavAndExposesSettingHelpTooltips()
    {
        var markup = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Components", "Pages", "Admin.razor"));
        var navMarkup = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Components", "Layout", "NavMenu.razor"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Not.Contain("<MudTabPanel Text=\"Tags\""));
            Assert.That(markup, Does.Contain("<MudTabPanel Text=\"OAuth\""));
            Assert.That(markup, Does.Contain("<MudTabPanel Text=\"Models\""));
            Assert.That(markup, Does.Contain("ChatModelProfileService ModelProfiles"));
            Assert.That(markup, Does.Contain("Maintenance runs"));
            Assert.That(markup, Does.Contain("Proposal reviews"));
            Assert.That(markup, Does.Contain("Admin maintenance chat"));
            Assert.That(markup, Does.Contain("admin-setting-key"));
            Assert.That(markup, Does.Contain("Icons.Material.Filled.Info"));
            Assert.That(markup, Does.Contain("@context.Item.HelpText"));
                Assert.That(markup, Does.Contain("<MudTabPanel Text=\"Maintenance\""));
                Assert.That(markup, Does.Contain("MaintenanceAgentService Agent"));
                Assert.That(markup, Does.Contain("SendMaintenanceMessageAsync"));
                Assert.That(markup, Does.Contain("admin-maintenance-layout"));
                Assert.That(markup, Does.Contain("Search transcripts"));
                Assert.That(markup, Does.Contain("_maintenanceTranscriptSearch"));
            Assert.That(navMarkup, Does.Contain("Href=\"/tags\""));
            Assert.That(navMarkup, Does.Contain("Tags"));
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
            Assert.That(markup, Does.Contain("Recent task activity"));
            Assert.That(markup, Does.Contain("ListRecentActivityAsync"));
            Assert.That(markup, Does.Contain("SelectProposalByIdAsync"));
            Assert.That(markup, Does.Contain("maintenance-activity-proposals"));
            Assert.That(css, Does.Contain(".proposal-active-run"));
            Assert.That(css, Does.Contain(".maintenance-activity-panel"));
            Assert.That(css, Does.Contain(".maintenance-activity-proposals"));
            Assert.That(css, Does.Contain("grid-template-columns: minmax(260px, 31%) minmax(0, 1fr);"));
            Assert.That(css, Does.Contain("grid-template-columns: minmax(220px, 1fr) repeat(4, minmax(92px, max-content));"));
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