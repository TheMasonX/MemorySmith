using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("")]
public class OAuthBridgeController : ControllerBase
{
    private const string GitHubAuthorizeEndpoint = "https://github.com/login/oauth/authorize";
    private const string GitHubTokenEndpoint = "https://github.com/login/oauth/access_token";
    private readonly IHttpClientFactory _httpClientFactory;

    public OAuthBridgeController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [AllowAnonymous]
    [HttpGet("authorize")]
    public IActionResult Authorize()
    {
        var query = Request.QueryString.HasValue ? Request.QueryString.Value : string.Empty;
        return Redirect($"{GitHubAuthorizeEndpoint}{query}");
    }

    [AllowAnonymous]
    [HttpPost("token")]
    public async Task<IActionResult> ExchangeCode(CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(cancellationToken);
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, GitHubTokenEndpoint)
        {
            Content = new StringContent(body, Encoding.UTF8)
        };

        if (!string.IsNullOrWhiteSpace(Request.ContentType))
        {
            request.Content.Headers.TryAddWithoutValidation("Content-Type", Request.ContentType);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

        return new ContentResult
        {
            StatusCode = (int)response.StatusCode,
            ContentType = contentType,
            Content = payload
        };
    }

    private async Task<string> ReadBodyAsync(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}