using System.Globalization;

namespace MemorySmith.App.Services;

public sealed record PageNavigationNode(
    string Key,
    string Label,
    string? Slug,
    PageSummary? Summary,
    IReadOnlyList<PageNavigationNode> Children,
    int PageCount)
{
    public bool IsFolder => Slug is null;
}

public sealed record PageNavigationTreeRow(
    PageNavigationNode Node,
    int Depth,
    bool IsExpanded);

public static class PageNavigationTreeBuilder
{
    public static IReadOnlyList<PageNavigationNode> Build(IReadOnlyList<PageSummary> pages)
    {
        var root = new FolderBuilder(string.Empty, string.Empty);
        foreach (var page in pages.OrderBy(page => page.Slug, StringComparer.OrdinalIgnoreCase))
        {
            var segments = page.Slug.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var current = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var segment = segments[i];
                var key = string.IsNullOrEmpty(current.Key) ? segment : $"{current.Key}/{segment}";
                if (!current.Folders.TryGetValue(segment, out var next))
                {
                    next = new FolderBuilder(key, ToDisplayLabel(segment));
                    current.Folders.Add(segment, next);
                }

                current = next;
            }

            current.Pages.Add(new PageNavigationNode(page.Slug, page.Title, page.Slug, page, [], 1));
        }

        return BuildChildren(root);
    }

    public static IReadOnlyList<PageNavigationTreeRow> Flatten(IReadOnlyList<PageNavigationNode> nodes, ISet<string> expandedKeys)
    {
        var rows = new List<PageNavigationTreeRow>();
        foreach (var node in nodes)
        {
            Append(node, depth: 0, expandedKeys, rows);
        }

        return rows;
    }

    public static IReadOnlySet<string> AncestorKeysForSlug(IReadOnlyList<PageNavigationNode> nodes, string? slug)
    {
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(slug))
        {
            return ancestors;
        }

        Traverse(nodes, slug, ancestors, []);
        return ancestors;
    }

    private static bool Traverse(
        IReadOnlyList<PageNavigationNode> nodes,
        string slug,
        HashSet<string> ancestors,
        IReadOnlyList<string> path)
    {
        foreach (var node in nodes)
        {
            if (!node.IsFolder && string.Equals(node.Slug, slug, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var key in path)
                {
                    ancestors.Add(key);
                }

                return true;
            }

            if (node.IsFolder)
            {
                var nextPath = path.Concat([node.Key]).ToArray();
                if (Traverse(node.Children, slug, ancestors, nextPath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void Append(PageNavigationNode node, int depth, ISet<string> expandedKeys, List<PageNavigationTreeRow> rows)
    {
        var isExpanded = node.IsFolder && expandedKeys.Contains(node.Key);
        rows.Add(new PageNavigationTreeRow(node, depth, isExpanded));
        if (!node.IsFolder || !isExpanded)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            Append(child, depth + 1, expandedKeys, rows);
        }
    }

    private static IReadOnlyList<PageNavigationNode> BuildChildren(FolderBuilder folder)
    {
        var folders = folder.Folders.Values
            .Select(child =>
            {
                var children = BuildChildren(child);
                var pageCount = children.Sum(node => node.PageCount);
                return new PageNavigationNode(child.Key, child.Label, null, null, children, pageCount);
            })
            .OrderBy(node => node.Label, StringComparer.OrdinalIgnoreCase);

        var pages = folder.Pages
            .OrderBy(node => node.Label, StringComparer.OrdinalIgnoreCase);

        return folders.Concat(pages).ToList();
    }

    private static string ToDisplayLabel(string segment)
    {
        var text = (segment ?? string.Empty).Replace('-', ' ').Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Untitled";
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text);
    }

    private sealed class FolderBuilder(string key, string label)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public Dictionary<string, FolderBuilder> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PageNavigationNode> Pages { get; } = [];
    }
}