using MemorySmith.App.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController : ControllerBase
{
    private readonly IChatAgent _chat;
    private readonly IChatProvider _provider;
    private readonly IOptionsMonitor<MemorySmithOptions> _options;

    public ChatController(IChatAgent chat, IChatProvider provider, IOptionsMonitor<MemorySmithOptions> options)
    {
        _chat = chat;
        _provider = provider;
        _options = options;
    }

    [HttpGet("config")]
    public async Task<ActionResult<ChatRuntimeConfiguration>> GetConfiguration(CancellationToken cancellationToken)
    {
        var chatOptions = _options.CurrentValue.Chat;
        try
        {
            var models = await _provider.ListModelsAsync(cancellationToken);
            return Ok(new ChatRuntimeConfiguration(chatOptions.Provider, chatOptions.OllamaEndpoint, chatOptions.OllamaModel, models));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return Ok(new ChatRuntimeConfiguration(chatOptions.Provider, chatOptions.OllamaEndpoint, chatOptions.OllamaModel, [], ex.Message));
        }
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