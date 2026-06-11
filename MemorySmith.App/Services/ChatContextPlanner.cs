using System.Text.RegularExpressions;

namespace MemorySmith.App.Services;

public sealed record ChatContextPlan(
    bool ShouldPreload,
    int MemoryLimit,
    int PageLimit,
    string Reason,
    string RecommendedToolName)
{
    public string Summary => ShouldPreload
        ? $"preload {MemoryLimit} memory result(s) and {PageLimit} page result(s); prefer {RecommendedToolName} if more evidence is needed"
        : $"skip preload; prefer {RecommendedToolName}; {Reason}";
}

public static partial class ChatContextPlanner
{
    public static ChatContextPlan Plan(MemoryChatRequest request, ChatOptions options, ChatIntentInterceptor intentInterceptor)
    {
        if (!options.PreloadContextEnabled)
        {
            return None("Preloaded context is disabled by configuration.", "memorysmith_hybrid_search");
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return None("The user message is empty.", "memorysmith_hybrid_search");
        }

        var intercepted = intentInterceptor.TryMatch(request.Message);
        if (intercepted is not null)
        {
            return None($"A deterministic intent interceptor will run {intercepted.ToolName}.", intercepted.ToolName);
        }

        var message = request.Message.Trim();
        if (ExactReplyRegex().IsMatch(message) || SimpleNoContextRegex().IsMatch(message))
        {
            return None("The prompt is a direct/simple reply that does not need local wiki context.", "memorysmith_hybrid_search");
        }

        if (request.Mode == MemoryChatMode.Agent && AgentWriteCommandRegex().IsMatch(message) && !EvidenceSeekingRegex().IsMatch(message))
        {
            return None("The Agent request appears write-only and does not ask for existing evidence.", "memorysmith_hybrid_search");
        }

        var localKnowledge = LocalKnowledgeRegex().IsMatch(message);
        var agentEvidence = request.Mode == MemoryChatMode.Agent && AgentContextRegex().IsMatch(message);
        if (!localKnowledge && !agentEvidence)
        {
            return None("No strong MemorySmith/wiki evidence intent was detected.", "memorysmith_hybrid_search");
        }

        var memoryBudget = Math.Clamp(Math.Min(options.MaxContextRecords, options.MaxPreloadedContextRecords), 0, 20);
        var pageBudget = Math.Clamp(Math.Min(options.MaxContextPages, options.MaxPreloadedContextPages), 0, 20);
        var wantsPages = PageIntentRegex().IsMatch(message);
        var wantsMemories = MemoryIntentRegex().IsMatch(message);
        var wantsContextPack = ContextPackIntentRegex().IsMatch(message);
        var wantsCode = CodeIntentRegex().IsMatch(message);

        if (wantsCode && !wantsMemories && !wantsPages)
        {
            return None("Detected codebase/source investigation intent.", "memorysmith_code_search");
        }

        var memoryLimit = memoryBudget;
        var pageLimit = pageBudget;
        if (wantsPages && !wantsMemories)
        {
            memoryLimit = 0;
        }
        else if (wantsMemories && !wantsPages)
        {
            pageLimit = 0;
        }

        // NOTE: memorysmith_unified_search was dropped from the tool catalog in the June 4
        // restructure, so the planner must not recommend it — the model would attempt to call a
        // nonexistent tool. memorysmith_hybrid_search is the closest existing tool for combined
        // memory evidence. Restoring a true unified (memory+page) search tool is tracked as a
        // follow-up feature; when it lands, revisit these recommendations.
        var recommendedTool = wantsContextPack
            ? "memorysmith_context_pack"
            : memoryLimit == 0 && pageLimit > 0
                ? "memorysmith_page_search"
                : "memorysmith_hybrid_search";

        if (memoryLimit == 0 && pageLimit == 0)
        {
            return None("The relevant preloaded context budgets are zero.", recommendedTool);
        }

        var reason = localKnowledge
            ? "Detected explicit MemorySmith/wiki evidence intent."
            : "Detected Agent evidence/review intent.";
        return new ChatContextPlan(true, memoryLimit, pageLimit, reason, recommendedTool);
    }

    private static ChatContextPlan None(string reason, string recommendedToolName) =>
        new(false, 0, 0, reason, recommendedToolName);

    [GeneratedRegex(@"^\s*(?:reply|respond|say|return|output|print)\s+(?:exactly|only|with)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ExactReplyRegex();

    [GeneratedRegex(@"^\s*(?:hi|hello|hey|thanks|thank\s+you|ok|okay|ping|test)\b[\s.!?]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SimpleNoContextRegex();

    [GeneratedRegex(@"\b(?:memorysmith|project\s+wiki|wiki|memories?|records?|pages?|repo(?:sitory)?|codebase|docs?|architecture|mcp|semantic|hybrid|context\s+pack|source\s+links?|data\s+path|windows\s+service|auth|rbac|index(?:ing)?|storage|blazor|ollama|github\s+copilot)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LocalKnowledgeRegex();

    [GeneratedRegex(@"\b(?:create|write|save|update|record)\b.{0,80}\b(?:page|memory|record|note)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AgentWriteCommandRegex();

    [GeneratedRegex(@"\b(?:based\s+on|using|from|according\s+to|look\s*up|search|find|existing|current|prior|summarize|review|audit)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex EvidenceSeekingRegex();

    [GeneratedRegex(@"\b(?:review|audit|plan|summarize|explain|diagnose|investigate|fix|implement|refactor|compare)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AgentContextRegex();

    [GeneratedRegex(@"\b(?:pages?|markdown|docs?|slug)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PageIntentRegex();

    [GeneratedRegex(@"\b(?:memories?|records?|tags?|status|semantic|hybrid|rrf|source\s+links?|context\s+pack|mcp)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MemoryIntentRegex();

    [GeneratedRegex(@"\b(?:context\s+pack|references?|conflicts?|backlinks?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ContextPackIntentRegex();

    [GeneratedRegex(@"\b(?:code|codebase|repo(?:sitory)?|source\s+code|source\s+file(?:s)?|symbol|method|class|interface|implementation|function|csproj|razor|\.cs|\.tsx?|\.jsx?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CodeIntentRegex();
}
