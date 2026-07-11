using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public static class TaskAssigneeModes
{
    public const string Directory = "Directory";
    public const string Custom = "Custom";
}

public static class TaskStatuses
{
    public const string Backlog = "Backlog";
    public const string Ready = "Ready";
    public const string InProgress = "InProgress";
    public const string Blocked = "Blocked";
    public const string Rejected = "Rejected";
    public const string Done = "Done";
    public const string Archived = "Archived";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Backlog, Ready, InProgress, Blocked, Rejected, Done, Archived
    };
}

public static class TaskPriorities
{
    public const string Critical = "Critical";
    public const string High = "High";
    public const string Medium = "Medium";
    public const string Low = "Low";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Critical, High, Medium, Low
    };
}

public sealed record TaskAttachment(
    string Id,
    string Name,
    string Kind,
    string Uri,
    DateTime AddedAtUtc);

public sealed record StoredTaskAttachmentFile(string FileName, string PublicUri, long Size);

public static partial class TaskAttachmentFiles
{
    public const string PublicPathPrefix = "/artifacts/task-attachments";

    public static async Task<StoredTaskAttachmentFile> SaveAsync(string taskId, string fileName, Stream source, long size, TaskAttachmentOptions options, CancellationToken cancellationToken)
    {
        if (size <= 0)
        {
            throw new ArgumentException("Attachment file is empty.");
        }

        if (size > options.MaxFileBytes)
        {
            throw new ArgumentException($"Attachment file exceeds the configured limit of {options.MaxFileBytes} bytes.");
        }

        var safeTaskId = SafeSegment(taskId, "task");
        var safeFileName = SafeFileName(fileName);
        var directory = Path.Combine(ResolveStorageRoot(options), safeTaskId);
        Directory.CreateDirectory(directory);

        var uniqueFileName = GetUniqueFileName(directory, safeFileName);
        var path = Path.Combine(directory, uniqueFileName);
        await using var target = File.Create(path);
        await source.CopyToAsync(target, cancellationToken);
        var publicUri = $"{PublicPathPrefix}/{Uri.EscapeDataString(safeTaskId)}/{Uri.EscapeDataString(uniqueFileName)}";
        return new StoredTaskAttachmentFile(uniqueFileName, publicUri, size);
    }

    public static string? ResolvePublicPath(TaskAttachmentOptions options, string taskId, string fileName)
    {
        if (!HasValidPercentEncoding(taskId) || !HasValidPercentEncoding(fileName))
        {
            return null;
        }

        var decodedTaskId = Uri.UnescapeDataString(taskId);
        var decodedFileName = Uri.UnescapeDataString(fileName);
        if (!string.Equals(decodedTaskId, SafeSegment(decodedTaskId, "task"), StringComparison.Ordinal) ||
            !string.Equals(decodedFileName, SafeFileName(decodedFileName), StringComparison.Ordinal))
        {
            return null;
        }

        var root = ResolveStorageRoot(options);
        var path = Path.GetFullPath(Path.Combine(root, decodedTaskId, decodedFileName));
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    public static string ResolveStorageRoot(TaskAttachmentOptions options) =>
        Path.GetFullPath(options.StoragePath);

    private static string GetUniqueFileName(string directory, string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = fileName;
        var index = 2;
        while (File.Exists(Path.Combine(directory, candidate)))
        {
            candidate = $"{baseName}-{index}{extension}";
            index++;
        }

        return candidate;
    }

    private static string SafeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var baseName = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        baseName = SafeFileSegmentRegex().Replace(baseName, "-").Trim('-');
        extension = SafeExtensionRegex().Replace(extension, string.Empty);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = $"attachment-{DateTime.UtcNow:yyyyMMddHHmmss}";
        }

        return string.IsNullOrWhiteSpace(extension) ? baseName : baseName + extension;
    }

    private static string SafeSegment(string value, string fallback)
    {
        var segment = SafeFileSegmentRegex().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(segment) ? fallback : segment;
    }

    private static bool HasValidPercentEncoding(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex SafeFileSegmentRegex();

    [GeneratedRegex("[^.a-z0-9]+")]
    private static partial Regex SafeExtensionRegex();
}

public sealed record TaskExternalLink(
    string Id,
    string Label,
    string Url,
    DateTime AddedAtUtc);

public sealed record TaskComment(
    string Id,
    string Author,
    string Body,
    DateTime CreatedAtUtc);

public sealed record TaskActivityEntry(
    string TaskId,
    string Action,
    string Actor,
    string? Note,
    DateTime OccurredAtUtc);

public sealed record TaskItem(
    string Id,
    string Key,
    string Title,
    string Description,
    string Type,
    string Status,
    string Priority,
    string AssigneeMode,
    string? AssigneeDirectoryId,
    string? AssigneeCustomText,
    string? Reporter,
    IReadOnlyList<string> Labels,
    IReadOnlyList<TaskAttachment> Attachments,
    IReadOnlyList<TaskExternalLink> ExternalLinks,
    IReadOnlyList<string> LinkedPages,
    IReadOnlyList<TaskComment> Comments,
    string? EpicId,
    string? ParentId,
    DateTime? DueDateUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? CompletedAtUtc,
    int Revision,
    bool IsArchived = false,
    bool HasLoadError = false,
    string? LoadError = null,
    string? SourceFilePath = null);

public sealed record TaskSummary(
    string Id,
    string Key,
    string Title,
    string Status,
    string Priority,
    string? Assignee,
    string AssigneeMode,
    string? AssigneeDirectoryId,
    string? AssigneeCustomText,
    DateTime UpdatedAtUtc,
    int AttachmentCount,
    int LinkCount,
    int CommentCount,
    bool HasLoadError = false,
    string? LoadError = null,
    string? SourceFilePath = null);

public sealed record TaskCreateRequest(
    string Title,
    string Description,
    string Type,
    string Status,
    string Priority,
    string AssigneeMode,
    string? AssigneeDirectoryId,
    string? AssigneeCustomText,
    string? Reporter,
    IReadOnlyList<string>? Labels,
    DateTime? DueDateUtc,
    string? EpicId,
    string? ParentId,
    string? Slug = null);

public sealed record TaskUpdateRequest(
    string? Title,
    string? Description,
    string? Type,
    string? Priority,
    string? AssigneeMode,
    string? AssigneeDirectoryId,
    string? AssigneeCustomText,
    string? Reporter,
    IReadOnlyList<string>? Labels,
    DateTime? DueDateUtc,
    string? EpicId,
    string? ParentId);

public sealed record TaskStatusUpdateRequest(string Status, string? Note = null);
public sealed record TaskCommentRequest(string Body);
public sealed record TaskPageLinkRequest(string Slug);
public sealed record TaskExternalLinkRequest(string Label, string Url);
public sealed record TaskAttachmentRequest(string Name, string Kind, string Uri);

public interface ITaskService
{
    Task<IReadOnlyList<TaskSummary>> ListAsync(string? query, string? status, string? assignee, int limit, CancellationToken cancellationToken);
    Task<TaskItem?> GetAsync(string idOrKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<TaskActivityEntry>> GetHistoryAsync(string idOrKey, int limit, CancellationToken cancellationToken);
    Task<TaskItem> CreateAsync(TaskCreateRequest request, string actor, CancellationToken cancellationToken);
    Task<TaskItem?> UpdateAsync(string idOrKey, TaskUpdateRequest request, string actor, CancellationToken cancellationToken);
    Task<TaskItem?> SetStatusAsync(string idOrKey, TaskStatusUpdateRequest request, string actor, CancellationToken cancellationToken);
    Task<TaskItem?> AddCommentAsync(string idOrKey, TaskCommentRequest request, string actor, CancellationToken cancellationToken);
    Task<TaskItem?> AddLinkedPageAsync(string idOrKey, TaskPageLinkRequest request, string actor, CancellationToken cancellationToken);
    Task<TaskItem?> AddExternalLinkAsync(string idOrKey, TaskExternalLinkRequest request, string actor, CancellationToken cancellationToken);
    Task<TaskItem?> AddAttachmentAsync(string idOrKey, TaskAttachmentRequest request, string actor, CancellationToken cancellationToken);
    Task<TaskItem?> RemoveAttachmentAsync(string idOrKey, string attachmentId, string actor, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string idOrKey, bool hardDelete, string actor, CancellationToken cancellationToken);
}

public sealed class FileTaskService : ITaskService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
    private static readonly JsonSerializerOptions JsonlOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IOptionsMonitor<MemorySmithOptions> _options;
    private readonly ILogger<FileTaskService> _logger;
    private readonly object _gate = new();

    public FileTaskService(IOptionsMonitor<MemorySmithOptions> options, ILogger<FileTaskService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task<IReadOnlyList<TaskSummary>> ListAsync(string? query, string? status, string? assignee, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var normalizedQuery = (query ?? string.Empty).Trim();
            var normalizedStatus = Normalize(status);
            var normalizedAssignee = Normalize(assignee);
            var cappedLimit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);

            var filteredItems = LoadAll(cancellationToken)
                .Where(item => string.IsNullOrWhiteSpace(normalizedStatus) || string.Equals(item.Status, normalizedStatus, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.IsNullOrWhiteSpace(normalizedAssignee) || string.Equals(ResolveAssignee(item), normalizedAssignee, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var hybridSearchEnabled = _options.CurrentValue.TaskSearch.HybridSemanticEnabled;
            List<TaskItem> items;
            if (string.IsNullOrWhiteSpace(normalizedQuery))
            {
                items = filteredItems
                    .OrderByDescending(item => item.UpdatedAtUtc)
                    .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(cappedLimit)
                    .ToList();
            }
            else if (hybridSearchEnabled)
            {
                items = filteredItems
                    .Select(item => new
                    {
                        Item = item,
                        Score = ComputeHybridQueryScore(item, normalizedQuery)
                    })
                    .Where(entry => entry.Score > 0)
                    .OrderByDescending(entry => entry.Score)
                    .ThenByDescending(entry => entry.Item.UpdatedAtUtc)
                    .ThenBy(entry => entry.Item.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(cappedLimit)
                    .Select(entry => entry.Item)
                    .ToList();
            }
            else
            {
                items = filteredItems
                    .Where(item => MatchesQuery(item, normalizedQuery))
                    .OrderByDescending(item => item.UpdatedAtUtc)
                    .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(cappedLimit)
                    .ToList();
            }

            var summaries = items
                .Select(ToSummary)
                .ToList();

            return Task.FromResult<IReadOnlyList<TaskSummary>>(summaries);
        }
    }

    public Task<TaskItem?> GetAsync(string idOrKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(FindByIdOrKey(idOrKey, cancellationToken));
        }
    }

    public Task<IReadOnlyList<TaskActivityEntry>> GetHistoryAsync(string idOrKey, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var item = FindByIdOrKey(idOrKey, cancellationToken);
            if (item is null)
            {
                return Task.FromResult<IReadOnlyList<TaskActivityEntry>>([]);
            }

            var path = ResolveActivityLogPath();
            if (!File.Exists(path))
            {
                return Task.FromResult<IReadOnlyList<TaskActivityEntry>>([]);
            }

            var cappedLimit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);
            var entries = File.ReadLines(path)
                .Select(TryParseActivity)
                .Where(entry => entry is not null && string.Equals(entry.TaskId, item.Id, StringComparison.OrdinalIgnoreCase))
                .Cast<TaskActivityEntry>()
                .OrderByDescending(entry => entry.OccurredAtUtc)
                .Take(cappedLimit)
                .ToList();

            return Task.FromResult<IReadOnlyList<TaskActivityEntry>>(entries);
        }
    }

    public Task<TaskItem> CreateAsync(TaskCreateRequest request, string actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ValidateCreate(request);
            var now = DateTime.UtcNow;
            var all = LoadAll(cancellationToken);
            var key = NextKey(all);
            var id = BuildId(key, request.Title, request.Slug);
            var item = new TaskItem(
                Id: id,
                Key: key,
                Title: request.Title.Trim(),
                Description: (request.Description ?? string.Empty).Trim(),
                Type: NormalizeOrDefault(request.Type, "Task"),
                Status: NormalizeOrDefault(request.Status, TaskStatuses.Backlog),
                Priority: NormalizeOrDefault(request.Priority, TaskPriorities.Medium),
                AssigneeMode: NormalizeAssigneeMode(request.AssigneeMode),
                AssigneeDirectoryId: NormalizeNullable(request.AssigneeDirectoryId),
                AssigneeCustomText: NormalizeNullable(request.AssigneeCustomText),
                Reporter: NormalizeNullable(request.Reporter),
                Labels: NormalizeLabels(request.Labels),
                Attachments: [],
                ExternalLinks: [],
                LinkedPages: [],
                Comments: [],
                EpicId: NormalizeNullable(request.EpicId),
                ParentId: NormalizeNullable(request.ParentId),
                DueDateUtc: request.DueDateUtc,
                CreatedAtUtc: now,
                UpdatedAtUtc: now,
                CompletedAtUtc: NormalizeOrDefault(request.Status, TaskStatuses.Backlog).Equals(TaskStatuses.Done, StringComparison.OrdinalIgnoreCase) ? now : null,
                Revision: 1,
                IsArchived: false);

            ValidateAssignee(item.AssigneeMode, item.AssigneeDirectoryId, item.AssigneeCustomText);
            Save(item);
            AppendActivity(new TaskActivityEntry(item.Id, "created", SafeActor(actor), null, now));
            return Task.FromResult(item);
        }
    }

    public Task<TaskItem?> UpdateAsync(string idOrKey, TaskUpdateRequest request, string actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var item = FindByIdOrKey(idOrKey, cancellationToken);
            if (item is null)
            {
                return Task.FromResult<TaskItem?>(null);
            }

            var assigneeMode = string.IsNullOrWhiteSpace(request.AssigneeMode) ? item.AssigneeMode : NormalizeAssigneeMode(request.AssigneeMode);
            var assigneeDirectoryId = string.Equals(assigneeMode, TaskAssigneeModes.Directory, StringComparison.OrdinalIgnoreCase)
                ? request.AssigneeDirectoryId is null ? item.AssigneeDirectoryId : NormalizeNullable(request.AssigneeDirectoryId)
                : request.AssigneeDirectoryId is null ? null : NormalizeNullable(request.AssigneeDirectoryId);
            var assigneeCustomText = string.Equals(assigneeMode, TaskAssigneeModes.Custom, StringComparison.OrdinalIgnoreCase)
                ? request.AssigneeCustomText is null && string.Equals(item.AssigneeMode, TaskAssigneeModes.Custom, StringComparison.OrdinalIgnoreCase) ? item.AssigneeCustomText : NormalizeNullable(request.AssigneeCustomText)
                : request.AssigneeCustomText is null ? null : NormalizeNullable(request.AssigneeCustomText);

            var updated = item with
            {
                Title = string.IsNullOrWhiteSpace(request.Title) ? item.Title : request.Title.Trim(),
                Description = request.Description is null ? item.Description : request.Description.Trim(),
                Type = string.IsNullOrWhiteSpace(request.Type) ? item.Type : request.Type.Trim(),
                Priority = string.IsNullOrWhiteSpace(request.Priority) ? item.Priority : request.Priority.Trim(),
                AssigneeMode = assigneeMode,
                AssigneeDirectoryId = assigneeDirectoryId,
                AssigneeCustomText = assigneeCustomText,
                Reporter = request.Reporter is null ? item.Reporter : NormalizeNullable(request.Reporter),
                Labels = request.Labels is null ? item.Labels : NormalizeLabels(request.Labels),
                DueDateUtc = request.DueDateUtc,
                EpicId = request.EpicId is null ? item.EpicId : NormalizeNullable(request.EpicId),
                ParentId = request.ParentId is null ? item.ParentId : NormalizeNullable(request.ParentId),
                UpdatedAtUtc = DateTime.UtcNow,
                Revision = item.Revision + 1
            };

            EnsureTaskIsEditable(item);
            ValidateAssignee(updated.AssigneeMode, updated.AssigneeDirectoryId, updated.AssigneeCustomText);
            ValidateTitle(updated.Title);
            Save(updated);
            AppendActivity(new TaskActivityEntry(updated.Id, "updated", SafeActor(actor), null, updated.UpdatedAtUtc));
            return Task.FromResult<TaskItem?>(updated);
        }
    }

    public Task<TaskItem?> SetStatusAsync(string idOrKey, TaskStatusUpdateRequest request, string actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var item = FindByIdOrKey(idOrKey, cancellationToken);
            if (item is null)
            {
                return Task.FromResult<TaskItem?>(null);
            }

            var now = DateTime.UtcNow;
            var status = NormalizeOrDefault(request.Status, item.Status);
            ValidateEnumValue(request.Status, TaskStatuses.All, "Status");
            EnsureTaskIsEditable(item);
            var updated = item with
            {
                Status = status,
                IsArchived = string.Equals(status, TaskStatuses.Archived, StringComparison.OrdinalIgnoreCase),
                CompletedAtUtc = string.Equals(status, TaskStatuses.Done, StringComparison.OrdinalIgnoreCase) ? now : item.CompletedAtUtc,
                UpdatedAtUtc = now,
                Revision = item.Revision + 1
            };

            Save(updated);
            AppendActivity(new TaskActivityEntry(updated.Id, "status_changed", SafeActor(actor), NormalizeNullable(request.Note) ?? status, now));
            return Task.FromResult<TaskItem?>(updated);
        }
    }

    public Task<TaskItem?> AddCommentAsync(string idOrKey, TaskCommentRequest request, string actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var item = FindByIdOrKey(idOrKey, cancellationToken);
            if (item is null)
            {
                return Task.FromResult<TaskItem?>(null);
            }

            var body = NormalizeNullable(request.Body);
            if (string.IsNullOrWhiteSpace(body))
            {
                throw new ArgumentException("Comment body is required.");
            }

            EnsureTaskIsEditable(item);
            var now = DateTime.UtcNow;
            var comment = new TaskComment($"c-{Guid.NewGuid():N}", SafeActor(actor), body, now);
            var updated = item with
            {
                Comments = item.Comments.Append(comment).ToList(),
                UpdatedAtUtc = now,
                Revision = item.Revision + 1
            };

            Save(updated);
            AppendActivity(new TaskActivityEntry(updated.Id, "comment_added", SafeActor(actor), Truncate(body, 280), now));
            return Task.FromResult<TaskItem?>(updated);
        }
    }

    public Task<TaskItem?> AddLinkedPageAsync(string idOrKey, TaskPageLinkRequest request, string actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var item = FindByIdOrKey(idOrKey, cancellationToken);
            if (item is null)
            {
                return Task.FromResult<TaskItem?>(null);
            }

            var slug = NormalizeNullable(request.Slug);
            if (string.IsNullOrWhiteSpace(slug))
            {
                throw new ArgumentException("Page slug is required.");
            }

            if (!PageSlugPolicy.TryNormalize(slug, out var canonicalSlug))
            {
                throw new ArgumentException("Page slug must be a safe page path using letters, numbers, hyphens, underscores, and forward slashes.");
            }

            var pageExists = LinkedPageExists(canonicalSlug);
            EnsureTaskIsEditable(item);

            var updated = item with
            {
                LinkedPages = item.LinkedPages.Contains(canonicalSlug, StringComparer.OrdinalIgnoreCase)
                    ? item.LinkedPages
                    : item.LinkedPages.Append(canonicalSlug).ToList(),
                UpdatedAtUtc = DateTime.UtcNow,
                Revision = item.Revision + 1
            };

            Save(updated);
            AppendActivity(new TaskActivityEntry(updated.Id, "page_link_added", SafeActor(actor), canonicalSlug, updated.UpdatedAtUtc));
            if (!pageExists)
            {
                AppendActivity(new TaskActivityEntry(updated.Id, "page_link_warning", SafeActor(actor), $"Linked page not found: {canonicalSlug}", updated.UpdatedAtUtc));
            }

            return Task.FromResult<TaskItem?>(updated);
        }
    }

    public Task<TaskItem?> AddExternalLinkAsync(string idOrKey, TaskExternalLinkRequest request, string actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var item = FindByIdOrKey(idOrKey, cancellationToken);
            if (item is null)
            {
                return Task.FromResult<TaskItem?>(null);
            }

            var label = NormalizeNullable(request.Label);
            var url = NormalizeNullable(request.Url);
            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(url))
            {
                throw new ArgumentException("External link label and url are required.");
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("External link url must be an absolute http or https url.");
            }

            EnsureTaskIsEditable(item);
            var now = DateTime.UtcNow;
            var link = new TaskExternalLink($"l-{Guid.NewGuid():N}", label, url, now);
            var updated = item with
            {
                ExternalLinks = item.ExternalLinks.Append(link).ToList(),
                UpdatedAtUtc = now,
                Revision = item.Revision + 1
            };

            Save(updated);
            AppendActivity(new TaskActivityEntry(updated.Id, "external_link_added", SafeActor(actor), url, now));
            return Task.FromResult<TaskItem?>(updated);
        }
    }

    public Task<TaskItem?> AddAttachmentAsync(string idOrKey, TaskAttachmentRequest request, string actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var item = FindByIdOrKey(idOrKey, cancellationToken);
            if (item is null)
            {
                return Task.FromResult<TaskItem?>(null);
            }

            var name = NormalizeNullable(request.Name);
            var kind = NormalizeAttachmentKind(request.Kind);
            var uri = NormalizeNullable(request.Uri);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uri))
            {
                throw new ArgumentException("Attachment name and uri are required.");
            }

            var attachmentUri = NormalizeAttachmentUri(item, kind, uri, cancellationToken);

            EnsureTaskIsEditable(item);
            var now = DateTime.UtcNow;
            var attachment = new TaskAttachment($"a-{Guid.NewGuid():N}", name, kind, attachmentUri, now);
            var updated = item with
            {
                Attachments = item.Attachments.Append(attachment).ToList(),
                UpdatedAtUtc = now,
                Revision = item.Revision + 1
            };

            Save(updated);
            AppendActivity(new TaskActivityEntry(updated.Id, "attachment_added", SafeActor(actor), name, now));
            return Task.FromResult<TaskItem?>(updated);
        }
    }

    public Task<TaskItem?> RemoveAttachmentAsync(string idOrKey, string attachmentId, string actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var item = FindByIdOrKey(idOrKey, cancellationToken);
            if (item is null)
            {
                return Task.FromResult<TaskItem?>(null);
            }

            var updatedAttachments = item.Attachments.Where(attachment => !string.Equals(attachment.Id, attachmentId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (updatedAttachments.Count == item.Attachments.Count)
            {
                return Task.FromResult<TaskItem?>(item);
            }

            EnsureTaskIsEditable(item);
            var now = DateTime.UtcNow;
            var updated = item with
            {
                Attachments = updatedAttachments,
                UpdatedAtUtc = now,
                Revision = item.Revision + 1
            };

            Save(updated);
            AppendActivity(new TaskActivityEntry(updated.Id, "attachment_removed", SafeActor(actor), attachmentId, now));
            return Task.FromResult<TaskItem?>(updated);
        }
    }

    public Task<bool> DeleteAsync(string idOrKey, bool hardDelete, string actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var item = FindByIdOrKey(idOrKey, cancellationToken);
            if (item is null)
            {
                return Task.FromResult(false);
            }

            EnsureTaskIsEditable(item);

            if (hardDelete)
            {
                File.Delete(GetTaskPath(item.Id));
                AppendActivity(new TaskActivityEntry(item.Id, "deleted", SafeActor(actor), "hard", DateTime.UtcNow));
                return Task.FromResult(true);
            }

            var now = DateTime.UtcNow;
            var updated = item with
            {
                Status = TaskStatuses.Archived,
                IsArchived = true,
                UpdatedAtUtc = now,
                Revision = item.Revision + 1
            };

            Save(updated);
            AppendActivity(new TaskActivityEntry(updated.Id, "deleted", SafeActor(actor), "soft", now));
            return Task.FromResult(true);
        }
    }

    private List<TaskItem> LoadAll(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = ResolveTasksRoot();
        Directory.CreateDirectory(root);
        var files = Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly);
        var results = new List<TaskItem>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = File.ReadAllText(file);
            try
            {
                var item = JsonSerializer.Deserialize<TaskItem>(json, JsonOptions);
                if (item is not null)
                {
                    results.Add(NormalizeLoadedTask(item, file));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse task file {TaskFile}. Loading fallback record.", file);
                results.Add(CreateMalformedTaskFallback(file, json, ex));
            }
        }

        return results;
    }

    private static TaskItem NormalizeLoadedTask(TaskItem item, string filePath)
    {
        var fileToken = Path.GetFileNameWithoutExtension(filePath);
        var fileName = Path.GetFileName(filePath);
        var normalizedAssigneeMode = NormalizeAssigneeMode(item.AssigneeMode);
        var normalizedAssigneeDirectoryId = normalizedAssigneeMode == TaskAssigneeModes.Directory
            ? NormalizeNullable(item.AssigneeDirectoryId)
            : null;
        var normalizedAssigneeCustomText = normalizedAssigneeMode == TaskAssigneeModes.Custom
            ? NormalizeNullable(item.AssigneeCustomText)
            : null;

        return item with
        {
            Id = NormalizeNullable(item.Id) ?? fileToken,
            Key = NormalizeNullable(item.Key) ?? InferKeyFromToken(fileToken),
            Title = NormalizeNullable(item.Title) ?? "Untitled task",
            Description = item.Description ?? string.Empty,
            Type = NormalizeOrDefault(item.Type, "Task"),
            Status = NormalizeOrDefault(item.Status, TaskStatuses.Backlog),
            Priority = NormalizeOrDefault(item.Priority, TaskPriorities.Medium),
            AssigneeMode = normalizedAssigneeMode,
            AssigneeDirectoryId = normalizedAssigneeDirectoryId,
            AssigneeCustomText = normalizedAssigneeCustomText,
            Reporter = NormalizeNullable(item.Reporter),
            Labels = NormalizeLabels(item.Labels),
            Attachments = item.Attachments ?? [],
            ExternalLinks = item.ExternalLinks ?? [],
            LinkedPages = (item.LinkedPages ?? []).Where(link => !string.IsNullOrWhiteSpace(link)).Select(link => link.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Comments = item.Comments ?? [],
            SourceFilePath = NormalizeNullable(item.SourceFilePath) ?? fileName
        };
    }

    private TaskItem? FindByIdOrKey(string idOrKey, CancellationToken cancellationToken)
    {
        var token = Normalize(idOrKey);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return LoadAll(cancellationToken).FirstOrDefault(item =>
            string.Equals(item.Id, token, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Key, token, StringComparison.OrdinalIgnoreCase));
    }

    private void Save(TaskItem item)
    {
        var path = GetTaskPath(item.Id);
        var tempPath = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(tempPath, JsonSerializer.Serialize(item, JsonOptions) + Environment.NewLine, Encoding.UTF8);
        File.Move(tempPath, path, true);
    }

    private void AppendActivity(TaskActivityEntry entry)
    {
        var path = ResolveActivityLogPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonlOptions) + Environment.NewLine, Encoding.UTF8);
    }

    private static TaskActivityEntry? TryParseActivity(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<TaskActivityEntry>(line, JsonlOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string ResolveTasksRoot()
    {
        var dataPath = Path.GetFullPath(_options.CurrentValue.DataPath);
        var dataRoot = Directory.GetParent(dataPath)?.FullName ?? Path.GetFullPath(Path.Combine(dataPath, ".."));
        return Path.Combine(dataRoot, "Tasks");
    }

    private string ResolvePagesRoot()
    {
        return Path.GetFullPath(_options.CurrentValue.PagesPath);
    }

    private string ResolveActivityLogPath()
    {
        var eventLogPath = Path.GetFullPath(_options.CurrentValue.EventLogPath);
        var eventsDir = Path.GetDirectoryName(eventLogPath) ?? Path.Combine(ResolveTasksRoot(), "..", "Events");
        return Path.Combine(eventsDir, "tasks.activity.jsonl");
    }

    private string GetTaskPath(string id) => Path.Combine(ResolveTasksRoot(), $"{id}.json");

    private static string NextKey(IReadOnlyCollection<TaskItem> all)
    {
        var next = all
            .Select(item => Regex.Match(item.Key ?? string.Empty, @"^TSK-(\d{4,})$", RegexOptions.IgnoreCase))
            .Where(match => match.Success)
            .Select(match => int.TryParse(match.Groups[1].Value, out var value) ? value : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        return $"TSK-{next:0000}";
    }

    private static string BuildId(string key, string title, string? slug)
    {
        var source = NormalizeNullable(slug) ?? title;
        var token = Regex.Replace((source ?? string.Empty).Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        token = string.IsNullOrWhiteSpace(token) ? "task" : token;
        return $"{key.ToLowerInvariant()}-{token}";
    }

    private bool LinkedPageExists(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return false;
        }

        var pagesRoot = ResolvePagesRoot();
        var relativePath = slug.Replace('/', Path.DirectorySeparatorChar);
        var markdownPath = Path.GetFullPath(Path.Combine(pagesRoot, relativePath + ".md"));
        var root = pagesRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return markdownPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(markdownPath);
    }

    private static TaskSummary ToSummary(TaskItem item) =>
        new(
            item.Id,
            item.Key,
            item.Title,
            item.Status,
            item.Priority,
            ResolveAssignee(item),
            item.AssigneeMode,
            item.AssigneeDirectoryId,
            item.AssigneeCustomText,
            item.UpdatedAtUtc,
            item.Attachments.Count,
            item.ExternalLinks.Count + item.LinkedPages.Count,
            item.Comments.Count,
            item.HasLoadError,
            item.LoadError,
            item.SourceFilePath);

    private static TaskItem CreateMalformedTaskFallback(string filePath, string rawJson, Exception ex)
    {
        var fileName = Path.GetFileName(filePath);
        var fileToken = Path.GetFileNameWithoutExtension(filePath);
        var extractedId = NormalizeNullable(TryExtractJsonStringProperty(rawJson, "id"));
        var extractedKey = NormalizeNullable(TryExtractJsonStringProperty(rawJson, "key"));
        var extractedTitle = NormalizeNullable(TryExtractJsonStringProperty(rawJson, "title"));
        var extractedStatus = NormalizeNullable(TryExtractJsonStringProperty(rawJson, "status"));
        var extractedPriority = NormalizeNullable(TryExtractJsonStringProperty(rawJson, "priority"));
        var extractedType = NormalizeNullable(TryExtractJsonStringProperty(rawJson, "type"));
        var extractedAssigneeMode = NormalizeNullable(TryExtractJsonStringProperty(rawJson, "assigneeMode"));
        var extractedAssigneeDirectoryId = NormalizeNullable(TryExtractJsonStringProperty(rawJson, "assigneeDirectoryId"));
        var extractedAssigneeCustomText = NormalizeNullable(TryExtractJsonStringProperty(rawJson, "assigneeCustomText"));
        var extractedReporter = NormalizeNullable(TryExtractJsonStringProperty(rawJson, "reporter"));

        var id = extractedId ?? fileToken;
        var key = extractedKey ?? InferKeyFromToken(fileToken);
        var title = extractedTitle ?? "Invalid task file";
        var status = extractedStatus ?? TaskStatuses.Backlog;
        var priority = extractedPriority ?? TaskPriorities.Medium;
        var taskType = extractedType ?? "Task";
        var assigneeMode = NormalizeAssigneeMode(extractedAssigneeMode);
        var description = $"Task file '{fileName}' could not be fully parsed. Fix the JSON and refresh.\n\nParser error: {Truncate(ex.Message, 280)}";
        var timestamp = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.UtcNow;
        if (timestamp.Year < 2000)
        {
            timestamp = DateTime.UtcNow;
        }

        return new TaskItem(
            Id: id,
            Key: key,
            Title: title,
            Description: description,
            Type: taskType,
            Status: status,
            Priority: priority,
            AssigneeMode: assigneeMode,
            AssigneeDirectoryId: assigneeMode == TaskAssigneeModes.Directory ? extractedAssigneeDirectoryId : null,
            AssigneeCustomText: assigneeMode == TaskAssigneeModes.Custom ? extractedAssigneeCustomText : null,
            Reporter: extractedReporter,
            Labels: [],
            Attachments: [],
            ExternalLinks: [],
            LinkedPages: [],
            Comments: [],
            EpicId: null,
            ParentId: null,
            DueDateUtc: null,
            CreatedAtUtc: timestamp,
            UpdatedAtUtc: timestamp,
            CompletedAtUtc: null,
            Revision: 0,
            IsArchived: false,
            HasLoadError: true,
            LoadError: Truncate(ex.Message, 500),
            SourceFilePath: fileName);
    }

    private static string InferKeyFromToken(string token)
    {
        var match = Regex.Match(token, @"tsk-(\d{4,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? $"TSK-{match.Groups[1].Value}" : "TSK-0000";
    }

    private static string? TryExtractJsonStringProperty(string rawJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || string.IsNullOrWhiteSpace(propertyName))
        {
            return null;
        }

        var pattern = $"\"{Regex.Escape(propertyName)}\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"";
        var match = Regex.Match(rawJson, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var encoded = $"\"{match.Groups[1].Value}\"";
        try
        {
            return JsonSerializer.Deserialize<string>(encoded, JsonlOptions);
        }
        catch
        {
            return match.Groups[1].Value;
        }
    }

    private static void EnsureTaskIsEditable(TaskItem item)
    {
        if (!item.HasLoadError)
        {
            return;
        }

        var source = string.IsNullOrWhiteSpace(item.SourceFilePath) ? item.Id : item.SourceFilePath;
        throw new ArgumentException($"Task '{source}' has malformed JSON and cannot be edited until fixed.");
    }

    private static string? ResolveAssignee(TaskItem item)
    {
        if (string.Equals(item.AssigneeMode, TaskAssigneeModes.Directory, StringComparison.OrdinalIgnoreCase))
        {
            return item.AssigneeDirectoryId;
        }

        return item.AssigneeCustomText;
    }

    private static bool MatchesQuery(TaskItem item, string query)
    {
        return (item.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
               || (item.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
               || item.Labels.Any(label => label.Contains(query, StringComparison.OrdinalIgnoreCase))
               || item.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
               || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static double ComputeHybridQueryScore(TaskItem item, string query)
    {
        var score = 0d;
        if (item.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 8;
        }

        if (item.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }

        if (item.Labels.Any(label => label.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            score += 3;
        }

        if (item.Key.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        var queryTokens = TokenizeForSemanticSearch(query);
        if (queryTokens.Count == 0)
        {
            return score;
        }

        var taskTokens = TokenizeForSemanticSearch(BuildTaskSearchText(item));
        if (taskTokens.Count == 0)
        {
            return score;
        }

        var overlap = queryTokens.Intersect(taskTokens, StringComparer.OrdinalIgnoreCase).Count();
        if (overlap == 0)
        {
            return score;
        }

        var coverage = (double)overlap / queryTokens.Count;
        var union = queryTokens.Union(taskTokens, StringComparer.OrdinalIgnoreCase).Count();
        var jaccard = union == 0 ? 0 : (double)overlap / union;

        return score + (coverage * 4.0) + (jaccard * 2.0);
    }

    private static HashSet<string> TokenizeForSemanticSearch(string text)
    {
        var tokens = Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]{2,}")
            .Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        tokens.RemoveWhere(token => token is "the" or "and" or "for" or "with" or "that" or "this" or "from" or "into" or "over" or "under" or "after" or "before" or "task" or "tasks");
        return tokens;
    }

    private static string BuildTaskSearchText(TaskItem item)
    {
        return string.Join(' ',
            item.Title,
            item.Description,
            item.Type,
            item.Status,
            item.Priority,
            item.Key,
            item.Id,
            string.Join(' ', item.Labels),
            item.Reporter ?? string.Empty,
            item.AssigneeDirectoryId ?? string.Empty,
            item.AssigneeCustomText ?? string.Empty);
    }

    private static IReadOnlyList<string> NormalizeLabels(IReadOnlyList<string>? labels) =>
        (labels ?? [])
            .Select(label => label?.Trim() ?? string.Empty)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();
    private static string? NormalizeNullable(string? value)
    {
        var normalized = Normalize(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string NormalizeOrDefault(string? value, string fallback)
    {
        var normalized = NormalizeNullable(value);
        return normalized ?? fallback;
    }

    private static string NormalizeAssigneeMode(string? mode)
    {
        var normalized = NormalizeNullable(mode);
        return string.Equals(normalized, TaskAssigneeModes.Custom, StringComparison.OrdinalIgnoreCase)
            ? TaskAssigneeModes.Custom
            : TaskAssigneeModes.Directory;
    }

    private static string NormalizeAttachmentKind(string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? "file" : kind.Trim().ToLowerInvariant();

    private string NormalizeAttachmentUri(TaskItem owner, string kind, string uri, CancellationToken cancellationToken)
    {
        var normalizedUri = uri.Trim();
        if (normalizedUri.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Attachment uri cannot contain traversal segments.");
        }

        if (string.Equals(kind, "task", StringComparison.OrdinalIgnoreCase))
        {
            var relatedToken = normalizedUri.StartsWith("task:", StringComparison.OrdinalIgnoreCase)
                ? normalizedUri[5..].Trim()
                : normalizedUri;
            var relatedTask = FindByIdOrKey(relatedToken, cancellationToken);
            if (relatedTask is null)
            {
                throw new ArgumentException("Related task attachment must reference an existing task id or key.");
            }

            if (string.Equals(relatedTask.Id, owner.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Related task attachment cannot reference the same task.");
            }

            return $"task:{relatedTask.Id}";
        }

        if (IsSafeLocalTaskAttachmentUri(normalizedUri))
        {
            return normalizedUri.Replace('\\', '/');
        }

        if (!Uri.TryCreate(normalizedUri, UriKind.Absolute, out var absoluteUri) ||
            (absoluteUri.Scheme != Uri.UriSchemeHttp && absoluteUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Attachment uri must be an absolute http/https url, a related task reference, or a safe local task attachment path.");
        }

        return normalizedUri;
    }

    private static bool IsSafeLocalTaskAttachmentUri(string uri)
    {
        var normalized = uri.Replace('\\', '/');
        if (!normalized.StartsWith("/artifacts/task-attachments/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(normalized, UriKind.Relative, out _))
        {
            return false;
        }

        var decoded = Uri.UnescapeDataString(normalized);
        return decoded.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(segment => segment is not "." and not "..");
    }

    private static void ValidateCreate(TaskCreateRequest request)
    {
        ValidateTitle(request.Title);
        ValidateSlug(request.Slug);
        ValidateAssignee(NormalizeAssigneeMode(request.AssigneeMode), NormalizeNullable(request.AssigneeDirectoryId), NormalizeNullable(request.AssigneeCustomText));
        ValidateEnumValue(request.Status, TaskStatuses.All, nameof(request.Status));
        ValidateEnumValue(request.Priority, TaskPriorities.All, nameof(request.Priority));
    }

    private static void ValidateEnumValue(string? value, HashSet<string> validValues, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return; // Will use default — no error for optional fields
        }

        var trimmed = value.Trim();
        if (!validValues.Contains(trimmed))
        {
            var valid = string.Join(", ", validValues.OrderBy(v => v));
            throw new ArgumentException($"'{trimmed}' is not a valid {fieldName}. Valid values: {valid}");
        }
    }

    private static void ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title is required.");
        }

        if (title.Trim().Length > 200)
        {
            throw new ArgumentException("Title must be 200 characters or fewer.");
        }
    }

    private static void ValidateSlug(string? slug)
    {
        var normalized = NormalizeNullable(slug);
        if (normalized is null)
        {
            return;
        }

        if (normalized.Length > 120)
        {
            throw new ArgumentException("Slug must be 120 characters or fewer.");
        }

        if (!Regex.IsMatch(normalized, "^[a-zA-Z0-9][a-zA-Z0-9\\s_-]*$"))
        {
            throw new ArgumentException("Slug may only contain letters, numbers, spaces, underscores, and hyphens.");
        }
    }

    private static void ValidateAssignee(string mode, string? directoryId, string? customText)
    {
        if (string.Equals(mode, TaskAssigneeModes.Directory, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(directoryId) || !string.IsNullOrWhiteSpace(customText))
            {
                throw new ArgumentException("Directory assignee mode requires assigneeDirectoryId and no assigneeCustomText.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(customText) || !string.IsNullOrWhiteSpace(directoryId))
        {
            throw new ArgumentException("Custom assignee mode requires assigneeCustomText and no assigneeDirectoryId.");
        }
    }

    private static string SafeActor(string actor)
    {
        var normalized = NormalizeNullable(actor);
        return normalized ?? "system";
    }

    private static string Truncate(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        return value[..max] + "...";
    }
}