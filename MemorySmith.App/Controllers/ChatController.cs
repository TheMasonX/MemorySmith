using MemorySmith.App.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize(Policy = MemorySmithPolicies.CanUseChat)]
public class ChatController : ControllerBase
{
    private readonly IChatAgent _chat;
    private readonly List<IChatProvider> _providers;
    private readonly IOptionsMonitor<MemorySmithOptions> _options;

    public ChatController(IChatAgent chat, IEnumerable<IChatProvider> providers, IOptionsMonitor<MemorySmithOptions> options)
    {
        _chat = chat;
        _providers = providers.ToList();
        _options = options;
    }

    [HttpGet("config")]
    public async Task<ActionResult<ChatRuntimeConfiguration>> GetConfiguration([FromQuery] string? provider, CancellationToken cancellationToken)
    {
        var chatOptions = _options.CurrentValue.Chat;
        var selectedProvider = ResolveProvider(provider ?? chatOptions.Provider);
        var model = DefaultModelForProvider(selectedProvider.Name, chatOptions);
        var endpoint = EndpointForProvider(selectedProvider.Name, chatOptions);
        var providerNames = _providers.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item).ToList();
        try
        {
            var models = await selectedProvider.ListModelsAsync(cancellationToken);
            return Ok(new ChatRuntimeConfiguration(selectedProvider.Name, endpoint, model, models, providerNames));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return Ok(new ChatRuntimeConfiguration(selectedProvider.Name, endpoint, model, [], providerNames, ex.Message));
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
            var chatOptions = _options.CurrentValue.Chat;
            var provider = string.IsNullOrWhiteSpace(request.Provider) ? chatOptions.Provider : request.Provider;
            var model = string.IsNullOrWhiteSpace(request.Model) ? DefaultModelForProvider(provider, chatOptions) : request.Model;
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ChatErrorMessages.Format(ex, provider, model) });
        }
    }

    private IChatProvider ResolveProvider(string providerName)
    {
        var selected = _providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, providerName, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(candidate.Name, "GitHub", StringComparison.OrdinalIgnoreCase) && string.Equals(providerName, "Copilot", StringComparison.OrdinalIgnoreCase)));
        return selected ?? _providers[0];
    }

    private static string DefaultModelForProvider(string providerName, ChatOptions options) =>
        string.Equals(providerName, "GitHub", StringComparison.OrdinalIgnoreCase) ? options.GitHubModel : options.OllamaModel;

    private static string EndpointForProvider(string providerName, ChatOptions options) =>
        string.Equals(providerName, "GitHub", StringComparison.OrdinalIgnoreCase) ? "GitHub Copilot SDK" : options.OllamaEndpoint;
}