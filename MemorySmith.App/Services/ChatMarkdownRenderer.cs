using System.Text.RegularExpressions;
using Markdig;

namespace MemorySmith.App.Services;

public static partial class ChatMarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static string RenderHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var html = Markdown.ToHtml(markdown, Pipeline);
        return LinkAttributeRegex().Replace(html, SanitizeLinkAttribute);
    }

    private static string SanitizeLinkAttribute(Match match)
    {
        var name = match.Groups["name"].Value;
        var value = match.Groups["value"].Value;
        return IsSafeLinkTarget(value) ? match.Value : $"{name}=\"#\"";
    }

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