using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MemorySmith.Core.Models;
namespace MemorySmith.App.Services;

public static class PageAccessLevels
{
    public const string Anonymous = "Anonymous";
    public const string Authenticated = "Authenticated";
    public const string Admin = "Admin";

    public static readonly IReadOnlyList<string> All = [Anonymous, Authenticated, Admin];

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = Anonymous;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var compact = value.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        normalized = compact switch
        {
            "anonymous" or "anon" or "public" => Anonymous,
            "authenticated" or "loggedin" or "signin" or "signedin" or "user" or "users" => Authenticated,
            "admin" or "administrator" => Admin,
            _ => Anonymous
        };
        return compact is "anonymous" or "anon" or "public" or "authenticated" or "loggedin" or "signin" or "signedin" or "user" or "users" or "admin" or "administrator";
    }

    public static string Normalize(string? value, string fallback = Anonymous) =>
        TryNormalize(value, out var normalized)
            ? normalized
            : TryNormalize(fallback, out var fallbackNormalized) ? fallbackNormalized : Anonymous;

    public static string Label(string value) => Normalize(value) switch
    {
        Authenticated => "Signed in",
        Admin => "Admin",
        _ => "Anonymous"
    };

    public static bool CanView(string minimumRole, ClaimsPrincipal? user, AuthOptions? auth)
    {
        if (IsAuthorizationDisabled(auth))
        {
            return true;
        }

        var normalized = Normalize(minimumRole);
        if (normalized == Anonymous)
        {
            return true;
        }

        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        return normalized != Admin || HasExplicitAdminRole(user);
    }

    public static bool CanView(string minimumRole, ICurrentUserContext? currentUser, AuthOptions? auth)
    {
        if (IsAuthorizationDisabled(auth))
        {
            return true;
        }

        var normalized = Normalize(minimumRole);
        if (normalized == Anonymous)
        {
            return true;
        }

        if (currentUser?.IsAuthenticated != true)
        {
            return false;
        }

        return normalized != Admin || HasExplicitAdminRole(currentUser);
    }

    public static bool CanSetMinimumRole(string minimumRole, ClaimsPrincipal? user, AuthOptions? auth)
    {
        if (IsAuthorizationDisabled(auth))
        {
            return true;
        }

        var normalized = Normalize(minimumRole);
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (HasExplicitAdminRole(user))
        {
            return true;
        }

        return normalized != Admin && HasEffectiveEditorRole(user, auth);
    }

    public static bool CanSetMinimumRole(string minimumRole, ICurrentUserContext? currentUser, AuthOptions? auth)
    {
        if (IsAuthorizationDisabled(auth))
        {
            return true;
        }

        var normalized = Normalize(minimumRole);
        if (currentUser?.IsAuthenticated != true)
        {
            return false;
        }

        if (HasExplicitAdminRole(currentUser))
        {
            return true;
        }

        return normalized != Admin && HasEffectiveEditorRole(currentUser, auth);
    }

    public static IReadOnlyList<string> EditableOptions(ICurrentUserContext? currentUser, AuthOptions? auth) =>
        IsAuthorizationDisabled(auth)
            ? All
            : HasExplicitAdminRole(currentUser)
            ? All
            : HasEffectiveEditorRole(currentUser, auth) ? [Anonymous, Authenticated] : [];

    public static string ResolveStoredMinimumRole(string? requestedMinimumRole, string? existingMinimumRole, string configuredDefaultMinimumRole)
    {
        if (!string.IsNullOrWhiteSpace(requestedMinimumRole))
        {
            return Normalize(requestedMinimumRole);
        }

        if (!string.IsNullOrWhiteSpace(existingMinimumRole))
        {
            return Normalize(existingMinimumRole);
        }

        return Normalize(configuredDefaultMinimumRole);
    }

    public static string DefaultForEditor(ICurrentUserContext? currentUser, AuthOptions? auth, string configuredDefault)
    {
        var normalized = Normalize(configuredDefault);
        return CanSetMinimumRole(normalized, currentUser, auth)
            ? normalized
            : HasEffectiveEditorRole(currentUser, auth) ? Authenticated : Anonymous;
    }

    private static bool HasExplicitAdminRole(ClaimsPrincipal? user) =>
        user?.FindAll(ClaimTypes.Role).Any(claim => string.Equals(claim.Value, MemorySmithRoles.Admin, StringComparison.OrdinalIgnoreCase)) == true;

    private static bool HasExplicitAdminRole(ICurrentUserContext? currentUser) =>
        currentUser?.Roles.Any(role => string.Equals(role, MemorySmithRoles.Admin, StringComparison.OrdinalIgnoreCase)) == true;

    private static bool HasEffectiveEditorRole(ClaimsPrincipal? user, AuthOptions? auth)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var roles = user.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToList();
        if (auth?.AutoEditorForAuthenticatedUsers == true)
        {
            return true;
        }

        if (roles.Any(role => string.Equals(role, MemorySmithRoles.Editor, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return roles.Count == 0 && HasConfiguredDefaultEditorRole(auth);
    }

    private static bool HasEffectiveEditorRole(ICurrentUserContext? currentUser, AuthOptions? auth)
    {
        if (currentUser?.IsAuthenticated != true)
        {
            return false;
        }

        if (auth?.AutoEditorForAuthenticatedUsers == true)
        {
            return true;
        }

        if (currentUser.Roles.Any(role => string.Equals(role, MemorySmithRoles.Editor, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return currentUser.Roles.Count == 0 && HasConfiguredDefaultEditorRole(auth);
    }

    private static bool HasConfiguredDefaultEditorRole(AuthOptions? auth) =>
        string.Equals(MemorySmithPermissionHandler.NormalizeAuthenticatedDefaultRole(auth?.AuthenticatedDefaultRole), MemorySmithRoles.Editor, StringComparison.OrdinalIgnoreCase);

    private static bool IsAuthorizationDisabled(AuthOptions? auth) => auth?.Enabled == false;
}

public sealed record PageSummary(
    string Slug,
    string Title,
    string Snippet,
    DateTime LastUpdatedUtc,
    string MinimumRole = PageAccessLevels.Anonymous);

public sealed record PageDocument(
    string Slug,
    string Title,
    string Markdown,
    string Html,
    DateTime LastUpdatedUtc,
    string RelativePath,
    string MinimumRole = PageAccessLevels.Anonymous);

public sealed record PageSaveRequest(string? Slug, string? Title, string Markdown, string? MinimumRole = null);

public sealed record PageSearchQuery(string? Query = null, int Limit = 50);

public sealed record PageAsset(string FileName, string MarkdownPath, string RequestPath, long Size);

public sealed record PageAssetAccessInfo(bool IsReferenced, string MinimumRole);

public interface IPageService
{
    Task<IReadOnlyList<PageSummary>> ListAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PageSummary>> SearchAsync(PageSearchQuery query, CancellationToken cancellationToken);
    Task<PageDocument?> GetAsync(string slug, CancellationToken cancellationToken);
    Task<PageDocument> SaveAsync(PageSaveRequest request, CancellationToken cancellationToken);
    Task<PageAsset> SaveAssetAsync(string fileName, Stream content, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string slug, CancellationToken cancellationToken);
    string RenderHtml(string markdown);
}

public sealed partial class FilePageService : IPageService
{
    private static readonly MarkdownPipeline AssetReferencePipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _rootPath;
    private readonly string _assetPath;
    private readonly bool _allowRawHtml;
    private readonly string _defaultMinimumRole;
    private readonly object _lock = new();
    private Dictionary<string, string>? _assetMinimumRoleIndex;

    public FilePageService(string rootPath, PageOptions? options = null)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _assetPath = Path.Combine(_rootPath, "assets");
        _allowRawHtml = options?.AllowRawHtml ?? false;
        _defaultMinimumRole = PageAccessLevels.Normalize(options?.DefaultMinimumRole);
        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(_assetPath);
    }

    public Task<IReadOnlyList<PageSummary>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<PageSummary>>(EnumeratePageFiles()
                .Select(ReadSummary)
                .OrderByDescending(page => page.LastUpdatedUtc)
                .ThenBy(page => page.Title, StringComparer.OrdinalIgnoreCase)
                .ToList());
        }
    }

    public Task<IReadOnlyList<PageSummary>> SearchAsync(PageSearchQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var limit = Math.Clamp(query.Limit, 1, 200);
        var searchText = query.Query?.Trim() ?? string.Empty;
        var tokens = TokenPattern().Matches(searchText).Select(match => match.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            var pages = EnumeratePageFiles()
                .Select(file => (File: file, Markdown: File.ReadAllText(file)))
                .Select(item =>
                {
                    var slug = ToSlug(item.File);
                    var title = ExtractTitle(item.Markdown, slug);
                    var score = Score(title, item.Markdown, searchText, tokens);
                    return (Summary: ToSummary(slug, title, item.Markdown, item.File), Score: score);
                })
                .Where(item => tokens.Count == 0 || item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Summary.LastUpdatedUtc)
                .ThenBy(item => item.Summary.Title, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(item => item.Summary)
                .ToList();

            return Task.FromResult<IReadOnlyList<PageSummary>>(pages);
        }
    }

    public Task<PageDocument?> GetAsync(string slug, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var normalizedSlug = NormalizeSlug(slug);
            var path = GetPagePath(normalizedSlug);
            return Task.FromResult(File.Exists(path) ? ReadDocument(path) : null);
        }
    }

    public Task<PageDocument> SaveAsync(PageSaveRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var slug = NormalizeSlug(string.IsNullOrWhiteSpace(request.Slug) ? request.Title : request.Slug);
            var path = GetPagePath(slug);
            var minimumRole = PageAccessLevels.Normalize(request.MinimumRole, File.Exists(path) ? ReadMinimumRole(path) : _defaultMinimumRole);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, EnsureMarkdownHasTitle(request.Markdown, request.Title));
            WriteMetadata(path, minimumRole);
            InvalidateAssetMinimumRoleIndex();
            return Task.FromResult(ReadDocument(path)!);
        }
    }

    public async Task<PageAsset> SaveAssetAsync(string fileName, Stream content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_assetPath);
        var safeFileName = GetUniqueAssetFileName(fileName);
        var path = Path.Combine(_assetPath, safeFileName);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(output, cancellationToken);
        return new PageAsset(safeFileName, $"assets/{safeFileName}", $"/page-assets/{safeFileName}", output.Length);
    }

    public Task<PageAssetAccessInfo> GetAssetAccessInfoAsync(string assetPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedAssetPath = NormalizeAssetLookupPath(assetPath);
        if (string.IsNullOrWhiteSpace(normalizedAssetPath))
        {
            return Task.FromResult(new PageAssetAccessInfo(false, PageAccessLevels.Anonymous));
        }

        lock (_lock)
        {
            _assetMinimumRoleIndex ??= BuildAssetMinimumRoleIndex();
            return Task.FromResult(_assetMinimumRoleIndex.TryGetValue(normalizedAssetPath, out var minimumRole)
                ? new PageAssetAccessInfo(true, minimumRole)
                : new PageAssetAccessInfo(false, PageAccessLevels.Anonymous));
        }
    }

    public Task<bool> DeleteAsync(string slug, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var path = GetPagePath(NormalizeSlug(slug));
            if (!File.Exists(path))
            {
                return Task.FromResult(false);
            }

            File.Delete(path);
            var metadataPath = GetMetadataPath(path);
            if (File.Exists(metadataPath))
            {
                File.Delete(metadataPath);
            }

            InvalidateAssetMinimumRoleIndex();

            return Task.FromResult(true);
        }
    }

    public string RenderHtml(string markdown) =>
        ChatMarkdownRenderer.RenderHtml(NormalizeAssetReferences(markdown), _allowRawHtml);

    private IEnumerable<string> EnumeratePageFiles() =>
        Directory.EnumerateFiles(_rootPath, "*.md", SearchOption.AllDirectories)
            .Where(path => !IsUnderPath(path, _assetPath));

    private PageDocument? ReadDocument(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var markdown = File.ReadAllText(path);
        var slug = ToSlug(path);
        var title = ExtractTitle(markdown, slug);
        return new PageDocument(
            slug,
            title,
            markdown,
            RenderHtml(markdown),
            File.GetLastWriteTimeUtc(path),
            Path.GetRelativePath(_rootPath, path),
            ReadMinimumRole(path));
    }

    private PageSummary ReadSummary(string path)
    {
        var markdown = File.ReadAllText(path);
        var slug = ToSlug(path);
        return ToSummary(slug, ExtractTitle(markdown, slug), markdown, path);
    }

    private PageSummary ToSummary(string slug, string title, string markdown, string path) =>
        new(slug, title, BuildSnippet(markdown), File.GetLastWriteTimeUtc(path), ReadMinimumRole(path));

    private string ReadMinimumRole(string pagePath)
    {
        var metadataPath = GetMetadataPath(pagePath);
        if (!File.Exists(metadataPath))
        {
            return _defaultMinimumRole;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<PageMetadata>(File.ReadAllText(metadataPath), MetadataJsonOptions);
            return PageAccessLevels.Normalize(metadata?.MinimumRole, _defaultMinimumRole);
        }
        catch (JsonException)
        {
            return _defaultMinimumRole;
        }
    }

    private static void WriteMetadata(string pagePath, string minimumRole)
    {
        var metadataPath = GetMetadataPath(pagePath);
        var tempPath = metadataPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(new PageMetadata(PageAccessLevels.Normalize(minimumRole)), MetadataJsonOptions) + Environment.NewLine);
        File.Move(tempPath, metadataPath, overwrite: true);
    }

    private static string GetMetadataPath(string pagePath) =>
        Path.Combine(Path.GetDirectoryName(pagePath)!, Path.GetFileNameWithoutExtension(pagePath) + ".page.json");

    private sealed record PageMetadata(string MinimumRole);

    private static string ExtractTitle(string markdown, string slug)
    {
        var titleLine = markdown.Split('\n', StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(titleLine)
            ? ToTitle(slug)
            : titleLine[2..].Trim();
    }

    private static string BuildSnippet(string markdown)
    {
        var text = MarkdownFormattingPattern().Replace(markdown, string.Empty);
        text = WhitespacePattern().Replace(text, " ").Trim();
        return text.Length <= 220 ? text : text[..220] + "...";
    }

    private static int Score(string title, string markdown, string query, HashSet<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return 1;
        }

        var score = 0;
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        if (markdown.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }

        foreach (var token in tokens)
        {
            if (title.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 4;
            }

            if (markdown.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }
        }

        return score;
    }

    private string GetPagePath(string slug)
    {
        var relative = slug.Replace('/', Path.DirectorySeparatorChar) + ".md";
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, relative));
        var root = _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Page slug resolves outside the configured pages directory.");
        }

        return fullPath;
    }

    private string ToSlug(string path)
    {
        var relative = Path.GetRelativePath(_rootPath, path);
        var withoutExtension = Path.ChangeExtension(relative, null) ?? relative;
        return NormalizeSlug(withoutExtension.Replace(Path.DirectorySeparatorChar, '/'));
    }

    public static string NormalizeSlug(string? value)
    {
        var slug = (value ?? string.Empty).Trim().Replace('\\', '/').Replace(' ', '-').ToLowerInvariant();
        if (slug.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            slug = slug[..^3];
        }

        slug = InvalidSlugCharacters().Replace(slug, "-");
        slug = DuplicateSeparators().Replace(slug, "-");
        slug = string.Join('/', slug.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(slug) ? $"page-{DateTime.UtcNow:yyyyMMddHHmmss}" : slug;
    }

    private void InvalidateAssetMinimumRoleIndex() =>
        _assetMinimumRoleIndex = null;

    private Dictionary<string, string> BuildAssetMinimumRoleIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumeratePageFiles())
        {
            var markdown = File.ReadAllText(path);
            var minimumRole = ReadMinimumRole(path);
            foreach (var assetPath in ExtractReferencedAssetPaths(markdown))
            {
                if (index.TryGetValue(assetPath, out var existingMinimumRole))
                {
                    index[assetPath] = MoreRestrictiveMinimumRole(existingMinimumRole, minimumRole);
                }
                else
                {
                    index[assetPath] = minimumRole;
                }
            }
        }

        return index;
    }

    private IEnumerable<string> ExtractReferencedAssetPaths(string markdown)
    {
        var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var document = Markdown.Parse(markdown ?? string.Empty, AssetReferencePipeline);
        foreach (var block in document)
        {
            CollectReferencedAssetPaths(block, assetPaths);
        }

        return assetPaths;
    }

    private static string NormalizeAssetLookupPath(string? assetPath)
    {
        var normalized = (assetPath ?? string.Empty).Replace('\\', '/').Trim();
        if (normalized.StartsWith("/page-assets/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["/page-assets/".Length..];
        }
        else if (normalized.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["assets/".Length..];
        }

        var terminatorIndex = normalized.IndexOfAny(['?', '#']);
        if (terminatorIndex >= 0)
        {
            normalized = normalized[..terminatorIndex];
        }

        return normalized.Trim('/');
    }

    private void CollectReferencedAssetPaths(Block block, ISet<string> assetPaths)
    {
        if (block is LeafBlock leafBlock && leafBlock.Inline is not null)
        {
            CollectReferencedAssetPaths(leafBlock.Inline, assetPaths);
        }

        if (_allowRawHtml && block is HtmlBlock htmlBlock)
        {
            AddHtmlAssetPaths(htmlBlock.Lines.ToString(), assetPaths);
        }

        if (block is ContainerBlock containerBlock)
        {
            foreach (var childBlock in containerBlock)
            {
                CollectReferencedAssetPaths(childBlock, assetPaths);
            }
        }
    }

    private void CollectReferencedAssetPaths(ContainerInline inline, ISet<string> assetPaths)
    {
        for (var current = inline.FirstChild; current is not null; current = current.NextSibling)
        {
            if (current is LinkInline link && TryNormalizeReferencedAssetPath(link.Url, out var assetPath))
            {
                assetPaths.Add(assetPath);
            }

            if (_allowRawHtml && current is HtmlInline htmlInline)
            {
                AddHtmlAssetPaths(htmlInline.Tag, assetPaths);
            }

            if (current is ContainerInline childInline)
            {
                CollectReferencedAssetPaths(childInline, assetPaths);
            }
        }
    }

    private void AddHtmlAssetPaths(string html, ISet<string> assetPaths)
    {
        foreach (Match match in HtmlAssetReferencePattern().Matches(html.Replace('\\', '/')))
        {
            if (TryNormalizeReferencedAssetPath(match.Groups[2].Value, out var assetPath))
            {
                assetPaths.Add(assetPath);
            }
        }
    }

    private static bool TryNormalizeReferencedAssetPath(string? assetPath, out string normalizedAssetPath)
    {
        normalizedAssetPath = string.Empty;
        var normalized = (assetPath ?? string.Empty).Replace('\\', '/').Trim();
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.TrimStart('/');
        if (normalized.StartsWith("page-assets/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["page-assets/".Length..];
        }
        else if (normalized.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["assets/".Length..];
        }
        else
        {
            return false;
        }

        var terminatorIndex = normalized.IndexOfAny(['?', '#']);
        if (terminatorIndex >= 0)
        {
            normalized = normalized[..terminatorIndex];
        }

        normalizedAssetPath = normalized.Trim('/');
        return !string.IsNullOrWhiteSpace(normalizedAssetPath);
    }

    private static string MoreRestrictiveMinimumRole(string left, string right) =>
        MinimumRoleRank(left) >= MinimumRoleRank(right)
            ? PageAccessLevels.Normalize(left)
            : PageAccessLevels.Normalize(right);

    private static int MinimumRoleRank(string minimumRole) => PageAccessLevels.Normalize(minimumRole) switch
    {
        PageAccessLevels.Anonymous => 0,
        PageAccessLevels.Authenticated => 1,
        PageAccessLevels.Admin => 2,
        _ => 0
    };

    private static string ToTitle(string slug) =>
        string.Join(' ', slug.Split(['/', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static string EnsureMarkdownHasTitle(string markdown, string? title)
    {
        var content = (markdown ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title) || content.Split('\n', StringSplitOptions.TrimEntries).Any(line => line.StartsWith("# ", StringComparison.Ordinal)))
        {
            return content;
        }

        return $"# {title.Trim()}\n\n{content}".Trim();
    }

    private static string NormalizeAssetReferences(string markdown)
    {
        var normalized = MarkdownAssetPattern().Replace(markdown, match => match.Groups[1].Value + ToAssetRequestPath(match.Groups[2].Value));
        return HtmlAssetPattern().Replace(normalized, match => match.Groups[1].Value + ToAssetRequestPath(match.Groups[2].Value));
    }

    private static string ToAssetRequestPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
            ? "/page-assets/" + normalized[7..]
            : path;
    }

    private string GetUniqueAssetFileName(string fileName)
    {
        var safeName = NormalizeAssetFileName(fileName);
        var name = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        var candidate = safeName;
        var suffix = 1;
        while (File.Exists(Path.Combine(_assetPath, candidate)))
        {
            candidate = $"{name}-{suffix++}{extension}";
        }

        return candidate;
    }

    private static string NormalizeAssetFileName(string fileName)
    {
        var name = Path.GetFileName(fileName).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"asset-{DateTime.UtcNow:yyyyMMddHHmmss}.bin";
        }

        var extension = Path.GetExtension(name).ToLowerInvariant();
        var baseName = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        baseName = AssetBaseNamePattern().Replace(baseName, "-").Trim('-');
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = $"asset-{DateTime.UtcNow:yyyyMMddHHmmss}";
        }

        extension = AssetExtensionPattern().Replace(extension, string.Empty);
        return string.IsNullOrWhiteSpace(extension) ? baseName : baseName + extension;
    }

    [GeneratedRegex("[A-Za-z0-9]+")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("[^a-z0-9/_-]+")]
    private static partial Regex InvalidSlugCharacters();

    [GeneratedRegex("[-_]{2,}")]
    private static partial Regex DuplicateSeparators();

    [GeneratedRegex(@"(\]\()((?:\./|/)?(?:assets|page-assets)/[^)\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownAssetPattern();

    [GeneratedRegex(@"((?:src|href)=['""'])((?:\./|/)?(?:assets|page-assets)/[^'""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlAssetPattern();

    [GeneratedRegex(@"[#>*_`\[\]()]")]
    private static partial Regex MarkdownFormattingPattern();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"((?:src|href)=['""'])((?:\./|/)?(?:assets|page-assets)/[^'""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlAssetReferencePattern();

    [GeneratedRegex("[^a-z0-9_-]+")]
    private static partial Regex AssetBaseNamePattern();

    [GeneratedRegex("[^.a-z0-9]+")]
    private static partial Regex AssetExtensionPattern();

    private static bool IsUnderPath(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}