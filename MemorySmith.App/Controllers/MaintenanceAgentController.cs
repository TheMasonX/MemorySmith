using MemorySmith.App.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/maintenance-agent")]
[Authorize(Policy = MemorySmithPolicies.CanApproveAgentWrites)]
[IgnoreAntiforgeryToken]
public sealed class MaintenanceAgentController : ControllerBase
{
    private readonly MaintenanceAgentService _agent;
    private readonly MaintenanceProposalWorkflow _proposals;
    private readonly MaintenanceTopicMapService _topicMap;

    public MaintenanceAgentController(
        MaintenanceAgentService agent,
        MaintenanceProposalWorkflow proposals,
        MaintenanceTopicMapService topicMap)
    {
        _agent = agent;
        _proposals = proposals;
        _topicMap = topicMap;
    }

    [HttpPost("run_maintenance_now")]
    public async Task<ActionResult<MaintenanceRunResult>> RunNow(CancellationToken cancellationToken) =>
        Ok(await _agent.RunMaintenanceNowAsync(cancellationToken));

    [HttpPost("run_maintenance_weekly")]
    public async Task<ActionResult<MaintenanceRunResult>> RunWeekly(CancellationToken cancellationToken) =>
        Ok(await _agent.RunMaintenanceWeeklyAsync(cancellationToken));

    [HttpPost("run_maintenance_on_demand")]
    public async Task<ActionResult<MaintenanceRunResult>> RunOnDemand([FromBody] MaintenanceOnDemandRequest request, CancellationToken cancellationToken) =>
        Ok(await _agent.RunMaintenanceOnDemandAsync(request.Task, cancellationToken));

    [HttpGet("proposals")]
    public async Task<ActionResult<IReadOnlyList<MaintenanceWriteProposal>>> ListProposals(CancellationToken cancellationToken) =>
        Ok(await _proposals.ListAsync(cancellationToken));

    [HttpGet("proposals/{proposalId}")]
    public async Task<ActionResult<MaintenanceWriteProposal>> GetProposal(string proposalId, CancellationToken cancellationToken)
    {
        var proposal = await _proposals.GetAsync(proposalId, cancellationToken);
        return proposal is null ? NotFound() : Ok(proposal);
    }

    [HttpPost("proposals/{proposalId}/approve")]
    public async Task<ActionResult<MaintenanceWriteProposal>> Approve(string proposalId, [FromBody] MaintenanceProposalActionRequest request, CancellationToken cancellationToken) =>
        Ok(await _proposals.ApproveAsync(proposalId, request.Comment, cancellationToken));

    [HttpPost("proposals/{proposalId}/respond")]
    public async Task<ActionResult<MaintenanceWriteProposal>> Respond(string proposalId, [FromBody] MaintenanceProposalActionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _proposals.RespondAsync(proposalId, request.Comment ?? string.Empty, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("proposals/{proposalId}/reject")]
    public async Task<ActionResult<MaintenanceWriteProposal>> Reject(string proposalId, [FromBody] MaintenanceProposalActionRequest request, CancellationToken cancellationToken) =>
        Ok(await _proposals.RejectAsync(proposalId, request.Comment, cancellationToken));

    [HttpGet("topic-map")]
    public async Task<ActionResult<TopicMapDocument>> TopicMap([FromQuery] bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && await _topicMap.LoadCachedAsync(cancellationToken) is { } cached)
        {
            return Ok(cached);
        }

        return Ok(await _topicMap.BuildAsync(cancellationToken));
    }

    [HttpGet("topic-map/mermaid")]
    public async Task<IActionResult> TopicMapMermaid([FromQuery] bool refresh = false, [FromQuery] int maxEdges = 80, CancellationToken cancellationToken = default)
    {
        var document = refresh
            ? await _topicMap.BuildAsync(cancellationToken)
            : await _topicMap.LoadCachedAsync(cancellationToken) ?? await _topicMap.BuildAsync(cancellationToken);
        return Content(MaintenanceTopicMapService.GenerateMermaid(document, maxEdges), "text/plain");
    }
}

public sealed record MaintenanceOnDemandRequest(string Task);

public sealed record MaintenanceProposalActionRequest(string? Comment = null);