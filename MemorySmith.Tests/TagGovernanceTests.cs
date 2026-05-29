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
    public void MemoryViewerMarkup_CollapsesSecondaryFiltersAndRelatedContextByDefault()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "MemoryViewer.razor"));
        var css = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "wwwroot", "app.css"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("wiki-commandbar-main"));
            Assert.That(markup, Does.Contain("wiki-secondary-filters"));
            Assert.That(markup, Does.Contain("Search options"));
            Assert.That(markup, Does.Contain("SecondaryFiltersSummary"));
            Assert.That(markup, Does.Contain("wiki-related-panel"));
            Assert.That(markup, Does.Contain("Related context"));
            Assert.That(markup, Does.Contain("RelatedContextSummary(_selectedRecord)"));
            Assert.That(css, Does.Contain(".wiki-secondary-filters"));
            Assert.That(css, Does.Contain(".wiki-secondary-filters-body"));
            Assert.That(css, Does.Contain(".wiki-related-panel"));
            Assert.That(css, Does.Contain(".wiki-related-panel[open] > .wiki-related-body"));
            Assert.That(css, Does.Not.Contain("max-height: 32%;"));
        });
    }

    [Test]
    public void PagesMarkup_RebalancesNavigationAndReadingSpaceOnNarrowViewports()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "Pages.razor"));
        var css = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "wwwroot", "app.css"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("SetNavigationModeAsync(TreeMode)"));
            Assert.That(markup, Does.Contain("SetNavigationModeAsync(FlatMode)"));
            Assert.That(markup, Does.Contain("SetNavigationModeAsync(TocMode)"));
            Assert.That(markup, Does.Contain("ToggleNavigationVisibilityAsync"));
            Assert.That(markup, Does.Contain("Toggle focus reading"));
            Assert.That(markup, Does.Contain("PagesBodyClass"));
            Assert.That(css, Does.Contain(".pages-body"));
            Assert.That(css, Does.Contain("grid-template-columns: minmax(272px, 30%) minmax(0, 1fr);"));
            Assert.That(css, Does.Contain(".pages-tree-row"));
            Assert.That(css, Does.Contain("min-height: 32px;"));
            Assert.That(css, Does.Contain(".pages-tree-folder .wiki-count"));
            Assert.That(css, Does.Contain("grid-template-rows: minmax(72px, 22%) minmax(0, 1fr);"));
            Assert.That(css, Does.Contain(".pages-navigation-pane .wiki-pane-header"));
            Assert.That(css, Does.Contain(".page-rendered pre"));
            Assert.That(css, Does.Contain("white-space: pre-wrap;"));
            Assert.That(css, Does.Contain("overflow-wrap: anywhere;"));
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
            Assert.That(markup, Does.Not.Contain("<MudTabPanel Text=\"Training\""));
            Assert.That(markup, Does.Contain("<MudTabPanel Text=\"OAuth\""));
            Assert.That(markup, Does.Contain("<MudTabPanel Text=\"Models\""));
            Assert.That(markup, Does.Contain("ChatModelProfileService ModelProfiles"));
            Assert.That(markup, Does.Contain("IEnumerable<IChatProvider> ChatProviders"));
            Assert.That(markup, Does.Contain("OnModelProfileProviderChangedAsync"));
            Assert.That(markup, Does.Contain("OnContextPresetChangedAsync"));
            Assert.That(markup, Does.Contain("_modelProfileModelOptions"));
            Assert.That(markup, Does.Contain("admin-model-select-field"));
            Assert.That(markup, Does.Contain("Context presets"));
            Assert.That(markup, Does.Contain("ModelVramEstimateHint"));
            Assert.That(markup, Does.Contain("Estimated VRAM envelope"));
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
            Assert.That(markup, Does.Contain("admin-users-table"));
            Assert.That(CountOccurrences(markup, "AllowReveal=\"false\""), Is.EqualTo(3));
            Assert.That(markup, Does.Contain("DataLabel=\"User\""));
            Assert.That(markup, Does.Contain("DataLabel=\"Last login\""));
            Assert.That(markup, Does.Contain("admin-user-primary"));
            Assert.That(markup, Does.Contain("admin-user-action-cell"));
            Assert.That(markup, Does.Contain("admin-config-summary"));
            Assert.That(markup, Does.Contain("admin-settings-nav"));
            Assert.That(markup, Does.Contain("_settingsDirtyOnly"));
            Assert.That(markup, Does.Contain("admin-setting-dirty-indicator"));
            Assert.That(markup, Does.Contain("SaveVisibleDirtySettingsAsync"));
            Assert.That(markup, Does.Contain("Save All Changes"));
            Assert.That(markup, Does.Contain("Config Import/Export"));
            Assert.That(markup, Does.Contain("ExportVisibleSettingsAsync"));
            Assert.That(markup, Does.Contain("ApplyImportedSettingsToVisibleAsync"));
            Assert.That(markup, Does.Contain("DataLabel=\"Setting\""));
            Assert.That(markup, Does.Contain("ResetFilteredSettings"));
            Assert.That(navMarkup, Does.Contain("Href=\"/tags\""));
            Assert.That(navMarkup, Does.Contain("Tags"));
            Assert.That(navMarkup, Does.Contain("Href=\"/code-search\""));
            Assert.That(navMarkup, Does.Contain("Code Search"));
            Assert.That(navMarkup, Does.Contain("Href=\"/models\""));
            Assert.That(navMarkup, Does.Contain("Models"));
            Assert.That(navMarkup, Does.Contain("Href=\"/training-workbench\""));
            Assert.That(navMarkup, Does.Contain("Training"));
            Assert.That(navMarkup, Does.Contain("Href=\"/maintenance\""));
            Assert.That(navMarkup, Does.Contain("Maintenance"));
            Assert.That(navMarkup, Does.Not.Contain("Href=\"/variables\""));
        });
    }

    [Test]
    public void CodeSearchAndTrainingWorkbenchMarkup_ExposeActionableCopyAndOpenControls()
    {
        var root = FindRepositoryRoot();
        var codeSearchMarkup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "CodeSearch.razor"));
        var trainingMarkup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "TrainingWorkbench.razor"));

        Assert.Multiple(() =>
        {
            Assert.That(codeSearchMarkup, Does.Contain("Copy file and line range"));
            Assert.That(codeSearchMarkup, Does.Contain("Copy snippet text"));
            Assert.That(codeSearchMarkup, Does.Contain("Operator cap"));
            Assert.That(codeSearchMarkup, Does.Contain("Open file in default app"));
            Assert.That(codeSearchMarkup, Does.Contain("CopyResultLocationAsync"));
            Assert.That(codeSearchMarkup, Does.Contain("OpenResultAsync"));
            Assert.That(codeSearchMarkup, Does.Contain("Icons.Material.Filled.OpenInNew"));
            Assert.That(trainingMarkup, Does.Contain("Training Settings Proxy"));
            Assert.That(trainingMarkup, Does.Contain("MudAutocomplete T=\"string\""));
            Assert.That(trainingMarkup, Does.Contain("SaveSelectedTrainingSettingAsync"));
            Assert.That(trainingMarkup, Does.Contain("ExportTrainingSettingsAsync"));
            Assert.That(trainingMarkup, Does.Contain("Training Deps"));
            Assert.That(trainingMarkup, Does.Contain("simulated mode"));
            Assert.That(trainingMarkup, Does.Contain("Copy status.json path"));
            Assert.That(trainingMarkup, Does.Contain("Open events.jsonl in default app"));
            Assert.That(trainingMarkup, Does.Contain("Open benchmark.json in default app"));
            Assert.That(trainingMarkup, Does.Contain("CopyTextAsync"));
            Assert.That(trainingMarkup, Does.Contain("OpenArtifactAsync"));
        });
    }

    [Test]
    public void AuthProviderSurfaces_UseRuntimeSchemeSupportForAvailability()
    {
        var root = FindRepositoryRoot();
        var adminMarkup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "Admin.razor"));
        var profileMarkup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "Profile.razor"));
        var authController = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Controllers", "AuthController.cs"));
        var securitySource = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Services", "SecurityServices.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(adminMarkup, Does.Contain("@inject IAuthenticationSchemeProvider SchemeProvider"));
            Assert.That(adminMarkup, Does.Contain("MemorySmithExternalAuthSupport.GetSupportedExternalProvidersAsync"));
            Assert.That(adminMarkup, Does.Contain("Configured in settings, but no runtime auth handler is registered"));
            Assert.That(profileMarkup, Does.Contain("@inject IAuthenticationSchemeProvider SchemeProvider"));
            Assert.That(profileMarkup, Does.Contain("IsConfiguredRuntimeExternalProvider"));
            Assert.That(authController, Does.Contain("MemorySmithExternalAuthSupport.IsConfiguredExternalProvider"));
            Assert.That(securitySource, Does.Contain("MemorySmithExternalAuthSupport.GetSupportedExternalProvidersAsync"));
            Assert.That(securitySource, Does.Contain("MemorySmithExternalAuthSupport.IsRuntimeSupportedExternalProvider"));
        });
    }

    [Test]
    public void ExternalAuthCallbacks_RecordDurableSuccessAndFailureEvidence()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Program.cs"));
        var securitySource = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Services", "SecurityServices.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(program, Does.Contain("ExternalAuthOutcomeRecorder"));
            Assert.That(program, Does.Contain("RecordSuccessAsync"));
            Assert.That(program, Does.Contain("RecordFailureIfNeededAsync"));
            Assert.That(program, Does.Contain("OnRemoteFailure = async ctx =>"));
            Assert.That(securitySource, Does.Contain("RecordWithActorAsync"));
            Assert.That(securitySource, Does.Contain("FailurePersistedKey"));
            Assert.That(securitySource, Does.Contain("auth.login.succeeded"));
            Assert.That(securitySource, Does.Contain("auth.login.failed"));
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
            Assert.That(markup, Does.Contain("maintenance-panel-scroll maintenance-panel-scroll-trace"));
            Assert.That(markup, Does.Contain("maintenance-proposal-link"));
            Assert.That(markup, Does.Contain("MudTooltip Text=\"@proposalId\""));
            Assert.That(css, Does.Contain(".maintenance-body"));
            Assert.That(css, Does.Contain(".maintenance-panel-scroll"));
            Assert.That(css, Does.Contain(".maintenance-proposal-link"));
            Assert.That(css, Does.Contain(".maintenance-action-row"));
            Assert.That(css, Does.Contain(".maintenance-active-run"));
        });
    }

    [Test]
    public void TasksMarkup_PrioritizesSelectedTaskSummaryAndFocusAction()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "Tasks.razor"));
        var css = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "wwwroot", "app.css"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("tasks-detail-summary"));
            Assert.That(markup, Does.Contain("tasks-detail-actions"));
            Assert.That(markup, Does.Contain("tasks-header-icon-action"));
            Assert.That(markup, Does.Contain("TaskListHeaderActionText"));
            Assert.That(markup, Does.Contain("tasks-detail-overview"));
            Assert.That(markup, Does.Contain("tasks-edit-shell"));
            Assert.That(markup, Does.Contain("tasks-read-shell"));
            Assert.That(css, Does.Contain(".tasks-detail-summary"));
            Assert.That(css, Does.Contain(".tasks-detail-actions"));
            Assert.That(css, Does.Contain(".tasks-header-icon-action"));
            Assert.That(css, Does.Contain(".tasks-detail-overview"));
            Assert.That(css, Does.Contain(".tasks-edit-shell"));
            Assert.That(css, Does.Contain(".tasks-detail-description"));
        });
    }

    [Test]
    public void TasksMarkup_UnifiesArtifactsAndShareableDeepLinks()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "Tasks.razor"));
        var css = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "wwwroot", "app.css"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("[SupplyParameterFromQuery(Name = \"task\")]"));
            Assert.That(markup, Does.Contain("CopyTaskLinkAsync"));
            Assert.That(markup, Does.Contain("SearchRelatedPagesAsync"));
            Assert.That(markup, Does.Contain("<MudTabPanel Text=\"Artifacts\""));
            Assert.That(markup, Does.Not.Contain("<MudTabPanel Text=\"Links\""));
            Assert.That(markup, Does.Not.Contain("<MudTabPanel Text=\"Attachments\""));
            Assert.That(markup, Does.Contain("Legacy External Links"));
            Assert.That(css, Does.Contain(".tasks-artifact-section"));
            Assert.That(css, Does.Contain(".tasks-artifacts-grid"));
        });
    }

    [Test]
    public void WorkbenchMarkup_ExposesFullTitlesForClippedPageTaskAndMemoryRows()
    {
        var root = FindRepositoryRoot();
        var pagesMarkup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "Pages.razor"));
        var tasksMarkup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "Tasks.razor"));
        var memoriesMarkup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "MemoryViewer.razor"));

        Assert.Multiple(() =>
        {
            Assert.That(pagesMarkup, Does.Contain("title=\"@row.Node.Label\""));
            Assert.That(pagesMarkup, Does.Contain("<div class=\"wiki-result-title\" title=\"@summary.Title\">"));
            Assert.That(tasksMarkup, Does.Contain("title=\"@($\"{task.Key} - {task.Title}\")\""));
            Assert.That(memoriesMarkup, Does.Contain("<div class=\"wiki-result-title\" title=\"@(string.IsNullOrWhiteSpace(memory.Title) ? \"(untitled)\" : memory.Title)\">"));
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
            Assert.That(auditMarkup, Does.Contain("Breakpoint=\"Breakpoint.Sm\""));
            Assert.That(historyMarkup, Does.Contain("Breakpoint=\"Breakpoint.Sm\""));
            Assert.That(auditMarkup, Does.Contain("DataLabel=\"Time\""));
            Assert.That(historyMarkup, Does.Contain("DataLabel=\"Artifact\""));
            Assert.That(auditMarkup, Does.Contain("MudTablePager"));
            Assert.That(historyMarkup, Does.Contain("MudTablePager"));
            Assert.That(auditMarkup, Does.Not.Contain("<SensitiveValue"));
            Assert.That(historyMarkup, Does.Not.Contain("<SensitiveValue"));
            Assert.That(markup, Does.Contain("Active admin section"));
            Assert.That(markup, Does.Contain("ActiveAdminSectionTitle"));
            Assert.That(markup, Does.Contain("CopyAdminValueAsync"));
            Assert.That(css, Does.Contain(".admin-grid-table .mud-table-cell"));
            Assert.That(css, Does.Contain(".admin-section-summary"));
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
    public void SensitiveValueMarkup_RevealsWithoutRenderingHideActions()
    {
        var markup = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Components", "SensitiveValue.razor"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("[Parameter] public bool AllowReveal { get; set; } = true;"));
            Assert.That(markup, Does.Contain("@if (AllowReveal && !_isRevealed)"));
            Assert.That(markup, Does.Contain("private void Reveal()"));
            Assert.That(markup, Does.Not.Contain("VisibilityOff"));
            Assert.That(markup, Does.Not.Contain("ToggleReveal"));
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
            Assert.That(source, Does.Contain("MemorySmith:ContentSecurityPolicyEnabled"));
            Assert.That(source, Does.Contain("MemorySmith:ContentSecurityPolicy"));
            Assert.That(source, Does.Contain("MemorySmith:XContentTypeOptionsEnabled"));
            Assert.That(source, Does.Contain("MemorySmith:XContentTypeOptions"));
            Assert.That(source, Does.Contain("MemorySmith:ReferrerPolicyEnabled"));
            Assert.That(source, Does.Contain("MemorySmith:ReferrerPolicy"));
            Assert.That(source, Does.Contain("MemorySmith:XFrameOptionsEnabled"));
            Assert.That(source, Does.Contain("MemorySmith:XFrameOptions"));
            Assert.That(source, Does.Contain("MemorySmith:PermissionsPolicyEnabled"));
            Assert.That(source, Does.Contain("MemorySmith:PermissionsPolicy"));
            Assert.That(appsettings, Does.Contain("\"SecurityProfile\": null"));
            Assert.That(appsettings, Does.Contain("\"ContentSecurityPolicyEnabled\": true"));
            Assert.That(appsettings, Does.Contain("\"XContentTypeOptionsEnabled\": true"));
            Assert.That(appsettings, Does.Contain("\"XContentTypeOptions\": \"nosniff\""));
            Assert.That(appsettings, Does.Contain("\"ReferrerPolicyEnabled\": true"));
            Assert.That(appsettings, Does.Contain("\"ReferrerPolicy\": \"strict-origin-when-cross-origin\""));
            Assert.That(appsettings, Does.Contain("\"XFrameOptionsEnabled\": true"));
            Assert.That(appsettings, Does.Contain("\"XFrameOptions\": \"DENY\""));
            Assert.That(appsettings, Does.Contain("\"PermissionsPolicyEnabled\": true"));
            Assert.That(appsettings, Does.Contain("\"PermissionsPolicy\":"));
        });
    }

    [Test]
    public void AdminSettings_ExposeMermaidPolicyControls()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Services", "AdminSettingsService.cs"));
        var appsettings = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "appsettings.json"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("MemorySmith:Markdown:MermaidEnabled"));
            Assert.That(source, Does.Contain("MemorySmith:Markdown:MermaidRestrictionMode"));
            Assert.That(source, Does.Contain("Mermaid restriction mode"));
            Assert.That(appsettings, Does.Contain("\"Markdown\""));
            Assert.That(appsettings, Does.Contain("\"MermaidEnabled\": true"));
            Assert.That(appsettings, Does.Contain("\"MermaidRestrictionMode\": \"restricted\""));
        });
    }

    [Test]
    public void AdminSettings_ExposeTrainingHarnessControls()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Services", "AdminSettingsService.cs"));
        var appsettings = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "appsettings.json"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("MemorySmith:Training:ChatTranscriptEnabled"));
            Assert.That(source, Does.Contain("MemorySmith:Training:StoreChatContent"));
            Assert.That(source, Does.Contain("MemorySmith:Training:TranscriptRetentionDays"));
            Assert.That(source, Does.Contain("MemorySmith:Training:TranscriptRedactionEnabled"));
            Assert.That(source, Does.Contain("MemorySmith:Training:FeedbackEnabled"));
            Assert.That(source, Does.Contain("MemorySmith:Training:PreferenceFormat"));
            Assert.That(appsettings, Does.Contain("\"Training\""));
            Assert.That(appsettings, Does.Contain("\"ChatTranscriptEnabled\": false"));
            Assert.That(appsettings, Does.Contain("\"TranscriptRetentionDays\": 90"));
            Assert.That(appsettings, Does.Contain("\"TranscriptRedactionEnabled\": true"));
            Assert.That(appsettings, Does.Contain("\"FeedbackEnabled\": false"));
        });
    }

    [Test]
    public void TrainingBootstrapScripts_AndDocs_ExposeDedicatedScratchWorkflow()
    {
        var root = FindRepositoryRoot();
        var runbook = File.ReadAllText(Path.Combine(root, "Data", "Pages", "guides", "local-finetune-harness-runbook.md"));
        var configReference = File.ReadAllText(Path.Combine(root, "Data", "Pages", "guides", "configuration-reference.md"));
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var setupScript = Path.Combine(root, "Scripts", "Setup-FinetuneTrainingEnv.ps1");
        var bashScript = Path.Combine(root, "Scripts", "setup-finetune-training-env.sh");
        var requirements = Path.Combine(root, "Scripts", "training", "requirements-training.txt");
        var unslothRequirements = Path.Combine(root, "Scripts", "training", "requirements-training-unsloth.txt");

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(setupScript), Is.True);
            Assert.That(File.Exists(bashScript), Is.True);
            Assert.That(File.Exists(requirements), Is.True);
            Assert.That(File.Exists(unslothRequirements), Is.True);
            Assert.That(runbook, Does.Contain("Setup-FinetuneTrainingEnv.ps1"));
            Assert.That(runbook, Does.Contain("D:\\temp\\memorysmith-training"));
            Assert.That(runbook, Does.Contain("IncludeUnsloth"));
            Assert.That(runbook, Does.Contain("TrainMode"));
            Assert.That(runbook, Does.Contain("RequireTrainingDependencies"));
            Assert.That(configReference, Does.Contain("Scripts/Setup-FinetuneTrainingEnv.ps1"));
            Assert.That(configReference, Does.Contain("MemorySmith__SettingsOverridePath"));
            Assert.That(configReference, Does.Contain("accelerator readiness"));
            Assert.That(configReference, Does.Contain("-TrainMode auto|simulated|lora"));
            Assert.That(readme, Does.Contain("Local Fine-Tune Bootstrap"));
            Assert.That(readme, Does.Contain("core GPU-capable training stack"));
            Assert.That(readme, Does.Contain("-TrainMode auto|simulated|lora"));
            Assert.That(readme, Does.Contain("RequireTrainingDependencies"));
            Assert.That(readme, Does.Contain("Run-FinetuneHarness.ps1 -RunId ft-smoke -TrainMode auto -RequireTrainingDependencies"));
        });
    }

    [Test]
    public void AdminSettings_ExposeClipboardExternalFetchPolicyControl()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Services", "AdminSettingsService.cs"));
        var appsettings = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "appsettings.json"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("MemorySmith:Chat:ClipboardFetchExternalImagesEnabled"));
            Assert.That(source, Does.Contain("Fetch external clipboard image URLs"));
            Assert.That(source, Does.Contain("avoid unprompted network fetches during paste"));
            Assert.That(appsettings, Does.Contain("\"ClipboardFetchExternalImagesEnabled\": false"));
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
    public void AdminSettings_ExposeCodeSearchRankingTuningKnobs()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Services", "AdminSettingsService.cs"));
        var appsettings = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "appsettings.json"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("MemorySmith:CodeSearch:HybridVectorWeight"));
            Assert.That(source, Does.Contain("MemorySmith:CodeSearch:HybridLexicalWeight"));
            Assert.That(source, Does.Contain("MemorySmith:CodeSearch:ZeroLexicalEvidencePenalty"));
            Assert.That(source, Does.Contain("MemorySmith:CodeSearch:LexicalScoreSaturation"));
            Assert.That(source, Does.Contain("MemorySmith:CodeSearch:LexicalFrequencyBonusScale"));
            Assert.That(source, Does.Contain("MemorySmith:CodeSearch:MaxLexicalFrequencyBonusPerToken"));
            Assert.That(source, Does.Contain("MemorySmith:CodeSearch:MinTokenCoverageWeight"));
            Assert.That(source, Does.Contain("MemorySmith:CodeSearch:MaxTokenCoverageWeight"));
            Assert.That(source, Does.Contain("MemorySmith:CodeSearch:VectorPrefilterFullScanFallbackCandidateCount"));
            Assert.That(source, Does.Contain("Code-search min token coverage weight"));
            Assert.That(source, Does.Contain("Code-search max token coverage weight"));
            Assert.That(source, Does.Contain("Code-search sparse prefilter fallback candidate count"));

            Assert.That(appsettings, Does.Contain("\"HybridVectorWeight\": 0.75"));
            Assert.That(appsettings, Does.Contain("\"HybridLexicalWeight\": 0.25"));
            Assert.That(appsettings, Does.Contain("\"ZeroLexicalEvidencePenalty\": 0.72"));
            Assert.That(appsettings, Does.Contain("\"LexicalScoreSaturation\": 4.0"));
            Assert.That(appsettings, Does.Contain("\"LexicalFrequencyBonusScale\": 0.1"));
            Assert.That(appsettings, Does.Contain("\"MaxLexicalFrequencyBonusPerToken\": 0.35"));
            Assert.That(appsettings, Does.Contain("\"MinTokenCoverageWeight\": 0.65"));
            Assert.That(appsettings, Does.Contain("\"MaxTokenCoverageWeight\": 1.15"));
            Assert.That(appsettings, Does.Contain("\"VectorPrefilterFullScanFallbackCandidateCount\": 24"));
        });
    }

    [Test]
    public void ChatMarkup_ReconcilesApproveAllBatchOutcomesAndPendingState()
    {
        var markup = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Components", "Pages", "Chat.razor"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("Accept all results"));
            Assert.That(markup, Does.Contain("RemoveAttemptedProposals(turn, memories, pages);"));
            Assert.That(markup, Does.Contain("-> accepted (submitted"));
            Assert.That(markup, Does.Contain("lineage: batchId="));
            Assert.That(markup, Does.Contain("-> rejected (no changes needed)"));
            Assert.That(markup, Does.Contain("IsBlockedApprovalException(ex) ? \"blocked\" : \"failed\""));
            Assert.That(markup, Does.Contain("PendingWriteCount(ChatSessionState session)"));
            Assert.That(markup, Does.Contain("UpdatePendingWriteStatus(ActiveSession"));
            Assert.That(markup, Does.Contain("UpdatePendingWriteStatus(active, \"Ready\")"));
            Assert.That(markup, Does.Contain("UpdatePendingWriteStatus(session, _mode == MemoryChatMode.Agent ? \"Agent ready\" : \"Chat ready\")"));
            Assert.That(markup, Does.Contain("var pendingWriteCount = PendingWriteCount(ActiveSession);"));
            Assert.That(markup, Does.Contain("RespondMemoryWriteAsync"));
            Assert.That(markup, Does.Contain("RespondPageWriteAsync"));
            Assert.That(markup, Does.Contain("Respond requires a revision note."));
            Assert.That(markup, Does.Contain("Agent writes sent back for revision"));
            Assert.That(markup, Does.Contain("ResponseCommentDraft"));
            Assert.That(markup, Does.Contain("Respond keeps the proposal diff and records this note in proposal history"));
        });
    }

    [Test]
    public void ChatMarkup_DefaultsSidebarClosedUntilHistoryOrTraceIsRequested()
    {
        var markup = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MemorySmith.App", "Components", "Pages", "Chat.razor"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("private bool _sidebarOpen;"));
            Assert.That(markup, Does.Contain("private void ToggleSidebar()"));
            Assert.That(markup, Does.Contain("private void ShowSidebarTab(ChatSidebarTab tab)"));
            Assert.That(markup, Does.Contain("_sidebarOpen = true;"));
            Assert.That(markup, Does.Contain("CollapseSidebarOnNarrowViewportAsync"));
        });
    }

    [Test]
    public void PagesMarkup_UsesHeaderShareActionsForSelectedPages()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "Pages.razor"));
        var css = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "wwwroot", "app.css"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("CopySelectedPageLinkAsync"));
            Assert.That(markup, Does.Contain("page-detail-actions"));
            Assert.That(markup, Does.Contain("Copy page link"));
            Assert.That(css, Does.Contain(".page-detail-actions"));
        });
    }

    [Test]
    public void ChatMarkup_SupportsQuestionCardsWithOtherResponses()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "Chat.razor"));
        var css = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "wwwroot", "app.css"));
        var prompt = File.ReadAllText(Path.Combine(root, "MemorySmith.Core", "Docs", "Prompts", "wiki-chat-agent.md"));
        var modelfile = File.ReadAllText(Path.Combine(root, "MemorySmith.Core", "Docs", "Prompts", "wiki-chat-agent.modelfile"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("\"questionCard\""));
            Assert.That(markup, Does.Contain("TryParseQuestionCard"));
            Assert.That(markup, Does.Contain("SendQuestionOptionAsync"));
            Assert.That(markup, Does.Contain("QuestionOtherDraft"));
            Assert.That(markup, Does.Contain("chat-question-card"));
            Assert.That(css, Does.Contain(".chat-question-card"));
            Assert.That(css, Does.Contain(".chat-question-card-other"));
            Assert.That(prompt, Does.Contain("\"questionCard\""));
            Assert.That(prompt, Does.Contain("\"responsePrefix\""));
            Assert.That(modelfile, Does.Contain("\"questionCard\""));
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

    [Test]
    public void ProposalsMarkup_ExplainsDisabledActionsAndPrioritizesReviewState()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "Proposals.razor"));
        var css = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "wwwroot", "app.css"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("proposal-review-state-card"));
            Assert.That(markup, Does.Contain("ProposalStateHeading(_selectedProposal)"));
            Assert.That(markup, Does.Contain("ProposalStateExplanation(_selectedProposal)"));
            Assert.That(markup, Does.Contain("ProposalAvailableActions(_selectedProposal)"));
            Assert.That(markup, Does.Contain("RequestReviewActionHelp(_selectedProposal)"));
            Assert.That(markup, Does.Contain("AcceptActionHelp(_selectedProposal)"));
            Assert.That(markup, Does.Contain("RespondActionHelp(_selectedProposal)"));
            Assert.That(markup, Does.Contain("RejectActionHelp(_selectedProposal)"));
            Assert.That(markup, Does.Contain("Generate a revised draft before accepting"));
            Assert.That(css, Does.Contain(".proposal-review-state-card"));
            Assert.That(css, Does.Contain(".proposal-action-button-shell"));
            Assert.That(css, Does.Contain(".proposals-detail-pane"));
            Assert.That(css, Does.Contain("grid-template-rows: minmax(190px, 34vh) minmax(0, 1fr);"));
        });
    }

    [Test]
    public void ProposalsMarkup_CollapsesMaintenanceContextForProposalFirstReview()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "Components", "Pages", "Proposals.razor"));
        var css = File.ReadAllText(Path.Combine(root, "MemorySmith.App", "wwwroot", "app.css"));

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("proposal-context-panel"));
            Assert.That(markup, Does.Contain("Maintenance context"));
            Assert.That(markup, Does.Contain("MaintenanceContextSummaryText"));
            Assert.That(markup, Does.Contain("MaintenanceContextActionText"));
            Assert.That(markup, Does.Contain("ToggleMaintenanceContext"));
            Assert.That(markup, Does.Contain("Open maintenance context"));
            Assert.That(markup, Does.Contain("_isMaintenanceContextExpanded = false;"));
            Assert.That(css, Does.Contain(".proposal-context-panel"));
            Assert.That(css, Does.Contain(".proposal-context-body"));
            Assert.That(css, Does.Contain(".proposal-context-actions"));
            Assert.That(css, Does.Contain(".proposal-context-toggle"));
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

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var startIndex = 0;

        while ((startIndex = source.IndexOf(value, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += value.Length;
        }

        return count;
    }
}