using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/governance")][IgnoreAntiforgeryToken]public sealed class GovernanceController : ControllerBase
{
    private readonly TagGovernanceService _tagGovernance;

    public GovernanceController(TagGovernanceService tagGovernance)
    {
        _tagGovernance = tagGovernance;
    }

    [HttpGet("tag-policy")]
    [Authorize(Policy = MemorySmithPolicies.CanAdminMemorySmith)]
    public ActionResult<TagGovernanceSnapshot> GetTagPolicy() => _tagGovernance.GetSnapshot();

    [HttpPut("tag-policy")]
    [Authorize(Policy = MemorySmithPolicies.CanAdminMemorySmith)]
    public ActionResult<TagGovernanceSnapshot> SaveTagPolicy([FromBody] TagPolicy policy) => _tagGovernance.SavePolicy(policy);

    [HttpGet("tag-suggestions")]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public ActionResult<IReadOnlyList<string>> GetTagSuggestions([FromQuery] string? prefix, [FromQuery] int limit = 20) =>
        Ok(_tagGovernance.GetTagCompletions(prefix, limit));

    [HttpPost("memory-diagnostics")]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public ActionResult<IReadOnlyList<MemoryDiagnostic>> GetMemoryDiagnostics([FromBody] MemoryRecord record) =>
        _tagGovernance.AnalyzeDraft(record).ToList();
}