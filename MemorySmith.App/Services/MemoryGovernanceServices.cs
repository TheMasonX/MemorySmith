using System.Text.Json;
using System.Text.RegularExpressions;
using MemorySmith.Core.Models;
using MemorySmith.Core.StateMachine;
using MemorySmith.Storage;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public sealed class TagPolicy
{
    public int SchemaVersion { get; set; } = 1;
    public string Mode { get; set; } = "warn";
    public List<TagNamespacePolicy> Namespaces { get; set; } = [];
    public PlainTagPolicy PlainTags { get; set; } = new();

    public static TagPolicy CreateDefault() => new()
    {
        Namespaces =
        [
            new() { Name = "kind", Cardinality = "single", ValueKind = "enum", AllowedValues = ["fact", "rule", "procedure", "decision", "plan", "research", "guide", "concept", "issue", "example", "index"] },
            new() { Name = "priority", Cardinality = "single", ValueKind = "enum", AllowedValues = ["critical", "high", "normal", "low"] },
            new() { Name = "audience", Cardinality = "many", ValueKind = "enum", AllowedValues = ["agent", "human", "chat", "developer", "admin"] },
            new() { Name = "scope", Cardinality = "many", ValueKind = "tag" },
            new() { Name = "review-after", Cardinality = "single", ValueKind = "year-month" },
            new() { Name = "expires", Cardinality = "single", ValueKind = "year-month" },
            new() { Name = "stale-risk", Cardinality = "single", ValueKind = "year-month" },
            new() { Name = "supersedes", Cardinality = "many", ValueKind = "memory-id" },
            new() { Name = "superseded-by", Cardinality = "many", ValueKind = "memory-id" }
        ],
        PlainTags = new PlainTagPolicy
        {
            Mode = "allowWithSuggestions",
            Allowlist = ["admin", "api", "architecture", "benchmarks", "chat", "configuration", "context-pack", "current-state", "deterministic-test", "diagnostics", "documentation", "github", "governance", "graph-fixture", "hybrid-search", "maintenance", "mcp", "memory-system", "ollama", "pages", "project-wiki", "search", "security", "semantic-search", "settings", "source-links", "staleness", "storage", "structured-output", "tag-governance", "test-fixture", "tests", "ui"],
            Blocklist = ["misc", "general", "important", "stuff", "todo", "notes", "old", "new", "core", "working", "deprecated", "unconsolidated"],
            Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["retrieval"] = "search",
                ["semantic-searching"] = "semantic-search",
                ["model-context-protocol"] = "mcp"
            }
        }
    };
}

public sealed class TagNamespacePolicy
{
    public string Name { get; set; } = string.Empty;
    public string Cardinality { get; set; } = "many";
    public string ValueKind { get; set; } = "tag";
    public List<string> AllowedValues { get; set; } = [];
}

public sealed class PlainTagPolicy
{
    public string Mode { get; set; } = "allowWithSuggestions";
    public List<string> Allowlist { get; set; } = [];
    public List<string> Blocklist { get; set; } = [];
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record TagPolicyLoadStatus(
    string Path,
    bool LoadedFromFile,
    bool UsingFallback,
    string Reason,
    string Message,
    string? ErrorType = null);

public sealed class TagPolicyService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _cacheLock = new();
    private readonly MemorySmithOptions _options;
    private string? _cachedPath;
    private DateTime? _cachedLastWriteUtc;
    private TagPolicy? _cachedPolicy;
    private TagPolicyLoadStatus? _cachedLoadStatus;

    public TagPolicyService(IOptions<MemorySmithOptions> options)
    {
        _options = options.Value;
    }

    public string GetPolicyPath() => Path.GetFullPath(_options.Governance.TagPolicyPath);

    public TagPolicyLoadStatus GetLoadStatus()
    {
        _ = GetPolicy();
        lock (_cacheLock)
        {
            return _cachedLoadStatus ?? MissingStatus(GetPolicyPath());
        }
    }

    public TagPolicy GetPolicy()
    {
        var path = GetPolicyPath();
        var lastWriteUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;

        lock (_cacheLock)
        {
            if (_cachedPolicy is not null &&
                string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) &&
                _cachedLastWriteUtc == lastWriteUtc)
            {
                return _cachedPolicy;
            }
        }

        var result = LoadPolicy(path);
        lock (_cacheLock)
        {
            _cachedPath = path;
            _cachedLastWriteUtc = lastWriteUtc;
            _cachedPolicy = result.Policy;
            _cachedLoadStatus = result.Status;
        }

        return result.Policy;
    }

    public void SavePolicy(TagPolicy policy)
    {
        var path = GetPolicyPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(policy, JsonOptions) + Environment.NewLine);

        lock (_cacheLock)
        {
            _cachedPath = path;
            _cachedLastWriteUtc = File.GetLastWriteTimeUtc(path);
            _cachedPolicy = policy;
            _cachedLoadStatus = LoadedStatus(path);
        }
    }

    private static TagPolicyLoadResult LoadPolicy(string path)
    {
        if (!File.Exists(path))
        {
            return new(TagPolicy.CreateDefault(), MissingStatus(path));
        }

        try
        {
            var policy = JsonSerializer.Deserialize<TagPolicy>(File.ReadAllText(path), JsonOptions);
            return policy is null
                ? new(TagPolicy.CreateDefault(), new TagPolicyLoadStatus(path, false, true, "empty", "Tag policy file did not contain a policy object; using built-in defaults."))
                : new(policy, LoadedStatus(path));
        }
        catch (Exception ex)
        {
            return new(TagPolicy.CreateDefault(), new TagPolicyLoadStatus(path, false, true, "failed", $"Tag policy file could not be loaded; using built-in defaults. {ex.Message}", ex.GetType().Name));
        }
    }

    private static TagPolicyLoadStatus LoadedStatus(string path) =>
        new(path, true, false, "loaded", "Tag policy loaded from the configured file.");

    private static TagPolicyLoadStatus MissingStatus(string path) =>
        new(path, false, true, "missing", "Tag policy file was not found; using built-in defaults.");

    private sealed record TagPolicyLoadResult(TagPolicy Policy, TagPolicyLoadStatus Status);
}

public sealed partial class MemoryDiagnosticsService
{
    private readonly TagPolicyService _tagPolicyService;
    private readonly VarResolver _vars;
    private readonly IMemoryStore _store;
    private readonly MemorySmithOptions _options;

    public MemoryDiagnosticsService(
        TagPolicyService tagPolicyService,
        VarResolver vars,
        IMemoryStore store,
        IOptions<MemorySmithOptions> options)
    {
        _tagPolicyService = tagPolicyService;
        _vars = vars;
        _store = store;
        _options = options.Value;
    }

    public IReadOnlyList<MemoryDiagnostic> Analyze(MemoryRecord record)
    {
        var recordsById = MemoryRecordLookup.ToRecordMap(_store.LoadAll());
        return Analyze(record, recordsById);
    }

    public IReadOnlyList<MemoryDiagnostic> Analyze(MemoryRecord record, IReadOnlyDictionary<string, MemoryRecord> recordsById)
    {
        var diagnostics = new List<MemoryDiagnostic>();
        var policy = _tagPolicyService.GetPolicy();
        diagnostics.AddRange(AnalyzeTags(record, recordsById, policy));
        diagnostics.AddRange(AnalyzeRelationships(record, recordsById));
        diagnostics.AddRange(AnalyzeSourceLinks(record));
        diagnostics.AddRange(AnalyzeStaleness(record));
        diagnostics.AddRange(AnalyzeMaintenanceRisk(record));
        return diagnostics
            .DistinctBy(diagnostic => $"{diagnostic.Code}|{diagnostic.Target}|{diagnostic.Message}", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyDictionary<string, IReadOnlyList<MemoryDiagnostic>> AnalyzeAll(IEnumerable<MemoryRecord> records)
    {
        var recordsById = MemoryRecordLookup.ToRecordMap(records);
        return recordsById.Values.ToDictionary(record => record.Id, record => Analyze(record, recordsById), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<MemoryDiagnostic> AnalyzePolicy(TagPolicy policy)
    {
        var diagnostics = new List<MemoryDiagnostic>();
        var loadStatus = _tagPolicyService.GetLoadStatus();
        if (loadStatus.UsingFallback)
        {
            diagnostics.Add(new MemoryDiagnostic(
                loadStatus.Reason == "missing" ? "tag.policy_missing" : "tag.policy_load_failed",
                loadStatus.Reason == "missing" ? "Info" : "Warning",
                "tag",
                loadStatus.Message,
                loadStatus.Path));
        }

        var seenNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var namespacePolicy in policy.Namespaces.Where(item => !string.IsNullOrWhiteSpace(item.Name)))
        {
            if (!seenNamespaces.Add(namespacePolicy.Name))
            {
                diagnostics.Add(Warning("tag.policy_duplicate_namespace", "tag", $"Tag policy namespace '{namespacePolicy.Name}' is defined more than once; first definition is used.", namespacePolicy.Name));
            }
        }

        return diagnostics;
    }

    private static IEnumerable<MemoryDiagnostic> AnalyzeTags(
        MemoryRecord record,
        IReadOnlyDictionary<string, MemoryRecord> recordsById,
        TagPolicy policy)
    {
        var namespacePolicies = new Dictionary<string, TagNamespacePolicy>(StringComparer.OrdinalIgnoreCase);
        var duplicateNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var namespacePolicy in policy.Namespaces.Where(item => !string.IsNullOrWhiteSpace(item.Name)))
        {
            if (!namespacePolicies.TryAdd(namespacePolicy.Name, namespacePolicy))
            {
                duplicateNamespaces.Add(namespacePolicy.Name);
            }
        }

        foreach (var duplicateNamespace in duplicateNamespaces.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            yield return Warning("tag.policy_duplicate_namespace", "tag", $"Tag policy namespace '{duplicateNamespace}' is defined more than once; first definition is used.", duplicateNamespace);
        }

        var namespaceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in record.Tags)
        {
            if (tag.StartsWith('#'))
            {
                yield return Warning("tag.leading_hash", "tag", $"Tag '{tag}' should omit the leading #.", tag);
                continue;
            }

            var separator = tag.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                var namespaceName = tag[..separator];
                var value = tag[(separator + 1)..];
                namespaceCounts[namespaceName] = namespaceCounts.GetValueOrDefault(namespaceName) + 1;

                if (!namespacePolicies.TryGetValue(namespaceName, out var namespacePolicy))
                {
                    yield return Warning("tag.unknown_namespace", "tag", $"Tag namespace '{namespaceName}' is not in the active policy.", tag);
                    continue;
                }

                foreach (var diagnostic in ValidateNamespacedTag(tag, namespacePolicy, value, recordsById))
                {
                    yield return diagnostic;
                }

                continue;
            }

            if (!PlainTagPattern().IsMatch(tag))
            {
                yield return Warning("tag.malformed_plain", "tag", $"Plain tag '{tag}' should use lowercase kebab-case.", tag);
            }

            if (policy.PlainTags.Blocklist.Any(blocked => string.Equals(blocked, tag, StringComparison.OrdinalIgnoreCase)))
            {
                yield return TagGovernanceService.ShouldBlockInvalidPlainTags(policy)
                    ? Error("tag.blocked", "tag", $"Plain tag '{tag}' is blocklisted by the active tag policy.", tag)
                    : Warning("tag.blocked", "tag", $"Plain tag '{tag}' is blocklisted by the active tag policy.", tag);
            }

            if (policy.PlainTags.Aliases.TryGetValue(tag, out var canonical))
            {
                yield return Info("tag.alias", "tag", $"Tag '{tag}' has canonical alias '{canonical}'.", tag);
            }

            if (policy.PlainTags.Allowlist.Count > 0 &&
                !policy.PlainTags.Allowlist.Any(allowed => string.Equals(allowed, tag, StringComparison.OrdinalIgnoreCase)) &&
                !policy.PlainTags.Aliases.ContainsKey(tag) &&
                !policy.PlainTags.Blocklist.Any(blocked => string.Equals(blocked, tag, StringComparison.OrdinalIgnoreCase)))
            {
                if (TagGovernanceService.ShouldBlockInvalidPlainTags(policy))
                {
                    yield return Error("tag.unknown_plain", "tag", $"Plain tag '{tag}' is not in the active allowlist.", tag);
                }
                else if (TagGovernanceService.ShouldWarnUnknownPlainTags(policy))
                {
                    yield return Warning("tag.unknown_plain", "tag", $"Plain tag '{tag}' is not in the active allowlist.", tag);
                }
                else if (TagGovernanceService.ShouldObserveUnknownPlainTags(policy))
                {
                    yield return Info("tag.unknown_plain", "tag", $"Plain tag '{tag}' is observed outside the active allowlist.", tag);
                }
            }
        }

        foreach (var namespacePolicy in policy.Namespaces.Where(item => string.Equals(item.Cardinality, "single", StringComparison.OrdinalIgnoreCase)))
        {
            if (namespaceCounts.GetValueOrDefault(namespacePolicy.Name) > 1)
            {
                yield return Warning("tag.cardinality", "tag", $"Only one '{namespacePolicy.Name}:' tag should be canonical on a memory.", namespacePolicy.Name);
            }
        }
    }

    private static IEnumerable<MemoryDiagnostic> ValidateNamespacedTag(
        string tag,
        TagNamespacePolicy namespacePolicy,
        string value,
        IReadOnlyDictionary<string, MemoryRecord> recordsById)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield return Warning("tag.empty_namespace_value", "tag", $"Tag '{tag}' needs a value after the colon.", tag);
            yield break;
        }

        if (string.Equals(namespacePolicy.ValueKind, "enum", StringComparison.OrdinalIgnoreCase) &&
            namespacePolicy.AllowedValues.Count > 0 &&
            !namespacePolicy.AllowedValues.Any(allowed => string.Equals(allowed, value, StringComparison.OrdinalIgnoreCase)))
        {
            yield return Warning("tag.invalid_namespace_value", "tag", $"Tag '{tag}' is not an allowed '{namespacePolicy.Name}:' value.", tag);
        }

        if (string.Equals(namespacePolicy.ValueKind, "year-month", StringComparison.OrdinalIgnoreCase) && !TryParseYearMonth(value, out _))
        {
            yield return Warning("tag.invalid_year_month", "tag", $"Tag '{tag}' should use YYYY-MM with a valid month.", tag);
        }

        if (string.Equals(namespacePolicy.ValueKind, "memory-id", StringComparison.OrdinalIgnoreCase))
        {
            if (!MemoryIdPattern().IsMatch(value))
            {
                yield return Warning("tag.invalid_memory_id", "tag", $"Tag '{tag}' should point to a valid memory id.", tag);
            }
            else if (!recordsById.ContainsKey(value))
            {
                yield return Warning("tag.missing_memory_target", "relationship", $"Tag '{tag}' points to missing memory '{value}'.", tag);
            }
        }
    }

    private static IEnumerable<MemoryDiagnostic> AnalyzeRelationships(MemoryRecord record, IReadOnlyDictionary<string, MemoryRecord> recordsById)
    {
        foreach (var diagnostic in AnalyzeRelationshipList(record, recordsById, record.References, "reference"))
        {
            yield return diagnostic;
        }

        foreach (var diagnostic in AnalyzeRelationshipList(record, recordsById, record.Conflicts, "conflict"))
        {
            yield return diagnostic;
        }
    }

    private static IEnumerable<MemoryDiagnostic> AnalyzeRelationshipList(
        MemoryRecord record,
        IReadOnlyDictionary<string, MemoryRecord> recordsById,
        IReadOnlyList<string> ids,
        string relationship)
    {
        foreach (var id in ids)
        {
            if (!MemoryIdPattern().IsMatch(id))
            {
                yield return Warning($"relationship.invalid_{relationship}", "relationship", $"{relationship} '{id}' is not a valid memory id.", id);
                continue;
            }

            if (string.Equals(id, record.Id, StringComparison.OrdinalIgnoreCase))
            {
                yield return Warning($"relationship.self_{relationship}", "relationship", $"Memory '{record.Id}' {relationship}s itself.", id);
            }

            if (!recordsById.ContainsKey(id))
            {
                yield return Warning($"relationship.missing_{relationship}", "relationship", $"{relationship} target '{id}' was not found.", id);
            }
        }
    }

    private IEnumerable<MemoryDiagnostic> AnalyzeSourceLinks(MemoryRecord record)
    {
        var vars = _vars.GetVars();
        for (var index = 0; index < record.SourceLinks.Count; index++)
        {
            var link = record.SourceLinks[index];
            var target = $"SourceLinks[{index}]";

            foreach (Match match in VariablePattern().Matches(link.Uri))
            {
                var name = match.Groups[1].Value;
                if (!vars.ContainsKey(name))
                {
                    yield return Warning("source.missing_variable", "source", $"Source link variable '%{name}%' is not defined.", target);
                }
            }

            foreach (var diagnostic in AnalyzeSourceLineRange(link, target))
            {
                yield return diagnostic;
            }

            var resolved = _vars.Resolve(link.Uri);
            if (IsWebUri(resolved))
            {
                continue;
            }

            if (!_vars.TryResolveLocalFile(link, out var fullPath, out var message))
            {
                yield return Warning("source.unresolved", "source", message ?? "Source link could not be resolved to a readable local path.", target);
                continue;
            }

            if (File.Exists(fullPath) && link.StartLine.HasValue)
            {
                var lineCount = File.ReadLines(fullPath).Count();
                if (link.StartLine.Value > lineCount || (link.EndLine.HasValue && link.EndLine.Value > lineCount))
                {
                    yield return Warning("source.line_out_of_range", "source", $"Source link line range exceeds file length ({lineCount} lines).", target);
                }
            }
        }
    }

    private static IEnumerable<MemoryDiagnostic> AnalyzeSourceLineRange(SourceLink link, string target)
    {
        if (link.StartLine is <= 0)
        {
            yield return Warning("source.invalid_line_range", "source", "Source link StartLine must be greater than 0 when provided.", target);
        }

        if (link.EndLine is <= 0)
        {
            yield return Warning("source.invalid_line_range", "source", "Source link EndLine must be greater than 0 when provided.", target);
        }

        if (link.StartLine.HasValue && link.EndLine.HasValue && link.EndLine.Value < link.StartLine.Value)
        {
            yield return Warning("source.invalid_line_range", "source", "Source link EndLine should be greater than or equal to StartLine.", target);
        }
    }

    private static IEnumerable<MemoryDiagnostic> AnalyzeStaleness(MemoryRecord record)
    {
        var currentMonth = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

        if (record.Status == MemoryStatus.Deprecated)
        {
            yield return Warning("stale.deprecated_status", "staleness", "Record is Deprecated; cite it only as historical context unless another record revives it.", nameof(MemoryRecord.Status));
        }

        foreach (var tag in record.Tags)
        {
            if (TryReadNamespacedValue(tag, "review-after", out var reviewAfter) && TryParseYearMonth(reviewAfter, out var reviewMonth) && reviewMonth.CompareTo(currentMonth) <= 0)
            {
                yield return Warning("stale.review_due", "staleness", $"Record is due for review after {reviewAfter}.", tag);
            }

            if (TryReadNamespacedValue(tag, "expires", out var expires) && TryParseYearMonth(expires, out var expiresMonth) && expiresMonth.CompareTo(currentMonth) <= 0)
            {
                yield return Warning("stale.expired", "staleness", $"Record has reached expires:{expires}; verify before citing it.", tag);
            }

            if (TryReadNamespacedValue(tag, "stale-risk", out var staleRisk) && TryParseYearMonth(staleRisk, out var staleRiskMonth) && staleRiskMonth.CompareTo(currentMonth) <= 0)
            {
                yield return Warning("stale.risk_due", "staleness", $"Record has stale-risk:{staleRisk}; check whether this topic has drifted.", tag);
            }

            if (TryReadNamespacedValue(tag, "superseded-by", out var newerId))
            {
                yield return Warning("stale.superseded", "staleness", $"Record is marked superseded by '{newerId}'.", tag);
            }
        }
    }

    private IEnumerable<MemoryDiagnostic> AnalyzeMaintenanceRisk(MemoryRecord record)
    {
        if (_options.Maintenance.AutomaticDeprecationEnabled || record.Status == MemoryStatus.Deprecated)
        {
            yield break;
        }

        var score = MemoryScorer.Score(record);
        if (score < MemoryStateMachine.DeprecationThreshold)
        {
            yield return Warning("maintenance.low_score_deprecation_recommended", "maintenance", $"Memory score {score:0.###} is below the deprecation threshold, but automatic deprecation is disabled.", nameof(MemoryRecord.Status));
        }
    }

    private static bool TryReadNamespacedValue(string tag, string namespaceName, out string value)
    {
        var prefix = namespaceName + ":";
        if (tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = tag[prefix.Length..];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryParseYearMonth(string value, out DateOnly month)
    {
        month = default;
        var parts = value.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0].Length != 4 || parts[1].Length != 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var year) || !int.TryParse(parts[1], out var monthNumber))
        {
            return false;
        }

        if (monthNumber is < 1 or > 12)
        {
            return false;
        }

        month = new DateOnly(year, monthNumber, 1);
        return true;
    }

    private static bool IsWebUri(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static MemoryDiagnostic Warning(string code, string category, string message, string? target = null) =>
        new(code, "Warning", category, message, target);

    private static MemoryDiagnostic Error(string code, string category, string message, string? target = null) =>
        new(code, "Error", category, message, target);

    private static MemoryDiagnostic Info(string code, string category, string message, string? target = null) =>
        new(code, "Info", category, message, target);

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex PlainTagPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex MemoryIdPattern();

    [GeneratedRegex(@"%(\w+)%")]
    private static partial Regex VariablePattern();
}