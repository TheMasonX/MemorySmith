using System.Net;
using System.Text.RegularExpressions;

namespace MemorySmith.App.Services;

public static partial class ChatReferenceLinkPolicy
{
    private static readonly Regex AnchorHrefRegex = new("<a\\b(?<before>[^>]*?)\\bhref=\"(?<href>[^\"]*)\"(?<after>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex InlineCodeRegex = new("<code>(?<value>[^<]+)</code>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PageSlugPattern = new("^[a-z0-9][a-z0-9/_-]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MemoryIdPattern = new("^[a-z0-9][a-z0-9._/-]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string LinkifyInlineCodeReferences(
        string html,
        IEnumerable<string> allowedPageSlugs,
        IEnumerable<string> allowedMemoryIds)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return html;
        }

        var pageSet = BuildNormalizedPageSet(allowedPageSlugs);
        var memorySet = BuildNormalizedMemorySet(allowedMemoryIds);
        if (pageSet.Count == 0 && memorySet.Count == 0)
        {
            return html;
        }

        return InlineCodeRegex.Replace(html, match =>
        {
            var raw = WebUtility.HtmlDecode(match.Groups["value"].Value).Trim();
            if (TryResolveInlineMemoryId(raw, memorySet, out var memoryId))
            {
                var href = MemoryHref(memoryId);
                return $"<a class=\"wiki-tag chat-resource chat-inline-ref\" href=\"{href}\" target=\"_blank\" rel=\"noopener noreferrer\">{WebUtility.HtmlEncode(raw)}</a>";
            }

            if (TryResolveInlinePageSlug(raw, pageSet, out var slug))
            {
                var href = PageHref(slug);
                return $"<a class=\"wiki-tag chat-resource chat-inline-ref\" href=\"{href}\" target=\"_blank\" rel=\"noopener noreferrer\">{WebUtility.HtmlEncode(raw)}</a>";
            }

            return match.Value;
        });
    }

    public static string FilterToAllowedTargets(
        string html,
        IEnumerable<string> allowedPageSlugs,
        IEnumerable<string> allowedMemoryIds)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return html;
        }

        var pageSet = BuildNormalizedPageSet(allowedPageSlugs);
        var memorySet = BuildNormalizedMemorySet(allowedMemoryIds);

        return AnchorHrefRegex.Replace(html, match =>
        {
            var before = match.Groups["before"].Value;
            var rawHref = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            var after = match.Groups["after"].Value;

            if (TryGetPageSlug(rawHref, out var slug))
            {
                var href = pageSet.Contains(slug) ? PageHref(slug) : "#";
                return $"<a{before}href=\"{href}\"{after}>";
            }

            if (TryGetMemoryId(rawHref, out var memoryId))
            {
                var href = memorySet.Contains(memoryId) ? MemoryHref(memoryId) : "#";
                return $"<a{before}href=\"{href}\"{after}>";
            }

            return match.Value;
        });
    }

    private static bool TryGetPageSlug(string href, out string slug)
    {
        slug = string.Empty;
        if (string.IsNullOrWhiteSpace(href))
        {
            return false;
        }

        var candidate = href.Trim();
        if (candidate.StartsWith("page:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[5..];
        }
        else if (candidate.StartsWith("/pages/", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[7..];
        }
        else
        {
            return false;
        }

        candidate = SplitPath(candidate);
        if (!TryNormalizePageSlug(candidate, out slug))
        {
            return false;
        }

        return true;
    }

    private static bool TryResolveInlinePageSlug(string value, IReadOnlySet<string> allowedPageSlugs, out string slug)
    {
        slug = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = NormalizeLabeledReference(value.Trim());
        if (TryGetPageSlug(candidate, out var hrefSlug) && allowedPageSlugs.Contains(hrefSlug))
        {
            slug = hrefSlug;
            return true;
        }

        if (candidate.StartsWith("page:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[5..];
        }

        if (TryNormalizePageSlug(candidate, out var normalized) && allowedPageSlugs.Contains(normalized))
        {
            slug = normalized;
            return true;
        }

        return false;
    }

    private static bool TryGetMemoryId(string href, out string memoryId)
    {
        memoryId = string.Empty;
        if (string.IsNullOrWhiteSpace(href))
        {
            return false;
        }

        var candidate = href.Trim();
        if (candidate.StartsWith("memory:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[7..];
        }
        else if (candidate.StartsWith("/api/memories/", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[14..];
        }
        else
        {
            return false;
        }

        candidate = SplitPath(candidate);
        candidate = Uri.UnescapeDataString(candidate);
        if (!MemoryIdPattern.IsMatch(candidate))
        {
            return false;
        }

        memoryId = candidate;
        return true;
    }

    private static bool TryResolveInlineMemoryId(string value, IReadOnlySet<string> allowedMemoryIds, out string memoryId)
    {
        memoryId = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = NormalizeLabeledReference(value.Trim());
        if (TryGetMemoryId(candidate, out var hrefMemoryId) && allowedMemoryIds.Contains(hrefMemoryId))
        {
            memoryId = hrefMemoryId;
            return true;
        }

        if (candidate.StartsWith("memory:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[7..];
        }

        candidate = Uri.UnescapeDataString(candidate);
        if (MemoryIdPattern.IsMatch(candidate) && allowedMemoryIds.Contains(candidate))
        {
            memoryId = candidate;
            return true;
        }

        return false;
    }

    private static bool TryNormalizePageSlug(string value, out string slug)
    {
        slug = string.Empty;
        var candidate = value.Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (candidate.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^3];
        }

        var decodedSegments = candidate
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (decodedSegments.Length == 0)
        {
            return false;
        }

        candidate = string.Join('/', decodedSegments);
        if (!PageSlugPattern.IsMatch(candidate))
        {
            return false;
        }

        slug = candidate.ToLowerInvariant();
        return true;
    }

    private static string SplitPath(string value)
    {
        var hashIndex = value.IndexOf('#');
        var queryIndex = value.IndexOf('?');
        var split = hashIndex >= 0 && queryIndex >= 0 ? Math.Min(hashIndex, queryIndex)
            : hashIndex >= 0 ? hashIndex
            : queryIndex;
        return split >= 0 ? value[..split] : value;
    }

    private static string NormalizeLabeledReference(string value)
    {
        var candidate = value.Trim();
        var dashIndex = candidate.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIndex > 0)
        {
            return candidate[..dashIndex].Trim();
        }

        var labeledColonIndex = candidate.IndexOf(": ", StringComparison.Ordinal);
        if (labeledColonIndex > 0)
        {
            return candidate[..labeledColonIndex].Trim();
        }

        return candidate;
    }

    private static HashSet<string> BuildNormalizedPageSet(IEnumerable<string> allowedPageSlugs) =>
        allowedPageSlugs
            .Where(static slug => !string.IsNullOrWhiteSpace(slug))
            .Select(static slug => slug.Trim())
            .Select(static slug => TryNormalizePageSlug(slug, out var normalized) ? normalized : string.Empty)
            .Where(static slug => !string.IsNullOrWhiteSpace(slug))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> BuildNormalizedMemorySet(IEnumerable<string> allowedMemoryIds) =>
        allowedMemoryIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => Uri.UnescapeDataString(id.Trim()))
            .Where(static id => MemoryIdPattern.IsMatch(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string PageHref(string slug) =>
        "/pages/" + string.Join('/', slug.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private static string MemoryHref(string id) =>
        "/api/memories/" + Uri.EscapeDataString(id);
}
