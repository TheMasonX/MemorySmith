using Microsoft.AspNetCore.Mvc;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using MemorySmith.Worker.Services;

namespace MemorySmith.Worker.Controllers;

[ApiController]
[Route("api/stats")]
public class StatsController : ControllerBase
{
    private readonly IMemoryStore _store;
    private readonly BackgroundServiceTelemetryTracker _telemetryTracker;

    public StatsController(IMemoryStore store, BackgroundServiceTelemetryTracker telemetryTracker)
    {
        _store = store;
        _telemetryTracker = telemetryTracker;
    }

    [HttpGet]
    public IActionResult GetStats()
    {
        var records = _store.LoadAll();
        return Ok(StatsSnapshotFactory.Build(records));
    }

    [HttpGet("services")]
    public IActionResult GetServiceTelemetry()
    {
        var snapshot = _telemetryTracker.GetSnapshot();
        return Ok(snapshot);
    }
}
