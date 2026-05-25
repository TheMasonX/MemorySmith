using System.Text.RegularExpressions;
using MemorySmith.Core.Models;
using MemorySmith.Storage;

namespace MemorySmith.App.Services;

public sealed record TagGovernanceSnapshot(
    TagPolicy Policy,
    TagPolicyLoadStatus PolicyLoadStatus,
    IReadOnlyList<TagUsageSummary> Tags,
    IReadOnlyList<TagGovernanceSuggestion> Suggestions,
    IReadOnlyList<MemoryDiagnostic> PolicyDiagnostics);

public sealed record TagUsageSummary(
    string Tag,
    int Count,
    double RecordRatio,
    bool IsNamespaced,
    bool IsAllowed,
    bool IsBlocked,
    string? AliasTarget);

public sealed record TagGovernanceSuggestion(
    string Kind,
    string Tag,
    string? SuggestedValue,
    int Count,
    string Reason);

public sealed class TagGovernanceService
{
    private static readonly Regex PlainTagPattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex NamespaceMistakePattern = new("^[a-z0-9]+[_-][a-z0-9]+:", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> LowValueTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "misc", "general", "important", "stuff", "todo", "notes", "old", "new"
    };

    private readonly TagPolicyService _policyService;
    private readonly MemoryDiagnosticsService _diagnostics;
    private readonly IMemoryStore _store;

    public TagGovernanceService(TagPolicyService policyService, MemoryDiagnosticsService diagnostics, IMemoryStore store)
    {
        _policyService = policyService;
        _diagnostics = diagnostics;
        _store = store;
    }

    public TagGovernanceSnapshot GetSnapshot()
    {
        var policy = NormalizePolicy(_policyService.GetPolicy());
        var loadStatus = _policyService.GetLoadStatus();
        var records = MemoryRecordLookup.ToRecordList(_store.LoadAll());
        var tags = BuildTagUsage(records, policy);
        return new TagGovernanceSnapshot(
            policy,
            loadStatus,
            tags,
            BuildSuggestions(records, tags, policy),
            _diagnostics.AnalyzePolicy(policy));
    }

    public TagGovernanceSnapshot SavePolicy(TagPolicy policy)
    {
        var normalized = NormalizePolicy(policy);
        _policyService.SavePolicy(normalized);
        return GetSnapshot();
    }

    public IReadOnlyList<string> GetTagCompletions(string? prefix, int limit = 20)
    {
        var normalizedPrefix = (prefix ?? string.Empty).Trim();
        var policy = NormalizePolicy(_policyService.GetPolicy());
        var records = MemoryRecordLookup.ToRecordList(_store.LoadAll());
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in records.SelectMany(record => record.Tags))
        {
            values.Add(tag);
        }

        foreach (var tag in policy.PlainTags.Allowlist)
        {
            values.Add(tag);
        }

        foreach (var alias in policy.PlainTags.Aliases)
        {
            values.Add(alias.Key);
            values.Add(alias.Value);
        }

        foreach (var namespacePolicy in policy.Namespaces)
        {
            if (namespacePolicy.AllowedValues.Count == 0)
            {
                values.Add(namespacePolicy.Name + ":");
                continue;
            }

            foreach (var allowed in namespacePolicy.AllowedValues)
            {
                values.Add(namespacePolicy.Name + ":" + allowed);
            }
        }

        return values
            .Where(tag => normalizedPrefix.Length == 0 || tag.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();
    }

    public IReadOnlyList<MemoryDiagnostic> AnalyzeDraft(MemoryRecord record)
    {
        var recordsById = MemoryRecordLookup.ToRecordMap(_store.LoadAll().Append(record));
        return _diagnostics.Analyze(record, recordsById);
    }

    internal static bool ShouldBlockInvalidPlainTags(TagPolicy policy) =>
        EffectivePlainTagMode(policy) is "blockUnknown" or "block";

    internal static bool ShouldWarnUnknownPlainTags(TagPolicy policy) =>
        EffectivePlainTagMode(policy) is "warn";

    internal static bool ShouldObserveUnknownPlainTags(TagPolicy policy) =>
        EffectivePlainTagMode(policy) is "observe" or "allowWithSuggestions";

    private static string EffectivePlainTagMode(TagPolicy policy) =>
        string.IsNullOrWhiteSpace(policy.PlainTags.Mode)
            ? policy.Mode.Trim()
            : policy.PlainTags.Mode.Trim();

    private static List<TagUsageSummary> BuildTagUsage(IReadOnlyList<MemoryRecord> records, TagPolicy policy)
    {
        var allowlist = policy.PlainTags.Allowlist.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blocklist = policy.PlainTags.Blocklist.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var counts = records
            .SelectMany(record => record.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .Select(group => new TagUsageSummary(
                group.Key,
                group.Count(),
                records.Count == 0 ? 0 : Math.Round(group.Count() / (double)records.Count, 4),
                group.Key.Contains(':', StringComparison.Ordinal),
                allowlist.Contains(group.Key),
                blocklist.Contains(group.Key),
                policy.PlainTags.Aliases.TryGetValue(group.Key, out var target) ? target : null))
            .OrderByDescending(tag => tag.Count)
            .ThenBy(tag => tag.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return counts;
    }

    private static List<TagGovernanceSuggestion> BuildSuggestions(IReadOnlyList<MemoryRecord> records, IReadOnlyList<TagUsageSummary> tags, TagPolicy policy)
    {
        var suggestions = new List<TagGovernanceSuggestion>();
        var broadThreshold = Math.Max(1, (int)Math.Ceiling(records.Count * 0.60));

        foreach (var tag in tags)
        {
            if (tag.Count >= broadThreshold)
            {
                suggestions.Add(new("broad-tag", tag.Tag, null, tag.Count, "This tag appears on most records and may not discriminate search results."));
            }

            if (tag.IsBlocked || LowValueTags.Contains(tag.Tag))
            {
                suggestions.Add(new("blocklist-candidate", tag.Tag, null, tag.Count, "This tag is low-value or already blocked by policy."));
            }

            if (tag.AliasTarget is not null)
            {
                suggestions.Add(new("alias-candidate", tag.Tag, tag.AliasTarget, tag.Count, "This tag already has a canonical alias target."));
            }

            if (!tag.IsNamespaced && !PlainTagPattern.IsMatch(tag.Tag))
            {
                suggestions.Add(new("malformed-tag", tag.Tag, ToKebabCase(tag.Tag), tag.Count, "Plain tags should use lowercase kebab-case."));
            }

            if (NamespaceMistakePattern.IsMatch(tag.Tag))
            {
                suggestions.Add(new("namespace-candidate", tag.Tag, tag.Tag.Replace('_', '-'), tag.Count, "This looks like a namespaced tag with inconsistent namespace spelling."));
            }
        }

        var rawTagCounts = records
            .SelectMany(record => record.Tags.Distinct(StringComparer.Ordinal))
            .GroupBy(tag => tag, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var group in rawTagCounts.Keys.GroupBy(tag => tag.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            var canonical = group.OrderBy(tag => tag, StringComparer.Ordinal).First();
            foreach (var variant in group.Where(tag => !string.Equals(tag, canonical, StringComparison.Ordinal)))
            {
                suggestions.Add(new("casing-variant", variant, canonical, rawTagCounts[variant], "This tag differs only by casing from another tag."));
            }
        }

        var plainTags = tags.Where(tag => !tag.IsNamespaced).ToList();
        for (var i = 0; i < plainTags.Count; i++)
        {
            for (var j = i + 1; j < plainTags.Count; j++)
            {
                var left = plainTags[i];
                var right = plainTags[j];
                if (Math.Abs(left.Tag.Length - right.Tag.Length) > 2)
                {
                    continue;
                }

                if (Levenshtein(left.Tag, right.Tag) is > 0 and <= 2)
                {
                    var canonical = left.Count >= right.Count ? left.Tag : right.Tag;
                    var duplicate = left.Count >= right.Count ? right : left;
                    suggestions.Add(new("near-duplicate", duplicate.Tag, canonical, duplicate.Count, "This tag is close to another tag and may be a typo or synonym."));
                }
            }
        }

        var allowlist = policy.PlainTags.Allowlist.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in plainTags.Where(tag => !tag.IsBlocked && tag.AliasTarget is null && !allowlist.Contains(tag.Tag)))
        {
            suggestions.Add(new("allowlist-candidate", tag.Tag, null, tag.Count, "This observed tag is not in the plain-tag allowlist."));
        }

        return suggestions
            .DistinctBy(suggestion => $"{suggestion.Kind}|{suggestion.Tag}|{suggestion.SuggestedValue}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(suggestion => suggestion.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(suggestion => suggestion.Count)
            .ThenBy(suggestion => suggestion.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static TagPolicy NormalizePolicy(TagPolicy? policy)
    {
        policy ??= TagPolicy.CreateDefault();
        policy.Mode = string.IsNullOrWhiteSpace(policy.Mode) ? "warn" : policy.Mode.Trim();
        policy.Namespaces = policy.Namespaces
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new TagNamespacePolicy
            {
                Name = item.Name.Trim(),
                Cardinality = string.IsNullOrWhiteSpace(item.Cardinality) ? "many" : item.Cardinality.Trim(),
                ValueKind = string.IsNullOrWhiteSpace(item.ValueKind) ? "tag" : item.ValueKind.Trim(),
                AllowedValues = item.AllowedValues.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            })
            .ToList();
        policy.PlainTags ??= new PlainTagPolicy();
        policy.PlainTags.Mode = string.IsNullOrWhiteSpace(policy.PlainTags.Mode) ? "allowWithSuggestions" : policy.PlainTags.Mode.Trim();
        policy.PlainTags.Allowlist = NormalizeTagList(policy.PlainTags.Allowlist);
        policy.PlainTags.Blocklist = NormalizeTagList(policy.PlainTags.Blocklist);
        policy.PlainTags.Aliases = policy.PlainTags.Aliases
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        return policy;
    }

    private static List<string> NormalizeTagList(IEnumerable<string> tags) =>
        tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ToKebabCase(string value)
    {
        var cleaned = Regex.Replace(value.Trim(), "[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(cleaned) ? value : cleaned;
    }

    private static int Levenshtein(string left, string right)
    {
        var costs = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
        {
            costs[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            var previous = costs[0];
            costs[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var current = costs[j];
                costs[j] = left[i - 1] == right[j - 1]
                    ? previous
                    : Math.Min(Math.Min(costs[j - 1], costs[j]), previous) + 1;
                previous = current;
            }
        }

        return costs[right.Length];
    }
}