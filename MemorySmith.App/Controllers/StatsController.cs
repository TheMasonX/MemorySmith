using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/stats")]
[Authorize(Policy = MemorySmithPolicies.CanViewMemorySmith)]
[IgnoreAntiforgeryToken]
public class StatsController : ControllerBase
{
    private readonly MemoryApplicationService _memories;

    public StatsController(MemoryApplicationService memories)
    {
        _memories = memories;
    }

    [HttpGet]
    public async Task<ActionResult<StatsSnapshot>> GetStats(CancellationToken cancellationToken)
    {
        return Ok(await _memories.GetStatsAsync(cancellationToken));
    }

    [HttpGet("services")]
    public async Task<ActionResult<IReadOnlyList<BackgroundServiceTelemetry>>> GetServiceTelemetry(CancellationToken cancellationToken)
    {
        return Ok(await _memories.GetTelemetryAsync(cancellationToken));
    }

    [HttpGet("activity")]
    public async Task<ActionResult<IReadOnlyList<ActivityBucket>>> GetActivity([FromQuery] int days = 30, CancellationToken cancellationToken = default)
    {
        return Ok(await _memories.GetActivityBucketsAsync(days, cancellationToken));
    }
}