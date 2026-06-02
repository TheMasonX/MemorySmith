using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MemorySmith.App.Services;

/// <summary>
/// Result of a deterministic intent match against the user's message.
/// </summary>
public sealed record ChatIntentMatch(string ToolName, JsonObject Arguments, string Reason);

/// <summary>
/// Recognises high-confidence natural-language intents and rewrites them as concrete
/// MemorySmith tool calls so the chat agent does not need to rely on the upstream provider
/// emitting valid tool-call JSON. Designed to be robust against models that decline to call tools.
/// </summary>
public sealed partial class ChatIntentInterceptor
{
    // Compiled regexes — accept everything after the keyword as the query.
    [GeneratedRegex(@"^\s*(?:please\s+)?(?:search|find|look\s*up|query)(?:\s+(?:the\s+)?(?:wiki|memorysmith|kb|knowledge\s*base|memories?|pages?|records?))?\s+(?:for|about|regarding)?\s*[:\-]?\s*(?<q>.+?)\s*[.?!]*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SearchRegex();

    [GeneratedRegex(@"^\s*(?:semantic(?:ally)?|vector|embedding)\s+search\s+(?:for|about)?\s*[:\-]?\s*(?<q>.+?)\s*[.?!]*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SemanticSearchRegex();

    [GeneratedRegex(@"^\s*(?:please\s+)?(?:search|find|look\s*up|query)\s+(?:the\s+)?(?:code|codebase|repo(?:sitory)?|source)\s+(?:for|about|regarding)?\s*[:\-]?\s*(?<q>.+?)\s*[.?!]*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CodeSearchRegex();

    [GeneratedRegex(@"^\s*(?:hybrid|rrf)\s+search\s+(?:for|about)?\s*[:\-]?\s*(?<q>.+?)\s*[.?!]*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HybridSearchRegex();

    [GeneratedRegex(@"^\s*(?:get|show|read|fetch|open)\s+(?:memory|record|memo)\s+(?:id\s+)?[`""']?(?<id>[A-Za-z0-9._\-:/]+)[`""']?\s*[.?!]*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GetMemoryRegex();

    [GeneratedRegex(@"^\s*(?:open|show|read|get|fetch)\s+(?:the\s+)?page\s+[`""']?(?<slug>[A-Za-z0-9._\-/]+)[`""']?\s*[.?!]*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex GetPageRegex();

    [GeneratedRegex(@"^\s*(?:context\s*pack|build\s+(?:a\s+)?context\s*pack)\s+(?:for|about)?\s*[:\-]?\s*(?<q>.+?)\s*[.?!]*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ContextPackRegex();

    public ChatIntentMatch? TryMatch(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        if (GetMemoryRegex().Match(message) is { Success: true } getMem)
        {
            return new ChatIntentMatch(
                "memorysmith_get",
                new JsonObject { ["id"] = getMem.Groups["id"].Value.Trim() },
                "Detected explicit 'get memory <id>' intent.");
        }

        if (GetPageRegex().Match(message) is { Success: true } getPage)
        {
            return new ChatIntentMatch(
                "memorysmith_page_get",
                new JsonObject { ["slug"] = getPage.Groups["slug"].Value.Trim() },
                "Detected explicit 'open page <slug>' intent.");
        }

        if (SemanticSearchRegex().Match(message) is { Success: true } sem)
        {
            return new ChatIntentMatch(
                "memorysmith_hybrid_search",
                new JsonObject { ["query"] = sem.Groups["q"].Value.Trim(), ["limit"] = 5 },
                "Detected explicit 'semantic search' intent — routing to hybrid search.");
        }

        if (CodeSearchRegex().Match(message) is { Success: true } code)
        {
            return new ChatIntentMatch(
                "memorysmith_code_search",
                new JsonObject { ["query"] = code.Groups["q"].Value.Trim(), ["limit"] = 5 },
                "Detected explicit 'search the codebase' intent.");
        }

        if (HybridSearchRegex().Match(message) is { Success: true } hyb)
        {
            return new ChatIntentMatch(
                "memorysmith_hybrid_search",
                new JsonObject { ["query"] = hyb.Groups["q"].Value.Trim(), ["limit"] = 5 },
                "Detected explicit 'hybrid search' intent.");
        }

        if (ContextPackRegex().Match(message) is { Success: true } pack)
        {
            return new ChatIntentMatch(
                "memorysmith_context_pack",
                new JsonObject { ["query"] = pack.Groups["q"].Value.Trim(), ["limit"] = 5 },
                "Detected explicit 'context pack' intent.");
        }

        if (SearchRegex().Match(message) is { Success: true } search)
        {
            return new ChatIntentMatch(
                "memorysmith_hybrid_search",
                new JsonObject { ["query"] = search.Groups["q"].Value.Trim(), ["limit"] = 5 },
                "Detected explicit 'search the wiki' intent.");
        }

        return null;
    }
}
