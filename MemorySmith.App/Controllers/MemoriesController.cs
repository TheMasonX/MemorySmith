using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/memories")]
[Authorize(Policy = MemorySmithPolicies.CanViewMemorySmith)]
public class MemoriesController : ControllerBase
{
    private readonly MemoryApplicationService _memories;

    public MemoriesController(MemoryApplicationService memories)
    {
        _memories = memories;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<MemoryMetadata>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] MemoryStatus? status = null,
        [FromQuery] string? tags = null,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _memories.GetMemoriesAsync(new MemoryListQuery(page, pageSize, status, tags), cancellationToken));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<MemoryRecord>> Get(string id, CancellationToken cancellationToken)
    {
        var record = await _memories.GetAsync(id, cancellationToken);
        return record is null ? NotFound() : Ok(record);
    }

    [HttpPost]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<ActionResult<MemoryRecord>> Create([FromBody] MemoryRecord record, CancellationToken cancellationToken)
    {
        try
        {
            var created = await _memories.CreateAsync(record, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (MemoryValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    [HttpPut("{id}")]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<ActionResult<MemoryRecord>> Update(string id, [FromBody] MemoryRecord record, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _memories.UpdateAsync(id, record, cancellationToken);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (MemoryValidationException ex)
        {
            return ToValidationProblem(ex);
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        return await _memories.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
    }

    [HttpPost("search")]
    public async Task<ActionResult<IReadOnlyList<MemoryRecord>>> Search([FromBody] MemorySearchQuery request, CancellationToken cancellationToken)
    {
        return Ok(await _memories.SearchAsync(request, cancellationToken));
    }

    [HttpPost("search/semantic")]
    public async Task<ActionResult<IReadOnlyList<MemorySearchResult>>> SemanticSearch([FromBody] SemanticMemorySearchQuery request, CancellationToken cancellationToken)
    {
        return Ok(await _memories.SemanticSearchAsync(request, cancellationToken));
    }

    [HttpPost("search/hybrid")]
    public async Task<ActionResult<IReadOnlyList<MemorySearchResult>>> HybridSearch([FromBody] HybridMemorySearchQuery request, CancellationToken cancellationToken)
    {
        return Ok(await _memories.HybridSearchAsync(request, cancellationToken));
    }

    [HttpPost("{id}/usage")]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<IActionResult> IncrementUsage(string id, CancellationToken cancellationToken)
    {
        var record = await _memories.IncrementUsageAsync(id, cancellationToken);
        return record is null ? NotFound() : Ok(new { record.UsageCount });
    }

    private ActionResult ToValidationProblem(MemoryValidationException exception)
    {
        foreach (var error in exception.Errors)
        {
            foreach (var message in error.Value)
            {
                ModelState.AddModelError(error.Key, message);
            }
        }

        return ValidationProblem(ModelState);
    }
}