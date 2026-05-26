using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public sealed record MaintenanceRunResult(
    string Trigger,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    IReadOnlyList<MaintenanceTaskOutput> Outputs,
    IReadOnlyList<string> Warnings,
    bool Skipped = false);

public sealed record MaintenanceRunActivity(
    string Trigger,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    IReadOnlyList<string> Tasks,
    int FindingCount,
    int ProposalCount,
    IReadOnlyList<string> ProposalIds,
    IReadOnlyList<string> Warnings,
    bool Skipped = false);

public sealed record MaintenanceActiveRunSnapshot(
    string RunId,
    string Trigger,
    string? Task,
    DateTimeOffset StartedAtUtc);

public sealed class MaintenanceActiveRunStore
{
    private readonly object _lock = new();
    private MaintenanceActiveRunSnapshot? _current;

    public MaintenanceActiveRunSnapshot? GetCurrent()
    {
        lock (_lock)
        {
            return _current;
        }
    }

    public MaintenanceActiveRunSnapshot Begin(string trigger, string? task, DateTimeOffset startedAtUtc)
    {
        var snapshot = new MaintenanceActiveRunSnapshot(Guid.NewGuid().ToString("N"), trigger, task, startedAtUtc);
        lock (_lock)
        {
            _current = snapshot;
        }

        return snapshot;
    }

    public void End(string runId)
    {
        lock (_lock)
        {
            if (string.Equals(_current?.RunId, runId, StringComparison.Ordinal))
            {
                _current = null;
            }
        }
    }
}

public sealed record MaintenanceAdminTranscriptEntry(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string UserMessage,
    string AssistantMessage,
    string? Provider,
    string? Model,
    IReadOnlyList<string> Warnings);

public sealed record MaintenanceProposalReviewRunResult(
    MaintenanceWriteProposal Proposal,
    MaintenanceWriteProposal? RevisedProposal,
    IReadOnlyList<string> Warnings);

public sealed record MaintenanceProposalReviewEnvelope(
    string? Recommendation,
    IReadOnlyList<string>? Comments,
    double? Confidence,
    MaintenanceWriteProposal? RevisedProposal = null);

public sealed record MaintenanceTaskOutput(
    [property: JsonPropertyName("task")] string Task,
    [property: JsonPropertyName("findings")] IReadOnlyList<MaintenanceFinding> Findings,
    [property: JsonPropertyName("proposals")] IReadOnlyList<MaintenanceWriteProposal> Proposals,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("metadata")] Dictionary<string, object?> Metadata);

public sealed record MaintenanceFinding(
    string Id,
    string Title,
    string Severity,
    string Message,
    string? Path = null,
    IReadOnlyList<string>? RelatedRecords = null,
    Dictionary<string, object?>? Metadata = null);

public sealed record MaintenanceProposalChange(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("before")] string Before,
    [property: JsonPropertyName("after")] string After,
    [property: JsonPropertyName("diff")] string Diff = "");

public sealed record MaintenanceEvidenceItem(
    string Kind,
    string Citation,
    string? SourceLink = null,
    string? Reference = null,
    string? Excerpt = null);

public sealed record MaintenanceProposalMetadata(
    [property: JsonPropertyName("task")] string Task,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("risk_level")] string RiskLevel,
    [property: JsonPropertyName("related_records")] IReadOnlyList<string> RelatedRecords,
    [property: JsonPropertyName("supersedes")] IReadOnlyList<string> Supersedes,
    [property: JsonPropertyName("superseded_by")] IReadOnlyList<string> SupersededBy,
    [property: JsonPropertyName("agent_version")] string AgentVersion,
    [property: JsonPropertyName("batchId")] string? BatchId = null,
    [property: JsonPropertyName("parentProposalId")] string? ParentProposalId = null,
    [property: JsonPropertyName("attempt")] int Attempt = 1);

public sealed record MaintenanceProposalHistoryEntry(
    string Action,
    string User,
    DateTimeOffset Timestamp,
    string? Comment = null);

public sealed record MaintenanceProposalComment(
    string User,
    DateTimeOffset Timestamp,
    string Comment);

public sealed record MaintenanceWriteProposal
{
    [JsonPropertyName("proposal_id")]
    public string ProposalId { get; init; } = Guid.NewGuid().ToString("D");

    [JsonPropertyName("changes")]
    public IReadOnlyList<MaintenanceProposalChange> Changes { get; init; } = [];

    [JsonPropertyName("evidence")]
    public IReadOnlyList<MaintenanceEvidenceItem> Evidence { get; init; } = [];

    [JsonPropertyName("related_records")]
    public IReadOnlyList<string> RelatedRecords { get; init; } = [];

    [JsonPropertyName("risk_level")]
    public string RiskLevel { get; init; } = MaintenanceProposalRiskLevels.Low;

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = MaintenanceProposalStatuses.Open;

    [JsonPropertyName("history")]
    public IReadOnlyList<MaintenanceProposalHistoryEntry> History { get; init; } = [];

    [JsonPropertyName("metadata")]
    public MaintenanceProposalMetadata Metadata { get; init; } = new(
        "maintenance",
        0,
        MaintenanceProposalRiskLevels.Low,
        [],
        [],
        [],
        "maintenance-agent.v1");

    [JsonPropertyName("comments")]
    public IReadOnlyList<MaintenanceProposalComment> Comments { get; init; } = [];

    [JsonPropertyName("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updated_at_utc")]
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public static class MaintenanceProposalStatuses
{
    public const string Open = "open";
    public const string NeedsRevision = "needs_revision";
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Open,
        NeedsRevision,
        Approved,
        Rejected
    };
}

public static class MaintenanceProposalRiskLevels
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
}

public sealed record MaintenanceResourceSnapshot(bool IsBusy, IReadOnlyList<string> MatchingProcesses, string? Reason = null);

public sealed class MaintenanceAgentConfigService
{
    private static readonly string DefaultReadMemoriesRoot = Path.Combine("..", "Data", "Memories");
    private static readonly string DefaultReadPagesRoot = Path.Combine("..", "Data", "Pages");
    private static readonly string DefaultWriteWorkingRoot = Path.Combine("..", "Data", "Memories", "Working");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly IOptionsMonitor<MemorySmithOptions> _options;

    public MaintenanceAgentConfigService(IOptionsMonitor<MemorySmithOptions> options)
    {
        _options = options;
    }

    public MaintenanceAgentOptions GetCurrent(MaintenanceAgentModelPurpose purpose = MaintenanceAgentModelPurpose.MaintenanceRun)
    {
        var options = Clone(_options.CurrentValue.MaintenanceAgent);
        Normalize(options, purpose);
        return options;
    }

    public IReadOnlyList<string> GetChatProposalWriteRoots()
    {
        var appOptions = _options.CurrentValue;
        return [Path.Combine(appOptions.DataPath, "Working"), appOptions.PagesPath];
    }

    public string ResolvePath(string path) => Path.GetFullPath(path);

    private MaintenanceAgentOptions Clone(MaintenanceAgentOptions source) =>
        JsonSerializer.Deserialize<MaintenanceAgentOptions>(JsonSerializer.Serialize(source, JsonOptions), JsonOptions) ?? new MaintenanceAgentOptions();

    private void Normalize(MaintenanceAgentOptions config, MaintenanceAgentModelPurpose purpose)
    {
        var appOptions = _options.CurrentValue;
        ApplyAssignedModelProfile(config, appOptions, purpose);
        config.Read = NormalizeRoots(
            config.Read,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [DefaultReadMemoriesRoot] = appOptions.DataPath,
                [DefaultReadPagesRoot] = appOptions.PagesPath
            },
            [appOptions.DataPath, appOptions.PagesPath]);

        config.Write = NormalizeRoots(
            config.Write,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [DefaultWriteWorkingRoot] = Path.Combine(appOptions.DataPath, "Working"),
                [DefaultReadPagesRoot] = appOptions.PagesPath
            },
            [Path.Combine(appOptions.DataPath, "Working"), appOptions.PagesPath]);

        if (string.IsNullOrWhiteSpace(config.Provider))
        {
            config.Provider = "Ollama";
        }

        if (string.IsNullOrWhiteSpace(config.Model))
        {
            config.Model = appOptions.Chat.OllamaModel;
        }

        if (string.IsNullOrWhiteSpace(config.OllamaEndpoint))
        {
            config.OllamaEndpoint = appOptions.Chat.OllamaEndpoint;
        }

        if (string.IsNullOrWhiteSpace(config.AgentVersion))
        {
            config.AgentVersion = "maintenance-agent.v1";
        }
    }

    private static List<string> NormalizeRoots(List<string> configuredRoots, IReadOnlyDictionary<string, string> defaultMappings, IReadOnlyList<string> fallbackRoots)
    {
        if (configuredRoots.Count == 0)
        {
            return fallbackRoots.ToList();
        }

        return configuredRoots
            .Select(root => defaultMappings.TryGetValue(root, out var mappedRoot) ? mappedRoot : root)
            .ToList();
    }

    private static void ApplyAssignedModelProfile(MaintenanceAgentOptions config, MemorySmithOptions appOptions, MaintenanceAgentModelPurpose purpose)
    {
        var assignmentId = purpose switch
        {
            MaintenanceAgentModelPurpose.ProposalReview => FirstNonEmpty(config.ProposalReviewModelProfileId, config.ModelProfileId),
            MaintenanceAgentModelPurpose.AdminChat => FirstNonEmpty(config.AdminChatModelProfileId, config.ModelProfileId),
            _ => config.ModelProfileId
        };
        if (string.IsNullOrWhiteSpace(assignmentId))
        {
            return;
        }

        var profile = appOptions.Chat.ModelProfiles.FirstOrDefault(candidate =>
            candidate.Enabled && string.Equals(candidate.Id, assignmentId, StringComparison.OrdinalIgnoreCase));
        if (profile is null || string.IsNullOrWhiteSpace(profile.Model))
        {
            return;
        }

        config.Provider = string.IsNullOrWhiteSpace(profile.Provider) ? config.Provider : profile.Provider;
        config.Model = profile.Model;
        if (string.Equals(config.Provider, "Ollama", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(config.OllamaEndpoint))
        {
            config.OllamaEndpoint = appOptions.Chat.OllamaEndpoint;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

public enum MaintenanceAgentModelPurpose
{
    MaintenanceRun,
    ProposalReview,
    AdminChat
}

public sealed class MaintenanceResourceProbe
{
    public Task<MaintenanceResourceSnapshot> ProbeAsync(MaintenanceAgentOptions config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!config.ResourceProbe.Enabled || !config.ResourceProbe.SkipWhenBusy)
        {
            return Task.FromResult(new MaintenanceResourceSnapshot(false, []));
        }

        var configuredNames = config.ResourceProbe.BusyProcessNames
            .Select(NormalizeProcessName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (configuredNames.Count == 0)
        {
            return Task.FromResult(new MaintenanceResourceSnapshot(false, []));
        }

        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var processName = NormalizeProcessName(process.ProcessName);
                if (configuredNames.Contains(processName))
                {
                    matches.Add(process.ProcessName);
                }
            }
            catch
            {
                // Process metadata can disappear mid-enumeration; skip it.
            }
            finally
            {
                process.Dispose();
            }
        }

        var ordered = matches.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        return Task.FromResult(new MaintenanceResourceSnapshot(
            ordered.Count > 0,
            ordered,
            ordered.Count > 0 ? "Configured busy processes are running; scheduled maintenance should wait." : null));
    }

    private static string NormalizeProcessName(string value) =>
        Path.GetFileNameWithoutExtension(value.Trim()).ToLowerInvariant();
}

public sealed class MaintenanceDiffService
{
    public string BuildUnifiedDiff(string path, string before, string after)
    {
        var beforeLines = SplitLines(before);
        var afterLines = SplitLines(after);
        var diff = new List<string>
        {
            $"--- {path}",
            $"+++ {path}"
        };

        var common = BuildCommonSubsequenceTable(beforeLines, afterLines);
        AppendDiff(beforeLines, afterLines, common, beforeLines.Count, afterLines.Count, diff);
        return string.Join(Environment.NewLine, diff);
    }

    public MaintenanceProposalChange WithDiff(MaintenanceProposalChange change) =>
        change with { Diff = string.IsNullOrWhiteSpace(change.Diff) ? BuildUnifiedDiff(change.Path, change.Before, change.After) : change.Diff };

    private static List<string> SplitLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();

    private static int[,] BuildCommonSubsequenceTable(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var table = new int[left.Count + 1, right.Count + 1];
        for (var i = 1; i <= left.Count; i++)
        {
            for (var j = 1; j <= right.Count; j++)
            {
                table[i, j] = string.Equals(left[i - 1], right[j - 1], StringComparison.Ordinal)
                    ? table[i - 1, j - 1] + 1
                    : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }

        return table;
    }

    private static void AppendDiff(IReadOnlyList<string> before, IReadOnlyList<string> after, int[,] table, int i, int j, List<string> diff)
    {
        if (i > 0 && j > 0 && string.Equals(before[i - 1], after[j - 1], StringComparison.Ordinal))
        {
            AppendDiff(before, after, table, i - 1, j - 1, diff);
            diff.Add(" " + before[i - 1]);
        }
        else if (j > 0 && (i == 0 || table[i, j - 1] >= table[i - 1, j]))
        {
            AppendDiff(before, after, table, i, j - 1, diff);
            diff.Add("+" + after[j - 1]);
        }
        else if (i > 0)
        {
            AppendDiff(before, after, table, i - 1, j, diff);
            diff.Add("-" + before[i - 1]);
        }
    }
}

public sealed class MaintenanceWritePermissionService
{
    private readonly MaintenanceAgentConfigService _config;

    public MaintenanceWritePermissionService(MaintenanceAgentConfigService config)
    {
        _config = config;
    }

    public string ValidateWritablePath(string path, IEnumerable<string>? additionalAllowedRoots = null)
    {
        var config = _config.GetCurrent();
        var fullPath = Path.GetFullPath(path);
        if (IsProhibitedPath(fullPath))
        {
            throw new InvalidOperationException("Maintenance proposals cannot modify schema or configuration files.");
        }

        var allowedRoots = config.Write.Concat(additionalAllowedRoots ?? []).Select(_config.ResolvePath).ToList();
        if (!allowedRoots.Any(root => IsUnderPath(fullPath, root)))
        {
            throw new InvalidOperationException($"Path '{path}' is outside the configured maintenance write directories.");
        }

        return fullPath;
    }

    public string ValidateReadablePath(string path)
    {
        var config = _config.GetCurrent();
        var fullPath = Path.GetFullPath(path);
        var allowedRoots = config.Read.Select(_config.ResolvePath).ToList();
        if (!allowedRoots.Any(root => IsUnderPath(fullPath, root)))
        {
            throw new InvalidOperationException($"Path '{path}' is outside the configured maintenance read directories.");
        }

        return fullPath;
    }

    private static bool IsUnderPath(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProhibitedPath(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        var extension = Path.GetExtension(fullPath);
        var segments = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => string.Equals(segment, "Schemas", StringComparison.OrdinalIgnoreCase)) ||
            fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("maintenance_agent.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("maintenance_agent.yaml", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".targets", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".yml", StringComparison.OrdinalIgnoreCase);
    }
}

public interface IMaintenanceProposalStore
{
    Task<IReadOnlyList<MaintenanceWriteProposal>> ListAsync(CancellationToken cancellationToken);
    Task<MaintenanceWriteProposal?> GetAsync(string proposalId, CancellationToken cancellationToken);
    Task<MaintenanceWriteProposal> SaveAsync(MaintenanceWriteProposal proposal, CancellationToken cancellationToken);
    Task ApplyAsync(MaintenanceWriteProposal proposal, CancellationToken cancellationToken);
}

public sealed class FileMaintenanceProposalStore : IMaintenanceProposalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly MaintenanceAgentConfigService _config;
    private readonly MaintenanceWritePermissionService _permissions;
    private readonly MaintenanceDiffService _diff;
    private readonly object _lock = new();

    public FileMaintenanceProposalStore(
        MaintenanceAgentConfigService config,
        MaintenanceWritePermissionService permissions,
        MaintenanceDiffService diff)
    {
        _config = config;
        _permissions = permissions;
        _diff = diff;
    }

    public Task<IReadOnlyList<MaintenanceWriteProposal>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var proposals = Directory.Exists(ProposalsPath)
                ? Directory.EnumerateFiles(ProposalsPath, "*.json", SearchOption.TopDirectoryOnly)
                    .Select(ReadProposal)
                    .Where(proposal => proposal is not null)
                    .Cast<MaintenanceWriteProposal>()
                    .OrderByDescending(proposal => proposal.UpdatedAtUtc)
                    .ThenBy(proposal => proposal.ProposalId, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
            return Task.FromResult<IReadOnlyList<MaintenanceWriteProposal>>(proposals);
        }
    }

    public Task<MaintenanceWriteProposal?> GetAsync(string proposalId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var path = GetProposalPath(proposalId);
            return Task.FromResult(File.Exists(path) ? ReadProposal(path) : null);
        }
    }

    public Task<MaintenanceWriteProposal> SaveAsync(MaintenanceWriteProposal proposal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = proposal with
        {
            Changes = proposal.Changes.Select(_diff.WithDiff).ToList(),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        var additionalWriteRoots = AdditionalWriteRootsFor(normalized);

        foreach (var change in normalized.Changes)
        {
            _permissions.ValidateWritablePath(change.Path, additionalWriteRoots);
        }

        lock (_lock)
        {
            Directory.CreateDirectory(ProposalsPath);
            File.WriteAllText(GetProposalPath(normalized.ProposalId), JsonSerializer.Serialize(normalized, JsonOptions) + Environment.NewLine);
        }

        return Task.FromResult(normalized);
    }

    public Task ApplyAsync(MaintenanceWriteProposal proposal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var additionalWriteRoots = AdditionalWriteRootsFor(proposal);
            var validatedChanges = proposal.Changes
                .Select(change => (Change: change, FullPath: _permissions.ValidateWritablePath(change.Path, additionalWriteRoots)))
                .ToList();

            foreach (var item in validatedChanges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = File.Exists(item.FullPath) ? File.ReadAllText(item.FullPath) : string.Empty;
                if (!string.Equals(current, item.Change.Before, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Current file content for '{item.Change.Path}' no longer matches the proposal.");
                }
            }

            foreach (var item in validatedChanges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(item.FullPath)!);
                File.WriteAllText(item.FullPath, item.Change.After);
            }
        }

        return Task.CompletedTask;
    }

    private IReadOnlyList<string> AdditionalWriteRootsFor(MaintenanceWriteProposal proposal) =>
        IsChatAgentProposal(proposal) ? _config.GetChatProposalWriteRoots() : [];

    private static bool IsChatAgentProposal(MaintenanceWriteProposal proposal) =>
        proposal.Metadata.AgentVersion.StartsWith("chat-agent.", StringComparison.OrdinalIgnoreCase);

    private string ProposalsPath => _config.ResolvePath(_config.GetCurrent().Storage.ProposalsPath);

    private string GetProposalPath(string proposalId) =>
        Path.Combine(ProposalsPath, Regex.Replace(proposalId, "[^a-zA-Z0-9_.-]", "-") + ".json");

    private static MaintenanceWriteProposal? ReadProposal(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<MaintenanceWriteProposal>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class MaintenanceProposalWorkflow
{
    private readonly IMaintenanceProposalStore _store;
    private readonly ICurrentUserContext _currentUser;
    private readonly AuditLogService? _audit;

    public MaintenanceProposalWorkflow(IMaintenanceProposalStore store, ICurrentUserContext currentUser, AuditLogService? audit = null)
    {
        _store = store;
        _currentUser = currentUser;
        _audit = audit;
    }

    public Task<IReadOnlyList<MaintenanceWriteProposal>> ListAsync(CancellationToken cancellationToken) =>
        _store.ListAsync(cancellationToken);

    public Task<MaintenanceWriteProposal?> GetAsync(string proposalId, CancellationToken cancellationToken) =>
        _store.GetAsync(proposalId, cancellationToken);

    public async Task<MaintenanceWriteProposal> SubmitAsync(MaintenanceWriteProposal proposal, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = NormalizeLineage(proposal.Metadata, proposal.ProposalId);
        var history = proposal.History.ToList();
        if (history.Count == 0)
        {
            history.Add(new MaintenanceProposalHistoryEntry("open", Actor(), now, ProposalSubmittedComment(metadata)));
        }

        var normalized = proposal with
        {
            Status = MaintenanceProposalStatuses.Open,
            Metadata = metadata,
            History = history,
            CreatedAtUtc = proposal.CreatedAtUtc == default ? now : proposal.CreatedAtUtc,
            UpdatedAtUtc = now
        };
        var saved = await _store.SaveAsync(normalized, cancellationToken);
        await RecordAuditAsync("maintenance.proposal.opened", saved, cancellationToken);
        return saved;
    }

    public async Task<MaintenanceWriteProposal> ApproveAsync(string proposalId, string? comment, CancellationToken cancellationToken)
    {
        var proposal = await LoadRequiredAsync(proposalId, cancellationToken);
        EnsureOpen(proposal, "Only open proposals can be approved.");
        await _store.ApplyAsync(proposal, cancellationToken);
        var updated = AppendHistory(proposal, MaintenanceProposalStatuses.Approved, "approve", comment);
        var saved = await _store.SaveAsync(updated, cancellationToken);
        await RecordAuditAsync("maintenance.proposal.approved", saved, cancellationToken);
        return saved;
    }

    public async Task<MaintenanceWriteProposal> RespondAsync(string proposalId, string comment, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            throw new ArgumentException("Respond requires a human comment.", nameof(comment));
        }

        var proposal = await LoadRequiredAsync(proposalId, cancellationToken);
        EnsureOpen(proposal, "Only open proposals can be sent back for revision.");
        var comments = proposal.Comments.ToList();
        comments.Add(new MaintenanceProposalComment(Actor(), DateTimeOffset.UtcNow, comment.Trim()));
        var updated = AppendHistory(proposal with { Comments = comments }, MaintenanceProposalStatuses.NeedsRevision, "respond", null);
        var saved = await _store.SaveAsync(updated, cancellationToken);
        await RecordAuditAsync("maintenance.proposal.responded", saved, cancellationToken);
        return saved;
    }

    public async Task<MaintenanceWriteProposal> RequestAgentReviewAsync(string proposalId, string? comment, CancellationToken cancellationToken)
    {
        var proposal = await LoadRequiredAsync(proposalId, cancellationToken);
        EnsureActionable(proposal);

        var requestComment = string.IsNullOrWhiteSpace(comment) ? "Agent review requested." : comment.Trim();
        var comments = proposal.Comments.ToList();
        comments.Add(new MaintenanceProposalComment(Actor(), DateTimeOffset.UtcNow, requestComment));
        var updated = AppendHistory(proposal with { Comments = comments }, proposal.Status, "agent_review_requested", null);
        var saved = await _store.SaveAsync(updated, cancellationToken);
        await RecordAuditAsync("maintenance.proposal.agent_review_requested", saved, cancellationToken);
        return saved;
    }

    public async Task<MaintenanceWriteProposal> RecordAgentReviewAsync(string proposalId, string comment, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            throw new ArgumentException("Agent review requires a comment.", nameof(comment));
        }

        var proposal = await LoadRequiredAsync(proposalId, cancellationToken);
        EnsureActionable(proposal);
        var comments = proposal.Comments.ToList();
        comments.Add(new MaintenanceProposalComment("Maintenance agent", DateTimeOffset.UtcNow, comment.Trim()));
        var updated = AppendHistory(proposal with { Comments = comments }, proposal.Status, "agent_review_completed", null, "Maintenance agent");
        var saved = await _store.SaveAsync(updated, cancellationToken);
        await RecordAuditAsync("maintenance.proposal.agent_review_completed", saved, cancellationToken);
        return saved;
    }

    public async Task<MaintenanceWriteProposal> SubmitAgentRevisionAsync(string originalProposalId, MaintenanceWriteProposal revisedProposal, CancellationToken cancellationToken)
    {
        var original = await LoadRequiredAsync(originalProposalId, cancellationToken);
        EnsureActionable(original);
        var revisionMetadata = revisedProposal.Metadata with
        {
            BatchId = string.IsNullOrWhiteSpace(revisedProposal.Metadata.BatchId) ? LineageBatchId(original) : revisedProposal.Metadata.BatchId.Trim(),
            ParentProposalId = string.IsNullOrWhiteSpace(revisedProposal.Metadata.ParentProposalId) ? original.ProposalId : revisedProposal.Metadata.ParentProposalId.Trim(),
            Attempt = NextLineageAttempt(original, revisedProposal.Metadata.Attempt),
            Supersedes = revisedProposal.Metadata.Supersedes.Append(original.ProposalId).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
        var revision = revisedProposal with
        {
            Metadata = revisionMetadata,
            History = revisedProposal.History.Append(new MaintenanceProposalHistoryEntry("open", "Maintenance agent", DateTimeOffset.UtcNow, $"Agent review proposed this revision. Lineage: {FormatProposalLineage(revisionMetadata)}.")).ToList()
        };
        var savedRevision = await SubmitAsync(revision, cancellationToken);
        var updatedOriginal = original with
        {
            Metadata = original.Metadata with
            {
                SupersededBy = original.Metadata.SupersededBy.Append(savedRevision.ProposalId).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            },
            History = original.History.Append(new MaintenanceProposalHistoryEntry("agent_revision_proposed", "Maintenance agent", DateTimeOffset.UtcNow, $"Revision proposed as {savedRevision.ProposalId}. Lineage: {FormatProposalLineage(savedRevision.Metadata)}.")).ToList(),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        var savedOriginal = await _store.SaveAsync(updatedOriginal, cancellationToken);
        await RecordAuditAsync("maintenance.proposal.agent_revision_proposed", savedOriginal, cancellationToken);
        return savedRevision;
    }

    public async Task<MaintenanceWriteProposal> RejectAsync(string proposalId, string? comment, CancellationToken cancellationToken)
    {
        var proposal = await LoadRequiredAsync(proposalId, cancellationToken);
        if (string.Equals(proposal.Status, MaintenanceProposalStatuses.Approved, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Approved proposals cannot be rejected.");
        }

        var updated = AppendHistory(proposal, MaintenanceProposalStatuses.Rejected, "reject", comment);
        var saved = await _store.SaveAsync(updated, cancellationToken);
        await RecordAuditAsync("maintenance.proposal.rejected", saved, cancellationToken);
        return saved;
    }

    public async Task<MaintenanceWriteProposal> SubmitRevisionAsync(string supersededProposalId, MaintenanceWriteProposal revisedProposal, CancellationToken cancellationToken)
    {
        var original = await LoadRequiredAsync(supersededProposalId, cancellationToken);
        if (!string.Equals(original.Status, MaintenanceProposalStatuses.NeedsRevision, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only proposals that need revision can be superseded by a revision.");
        }

        var revised = revisedProposal with
        {
            Metadata = revisedProposal.Metadata with
            {
                BatchId = string.IsNullOrWhiteSpace(revisedProposal.Metadata.BatchId) ? LineageBatchId(original) : revisedProposal.Metadata.BatchId.Trim(),
                ParentProposalId = string.IsNullOrWhiteSpace(revisedProposal.Metadata.ParentProposalId) ? original.ProposalId : revisedProposal.Metadata.ParentProposalId.Trim(),
                Attempt = NextLineageAttempt(original, revisedProposal.Metadata.Attempt),
                Supersedes = revisedProposal.Metadata.Supersedes.Append(original.ProposalId).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            }
        };
        var savedRevision = await SubmitAsync(revised, cancellationToken);
        var updatedOriginal = original with
        {
            Metadata = original.Metadata with
            {
                SupersededBy = original.Metadata.SupersededBy.Append(savedRevision.ProposalId).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            },
            History = original.History.Append(new MaintenanceProposalHistoryEntry("superseded", Actor(), DateTimeOffset.UtcNow, $"Revision submitted as {savedRevision.ProposalId}. Lineage: {FormatProposalLineage(savedRevision.Metadata)}.")).ToList(),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await _store.SaveAsync(updatedOriginal, cancellationToken);
        return savedRevision;
    }

    private async Task<MaintenanceWriteProposal> LoadRequiredAsync(string proposalId, CancellationToken cancellationToken) =>
        await _store.GetAsync(proposalId, cancellationToken) ?? throw new InvalidOperationException($"Proposal '{proposalId}' was not found.");

    private static void EnsureActionable(MaintenanceWriteProposal proposal)
    {
        if (string.Equals(proposal.Status, MaintenanceProposalStatuses.Approved, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(proposal.Status, MaintenanceProposalStatuses.Rejected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Closed proposals cannot be changed.");
        }
    }

    private static void EnsureOpen(MaintenanceWriteProposal proposal, string message)
    {
        EnsureActionable(proposal);
        if (!string.Equals(proposal.Status, MaintenanceProposalStatuses.Open, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static MaintenanceProposalMetadata NormalizeLineage(MaintenanceProposalMetadata metadata, string proposalId)
    {
        var batchId = string.IsNullOrWhiteSpace(metadata.BatchId) ? proposalId : metadata.BatchId.Trim();
        var parentProposalId = string.IsNullOrWhiteSpace(metadata.ParentProposalId) ? null : metadata.ParentProposalId.Trim();
        var attempt = metadata.Attempt <= 0 ? 1 : metadata.Attempt;
        return metadata with { BatchId = batchId, ParentProposalId = parentProposalId, Attempt = attempt };
    }

    private static string LineageBatchId(MaintenanceWriteProposal proposal) =>
        string.IsNullOrWhiteSpace(proposal.Metadata.BatchId) ? proposal.ProposalId : proposal.Metadata.BatchId.Trim();

    private static int NextLineageAttempt(MaintenanceWriteProposal original, int requestedAttempt)
    {
        if (requestedAttempt > 1)
        {
            return requestedAttempt;
        }

        return Math.Max(original.Metadata.Attempt, 1) + 1;
    }

    private static string ProposalSubmittedComment(MaintenanceProposalMetadata metadata) =>
        $"Proposal submitted. Lineage: {FormatProposalLineage(metadata)}.";

    private static string FormatProposalLineage(MaintenanceProposalMetadata metadata) =>
        $"batchId={LineageValue(metadata.BatchId)}; parentProposalId={LineageValue(metadata.ParentProposalId)}; attempt={Math.Max(metadata.Attempt, 1)}";

    private static string LineageValue(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    private MaintenanceWriteProposal AppendHistory(MaintenanceWriteProposal proposal, string status, string action, string? comment, string? actor = null)
    {
        var history = proposal.History.ToList();
        history.Add(new MaintenanceProposalHistoryEntry(action, string.IsNullOrWhiteSpace(actor) ? Actor() : actor, DateTimeOffset.UtcNow, string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()));
        return proposal with
        {
            Status = status,
            History = history,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private string Actor() => string.IsNullOrWhiteSpace(_currentUser.DisplayName) ? "Unknown user" : _currentUser.DisplayName;

    private Task RecordAuditAsync(string action, MaintenanceWriteProposal proposal, CancellationToken cancellationToken) =>
        _audit?.RecordAsync(
            action,
            "MaintenanceProposal",
            proposal.ProposalId,
            MemorySmithAuditOutcomes.Success,
            details: new { proposal.Status, proposal.RiskLevel, proposal.Confidence, ChangeCount = proposal.Changes.Count },
            cancellationToken: cancellationToken) ?? Task.CompletedTask;
}

public sealed record TopicMapDocument(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<TopicMapNode> Nodes,
    IReadOnlyList<TopicMapEdge> Edges,
    IReadOnlyList<TopicCluster> Clusters,
    IReadOnlyList<string> OrphanedNodes,
    IReadOnlyList<IReadOnlyList<string>> SupersessionChains,
    IReadOnlyList<IReadOnlyList<string>> DependencyCycles,
    IReadOnlyDictionary<string, int> StalenessHeatmap);

public sealed record TopicMapNode(
    string Id,
    string Kind,
    string Title,
    string? Path,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Headings,
    DateTimeOffset? LastUpdatedUtc,
    string StalenessStatus);

public sealed record TopicMapEdge(string SourceId, string TargetId, string Type, string? SourcePath = null);

public sealed record TopicCluster(string Key, IReadOnlyList<string> NodeIds);

public sealed class MaintenanceTopicMapService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex HeadingPattern = new("^(#{1,6})\\s+(.+?)\\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex MarkdownLinkPattern = new("\\[[^\\]]+\\]\\(([^)]+)\\)", RegexOptions.Compiled);

    private readonly IMemoryStore _memoryStore;
    private readonly IPageService _pages;
    private readonly MaintenanceAgentConfigService _config;

    public MaintenanceTopicMapService(IMemoryStore memoryStore, IPageService pages, MaintenanceAgentConfigService config)
    {
        _memoryStore = memoryStore;
        _pages = pages;
        _config = config;
    }

    public async Task<TopicMapDocument> BuildAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var records = _memoryStore.LoadAll().ToList();
        var nodes = new List<TopicMapNode>();
        var edges = new List<TopicMapEdge>();
        var recordIds = records.Select(record => record.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            var staleness = ClassifyStaleness(record.Tags, record.LastUpdated);
            nodes.Add(new TopicMapNode(
                record.Id,
                "memory",
                string.IsNullOrWhiteSpace(record.Title) ? record.Id : record.Title,
                null,
                record.Tags,
                ExtractHeadings(record.Content),
                record.LastUpdated,
                staleness));

            edges.AddRange(record.References.Select(target => new TopicMapEdge(record.Id, target, "References")));
            edges.AddRange(record.Conflicts.Select(target => new TopicMapEdge(record.Id, target, "ConflictsWith")));
            edges.AddRange(ExtractTagRelationships(record.Id, record.Tags));
        }

        var pageSummaries = await _pages.ListAsync(cancellationToken);
        foreach (var summary in pageSummaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _pages.GetAsync(summary.Slug, cancellationToken);
            if (page is null)
            {
                continue;
            }

            var nodeId = "page:" + page.Slug;
            nodes.Add(new TopicMapNode(
                nodeId,
                "page",
                page.Title,
                page.RelativePath,
                ExtractPageTags(page.Markdown),
                ExtractHeadings(page.Markdown),
                page.LastUpdatedUtc,
                "current"));

            edges.AddRange(ExtractPageLinks(nodeId, page.Markdown));
            foreach (var recordId in recordIds.Where(id => page.Markdown.Contains(id, StringComparison.OrdinalIgnoreCase)))
            {
                edges.Add(new TopicMapEdge(nodeId, recordId, "Mentions", page.RelativePath));
            }
        }

        var document = new TopicMapDocument(
            DateTimeOffset.UtcNow,
            nodes.OrderBy(node => node.Kind, StringComparer.OrdinalIgnoreCase).ThenBy(node => node.Id, StringComparer.OrdinalIgnoreCase).ToList(),
            edges.OrderBy(edge => edge.SourceId, StringComparer.OrdinalIgnoreCase).ThenBy(edge => edge.Type, StringComparer.OrdinalIgnoreCase).ThenBy(edge => edge.TargetId, StringComparer.OrdinalIgnoreCase).ToList(),
            BuildClusters(nodes),
            FindOrphans(nodes, edges),
            BuildSupersessionChains(edges),
            FindDependencyCycles(edges),
            BuildStalenessHeatmap(nodes));

        await SaveCacheAsync(document, cancellationToken);
        return document;
    }

    public async Task<TopicMapDocument?> LoadCachedAsync(CancellationToken cancellationToken)
    {
        var path = _config.ResolvePath(_config.GetCurrent().Storage.TopicMapCachePath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<TopicMapDocument>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static string GenerateMermaid(TopicMapDocument document, int maxEdges = 80)
    {
        var lines = new List<string> { "graph TD" };
        foreach (var edge in document.Edges.Take(Math.Max(1, maxEdges)))
        {
            lines.Add($"    {SafeMermaidId(edge.SourceId)}[\"{EscapeMermaid(edge.SourceId)}\"] -->|{EscapeMermaid(edge.Type)}| {SafeMermaidId(edge.TargetId)}[\"{EscapeMermaid(edge.TargetId)}\"]");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task SaveCacheAsync(TopicMapDocument document, CancellationToken cancellationToken)
    {
        var path = _config.ResolvePath(_config.GetCurrent().Storage.TopicMapCachePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
    }

    private static IReadOnlyList<string> ExtractHeadings(string markdown) =>
        HeadingPattern.Matches(markdown ?? string.Empty)
            .Select(match => match.Groups[2].Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string> ExtractPageTags(string markdown)
    {
        var tags = new List<string>();
        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
            {
                tags.AddRange(trimmed[5..].Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<TopicMapEdge> ExtractTagRelationships(string sourceId, IEnumerable<string> tags)
    {
        foreach (var tag in tags)
        {
            var parts = tag.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1]))
            {
                continue;
            }

            var type = parts[0].ToLowerInvariant() switch
            {
                "supersedes" => "Supersedes",
                "superseded-by" => "SupersededBy",
                "depends-on" => "DependsOn",
                "dependson" => "DependsOn",
                "conflicts-with" => "ConflictsWith",
                _ => null
            };
            if (type is not null)
            {
                yield return new TopicMapEdge(sourceId, parts[1], type);
            }
        }
    }

    private static IEnumerable<TopicMapEdge> ExtractPageLinks(string sourceId, string markdown)
    {
        foreach (Match match in MarkdownLinkPattern.Matches(markdown ?? string.Empty))
        {
            var target = match.Groups[1].Value.Trim();
            if (target.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                var slug = target.Replace('\\', '/').TrimStart('/');
                if (slug.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    slug = slug[..^3];
                }

                yield return new TopicMapEdge(sourceId, "page:" + FilePageService.NormalizeSlug(slug), "LinksTo");
            }
        }
    }

    private static string ClassifyStaleness(IEnumerable<string> tags, DateTime lastUpdated)
    {
        var nowMonth = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        foreach (var tag in tags)
        {
            if (TryReadMonthTag(tag, "expires", out var expires) && expires <= nowMonth)
            {
                return "expired";
            }

            if (TryReadMonthTag(tag, "review-after", out var reviewAfter) && reviewAfter <= nowMonth)
            {
                return "review_due";
            }
        }

        return lastUpdated < DateTime.UtcNow.AddYears(-1) ? "old" : "current";
    }

    private static bool TryReadMonthTag(string tag, string prefix, out DateOnly value)
    {
        value = default;
        if (!tag.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return DateOnly.TryParseExact(tag[(prefix.Length + 1)..], "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    private static IReadOnlyList<TopicCluster> BuildClusters(IEnumerable<TopicMapNode> nodes) =>
        nodes
            .SelectMany(node => node.Tags.Where(tag => !tag.Contains(':')).Select(tag => (Tag: tag.ToLowerInvariant(), node.Id)))
            .GroupBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => new TopicCluster(group.Key, group.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(cluster => cluster.NodeIds.Count)
            .ThenBy(cluster => cluster.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string> FindOrphans(IReadOnlyList<TopicMapNode> nodes, IReadOnlyList<TopicMapEdge> edges)
    {
        var connected = edges.Select(edge => edge.SourceId).Concat(edges.Select(edge => edge.TargetId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return nodes.Where(node => !connected.Contains(node.Id)).Select(node => node.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<IReadOnlyList<string>> BuildSupersessionChains(IReadOnlyList<TopicMapEdge> edges)
    {
        var supersedes = edges.Where(edge => edge.Type is "Supersedes" or "SupersededBy").ToList();
        return supersedes
            .Select(edge => edge.Type == "Supersedes" ? new[] { edge.SourceId, edge.TargetId } : [edge.TargetId, edge.SourceId])
            .Select(chain => (IReadOnlyList<string>)chain)
            .ToList();
    }

    private static IReadOnlyList<IReadOnlyList<string>> FindDependencyCycles(IReadOnlyList<TopicMapEdge> edges)
    {
        var dependencies = edges.Where(edge => edge.Type == "DependsOn").ToList();
        var graph = dependencies.GroupBy(edge => edge.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.TargetId).ToList(), StringComparer.OrdinalIgnoreCase);
        var cycles = new List<IReadOnlyList<string>>();
        foreach (var node in graph.Keys)
        {
            Visit(node, [], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        return cycles;

        void Visit(string node, List<string> path, HashSet<string> seen)
        {
            if (seen.Contains(node))
            {
                var index = path.FindIndex(item => string.Equals(item, node, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    cycles.Add(path.Skip(index).Append(node).ToList());
                }

                return;
            }

            if (!graph.TryGetValue(node, out var next))
            {
                return;
            }

            var nextSeen = new HashSet<string>(seen, StringComparer.OrdinalIgnoreCase) { node };
            var nextPath = path.Append(node).ToList();
            foreach (var child in next)
            {
                Visit(child, nextPath, nextSeen);
            }
        }
    }

    private static IReadOnlyDictionary<string, int> BuildStalenessHeatmap(IEnumerable<TopicMapNode> nodes) =>
        nodes.GroupBy(node => node.StalenessStatus, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static string SafeMermaidId(string value) => "n" + Regex.Replace(value, "[^A-Za-z0-9_]", "_");

    private static string EscapeMermaid(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}

public sealed class MaintenanceAgentService
{
    private static readonly Regex TranscriptBearerPattern = new(@"\bBearer\s+[A-Za-z0-9._\-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TranscriptSecretPattern = new(@"\b(api[_-]?key|token|secret|password|authorization)\b\s*[:=]\s*[^\s,;]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly JsonSerializerOptions AgentJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions ActivityJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly MaintenanceAgentConfigService _config;
    private readonly MaintenanceResourceProbe _resourceProbe;
    private readonly MaintenanceTopicMapService _topicMap;
    private readonly MaintenanceProposalWorkflow _proposalWorkflow;
    private readonly IEnumerable<IChatProvider> _providers;
    private readonly ILogger<MaintenanceAgentService> _logger;
    private readonly MaintenanceActiveRunStore _activeRuns;
    private readonly IChatAgent? _chatAgent;

    public MaintenanceAgentService(
        MaintenanceAgentConfigService config,
        MaintenanceResourceProbe resourceProbe,
        MaintenanceTopicMapService topicMap,
        MaintenanceProposalWorkflow proposalWorkflow,
        IEnumerable<IChatProvider> providers,
        ILogger<MaintenanceAgentService> logger,
        MaintenanceActiveRunStore? activeRuns = null,
        IChatAgent? chatAgent = null)
    {
        _config = config;
        _resourceProbe = resourceProbe;
        _topicMap = topicMap;
        _proposalWorkflow = proposalWorkflow;
        _providers = providers;
        _logger = logger;
        _activeRuns = activeRuns ?? new MaintenanceActiveRunStore();
        _chatAgent = chatAgent;
    }

    public Task<MaintenanceRunResult> RunMaintenanceNowAsync(CancellationToken cancellationToken) =>
        RunAsync("run_maintenance_now", cancellationToken);

    public Task<MaintenanceRunResult> RunMaintenanceWeeklyAsync(CancellationToken cancellationToken) =>
        RunAsync("run_maintenance_weekly", cancellationToken);

    public Task<MaintenanceRunResult> RunMaintenanceOnDemandAsync(string task, CancellationToken cancellationToken) =>
        RunAsync("run_maintenance_on_demand", cancellationToken, task);

    public MaintenanceActiveRunSnapshot? GetActiveRun() => _activeRuns.GetCurrent();

    public async Task<IReadOnlyList<MaintenanceRunActivity>> ListRecentActivityAsync(int maxEntries, CancellationToken cancellationToken)
    {
        var config = _config.GetCurrent();
        var path = _config.ResolvePath(config.Storage.ActivityLogPath);
        if (!File.Exists(path))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        return lines
            .Reverse()
            .Select(TryParseActivity)
            .Where(activity => activity is not null)
            .Cast<MaintenanceRunActivity>()
            .Take(Math.Clamp(maxEntries, 1, 100))
            .ToList();
    }

    public async Task<IReadOnlyList<MaintenanceAdminTranscriptEntry>> ListRecentTranscriptsAsync(int maxEntries, CancellationToken cancellationToken, string? search = null)
    {
        var config = _config.GetCurrent();
        var path = _config.ResolvePath(config.Storage.TranscriptLogPath);
        if (!File.Exists(path))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        return lines
            .Reverse()
            .Select(TryParseTranscript)
            .Where(entry => entry is not null)
            .Cast<MaintenanceAdminTranscriptEntry>()
            .Where(entry => TranscriptMatches(entry, search))
            .Take(Math.Clamp(maxEntries, 1, 100))
            .ToList();
    }

    public async Task<MaintenanceAdminTranscriptEntry> SendAdminMessageAsync(string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new InvalidOperationException("Maintenance agent messages cannot be empty.");
        }

        var config = _config.GetCurrent(MaintenanceAgentModelPurpose.AdminChat);
        var prompt = message.Trim();
        var warnings = new List<string>();
        MaintenanceAdminTranscriptEntry entry;
        if (!config.UseLlm)
        {
            warnings.Add("Maintenance agent admin chat is disabled because LLM review is disabled for the maintenance agent.");
            entry = CreateTranscriptEntry(config, prompt, warnings[0], null, null, warnings);
            await AppendTranscriptAsync(config, entry, cancellationToken);
            return entry;
        }

        var provider = _providers.FirstOrDefault(candidate => string.Equals(candidate.Name, config.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            warnings.Add($"Maintenance agent admin chat is disabled because provider '{config.Provider}' is not registered.");
            entry = CreateTranscriptEntry(config, prompt, warnings[0], null, null, warnings);
            await AppendTranscriptAsync(config, entry, cancellationToken);
            return entry;
        }

        try
        {
            if (_chatAgent is not null)
            {
                var response = await _chatAgent.SendAsync(new MemoryChatRequest(
                    prompt,
                    MemoryChatMode.Chat,
                    History: [new ChatMessage("system", BuildAdminChatSystemPrompt(config))],
                    Model: config.Model,
                    Provider: config.Provider), cancellationToken);
                entry = CreateTranscriptEntry(config, prompt, response.Reply.Trim(), response.ProviderName, response.Model, warnings);
            }
            else
            {
                var response = await provider.CompleteAsync(new ChatProviderRequest(
                [
                    new ChatMessage("system", BuildAdminChatSystemPrompt(config)),
                    new ChatMessage("user", prompt)
                ], MemoryChatMode.Agent, config.Model, Provider: config.Provider), cancellationToken);
                entry = CreateTranscriptEntry(config, prompt, response.Content.Trim(), response.ProviderName, response.Model, warnings);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            warnings.Add($"Maintenance agent admin chat failed: {ex.Message}");
            entry = CreateTranscriptEntry(config, prompt, warnings[0], provider.Name, config.Model, warnings);
        }

        await AppendTranscriptAsync(config, entry, cancellationToken);
        return entry;
    }

    public async Task<MaintenanceProposalReviewRunResult> ReviewProposalAsync(string proposalId, string? requesterComment, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var requested = await _proposalWorkflow.RequestAgentReviewAsync(proposalId, requesterComment, cancellationToken);
        var config = _config.GetCurrent(MaintenanceAgentModelPurpose.ProposalReview);
        if (!config.UseLlm)
        {
            warnings.Add("Proposal review request was recorded, but LLM review is disabled for the maintenance agent.");
            return new MaintenanceProposalReviewRunResult(requested, null, warnings);
        }

        var provider = _providers.FirstOrDefault(candidate => string.Equals(candidate.Name, config.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            warnings.Add($"Proposal review request was recorded, but provider '{config.Provider}' is not registered.");
            return new MaintenanceProposalReviewRunResult(requested, null, warnings);
        }

        try
        {
            var providerResponse = await provider.CompleteAsync(new ChatProviderRequest(
            [
                new ChatMessage("system", "You are MemorySmith's proposal review agent. Return strict JSON with recommendation, comments, confidence, and optional revisedProposal."),
                new ChatMessage("user", BuildProposalReviewPrompt(requested, requesterComment, config))
            ], MemoryChatMode.Agent, config.Model, Provider: config.Provider), cancellationToken);
            var review = ParseProposalReview(providerResponse.Content);
            var reviewed = await _proposalWorkflow.RecordAgentReviewAsync(requested.ProposalId, FormatProposalReviewComment(review, providerResponse), cancellationToken);
            MaintenanceWriteProposal? revisedProposal = null;
            if (review.RevisedProposal is not null)
            {
                try
                {
                    revisedProposal = await _proposalWorkflow.SubmitAgentRevisionAsync(reviewed.ProposalId, NormalizeReviewRevision(review.RevisedProposal, reviewed, config), cancellationToken);
                    reviewed = await _proposalWorkflow.GetAsync(reviewed.ProposalId, cancellationToken) ?? reviewed;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Agent review comment was recorded, but the revised proposal was not saved: {ex.Message}");
                    _logger.LogWarning(ex, "Proposal review revision was not saved for {ProposalId}", reviewed.ProposalId);
                }
            }

            return new MaintenanceProposalReviewRunResult(reviewed, revisedProposal, warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            warnings.Add($"Proposal review request was recorded, but agent review failed: {ex.Message}");
            _logger.LogWarning(ex, "Proposal review failed for {ProposalId}", requested.ProposalId);
            var reviewed = await _proposalWorkflow.RecordAgentReviewAsync(requested.ProposalId, $"Agent review failed: {ex.Message}", cancellationToken);
            return new MaintenanceProposalReviewRunResult(reviewed, null, warnings);
        }
    }

    private async Task<MaintenanceRunResult> RunAsync(string trigger, CancellationToken cancellationToken, string? taskFilter = null)
    {
        var started = DateTimeOffset.UtcNow;
        var activeRun = _activeRuns.Begin(trigger, taskFilter, started);
        var config = _config.GetCurrent();
        var warnings = new List<string>();
        try
        {
            var resource = await _resourceProbe.ProbeAsync(config, cancellationToken);
            if (resource.IsBusy && trigger == "run_maintenance_weekly")
            {
                warnings.Add(resource.Reason ?? "Resource probe reported a busy session.");
                warnings.AddRange(resource.MatchingProcesses.Select(name => $"Busy process: {name}"));
                var skipped = new MaintenanceRunResult(trigger, started, DateTimeOffset.UtcNow, [], warnings, Skipped: true);
                await SaveRunStateAsync(config, skipped, cancellationToken);
                return skipped;
            }

            var topicMap = await _topicMap.BuildAsync(cancellationToken);
            var outputs = new List<MaintenanceTaskOutput>();
            foreach (var task in EnabledTasks(config, taskFilter))
            {
                var output = BuildDeterministicOutput(task, topicMap, config, started);
                outputs.Add(await SubmitOutputProposalsAsync(output, warnings, cancellationToken));
            }

            if (config.UseLlm)
            {
                var llmOutput = await TryRunLlmReviewAsync(config, outputs, warnings, cancellationToken);
                if (llmOutput is not null)
                {
                    outputs.Add(await SubmitOutputProposalsAsync(llmOutput, warnings, cancellationToken));
                }
                else
                {
                    warnings.Add("LLM review was skipped or unavailable; deterministic maintenance outputs were returned.");
                }
            }

            var result = new MaintenanceRunResult(trigger, started, DateTimeOffset.UtcNow, outputs, warnings);
            await SaveRunStateAsync(config, result, cancellationToken);
            return result;
        }
        finally
        {
            _activeRuns.End(activeRun.RunId);
        }
    }

    private static IEnumerable<string> EnabledTasks(MaintenanceAgentOptions config, string? taskFilter)
    {
        if (!string.IsNullOrWhiteSpace(taskFilter))
        {
            yield return taskFilter.Trim();
            yield break;
        }

        foreach (var task in config.Tasks.Where(item => item.Value).Select(item => item.Key).OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            yield return task;
        }
    }

    private async Task<MaintenanceTaskOutput> SubmitOutputProposalsAsync(MaintenanceTaskOutput output, List<string> warnings, CancellationToken cancellationToken)
    {
        if (output.Proposals.Count == 0)
        {
            return output;
        }

        var saved = new List<MaintenanceWriteProposal>();
        foreach (var proposal in output.Proposals)
        {
            try
            {
                saved.Add(await _proposalWorkflow.SubmitAsync(proposal, cancellationToken));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Proposal for task '{output.Task}' was not saved: {ex.Message}");
                _logger.LogWarning(ex, "Maintenance proposal for task {Task} was not saved", output.Task);
            }
        }

        return output with { Proposals = saved };
    }

    private static MaintenanceTaskOutput BuildDeterministicOutput(string task, TopicMapDocument topicMap, MaintenanceAgentOptions config, DateTimeOffset runStartedAtUtc)
    {
        var findings = task switch
        {
            "staleness_scan" => topicMap.Nodes
                .Where(node => node.StalenessStatus is "expired" or "review_due")
                .Select(node => new MaintenanceFinding(node.Id, node.Title, node.StalenessStatus == "expired" ? "high" : "medium", $"Node is {node.StalenessStatus.Replace('_', ' ')}.", node.Path, [node.Id]))
                .Take(config.MaxFindingsPerTask)
                .ToList(),
            "relationship_integrity" => topicMap.DependencyCycles
                .Select((cycle, index) => new MaintenanceFinding($"depends-cycle-{index + 1}", "DependsOn cycle", "high", "DependsOn cycle detected: " + string.Join(" -> ", cycle), RelatedRecords: cycle))
                .Concat(FindMissingSupersessionPairs(topicMap))
                .Take(config.MaxFindingsPerTask)
                .ToList(),
            "topic_map" => topicMap.OrphanedNodes
                .Select(id => new MaintenanceFinding(id, id, "low", "Node has no incoming or outgoing topic-map edges.", RelatedRecords: [id]))
                .Take(config.MaxFindingsPerTask)
                .ToList(),
            "embedding_chunking_maintenance" => topicMap.Nodes
                .Where(node => node.Kind == "page" && node.Headings.Count == 0)
                .Select(node => new MaintenanceFinding(node.Id, node.Title, "medium", "Page has no Markdown headings for heading-aware chunking.", node.Path, [node.Id]))
                .Take(config.MaxFindingsPerTask)
                .ToList(),
            _ => []
        };
        var proposals = BuildDeterministicProposals(task, findings, config, runStartedAtUtc);

        return new MaintenanceTaskOutput(
            task,
            findings,
            proposals,
            [],
            task == "synthesis" ? 0.45 : 0.78,
            new Dictionary<string, object?>
            {
                ["schemaVersion"] = "memorysmith.maintenance.task.v1",
                ["agentVersion"] = config.AgentVersion,
                ["directWrite"] = config.DirectWrite,
                ["nodeCount"] = topicMap.Nodes.Count,
                ["edgeCount"] = topicMap.Edges.Count
            });
    }

    private static IReadOnlyList<MaintenanceWriteProposal> BuildDeterministicProposals(
        string task,
        IReadOnlyList<MaintenanceFinding> findings,
        MaintenanceAgentOptions config,
        DateTimeOffset runStartedAtUtc)
    {
        if (findings.Count == 0)
        {
            return [];
        }

        var pagesRoot = ResolveWritablePagesRoot(config);
        if (pagesRoot is null)
        {
            return [];
        }

        var relatedRecords = findings
            .SelectMany(finding => finding.RelatedRecords ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("page:", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();
        var risk = findings.Any(finding => string.Equals(finding.Severity, "high", StringComparison.OrdinalIgnoreCase))
            ? MaintenanceProposalRiskLevels.High
            : findings.Any(finding => string.Equals(finding.Severity, "medium", StringComparison.OrdinalIgnoreCase))
                ? MaintenanceProposalRiskLevels.Medium
                : MaintenanceProposalRiskLevels.Low;
        var path = Path.Combine(pagesRoot, "maintenance-agent", $"{runStartedAtUtc:yyyyMMdd-HHmmssfff}-{Slugify(task)}.md");
        var markdown = BuildFindingsReviewPage(task, findings, runStartedAtUtc, config.AgentVersion);
        var proposalId = $"maintenance-{Slugify(task)}-{runStartedAtUtc:yyyyMMddHHmmssfff}";

        return
        [
            new MaintenanceWriteProposal
            {
                ProposalId = proposalId,
                Changes = [new MaintenanceProposalChange(path, string.Empty, markdown)],
                Evidence = findings.Take(20).Select(finding => new MaintenanceEvidenceItem(
                    "finding",
                    finding.Id,
                    Reference: finding.Path,
                    Excerpt: finding.Message)).ToList(),
                RelatedRecords = relatedRecords,
                RiskLevel = risk,
                Confidence = 0.78,
                Metadata = new MaintenanceProposalMetadata(task, 0.78, risk, relatedRecords, [], [], config.AgentVersion)
            }
        ];
    }

    private static string? ResolveWritablePagesRoot(MaintenanceAgentOptions config)
    {
        var candidate = config.Write
            .Select(Path.GetFullPath)
            .FirstOrDefault(path => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Equals("Pages", StringComparison.OrdinalIgnoreCase));
        return candidate;
    }

    private static string BuildFindingsReviewPage(string task, IReadOnlyList<MaintenanceFinding> findings, DateTimeOffset runStartedAtUtc, string agentVersion)
    {
        var lines = new List<string>
        {
            $"# Maintenance Review: {task.Replace('_', ' ')}",
            string.Empty,
            $"Generated: {runStartedAtUtc:O}",
            $"Agent version: {agentVersion}",
            string.Empty,
            "## Findings",
            string.Empty
        };

        foreach (var finding in findings)
        {
            lines.Add($"### {finding.Title}");
            lines.Add(string.Empty);
            lines.Add($"- Id: `{finding.Id}`");
            lines.Add($"- Severity: `{finding.Severity}`");
            if (!string.IsNullOrWhiteSpace(finding.Path))
            {
                lines.Add($"- Path: `{finding.Path}`");
            }

            if (finding.RelatedRecords is { Count: > 0 })
            {
                lines.Add("- Related records: " + string.Join(", ", finding.RelatedRecords.Select(record => $"`{record}`")));
            }

            lines.Add(string.Empty);
            lines.Add(finding.Message);
            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
    }

    private static string Slugify(string value)
    {
        var slug = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "maintenance" : slug;
    }

    private static IEnumerable<MaintenanceFinding> FindMissingSupersessionPairs(TopicMapDocument topicMap)
    {
        var supersedes = topicMap.Edges.Where(edge => edge.Type == "Supersedes").ToList();
        var supersededBy = topicMap.Edges.Where(edge => edge.Type == "SupersededBy").ToList();
        foreach (var edge in supersedes)
        {
            var hasReverse = supersededBy.Any(reverse =>
                string.Equals(reverse.SourceId, edge.TargetId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(reverse.TargetId, edge.SourceId, StringComparison.OrdinalIgnoreCase));
            if (!hasReverse)
            {
                yield return new MaintenanceFinding(
                    $"missing-superseded-by-{edge.SourceId}-{edge.TargetId}",
                    "Missing SupersededBy mirror",
                    "medium",
                    $"{edge.SourceId} supersedes {edge.TargetId}, but the reverse SupersededBy relation was not found.",
                    RelatedRecords: [edge.SourceId, edge.TargetId]);
            }
        }
    }

    private async Task<MaintenanceTaskOutput?> TryRunLlmReviewAsync(MaintenanceAgentOptions config, IReadOnlyList<MaintenanceTaskOutput> outputs, List<string> warnings, CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(candidate => string.Equals(candidate.Name, config.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            warnings.Add($"LLM review skipped: provider '{config.Provider}' is not registered.");
            return null;
        }

        try
        {
            var prompt = BuildLlmPrompt(config, outputs);
            var response = await provider.CompleteAsync(new ChatProviderRequest(
            [
                new ChatMessage("system", "You are MemorySmith's maintenance agent. Return only strict JSON using the required maintenance task envelope."),
                new ChatMessage("user", prompt)
            ], MemoryChatMode.Agent, config.Model, Provider: config.Provider), cancellationToken);
            var parsed = ParseTaskOutput(response.Content);
            return parsed with
            {
                Metadata = (parsed.Metadata ?? new Dictionary<string, object?>()).Concat(new Dictionary<string, object?>
                {
                    ["provider"] = response.ProviderName,
                    ["model"] = response.Model
                }).ToDictionary(item => item.Key, item => item.Value)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            warnings.Add($"LLM review skipped: {ex.Message}");
            _logger.LogWarning(ex, "Maintenance LLM review failed");
            return null;
        }
    }

    private static string BuildLlmPrompt(MaintenanceAgentOptions config, IReadOnlyList<MaintenanceTaskOutput> outputs) =>
        JsonSerializer.Serialize(new
        {
            instruction = "Review the deterministic maintenance findings. Return structured JSON with task, findings, proposals, warnings, confidence, metadata. Generate write proposals only, never direct writes.",
            config = new
            {
                config.Read,
                config.Write,
                config.DirectWrite,
                config.Tasks,
                config.AgentVersion
            },
            deterministicOutputs = outputs
        }, AgentJsonOptions);

    private static MaintenanceTaskOutput ParseTaskOutput(string json)
    {
        var payload = ExtractJsonObjectPayload(json);
        try
        {
            var deserialized = JsonSerializer.Deserialize<MaintenanceTaskOutput>(payload, AgentJsonOptions);
            if (deserialized is not null)
            {
                return NormalizeTaskOutput(deserialized);
            }
        }
        catch (JsonException)
        {
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        return new MaintenanceTaskOutput(
            root.TryGetProperty("task", out var task) ? task.GetString() ?? "llm_review" : "llm_review",
            [],
            [],
            root.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array
                ? warnings.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToList()
                : [],
            root.TryGetProperty("confidence", out var confidence) && confidence.TryGetDouble(out var value) ? value : 0.5,
            new Dictionary<string, object?> { ["schemaVersion"] = "memorysmith.maintenance.task.v1", ["source"] = "llm" });
    }

    private static MaintenanceTaskOutput NormalizeTaskOutput(MaintenanceTaskOutput output) =>
        output with
        {
            Task = string.IsNullOrWhiteSpace(output.Task) ? "llm_review" : output.Task,
            Findings = output.Findings is null ? [] : output.Findings,
            Proposals = output.Proposals is null ? [] : output.Proposals,
            Warnings = output.Warnings is null ? [] : output.Warnings,
            Metadata = output.Metadata ?? new Dictionary<string, object?>()
        };

    private static string BuildProposalReviewPrompt(MaintenanceWriteProposal proposal, string? requesterComment, MaintenanceAgentOptions config) =>
        JsonSerializer.Serialize(new
        {
            instruction = "Review this MemorySmith write proposal like a cautious pull request reviewer. Do not approve or apply writes. Return strict JSON with recommendation, comments, confidence, and optional revisedProposal. Include revisedProposal only when a concrete safer revision is warranted and it preserves the same proposal contract.",
            reviewerComment = string.IsNullOrWhiteSpace(requesterComment) ? null : requesterComment.Trim(),
            config = new
            {
                config.AgentVersion,
                config.Read,
                config.Write
            },
            proposal
        }, AgentJsonOptions);

    private static MaintenanceProposalReviewEnvelope ParseProposalReview(string content)
    {
        var payload = ExtractJsonObjectPayload(content);
        try
        {
            var parsed = JsonSerializer.Deserialize<MaintenanceProposalReviewEnvelope>(payload, AgentJsonOptions);
            if (parsed is not null)
            {
                return parsed with
                {
                    Recommendation = string.IsNullOrWhiteSpace(parsed.Recommendation) ? "reviewed" : parsed.Recommendation.Trim(),
                    Comments = parsed.Comments?.Where(comment => !string.IsNullOrWhiteSpace(comment)).Select(comment => comment.Trim()).ToList() ?? []
                };
            }
        }
        catch (JsonException)
        {
        }

        return new MaintenanceProposalReviewEnvelope("reviewed", [string.IsNullOrWhiteSpace(content) ? "Agent review returned no text." : content.Trim()], null);
    }

    private static string ExtractJsonObjectPayload(string content)
    {
        var trimmed = (content ?? string.Empty).Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = trimmed.IndexOf('\n');
            if (firstLineEnd >= 0)
            {
                trimmed = trimmed[(firstLineEnd + 1)..].Trim();
                if (trimmed.EndsWith("```", StringComparison.Ordinal))
                {
                    trimmed = trimmed[..^3].Trim();
                }
            }
        }

        var firstObjectBrace = trimmed.IndexOf('{');
        var lastObjectBrace = trimmed.LastIndexOf('}');
        return firstObjectBrace >= 0 && lastObjectBrace >= firstObjectBrace
            ? trimmed[firstObjectBrace..(lastObjectBrace + 1)]
            : trimmed;
    }

    private static string FormatProposalReviewComment(MaintenanceProposalReviewEnvelope review, ChatProviderResponse response)
    {
        var lines = new List<string>
        {
            $"Recommendation: {review.Recommendation ?? "reviewed"}"
        };
        if (review.Confidence is not null)
        {
            lines.Add($"Confidence: {review.Confidence.Value:P0}");
        }

        lines.Add($"Model: {response.ProviderName} / {response.Model}");
        lines.Add(string.Empty);
        lines.AddRange((review.Comments is { Count: > 0 } ? review.Comments : [response.Content]).Select(comment => $"- {comment}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static MaintenanceWriteProposal NormalizeReviewRevision(MaintenanceWriteProposal revision, MaintenanceWriteProposal original, MaintenanceAgentOptions config)
    {
        if (revision.Changes.Count == 0)
        {
            throw new InvalidOperationException("Agent review revisions must include at least one change.");
        }

        var relatedRecords = revision.RelatedRecords.Count > 0 ? revision.RelatedRecords : original.RelatedRecords;
        var riskLevel = string.IsNullOrWhiteSpace(revision.RiskLevel) ? original.RiskLevel : revision.RiskLevel;
        var confidence = revision.Confidence > 0 ? revision.Confidence : Math.Min(original.Confidence, 0.7);
        return revision with
        {
            ProposalId = string.IsNullOrWhiteSpace(revision.ProposalId) ? $"agent-review-{Slugify(original.ProposalId)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}" : revision.ProposalId,
            Evidence = revision.Evidence.Count > 0 ? revision.Evidence : original.Evidence,
            RelatedRecords = relatedRecords,
            RiskLevel = riskLevel,
            Confidence = confidence,
            Metadata = revision.Metadata with
            {
                Task = string.IsNullOrWhiteSpace(revision.Metadata.Task) ? "proposal_review" : revision.Metadata.Task,
                Confidence = revision.Metadata.Confidence > 0 ? revision.Metadata.Confidence : confidence,
                RiskLevel = string.IsNullOrWhiteSpace(revision.Metadata.RiskLevel) ? riskLevel : revision.Metadata.RiskLevel,
                RelatedRecords = revision.Metadata.RelatedRecords.Count > 0 ? revision.Metadata.RelatedRecords : relatedRecords,
                Supersedes = revision.Metadata.Supersedes.Append(original.ProposalId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                AgentVersion = string.IsNullOrWhiteSpace(revision.Metadata.AgentVersion) ? config.AgentVersion : revision.Metadata.AgentVersion
            }
        };
    }

    private async Task SaveRunStateAsync(MaintenanceAgentOptions config, MaintenanceRunResult run, CancellationToken cancellationToken)
    {
        var path = _config.ResolvePath(config.Storage.LastRunPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = new
        {
            run.Trigger,
            run.StartedAtUtc,
            run.FinishedAtUtc,
            run.Warnings,
            run.Outputs,
            run.Skipped
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine, cancellationToken);
        await AppendActivityAsync(config, ToActivity(run), cancellationToken);
    }

    private async Task AppendActivityAsync(MaintenanceAgentOptions config, MaintenanceRunActivity activity, CancellationToken cancellationToken)
    {
        var path = _config.ResolvePath(config.Storage.ActivityLogPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, JsonSerializer.Serialize(activity, ActivityJsonOptions) + Environment.NewLine, cancellationToken);
    }

    private async Task AppendTranscriptAsync(MaintenanceAgentOptions config, MaintenanceAdminTranscriptEntry entry, CancellationToken cancellationToken)
    {
        var path = _config.ResolvePath(config.Storage.TranscriptLogPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry, ActivityJsonOptions) + Environment.NewLine, cancellationToken);
        await TrimTranscriptLogAsync(config, path, cancellationToken);
    }

    private static async Task TrimTranscriptLogAsync(MaintenanceAgentOptions config, string path, CancellationToken cancellationToken)
    {
        var retention = Math.Clamp(config.Storage.TranscriptRetentionEntries, 1, 10000);
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        if (lines.Length <= retention)
        {
            return;
        }

        await File.WriteAllLinesAsync(path, lines.Skip(lines.Length - retention), cancellationToken);
    }

    private static MaintenanceRunActivity ToActivity(MaintenanceRunResult run) =>
        new(
            run.Trigger,
            run.StartedAtUtc,
            run.FinishedAtUtc,
            run.Outputs.Select(output => output.Task).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            run.Outputs.Sum(output => output.Findings.Count),
            run.Outputs.Sum(output => output.Proposals.Count),
            run.Outputs.SelectMany(output => output.Proposals.Select(proposal => proposal.ProposalId)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            run.Warnings,
            run.Skipped);

    private static MaintenanceRunActivity? TryParseActivity(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            var activity = JsonSerializer.Deserialize<MaintenanceRunActivity>(line, ActivityJsonOptions);
            return activity is null
                ? null
                : activity with
                {
                    Tasks = activity.Tasks ?? [],
                    ProposalIds = activity.ProposalIds ?? [],
                    Warnings = activity.Warnings ?? []
                };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MaintenanceAdminTranscriptEntry CreateTranscriptEntry(MaintenanceAgentOptions config, string userMessage, string assistantMessage, string? provider, string? model, IReadOnlyList<string> warnings)
    {
        var now = DateTimeOffset.UtcNow;
        return new MaintenanceAdminTranscriptEntry(
            $"maintenance-chat-{now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}",
            now,
            RedactTranscriptText(config, userMessage),
            RedactTranscriptText(config, string.IsNullOrWhiteSpace(assistantMessage) ? "The maintenance agent returned an empty response." : assistantMessage),
            provider,
            model,
            warnings.Select(warning => RedactTranscriptText(config, warning)).ToList());
    }

    private static bool TranscriptMatches(MaintenanceAdminTranscriptEntry entry, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var value = search.Trim();
        return entry.UserMessage.Contains(value, StringComparison.OrdinalIgnoreCase)
            || entry.AssistantMessage.Contains(value, StringComparison.OrdinalIgnoreCase)
            || (entry.Provider?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false)
            || (entry.Model?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false)
            || entry.Warnings.Any(warning => warning.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static string RedactTranscriptText(MaintenanceAgentOptions config, string value)
    {
        if (!config.Storage.TranscriptRedactionEnabled || string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var redacted = TranscriptBearerPattern.Replace(value, "Bearer [redacted]");
        return TranscriptSecretPattern.Replace(redacted, match =>
        {
            var key = match.Groups[1].Value;
            return $"{key}=[redacted]";
        });
    }

    private static MaintenanceAdminTranscriptEntry? TryParseTranscript(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MaintenanceAdminTranscriptEntry>(line, ActivityJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildAdminChatSystemPrompt(MaintenanceAgentOptions config) =>
        $$"""
        You are MemorySmith's non-mutating maintenance agent for admin operations.
        Answer questions about maintenance tasks, proposal review, wiki health, and operational status.
        Do not claim that you wrote files, approved proposals, changed settings, or mutated memories/pages.
        If a change is needed, tell the admin it must go through the proposal workflow or a future approved maintenance task.
        Current agent version: {{config.AgentVersion}}.
        """;
}

public sealed class MaintenanceAgentSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MaintenanceAgentConfigService _config;
    private readonly ILogger<MaintenanceAgentSchedulerService> _logger;

    public MaintenanceAgentSchedulerService(IServiceScopeFactory scopeFactory, MaintenanceAgentConfigService config, ILogger<MaintenanceAgentSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? lastRun = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var config = _config.GetCurrent();
            var persistedLastRun = await LoadPersistedLastRunAsync(config, stoppingToken);
            var effectiveLastRun = MostRecent(lastRun, persistedLastRun);
            var localNow = DateTimeOffset.Now;
            var utcNow = DateTimeOffset.UtcNow;

            if (config.Schedule.Enabled && IsWeeklyWindow(config.Schedule, localNow) && ShouldRun(effectiveLastRun, config.Schedule, utcNow))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var agent = scope.ServiceProvider.GetRequiredService<MaintenanceAgentService>();
                    await agent.RunMaintenanceWeeklyAsync(stoppingToken);
                    lastRun = await LoadPersistedLastRunAsync(config, stoppingToken) ?? DateTimeOffset.UtcNow;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Maintenance agent weekly run failed");
                }
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    public static DateTimeOffset? ParsePersistedLastRun(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return TryReadTimestamp(root, "finishedAtUtc") ?? TryReadTimestamp(root, "startedAtUtc");
    }

    public static DateTimeOffset? MostRecent(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left >= right ? left : right;
    }

    public static bool IsWeeklyWindow(MaintenanceAgentScheduleOptions schedule, DateTimeOffset localNow) =>
        Enum.TryParse<DayOfWeek>(schedule.WeeklyDay, ignoreCase: true, out var day) &&
        localNow.DayOfWeek == day &&
        localNow.Hour == Math.Clamp(schedule.WeeklyHourLocal, 0, 23);

    public static bool ShouldRun(DateTimeOffset? lastRun, MaintenanceAgentScheduleOptions schedule, DateTimeOffset utcNow) =>
        lastRun is null || utcNow - lastRun.Value >= TimeSpan.FromHours(Math.Max(1, schedule.MinimumHoursBetweenRuns));

    private async Task<DateTimeOffset?> LoadPersistedLastRunAsync(MaintenanceAgentOptions config, CancellationToken cancellationToken)
    {
        var path = _config.ResolvePath(config.Storage.LastRunPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return ParsePersistedLastRun(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read persisted maintenance-agent last-run state from {Path}", path);
            return null;
        }
    }

    private static DateTimeOffset? TryReadTimestamp(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        property.TryGetDateTimeOffset(out var timestamp)
            ? timestamp
            : null;
}