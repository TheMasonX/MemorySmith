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
        html = LinkAttributeRegex().Replace(html, SanitizeQuotedLinkAttribute);
        return UnquotedLinkAttributeRegex().Replace(html, SanitizeUnquotedLinkAttribute);
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

    private static string SanitizeQuotedLinkAttribute(Match match)
    {
        var name = match.Groups["name"].Value;
        var quote = match.Groups["quote"].Value;
        var value = match.Groups["value"].Value;
        var normalized = NormalizeLinkTarget(name, value);
        return IsSafeLinkTarget(normalized)
            ? $"{name}={quote}{normalized}{quote}"
            : $"{name}={quote}{UnsafeAttributeFallback(name)}{quote}";
    }

    private static string SanitizeUnquotedLinkAttribute(Match match)
    {
        var name = match.Groups["name"].Value;
        var value = match.Groups["value"].Value;
        var normalized = NormalizeLinkTarget(name, value);
        var sanitized = IsSafeLinkTarget(normalized) ? normalized : UnsafeAttributeFallback(name);
        return $"{name}=\"{sanitized}\"";
    }

    private static string NormalizeLinkTarget(string attributeName, string value)
    {
        if (!attributeName.Equals("href", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (TryNormalizeStructuredReference(value, out var structured))
        {
            return structured;
        }

        return TryNormalizePageLink(value, out var normalized) ? normalized : value;
    }

    private static bool TryNormalizeStructuredReference(string value, out string normalized)
    {
        normalized = value;
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        if (TryNormalizeMemoryReference(trimmed, out normalized))
        {
            return true;
        }

        if (TryNormalizePrefixedPageReference(trimmed, out normalized))
        {
            return true;
        }

        if (!TryExtractReferenceToken(trimmed, out var token))
        {
            return false;
        }

        if (TryNormalizeMemoryReference(token, out normalized))
        {
            return true;
        }

        return TryNormalizePrefixedPageReference(token, out normalized);
    }

    private static bool TryNormalizeMemoryReference(string value, out string normalized)
    {
        normalized = value;
        var candidate = value.Trim();
        if (candidate.StartsWith("memory:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[7..];
        }

        if (!TryNormalizeMemoryId(candidate, out var memoryId))
        {
            return false;
        }

        normalized = "/api/memories/" + Uri.EscapeDataString(memoryId);
        return true;
    }

    private static bool TryNormalizePrefixedPageReference(string value, out string normalized)
    {
        normalized = value;
        var candidate = value.Trim();
        if (!candidate.StartsWith("page:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        candidate = candidate[5..];
        if (!TryNormalizePageSlug(candidate, out var slug))
        {
            return false;
        }

        normalized = "/pages/" + string.Join('/', slug.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return true;
    }

    private static bool TryNormalizeMemoryId(string value, out string id)
    {
        id = string.Empty;
        var candidate = value.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (TryExtractReferenceToken(candidate, out var token))
        {
            candidate = token;
        }

        if (candidate.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!MemoryIdPattern.IsMatch(candidate))
        {
            return false;
        }

        if (!LooksLikeWikiIdentifier(candidate))
        {
            return false;
        }

        id = candidate;
        return true;
    }

    private static bool TryNormalizePageSlug(string value, out string slug)
    {
        slug = string.Empty;
        var candidate = value.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (TryExtractReferenceToken(candidate, out var token))
        {
            candidate = token;
        }

        candidate = candidate.TrimStart('/');
        if (candidate.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^3];
        }

        if (!PageSlugPattern.IsMatch(candidate))
        {
            return false;
        }

        slug = candidate.ToLowerInvariant();
        return true;
    }

    private static bool TryExtractReferenceToken(string value, out string token)
    {
        token = string.Empty;
        foreach (var delimiter in ReferenceLabelDelimiters)
        {
            var index = value.IndexOf(delimiter, StringComparison.Ordinal);
            if (index <= 0)
            {
                continue;
            }

            var head = value[..index].Trim();
            var tail = value[(index + delimiter.Length)..].Trim();
            if (string.IsNullOrWhiteSpace(head) || string.IsNullOrWhiteSpace(tail))
            {
                continue;
            }

            if (!LooksLikeWikiIdentifier(head) &&
                !head.StartsWith("memory:", StringComparison.OrdinalIgnoreCase) &&
                !head.StartsWith("page:", StringComparison.OrdinalIgnoreCase) &&
                !head.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            token = head;
            return true;
        }

        return false;
    }

    private static bool LooksLikeWikiIdentifier(string value)
    {
        if (value.Length < 6)
        {
            return false;
        }

        return value.IndexOfAny(['-', '_', '/', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9']) >= 0;
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

    [GeneratedRegex("\\b(?<name>href|src)=(?<quote>[\"'])(?<value>[^\"']*)\\k<quote>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkAttributeRegex();

    [GeneratedRegex("\\b(?<name>href|src)=(?<value>(?![\"'])[^\\s>]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnquotedLinkAttributeRegex();

    private static readonly string[] ReferenceLabelDelimiters = [": ", " - "];
    private static readonly Regex PageSlugPattern = new("^[a-z0-9][a-z0-9/_-]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MemoryIdPattern = new("^[a-z0-9][a-z0-9._/-]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}