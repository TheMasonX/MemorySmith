using MemorySmith.App.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/diagnostics")]
[Authorize(Policy = MemorySmithPolicies.CanAdminMemorySmith)]
[IgnoreAntiforgeryToken]
public class DiagnosticsController : ControllerBase
{
    private readonly OperationalDiagnosticsService _diagnostics;
    private readonly MeasurementBaselineService _measurementBaseline;
    private readonly LoggingObservabilityService _logs;

    public DiagnosticsController(
        OperationalDiagnosticsService diagnostics,
        MeasurementBaselineService measurementBaseline,
        LoggingObservabilityService logs)
    {
        _diagnostics = diagnostics;
        _measurementBaseline = measurementBaseline;
        _logs = logs;
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

    [HttpGet("logs")]
    public async Task<ActionResult<IReadOnlyList<LogEntryDto>>> SearchLogs(
        [FromQuery] string? text,
        [FromQuery] string? level,
        [FromQuery] int hours = 24,
        [FromQuery] int limit = 200,
        [FromQuery] bool includeWindowsEventLog = true,
        [FromQuery] bool includeStructuredLogs = true,
        CancellationToken cancellationToken = default)
    {
        var results = await _logs.SearchAsync(
            new LogSearchQuery(text, level, hours, limit, includeWindowsEventLog, includeStructuredLogs),
            cancellationToken);
        return Ok(results);
    }

    [HttpGet("logs/metrics")]
    public async Task<ActionResult<LogMetricsSnapshot>> GetLogMetrics([FromQuery] int? days, CancellationToken cancellationToken = default)
    {
        return Ok(await _logs.GetMetricsAsync(days, cancellationToken));
    }
}