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
    private const string RetrievalModeHeader = "X-MemorySmith-Retrieval-Mode";
    private const string RetrievalProviderKindHeader = "X-MemorySmith-Retrieval-Provider-Kind";
    private const string RetrievalProviderNameHeader = "X-MemorySmith-Retrieval-Provider-Name";
    private const string RetrievalProviderPrimaryHeader = "X-MemorySmith-Retrieval-Provider-Primary";
    private const string RetrievalProviderMessageHeader = "X-MemorySmith-Retrieval-Provider-Message";

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
    public async Task<IActionResult> Search([FromBody] MemorySearchQuery request, [FromQuery] string? format = null, CancellationToken cancellationToken = default)
    {
        SetRetrievalMetadataHeaders("lexical", MemoryApplicationService.GetLexicalProviderMetadata());
        if (IsEnvelopeFormat(format))
        {
            var results = await _memories.LexicalSearchAsync(request, cancellationToken);
            return Ok(_memories.BuildRetrievalEnvelope("lexical", MemoryApplicationService.GetLexicalProviderMetadata(), results));
        }

        return Ok(await _memories.SearchAsync(request, cancellationToken));
    }

    [HttpPost("search/semantic")]
    public async Task<IActionResult> SemanticSearch([FromBody] SemanticMemorySearchQuery request, [FromQuery] string? format = null, CancellationToken cancellationToken = default)
    {
        var results = await _memories.SemanticSearchAsync(request, cancellationToken);
        SetRetrievalMetadataHeaders("semantic", _memories.GetSemanticProviderMetadata());
        return IsEnvelopeFormat(format)
            ? Ok(_memories.BuildRetrievalEnvelope("semantic", _memories.GetSemanticProviderMetadata(), results))
            : Ok(results);
    }

    [HttpPost("search/hybrid")]
    public async Task<IActionResult> HybridSearch([FromBody] HybridMemorySearchQuery request, [FromQuery] string? format = null, CancellationToken cancellationToken = default)
    {
        var results = await _memories.HybridSearchAsync(request, cancellationToken);
        SetRetrievalMetadataHeaders("hybrid", _memories.GetSemanticProviderMetadata());
        return IsEnvelopeFormat(format)
            ? Ok(_memories.BuildRetrievalEnvelope("hybrid", _memories.GetSemanticProviderMetadata(), results))
            : Ok(results);
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

    private static bool IsEnvelopeFormat(string? format) =>
        string.Equals(format, "envelope", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "json-v2", StringComparison.OrdinalIgnoreCase);

    private void SetRetrievalMetadataHeaders(string mode, RetrievalProviderMetadata provider)
    {
        Response.Headers[RetrievalModeHeader] = mode;
        Response.Headers[RetrievalProviderKindHeader] = provider.Kind;
        Response.Headers[RetrievalProviderNameHeader] = provider.Mode;
        Response.Headers[RetrievalProviderPrimaryHeader] = provider.Available ? "true" : "false";
        if (!string.IsNullOrWhiteSpace(provider.Reason))
        {
            Response.Headers[RetrievalProviderMessageHeader] = provider.Reason;
        }
    }
}