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