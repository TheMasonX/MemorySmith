using MemorySmith.App.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/tasks")]
[Authorize(Policy = MemorySmithPolicies.CanViewMemorySmith)]
public sealed class TasksController : ControllerBase
{
    private readonly ITaskService _tasks;
    private readonly IAuthorizationService _authorization;
    private readonly IOptionsMonitor<MemorySmithOptions> _options;

    public TasksController(ITaskService tasks, IAuthorizationService authorization, IOptionsMonitor<MemorySmithOptions> options)
    {
        _tasks = tasks;
        _authorization = authorization;
        _options = options;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TaskSummary>>> List(
        [FromQuery] string? query,
        [FromQuery] string? status,
        [FromQuery] string? assignee,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _tasks.ListAsync(query, status, assignee, limit, cancellationToken));
    }

    [HttpGet("{idOrKey}")]
    public async Task<ActionResult<TaskItem>> Get(string idOrKey, CancellationToken cancellationToken)
    {
        var task = await _tasks.GetAsync(idOrKey, cancellationToken);
        return task is null ? NotFound() : Ok(task);
    }

    [HttpGet("{idOrKey}/history")]
    public async Task<ActionResult<IReadOnlyList<TaskActivityEntry>>> GetHistory(string idOrKey, [FromQuery] int limit = 100, CancellationToken cancellationToken = default)
    {
        var task = await _tasks.GetAsync(idOrKey, cancellationToken);
        if (task is null)
        {
            return NotFound();
        }

        return Ok(await _tasks.GetHistoryAsync(idOrKey, limit, cancellationToken));
    }

    [HttpPost]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<ActionResult<TaskItem>> Create([FromBody] TaskCreateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var created = await _tasks.CreateAsync(request, Actor(), cancellationToken);
            return CreatedAtAction(nameof(Get), new { idOrKey = created.Id }, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("{idOrKey}")]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<ActionResult<TaskItem>> Update(string idOrKey, [FromBody] TaskUpdateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _tasks.UpdateAsync(idOrKey, request, Actor(), cancellationToken);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("{idOrKey}/status")]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<ActionResult<TaskItem>> SetStatus(string idOrKey, [FromBody] TaskStatusUpdateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _tasks.SetStatusAsync(idOrKey, request, Actor(), cancellationToken);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("{idOrKey}/comments")]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<ActionResult<TaskItem>> AddComment(string idOrKey, [FromBody] TaskCommentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _tasks.AddCommentAsync(idOrKey, request, Actor(), cancellationToken);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("{idOrKey}/links/pages")]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<ActionResult<TaskItem>> AddLinkedPage(string idOrKey, [FromBody] TaskPageLinkRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _tasks.AddLinkedPageAsync(idOrKey, request, Actor(), cancellationToken);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("{idOrKey}/links/external")]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<ActionResult<TaskItem>> AddExternalLink(string idOrKey, [FromBody] TaskExternalLinkRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _tasks.AddExternalLinkAsync(idOrKey, request, Actor(), cancellationToken);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("{idOrKey}/attachments")]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<ActionResult<TaskItem>> AddAttachment(string idOrKey, [FromBody] TaskAttachmentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _tasks.AddAttachmentAsync(idOrKey, request, Actor(), cancellationToken);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("{idOrKey}/attachments/files")]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<ActionResult<TaskItem>> AddFileAttachment(string idOrKey, [FromForm] IFormFile file, [FromForm] string? name, CancellationToken cancellationToken)
    {
        try
        {
            var task = await _tasks.GetAsync(idOrKey, cancellationToken);
            if (task is null)
            {
                return NotFound();
            }

            if (file.Length <= 0)
            {
                return BadRequest("Attachment file is empty.");
            }

            var attachmentOptions = _options.CurrentValue.TaskAttachments;
            if (file.Length > attachmentOptions.MaxFileBytes)
            {
                return BadRequest($"Attachment file exceeds the configured limit of {attachmentOptions.MaxFileBytes} bytes.");
            }

            await using var stream = file.OpenReadStream();
            var stored = await TaskAttachmentFiles.SaveAsync(task.Id, file.FileName, stream, file.Length, attachmentOptions, cancellationToken);
            var displayName = string.IsNullOrWhiteSpace(name) ? file.FileName : name.Trim();
            var updated = await _tasks.AddAttachmentAsync(task.Id, new TaskAttachmentRequest(displayName, "file", stored.PublicUri), Actor(), cancellationToken);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("{idOrKey}/attachments/{attachmentId}")]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<ActionResult<TaskItem>> RemoveAttachment(string idOrKey, string attachmentId, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _tasks.RemoveAttachmentAsync(idOrKey, attachmentId, Actor(), cancellationToken);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("{idOrKey}")]
    [Authorize(Policy = MemorySmithPolicies.CanEditMemorySmith)]
    public async Task<IActionResult> Delete(string idOrKey, [FromQuery] bool hard = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (hard)
            {
                var result = await _authorization.AuthorizeAsync(User, null, MemorySmithPolicies.CanAdminMemorySmith);
                if (!result.Succeeded)
                {
                    return Forbid();
                }
            }

            return await _tasks.DeleteAsync(idOrKey, hard, Actor(), cancellationToken) ? NoContent() : NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private string Actor()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return "anonymous";
        }

        return User.Identity?.Name ?? User.FindFirst("name")?.Value ?? User.FindFirst("sub")?.Value ?? "authenticated-user";
    }
}