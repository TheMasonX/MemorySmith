using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/search")]
[Authorize(Policy = MemorySmithPolicies.CanViewMemorySmith)]
[IgnoreAntiforgeryToken]
public class SearchController : ControllerBase
{
    private readonly MemoryApplicationService _memories;
    private readonly IPageService _pages;
    private readonly IOptionsMonitor<MemorySmithOptions> _options;

    public SearchController(MemoryApplicationService memories, IPageService pages, IOptionsMonitor<MemorySmithOptions> options)
    {
        _memories = memories;
        _pages = pages;
        _options = options;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UnifiedSearchResult>>> Search([FromQuery] string? query, [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var memoryLimit = Math.Max(1, limit / 2);
        var pageLimit = Math.Max(1, limit - memoryLimit);
        var memoryResults = await _memories.HybridSearchAsync(new HybridMemorySearchQuery(query, Limit: memoryLimit), cancellationToken);
        var pageResults = await _pages.SearchVisibleAsync(
            query,
            pageLimit,
            page => PageAccessLevels.CanView(page.MinimumRole, User, _options.CurrentValue.Auth),
            cancellationToken);

        var results = memoryResults.Select(memory => new UnifiedSearchResult(
                "memory",
                memory.Id,
                memory.Title,
                memory.Snippet,
                "/memories",
                memory.Score,
                memory.LastUpdated,
                memory.Diagnostics,
                _memories.GetSemanticProviderMetadata()))
            .Concat(pageResults.Select(page => new UnifiedSearchResult(
                "page",
                page.Slug,
                page.Title,
                page.Snippet,
                ToPageUrl(page.Slug),
                null,
                page.LastUpdatedUtc,
                [],
                new RetrievalProviderMetadata("page", "markdown-lexical", true, "Markdown page lexical search."))))
            .OrderByDescending(result => result.Score ?? 0)
            .ThenByDescending(result => result.LastUpdatedUtc)
            .ThenBy(result => result.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        return Ok(results);
    }

    private static string ToPageUrl(string slug) =>
        "/pages/" + string.Join('/', slug.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.EscapeDataString));
}

public sealed record UnifiedSearchResult(
    string Kind,
    string Id,
    string Title,
    string Snippet,
    string Url,
    double? Score,
    DateTime LastUpdatedUtc,
    IReadOnlyList<MemoryDiagnostic> Diagnostics,
    RetrievalProviderMetadata Provider);