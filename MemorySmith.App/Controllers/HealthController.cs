using MemorySmith.App.Services;
using MemorySmith.Storage;
using Microsoft.AspNetCore.Mvc;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    private readonly MemoryApplicationService _memories;
    private readonly IEventStore _eventStore;

    public HealthController(MemoryApplicationService memories, IEventStore eventStore)
    {
        _memories = memories;
        _eventStore = eventStore;
    }

    [HttpGet("live")]
    public IActionResult Live()
    {
        return Ok(new { status = "Healthy" });
    }

    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken cancellationToken)
    {
        try
        {
            _ = await _memories.GetStatsAsync(cancellationToken);
            _ = _eventStore.GetEvents().Take(1).ToList();
            return Ok(new { status = "Ready" });
        }
        catch (Exception ex)
        {
            return Problem(title: "MemorySmith is not ready", detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}