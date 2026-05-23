using System.Text.RegularExpressions;
using Markdig;

namespace MemorySmith.App.Services;

public static partial class ChatMarkdownRenderer
{
    private static readonly MarkdownPipeline SafePipeline = BuildPipeline(allowRawHtml: false);
    private static readonly MarkdownPipeline TrustedPipeline = BuildPipeline(allowRawHtml: true);
    private static readonly HashSet<string> ReservedRootRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "pages",
        "memories",
        "chat",
        "health",
        "variables",
        "about",
        "login",
        "profile",
        "proposals",
        "maintenance",
        "admin",
        "api",
        "mcp",
        "page-assets",
        "_content",
        "css",
        "js"
    };

    public static string RenderHtml(string? markdown, bool allowRawHtml = false)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var html = Markdown.ToHtml(markdown, allowRawHtml ? TrustedPipeline : SafePipeline);
        return LinkAttributeRegex().Replace(html, SanitizeLinkAttribute);
    }

    private static MarkdownPipeline BuildPipeline(bool allowRawHtml)
    {
        var builder = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions();
        builder.Extensions.Add(new MermaidExtension());

        if (!allowRawHtml)
        {
            builder.DisableHtml();
        }

        return builder.Build();
    }

    private static string SanitizeLinkAttribute(Match match)
    {
        var name = match.Groups["name"].Value;
        var value = match.Groups["value"].Value;
        var normalized = NormalizeLinkTarget(name, value);
        return IsSafeLinkTarget(normalized) ? $"{name}=\"{normalized}\"" : $"{name}=\"{UnsafeAttributeFallback(name)}\"";
    }

    private static string NormalizeLinkTarget(string attributeName, string value)
    {
        if (!attributeName.Equals("href", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return TryNormalizePageLink(value, out var normalized) ? normalized : value;
    }

    private static bool TryNormalizePageLink(string value, out string normalized)
    {
        normalized = value;
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (trimmed.StartsWith('#') ||
            trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hashIndex = trimmed.IndexOf('#');
        var queryIndex = trimmed.IndexOf('?');
        var suffixIndex = hashIndex >= 0 && queryIndex >= 0 ? Math.Min(hashIndex, queryIndex)
            : hashIndex >= 0 ? hashIndex
            : queryIndex;
        var suffix = suffixIndex >= 0 ? trimmed[suffixIndex..] : string.Empty;
        var path = suffixIndex >= 0 ? trimmed[..suffixIndex] : trimmed;

        while (path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith("../", StringComparison.Ordinal))
        {
            path = path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path[3..];
        }

        path = path.TrimStart('/');
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\'))
        {
            return false;
        }

        if (path.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^3];
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        var firstSegment = segments[0];
        if (ReservedRootRoutes.Contains(firstSegment))
        {
            return false;
        }

        var lastSegment = segments[^1];
        var lastDot = lastSegment.LastIndexOf('.');
        if (lastDot > 0)
        {
            return false;
        }

        normalized = "/pages/" + string.Join('/', segments.Select(Uri.EscapeDataString)) + suffix;
        return true;
    }

    private static string UnsafeAttributeFallback(string attributeName) =>
        attributeName.Equals("src", StringComparison.OrdinalIgnoreCase) ? string.Empty : "#";

    private static bool IsSafeLinkTarget(string value)
    {
        var target = value.Trim();
        if (string.IsNullOrWhiteSpace(target) || target.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (target.StartsWith('#') || target.StartsWith('/') || target.StartsWith("./", StringComparison.Ordinal) || target.StartsWith("../", StringComparison.Ordinal))
        {
            return true;
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return uri.Scheme is "http" or "https" or "mailto";
        }

        return !target.Contains(':', StringComparison.Ordinal);
    }

    [GeneratedRegex("\\b(?<name>href|src)=\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkAttributeRegex();
}