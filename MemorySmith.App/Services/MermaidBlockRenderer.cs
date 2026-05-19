using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace MemorySmith.App.Services;

public sealed class MermaidBlockRenderer : HtmlObjectRenderer<FencedCodeBlock>
{
    protected override void Write(HtmlRenderer renderer, FencedCodeBlock obj)
    {
        if (IsCompleteMermaidBlock(obj))
        {
            WriteMermaidBlock(renderer, obj);
            return;
        }

        PrismCodeBlockRenderer.WritePrismBlock(renderer, obj);
    }

    internal static bool IsMermaid(string? info) =>
        string.Equals((info ?? string.Empty).Trim(), "mermaid", StringComparison.OrdinalIgnoreCase);

    internal static bool IsCompleteMermaidBlock(FencedCodeBlock block) =>
        IsMermaid(block.Info) && block.ClosingFencedCharCount >= block.OpeningFencedCharCount;

    internal static void WriteMermaidBlock(HtmlRenderer renderer, CodeBlock obj)
    {
        renderer.EnsureLine();
        renderer.Write("<pre class=\"mermaid\">");
        renderer.WriteLeafRawLines(obj, writeEndOfLines: true, escape: true, softEscape: true);
        renderer.Write("</pre>");
        renderer.EnsureLine();
    }
}