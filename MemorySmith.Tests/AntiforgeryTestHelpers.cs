using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

internal static class AntiforgeryTestHelpers
{
    public static async Task<HttpResponseMessage> PostAsJsonWithAntiforgeryAsync(
        this HttpClient client,
        IServiceProvider services,
        string requestUri,
        object value,
        JsonSerializerOptions? options = null,
        ClaimsPrincipal? user = null)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            User = user ?? new ClaimsPrincipal(new ClaimsIdentity())
        };
        var antiforgery = services.GetRequiredService<IAntiforgery>();
        var tokenSet = antiforgery.GetAndStoreTokens(httpContext);
        var cookieName = services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name;

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(value, options: options)
        };
        request.Headers.TryAddWithoutValidation(tokenSet.HeaderName ?? "RequestVerificationToken", tokenSet.RequestToken);
        request.Headers.TryAddWithoutValidation("Cookie", $"{cookieName}={tokenSet.CookieToken}");
        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> PutAsJsonWithAntiforgeryAsync(
        this HttpClient client,
        IServiceProvider services,
        string requestUri,
        object value,
        JsonSerializerOptions? options = null,
        ClaimsPrincipal? user = null)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            User = user ?? new ClaimsPrincipal(new ClaimsIdentity())
        };
        var antiforgery = services.GetRequiredService<IAntiforgery>();
        var tokenSet = antiforgery.GetAndStoreTokens(httpContext);
        var cookieName = services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name;

        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri)
        {
            Content = JsonContent.Create(value, options: options)
        };
        request.Headers.TryAddWithoutValidation(tokenSet.HeaderName ?? "RequestVerificationToken", tokenSet.RequestToken);
        request.Headers.TryAddWithoutValidation("Cookie", $"{cookieName}={tokenSet.CookieToken}");
        return await client.SendAsync(request);
    }
}
