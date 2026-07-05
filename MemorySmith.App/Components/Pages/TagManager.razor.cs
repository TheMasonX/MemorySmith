using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace MemorySmith.App.Components.Pages;

public partial class TagManager
{
    [Inject]
    private TagGovernanceService TagGovernance { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    private string InstanceName => Options.CurrentValue.InstanceName;

    private readonly string[] _policyModes = ["observe", "warn", "block"];
    private readonly string[] _plainTagModes = ["allowWithSuggestions", "observe", "warn", "blockUnknown"];
    private List<TagUsageSummary> _tagUsage = [];
    private List<TagGovernanceSuggestion> _suggestions = [];
    private List<MemoryDiagnostic> _policyDiagnostics = [];
    private List<NamespaceEditRow> _namespaceRows = [];
    private TagPolicyLoadStatus? _policyLoadStatus;
    private string _policyMode = "warn";
    private string _plainMode = "allowWithSuggestions";
    private string _allowlistText = string.Empty;
    private string _blocklistText = string.Empty;
    private string _aliasesText = string.Empty;
    private bool _isBusy;

    protected override Task OnInitializedAsync() => LoadAsync();

    private Task LoadAsync()
    {
        _isBusy = true;
        var snapshot = TagGovernance.GetSnapshot();
        PopulateEditor(snapshot);
        _isBusy = false;
        return Task.CompletedTask;
    }

    private Task SaveAsync()
    {
        _isBusy = true;
        var policy = BuildPolicyFromEditor(out var inputDiagnostics);
        var snapshot = TagGovernance.SavePolicy(policy);
        PopulateEditor(snapshot);
        _policyDiagnostics = inputDiagnostics.Concat(_policyDiagnostics).ToList();
        _isBusy = false;
        Snackbar.Add(inputDiagnostics.Count == 0 ? "Tag policy saved" : "Tag policy saved with warnings", inputDiagnostics.Count == 0 ? Severity.Success : Severity.Warning);
        return Task.CompletedTask;
    }

    private void PopulateEditor(TagGovernanceSnapshot snapshot)
    {
        _policyMode = snapshot.Policy.Mode;
        _plainMode = snapshot.Policy.PlainTags.Mode;
        _allowlistText = string.Join(Environment.NewLine, snapshot.Policy.PlainTags.Allowlist);
        _blocklistText = string.Join(Environment.NewLine, snapshot.Policy.PlainTags.Blocklist);
        _aliasesText = string.Join(Environment.NewLine, snapshot.Policy.PlainTags.Aliases.Select(alias => $"{alias.Key} = {alias.Value}"));
        _namespaceRows = snapshot.Policy.Namespaces.Select(NamespaceEditRow.FromPolicy).ToList();
        _policyLoadStatus = snapshot.PolicyLoadStatus;
        _tagUsage = snapshot.Tags.ToList();
        _suggestions = snapshot.Suggestions.ToList();
        _policyDiagnostics = snapshot.PolicyDiagnostics.ToList();
    }

    private TagPolicy BuildPolicyFromEditor(out List<MemoryDiagnostic> inputDiagnostics)
    {
        inputDiagnostics = [];
        return new TagPolicy
        {
            SchemaVersion = 1,
            Mode = _policyMode,
            Namespaces = _namespaceRows.Select(row => row.ToPolicy()).ToList(),
            PlainTags = new PlainTagPolicy
            {
                Mode = _plainMode,
                Allowlist = SplitLines(_allowlistText),
                Blocklist = SplitLines(_blocklistText),
                Aliases = ParseAliases(_aliasesText, inputDiagnostics)
            }
        };
    }

    private void AddNamespace()
    {
        _namespaceRows.Add(new NamespaceEditRow { Cardinality = "many", ValueKind = "tag" });
    }

    private void RemoveNamespace(NamespaceEditRow namespaceRow)
    {
        _namespaceRows.Remove(namespaceRow);
    }

    private Task ApproveSuggestionAsync(TagGovernanceSuggestion suggestion) =>
        ApplySuggestionDecisionAsync(suggestion, approve: true);

    private Task RejectSuggestionAsync(TagGovernanceSuggestion suggestion) =>
        ApplySuggestionDecisionAsync(suggestion, approve: false);

    private static string SuggestionDecisionTooltip(TagGovernanceSuggestion suggestion, bool approve)
    {
        var value = approve
            ? (string.IsNullOrWhiteSpace(suggestion.SuggestedValue) ? suggestion.Tag : suggestion.SuggestedValue)
            : suggestion.Tag;
        var action = approve
            ? $"Approve suggestion and add '{value}' to the allowlist."
            : $"Reject suggestion and add '{value}' to the blocklist.";

        return string.IsNullOrWhiteSpace(suggestion.Reason)
            ? action
            : $"{action} {suggestion.Reason}";
    }

    private Task ApplySuggestionDecisionAsync(TagGovernanceSuggestion suggestion, bool approve)
    {
        var value = approve
            ? (string.IsNullOrWhiteSpace(suggestion.SuggestedValue) ? suggestion.Tag : suggestion.SuggestedValue)
            : suggestion.Tag;
        if (string.IsNullOrWhiteSpace(value))
        {
            Snackbar.Add("Suggestion did not include a tag value.", Severity.Warning);
            return Task.CompletedTask;
        }

        _isBusy = true;
        var allowlist = SplitLines(_allowlistText);
        var blocklist = SplitLines(_blocklistText);
        if (approve)
        {
            AddDistinct(allowlist, value);
            blocklist.RemoveAll(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            AddDistinct(blocklist, value);
            allowlist.RemoveAll(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
        }

        _allowlistText = string.Join(Environment.NewLine, allowlist.OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        _blocklistText = string.Join(Environment.NewLine, blocklist.OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        var policy = BuildPolicyFromEditor(out var inputDiagnostics);
        var snapshot = TagGovernance.SavePolicy(policy);
        PopulateEditor(snapshot);
        _policyDiagnostics = inputDiagnostics.Concat(_policyDiagnostics).ToList();
        _isBusy = false;
        Snackbar.Add(approve ? $"Approved '{value}' for the allowlist." : $"Rejected '{value}' to the blocklist.", inputDiagnostics.Count == 0 ? Severity.Success : Severity.Warning);
        return Task.CompletedTask;
    }

    private static List<string> SplitLines(string text) =>
        text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void AddDistinct(List<string> values, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 0 && values.All(item => !string.Equals(item, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            values.Add(trimmed);
        }
    }

    private static Dictionary<string, string> ParseAliases(string text, List<MemoryDiagnostic> diagnostics)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                diagnostics.Add(new MemoryDiagnostic("tag.policy_alias_parse", "Warning", "tag", $"Alias line '{line}' should use source = canonical.", line));
                continue;
            }

            aliases[parts[0]] = parts[1];
        }

        return aliases;
    }

    private static string PolicyLabel(TagUsageSummary tag)
    {
        if (tag.IsBlocked)
        {
            return "Blocked";
        }

        if (tag.AliasTarget is not null)
        {
            return $"Alias -> {tag.AliasTarget}";
        }

        if (tag.IsAllowed)
        {
            return "Allowed";
        }

        return tag.IsNamespaced ? "Namespaced" : "Observed";
    }

    private static string DiagnosticClass(MemoryDiagnostic diagnostic) => diagnostic.Severity switch
    {
        "Warning" => "is-warning",
        "Error" => "is-error",
        _ => "is-info"
    };

    private static string PolicyLoadLabel(TagPolicyLoadStatus? status) => status is null
        ? "Policy status unknown"
        : status.LoadedFromFile
            ? "Loaded from file"
            : $"Using defaults ({status.Reason})";

    private static string PolicyLoadClass(TagPolicyLoadStatus? status) => status is null || !status.UsingFallback
        ? "tag-policy-source"
        : status.Reason == "missing"
            ? "tag-policy-source is-info"
            : "tag-policy-source is-warning";

    private sealed class NamespaceEditRow
    {
        public string Name { get; set; } = string.Empty;
        public string Cardinality { get; set; } = "many";
        public string ValueKind { get; set; } = "tag";
        public string AllowedValuesText { get; set; } = string.Empty;

        public static NamespaceEditRow FromPolicy(TagNamespacePolicy policy) => new()
        {
            Name = policy.Name,
            Cardinality = policy.Cardinality,
            ValueKind = policy.ValueKind,
            AllowedValuesText = string.Join(", ", policy.AllowedValues)
        };

        public TagNamespacePolicy ToPolicy() => new()
        {
            Name = Name,
            Cardinality = Cardinality,
            ValueKind = ValueKind,
            AllowedValues = SplitLines(AllowedValuesText)
        };
    }
}