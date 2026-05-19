using System.Text.RegularExpressions;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace MemorySmith.App.Services;

public sealed partial class PrismCodeBlockRenderer : CodeBlockRenderer
{
    protected override void Write(HtmlRenderer renderer, CodeBlock obj)
    {
        WritePrismBlock(renderer, obj);
    }

    internal static void WritePrismBlock(HtmlRenderer renderer, CodeBlock obj)
    {
        var language = obj is FencedCodeBlock fencedCodeBlock
            ? NormalizeLanguage(fencedCodeBlock.Info)
            : string.Empty;

        if (obj is FencedCodeBlock mermaidBlock && MermaidBlockRenderer.IsCompleteMermaidBlock(mermaidBlock))
        {
            MermaidBlockRenderer.WriteMermaidBlock(renderer, obj);
            return;
        }

        renderer.EnsureLine();
        renderer.Write("<pre><code");
        if (!string.IsNullOrWhiteSpace(language))
        {
            renderer.Write(" class=\"language-");
            renderer.WriteEscape(language);
            renderer.Write("\"");
        }

        renderer.Write(">");
        renderer.WriteLeafRawLines(obj, writeEndOfLines: true, escape: true, softEscape: true);
        renderer.Write("</code></pre>");
        renderer.EnsureLine();
    }

    private static string NormalizeLanguage(string? info)
    {
        var language = (info ?? string.Empty).Trim();
        if (language.Length == 0)
        {
            return string.Empty;
        }

        language = language.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        return LanguageClassPattern().Replace(language.ToLowerInvariant(), "-").Trim('-');
    }

    [GeneratedRegex("[^a-z0-9_+#-]+", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageClassPattern();
}