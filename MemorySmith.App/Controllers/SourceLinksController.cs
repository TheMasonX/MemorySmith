using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/source-links")]
public class SourceLinksController : ControllerBase
{
    private readonly VarResolver _vars;

    public SourceLinksController(VarResolver vars)
    {
        _vars = vars;
    }

    [HttpPost("open")]
    public async Task<ActionResult<SourceOpenResult>> Open([FromBody] SourceLink link)
    {
        var result = await _vars.OpenWithDefaultAppAsync(link);
        return result.Opened ? Ok(result) : BadRequest(result);
    }
}