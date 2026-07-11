using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/source-links")]
[Authorize(Policy = MemorySmithPolicies.CanReadSourceBundle)]
[IgnoreAntiforgeryToken]
public class SourceLinksController : ControllerBase
{
    private readonly VarResolver _vars;
    private readonly AuditLogService _audit;

    public SourceLinksController(VarResolver vars, AuditLogService audit)
    {
        _vars = vars;
        _audit = audit;
    }

    [HttpPost("open")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<SourceOpenResult>> Open([FromBody] SourceLink link)
    {
        var result = await _vars.OpenWithDefaultAppAsync(link);
        await _audit.RecordAsync(
            "source-link.open",
            "SourceLink",
            link.Uri,
            result.Opened ? MemorySmithAuditOutcomes.Success : MemorySmithAuditOutcomes.Failure,
            details: new { resolvedUri = result.ResolvedUri, message = result.Message });
        return result.Opened ? Ok(result) : BadRequest(result);
    }
}