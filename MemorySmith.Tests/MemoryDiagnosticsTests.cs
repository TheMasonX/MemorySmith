using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

[TestFixture]
public class MemoryDiagnosticsTests
{
    private string _tempRoot = null!;
    private InMemoryMemoryStore _store = null!;
    private TestVarStore _vars = null!;
    private MemoryDiagnosticsService _diagnostics = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _store = new InMemoryMemoryStore();
        _vars = new TestVarStore();
        var options = Options.Create(new MemorySmithOptions
        {
            Governance = new GovernanceOptions
            {
                TagPolicyPath = Path.Combine(_tempRoot, "Policies", "tag-policy.json")
            },
            SourceLinks = new SourceLinkOptions
            {
                AllowedFileRootVariables = ["AllowedRoot"]
            },
            Maintenance = new MaintenanceOptions
            {
                AutomaticDeprecationEnabled = false
            }
        });
        _vars.Save(new Dictionary<string, string> { ["AllowedRoot"] = _tempRoot + Path.DirectorySeparatorChar });
        var resolver = new VarResolver(_vars, options);
        _diagnostics = new MemoryDiagnosticsService(new TagPolicyService(options), resolver, _store, options);
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
    public void Analyze_FlagsMalformedTagsAndCardinality()
    {
        var record = new MemoryRecord
        {
            Id = "tagged",
            Title = "Tagged",
            Content = "Tagged content",
            Tags = ["#bad", "kind:rule", "kind:guide", "expires:2026-13", "retrieval", "working"]
        };
        _store.Save(record);

        var codes = _diagnostics.Analyze(record).Select(diagnostic => diagnostic.Code).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(codes, Does.Contain("tag.leading_hash"));
            Assert.That(codes, Does.Contain("tag.cardinality"));
            Assert.That(codes, Does.Contain("tag.invalid_year_month"));
            Assert.That(codes, Does.Contain("tag.alias"));
            Assert.That(codes, Does.Contain("tag.blocked"));
        });
    }

    [Test]
    public void Analyze_ToleratesDuplicateIdsInStore()
    {
        var record = new MemoryRecord
        {
            Id = "duplicate-id",
            Title = "Duplicate",
            Content = "Duplicate content",
            References = ["missing-reference"]
        };
        var duplicateStore = new DuplicateMemoryStore(
        [
            record,
            new MemoryRecord
            {
                Id = "DUPLICATE-ID",
                Title = "Newer Duplicate",
                Content = "Newer duplicate content",
                LastUpdated = DateTime.UtcNow.AddMinutes(1)
            }
        ]);
        var options = CreateOptions();
        var diagnostics = new MemoryDiagnosticsService(new TagPolicyService(options), new VarResolver(_vars, options), duplicateStore, options);

        var codes = diagnostics.Analyze(record).Select(diagnostic => diagnostic.Code).ToList();

        Assert.That(codes, Does.Contain("relationship.missing_reference"));
    }

    [Test]
    public void Analyze_WarnsForDuplicatePolicyNamespacesWithoutThrowing()
    {
        var policyPath = Path.Combine(_tempRoot, "Policies", "tag-policy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);
        File.WriteAllText(policyPath, """
            {
              "schemaVersion": 1,
              "mode": "warn",
              "namespaces": [
                { "name": "kind", "cardinality": "single", "valueKind": "tag" },
                { "name": "kind", "cardinality": "many", "valueKind": "tag" }
              ],
              "plainTags": { "mode": "allowWithSuggestions", "allowlist": [], "blocklist": [], "aliases": {} }
            }
            """);
        var record = new MemoryRecord
        {
            Id = "policy-duplicate",
            Title = "Policy Duplicate",
            Content = "Policy duplicate content",
            Tags = ["kind:rule"]
        };
        _store.Save(record);

        var codes = _diagnostics.Analyze(record).Select(diagnostic => diagnostic.Code).ToList();

        Assert.That(codes, Does.Contain("tag.policy_duplicate_namespace"));
    }

    [Test]
    public void TagPolicyService_CachesPolicyUntilFileChanges()
    {
        var options = CreateOptions();
        var service = new TagPolicyService(options);
        service.SavePolicy(new TagPolicy
        {
            Namespaces = [new TagNamespacePolicy { Name = "kind" }]
        });

        var first = service.GetPolicy();
        var second = service.GetPolicy();
        var policyPath = service.GetPolicyPath();
        File.WriteAllText(policyPath, """
            {
              "schemaVersion": 1,
              "mode": "warn",
              "namespaces": [ { "name": "custom", "cardinality": "many", "valueKind": "tag" } ],
              "plainTags": { "mode": "allowWithSuggestions", "allowlist": [], "blocklist": [], "aliases": {} }
            }
            """);
        File.SetLastWriteTimeUtc(policyPath, DateTime.UtcNow.AddMinutes(1));

        var updated = service.GetPolicy();

        Assert.Multiple(() =>
        {
            Assert.That(ReferenceEquals(first, second), Is.True);
            Assert.That(ReferenceEquals(first, updated), Is.False);
            Assert.That(updated.Namespaces.Select(namespacePolicy => namespacePolicy.Name), Does.Contain("custom"));
        });
    }

    [Test]
    public void Analyze_FlagsDanglingRelationshipsAndSupersessionTargets()
    {
        var record = new MemoryRecord
        {
            Id = "root",
            Title = "Root",
            Content = "Root content",
            References = ["missing-reference"],
            Conflicts = ["root"],
            Tags = ["superseded-by:missing-newer-record"]
        };
        _store.Save(record);

        var codes = _diagnostics.Analyze(record).Select(diagnostic => diagnostic.Code).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(codes, Does.Contain("relationship.missing_reference"));
            Assert.That(codes, Does.Contain("relationship.self_conflict"));
            Assert.That(codes, Does.Contain("tag.missing_memory_target"));
            Assert.That(codes, Does.Contain("stale.superseded"));
        });
    }

    [Test]
    public void Analyze_FlagsSourceLinkProblems()
    {
        var sourcePath = Path.Combine(_tempRoot, "source.txt");
        File.WriteAllText(sourcePath, "one" + Environment.NewLine + "two" + Environment.NewLine);
        var record = new MemoryRecord
        {
            Id = "sources",
            Title = "Sources",
            Content = "Source content",
            SourceLinks =
            [
                new SourceLink { Uri = "%MissingRoot%source.txt" },
                new SourceLink { Uri = "%AllowedRoot%source.txt", StartLine = 8 },
                new SourceLink { Uri = "%AllowedRoot%source.txt", StartLine = 3, EndLine = 2 }
            ]
        };
        _store.Save(record);

        var codes = _diagnostics.Analyze(record).Select(diagnostic => diagnostic.Code).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(codes, Does.Contain("source.missing_variable"));
            Assert.That(codes, Does.Contain("source.line_out_of_range"));
            Assert.That(codes, Does.Contain("source.invalid_line_range"));
        });
    }

    [Test]
    public void Analyze_FlagsStaleAndMaintenanceWarningsWithoutChangingStatus()
    {
        var record = new MemoryRecord
        {
            Id = "stale-low-score",
            Title = "Stale",
            Content = "Stale content",
            Status = MemoryStatus.Unconsolidated,
            Confidence = 0,
            Tags = ["review-after:2020-01", "expires:2020-02", "stale-risk:2020-03"],
            LastUpdated = DateTime.UtcNow.AddYears(-5)
        };
        _store.Save(record);

        var codes = _diagnostics.Analyze(record).Select(diagnostic => diagnostic.Code).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(codes, Does.Contain("stale.review_due"));
            Assert.That(codes, Does.Contain("stale.expired"));
            Assert.That(codes, Does.Contain("stale.risk_due"));
            Assert.That(codes, Does.Contain("maintenance.low_score_deprecation_recommended"));
            Assert.That(record.Status, Is.EqualTo(MemoryStatus.Unconsolidated));
        });
    }

    private IOptions<MemorySmithOptions> CreateOptions() => Options.Create(new MemorySmithOptions
    {
        Governance = new GovernanceOptions
        {
            TagPolicyPath = Path.Combine(_tempRoot, "Policies", "tag-policy.json")
        },
        SourceLinks = new SourceLinkOptions
        {
            AllowedFileRootVariables = ["AllowedRoot"]
        },
        Maintenance = new MaintenanceOptions
        {
            AutomaticDeprecationEnabled = false
        }
    });

    private sealed class TestVarStore : IVarStore
    {
        private Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> Load() => new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase);

        public void Save(IReadOnlyDictionary<string, string> vars) =>
            _values = new Dictionary<string, string>(vars, StringComparer.OrdinalIgnoreCase);
    }
}