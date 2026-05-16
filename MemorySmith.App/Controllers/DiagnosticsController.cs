using MemorySmith.App.Services;
using Microsoft.AspNetCore.Mvc;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/diagnostics")]
public class DiagnosticsController : ControllerBase
{
    private readonly OperationalDiagnosticsService _diagnostics;

    public DiagnosticsController(OperationalDiagnosticsService diagnostics)
    {
        _diagnostics = diagnostics;
    }

    [HttpGet]
    public ActionResult<OperationalDiagnosticsSnapshot> Get()
    {
        return Ok(_diagnostics.GetSnapshot());
    }
}