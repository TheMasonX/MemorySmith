using MemorySmith.App.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/diagnostics")]
[Authorize(Policy = MemorySmithPolicies.CanAdminMemorySmith)]
public class DiagnosticsController : ControllerBase
{
    private readonly OperationalDiagnosticsService _diagnostics;
    private readonly MeasurementBaselineService _measurementBaseline;

    public DiagnosticsController(OperationalDiagnosticsService diagnostics, MeasurementBaselineService measurementBaseline)
    {
        _diagnostics = diagnostics;
        _measurementBaseline = measurementBaseline;
    }

    [HttpGet]
    public ActionResult<OperationalDiagnosticsSnapshot> Get()
    {
        return Ok(_diagnostics.GetSnapshot());
    }

    [HttpGet("measurement-baseline")]
    public async Task<ActionResult<MeasurementBaselineSnapshot>> GetMeasurementBaseline(CancellationToken cancellationToken)
    {
        return Ok(await _measurementBaseline.GetSnapshotAsync(cancellationToken));
    }
}