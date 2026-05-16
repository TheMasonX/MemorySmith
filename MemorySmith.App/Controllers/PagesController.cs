using MemorySmith.App.Services;
using Microsoft.AspNetCore.Mvc;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/pages")]
public class PagesController : ControllerBase
{
    private readonly IPageService _pages;

    public PagesController(IPageService pages)
    {
        _pages = pages;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PageSummary>>> GetAll([FromQuery] string? query, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        return Ok(string.IsNullOrWhiteSpace(query)
            ? await _pages.ListAsync(cancellationToken)
            : await _pages.SearchAsync(new PageSearchQuery(query, limit), cancellationToken));
    }

    [HttpGet("{**slug}")]
    public async Task<ActionResult<PageDocument>> Get(string slug, CancellationToken cancellationToken)
    {
        var page = await _pages.GetAsync(slug, cancellationToken);
        return page is null ? NotFound() : Ok(page);
    }

    [HttpGet("{slug}/html")]
    public async Task<IActionResult> GetHtml(string slug, CancellationToken cancellationToken)
    {
        var page = await _pages.GetAsync(slug, cancellationToken);
        return page is null ? NotFound() : Content(page.Html, "text/html");
    }

    [HttpPost]
    public async Task<ActionResult<PageDocument>> Save([FromBody] PageSaveRequest request, CancellationToken cancellationToken)
    {
        var page = await _pages.SaveAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { slug = page.Slug }, page);
    }

    [HttpPut("{**slug}")]
    public async Task<ActionResult<PageDocument>> Update(string slug, [FromBody] PageSaveRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _pages.SaveAsync(request with { Slug = slug }, cancellationToken));
    }

    [HttpDelete("{**slug}")]
    public async Task<IActionResult> Delete(string slug, CancellationToken cancellationToken)
    {
        return await _pages.DeleteAsync(slug, cancellationToken) ? NoContent() : NotFound();
    }
}