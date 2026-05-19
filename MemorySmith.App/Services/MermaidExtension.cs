using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace MemorySmith.App.Services;

public sealed class MermaidExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is not HtmlRenderer htmlRenderer)
        {
            return;
        }

        htmlRenderer.ObjectRenderers.TryRemove<CodeBlockRenderer>();
        htmlRenderer.ObjectRenderers.AddIfNotAlready(new MermaidBlockRenderer());
        htmlRenderer.ObjectRenderers.AddIfNotAlready(new PrismCodeBlockRenderer());
    }
}