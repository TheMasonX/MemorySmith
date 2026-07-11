using MemorySmith.App.Services;
using MemorySmith.App.Services.Training;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize(Policy = MemorySmithPolicies.CanUseChat)]
[IgnoreAntiforgeryToken]
public class ChatController : ControllerBase
{
    private readonly IChatAgent _chat;
    private readonly List<IChatProvider> _providers;
    private readonly IOptionsMonitor<MemorySmithOptions> _options;
    private readonly ChatModelProfileService _modelProfiles;
    private readonly IChatFeedbackStore _feedback;

    public ChatController(IChatAgent chat, IEnumerable<IChatProvider> providers, IOptionsMonitor<MemorySmithOptions> options, ChatModelProfileService modelProfiles, IChatFeedbackStore feedback)
    {
        _chat = chat;
        _providers = providers.ToList();
        _options = options;
        _modelProfiles = modelProfiles;
        _feedback = feedback;
    }

    [HttpGet("config")]
    public async Task<ActionResult<ChatRuntimeConfiguration>> GetConfiguration([FromQuery] string? provider, CancellationToken cancellationToken)
    {
        var chatOptions = _options.CurrentValue.Chat;
        var roles = CurrentRoles();
        var profiles = _modelProfiles.ListEnabledProfilesForRoles(roles);
        var defaultProfile = _modelProfiles.GetDefaultProfileForRoles(roles);
        var selectedProvider = ResolveProvider(provider ?? defaultProfile?.Provider ?? chatOptions.Provider);
        var model = defaultProfile is null ? string.Empty : defaultProfile.Model;
        var endpoint = EndpointForProvider(selectedProvider.Name, chatOptions);
        var providerNames = _providers.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item).ToList();
        var providerCapabilities = _providers
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Capabilities, StringComparer.OrdinalIgnoreCase);
        var disabledReason = defaultProfile is null ? "Chat is disabled until an Admin defines an enabled default model profile." : null;
        try
        {
            var models = await selectedProvider.ListModelsAsync(cancellationToken);
            return Ok(new ChatRuntimeConfiguration(selectedProvider.Name, endpoint, model, models, providerNames, providerCapabilities, null, profiles, defaultProfile?.Id, defaultProfile is not null, disabledReason));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return Ok(new ChatRuntimeConfiguration(selectedProvider.Name, endpoint, model, [], providerNames, providerCapabilities, ex.Message, profiles, defaultProfile?.Id, defaultProfile is not null, disabledReason));
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

    [HttpPost("feedback")]
    public async Task<ActionResult<ChatFeedbackRecord>> UpsertFeedback([FromBody] ChatFeedbackRequest request, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Training.FeedbackEnabled)
        {
            return BadRequest(new { error = "Chat feedback capture is disabled." });
        }

        if (string.IsNullOrWhiteSpace(request.TurnId))
        {
            return BadRequest(new { error = "TurnId is required." });
        }

        var principalId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        var rating = request.Rating switch
        {
            > 0 => FeedbackRating.ThumbsUp,
            < 0 => FeedbackRating.ThumbsDown,
            _ => FeedbackRating.Cleared
        };

        var saved = await _feedback.UpsertAsync(
            request.TurnId,
            string.IsNullOrWhiteSpace(request.SessionId) ? "session-unknown" : request.SessionId,
            principalId,
            rating,
            request.Note,
            cancellationToken);
        return Ok(saved);
    }

    [HttpGet("feedback")]
    public async Task<ActionResult<ChatFeedbackRecord>> GetFeedback([FromQuery] string turnId, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Training.FeedbackEnabled)
        {
            return BadRequest(new { error = "Chat feedback capture is disabled." });
        }

        if (string.IsNullOrWhiteSpace(turnId))
        {
            return BadRequest(new { error = "turnId is required." });
        }

        var principalId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        var existing = await _feedback.GetForTurnAsync(turnId, principalId, cancellationToken);
        return existing is null ? NotFound() : Ok(existing);
    }

    private IChatProvider ResolveProvider(string providerName)
    {
        var selected = _providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, providerName, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(candidate.Name, "GitHub", StringComparison.OrdinalIgnoreCase) && string.Equals(providerName, "Copilot", StringComparison.OrdinalIgnoreCase)) ||
            (string.Equals(candidate.Name, "OpenAI", StringComparison.OrdinalIgnoreCase) && (string.Equals(providerName, "DeepSeek", StringComparison.OrdinalIgnoreCase) || string.Equals(providerName, "OpenRouter", StringComparison.OrdinalIgnoreCase))));
        return selected ?? _providers[0];
    }

    private static string DefaultModelForProvider(string providerName, ChatOptions options) =>
        string.Equals(providerName, "GitHub", StringComparison.OrdinalIgnoreCase) ? options.GitHubModel
        : string.Equals(providerName, "OpenAI", StringComparison.OrdinalIgnoreCase) ? options.OpenAIModel
        : options.OllamaModel;

    private static string EndpointForProvider(string providerName, ChatOptions options) =>
        string.Equals(providerName, "GitHub", StringComparison.OrdinalIgnoreCase) ? "GitHub Copilot SDK"
        : string.Equals(providerName, "OpenAI", StringComparison.OrdinalIgnoreCase) ? (!string.IsNullOrWhiteSpace(options.OpenAIEndpoint) ? options.OpenAIEndpoint : "https://api.deepseek.com")
        : options.OllamaEndpoint;

    private IReadOnlyList<string> CurrentRoles() => HttpContext.User.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToList();
}

public sealed record ChatFeedbackRequest(string TurnId, string SessionId, int Rating, string? Note = null);