using MemorySmith.App.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/pages")]
public class PagesController : ControllerBase
{
    private readonly IPageService _pages;
    private readonly IOptionsMonitor<MemorySmithOptions> _options;

    public PagesController(IPageService pages, IOptionsMonitor<MemorySmithOptions> options)
    {
        _pages = pages;
        _options = options;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PageSummary>>> GetAll([FromQuery] string? query, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        var requestedLimit = Math.Clamp(limit, 1, 200);
        var pages = string.IsNullOrWhiteSpace(query)
            ? await _pages.ListAsync(cancellationToken)
            : await _pages.SearchAsync(new PageSearchQuery(query, 200), cancellationToken);

        var visiblePages = FilterVisible(pages);
        return Ok(string.IsNullOrWhiteSpace(query) ? visiblePages : visiblePages.Take(requestedLimit).ToList());
    }

    [HttpGet("{**slug}")]
    public async Task<IActionResult> Get(string slug, CancellationToken cancellationToken)
    {
        var normalizedSlug = slug.TrimEnd('/');
        if (normalizedSlug.Length > "/html".Length && normalizedSlug.EndsWith("/html", StringComparison.OrdinalIgnoreCase))
        {
            return await GetHtmlCore(normalizedSlug[..^"/html".Length], cancellationToken);
        }

        var page = await _pages.GetAsync(slug, cancellationToken);
        if (page is null)
        {
            return NotFound();
        }

        return CanView(page) ? Ok(page) : Forbid();
    }

    private async Task<IActionResult> GetHtmlCore(string slug, CancellationToken cancellationToken)
    {
        var page = await _pages.GetAsync(slug, cancellationToken);
        if (page is null)
        {
            return NotFound();
        }

        return CanView(page) ? Content(page.Html, "text/html") : Forbid();
    }

    [HttpPost]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<ActionResult<PageDocument>> Save([FromBody] PageSaveRequest request, CancellationToken cancellationToken)
    {
        if (!TryResolveMinimumRole(request, existing: null, out var resolvedMinimumRole, out var validationResult))
        {
            return validationResult!;
        }

        var page = await _pages.SaveAsync(request with { MinimumRole = resolvedMinimumRole }, cancellationToken);
        return CreatedAtAction(nameof(Get), new { slug = page.Slug }, page);
    }

    [HttpPut("{**slug}")]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<ActionResult<PageDocument>> Update(string slug, [FromBody] PageSaveRequest request, CancellationToken cancellationToken)
    {
        var existing = await _pages.GetAsync(slug, cancellationToken);
        if (existing is not null && !CanView(existing))
        {
            return Forbid();
        }

        if (!TryResolveMinimumRole(request, existing, out var resolvedMinimumRole, out var validationResult))
        {
            return validationResult!;
        }

        return Ok(await _pages.SaveAsync(request with { Slug = slug, MinimumRole = resolvedMinimumRole }, cancellationToken));
    }

    [HttpDelete("{**slug}")]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<IActionResult> Delete(string slug, CancellationToken cancellationToken)
    {
        var existing = await _pages.GetAsync(slug, cancellationToken);
        if (existing is not null && !CanView(existing))
        {
            return Forbid();
        }

        return await _pages.DeleteAsync(slug, cancellationToken) ? NoContent() : NotFound();
    }

    private IReadOnlyList<PageSummary> FilterVisible(IReadOnlyList<PageSummary> pages) =>
        pages.Where(page => PageAccessLevels.CanView(page.MinimumRole, User, _options.CurrentValue.Auth)).ToList();

    private bool CanView(PageDocument page) =>
        PageAccessLevels.CanView(page.MinimumRole, User, _options.CurrentValue.Auth);

    private bool TryResolveMinimumRole(PageSaveRequest request, PageDocument? existing, out string resolvedMinimumRole, out ActionResult<PageDocument>? result)
    {
        result = null;
        resolvedMinimumRole = existing?.MinimumRole ?? PageAccessLevels.Normalize(_options.CurrentValue.Pages.DefaultMinimumRole);
        string? normalizedRequestedRole = null;

        if (!string.IsNullOrWhiteSpace(request.MinimumRole) && !PageAccessLevels.TryNormalize(request.MinimumRole, out normalizedRequestedRole))
        {
            result = BadRequest("Choose Anonymous, Authenticated, or Admin for page visibility.");
            return false;
        }

        resolvedMinimumRole = PageAccessLevels.ResolveStoredMinimumRole(
            normalizedRequestedRole,
            existing?.MinimumRole,
            _options.CurrentValue.Pages.DefaultMinimumRole);

        if (existing is not null && string.Equals(existing.MinimumRole, resolvedMinimumRole, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(resolvedMinimumRole, PageAccessLevels.Admin, StringComparison.OrdinalIgnoreCase)
            && !PageAccessLevels.CanSetMinimumRole(PageAccessLevels.Admin, User, _options.CurrentValue.Auth))
        {
            result = Forbid();
            return false;
        }

        return true;
    }
}