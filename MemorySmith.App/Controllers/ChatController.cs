using MemorySmith.App.Services;
using Microsoft.AspNetCore.Mvc;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController : ControllerBase
{
    private readonly IChatAgent _chat;

    public ChatController(IChatAgent chat)
    {
        _chat = chat;
    }

    [HttpPost]
    public async Task<ActionResult<MemoryChatResponse>> Send([FromBody] MemoryChatRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _chat.SendAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }
}