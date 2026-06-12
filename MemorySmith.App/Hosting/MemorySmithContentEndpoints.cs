namespace MemorySmith.App.Hosting;

using MemorySmith.App.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using System.Security.Claims;

/// <summary>
/// Authenticated content endpoints: task attachments and page assets, including the
/// path-sanitization and per-asset visibility rules. Extracted from Program.cs (TSK-0282);
/// the helpers are <c>internal static</c> so the traversal/encoding rules are directly
/// unit-testable.
/// </summary>
public static class MemorySmithContentEndpoints
{
    public static WebApplication MapMemorySmithContentEndpoints(this WebApplication app)
    {
        var pagesPath = app.Configuration["MemorySmith:PagesPath"] ?? Path.Combine("..", "Data", "Pages");
        var pageAssetsPath = Path.GetFullPath(Path.Combine(pagesPath, "assets"));
        Directory.CreateDirectory(pageAssetsPath);
        var contentTypeProvider = new FileExtensionContentTypeProvider();
        app.MapGet("/artifacts/task-attachments/{taskId}/{fileName}", (
            string taskId,
            string fileName,
            IOptionsMonitor<MemorySmithOptions> options) =>
        {
            var resolvedPath = TaskAttachmentFiles.ResolvePublicPath(options.CurrentValue.TaskAttachments, taskId, fileName);
            if (resolvedPath is null)
            {
                return Results.BadRequest();
            }

            if (!File.Exists(resolvedPath))
            {
                return Results.NotFound();
            }

            return Results.File(
                resolvedPath,
                contentTypeProvider.TryGetContentType(resolvedPath, out var contentType) ? contentType : "application/octet-stream");
        }).RequireAuthorization(MemorySmithPolicies.CanViewMemorySmith);

        app.MapGet("/page-assets/{**assetPath}", async (
            string assetPath,
            FilePageService pages,
            IOptionsMonitor<MemorySmithOptions> options,
            IAuthorizationService authorization,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var resolvedAssetPath = ResolvePageAssetPath(pageAssetsPath, assetPath);
            if (resolvedAssetPath is null)
            {
                return Results.BadRequest();
            }

            if (!File.Exists(resolvedAssetPath))
            {
                return Results.NotFound();
            }

            var normalizedAssetPath = NormalizePageAssetRequestPath(assetPath);
            if (normalizedAssetPath is null)
            {
                return Results.BadRequest();
            }

            var canView = await CanViewPageAssetAsync(pages, normalizedAssetPath, httpContext.User, options.CurrentValue.Auth, authorization, cancellationToken);
            if (!canView)
            {
                return Results.NotFound();
            }

            return Results.File(
                resolvedAssetPath,
                contentTypeProvider.TryGetContentType(resolvedAssetPath, out var contentType) ? contentType : "application/octet-stream");
        });

        return app;
    }

    internal static string? ResolvePageAssetPath(string pageAssetsPath, string assetPath)
    {
        var normalizedAssetPath = NormalizePageAssetRequestPath(assetPath);
        if (string.IsNullOrWhiteSpace(normalizedAssetPath) || normalizedAssetPath.Split('/').Any(segment => segment is ".." or "."))
        {
            return null;
        }

        var resolvedPath = Path.GetFullPath(Path.Combine(pageAssetsPath, normalizedAssetPath.Replace('/', Path.DirectorySeparatorChar)));
        var normalizedRoot = pageAssetsPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return resolvedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ? resolvedPath : null;
    }

    internal static string? NormalizePageAssetRequestPath(string assetPath)
    {
        var normalizedAssetPath = (assetPath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        if (!HasValidPercentEncoding(normalizedAssetPath))
        {
            return null;
        }

        try
        {
            normalizedAssetPath = Uri.UnescapeDataString(normalizedAssetPath);
        }
        catch (UriFormatException)
        {
            return null;
        }

        var terminatorIndex = normalizedAssetPath.IndexOfAny(['?', '#']);
        return terminatorIndex >= 0
            ? normalizedAssetPath[..terminatorIndex]
            : normalizedAssetPath;
    }

    internal static async Task<bool> CanViewPageAssetAsync(
        FilePageService pages,
        string assetPath,
        ClaimsPrincipal user,
        AuthOptions auth,
        IAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        var accessInfo = await pages.GetAssetAccessInfoAsync(assetPath, cancellationToken);
        if (accessInfo.IsReferenced)
        {
            return PageAccessLevels.CanView(accessInfo.MinimumRole, user, auth);
        }

        return
            (await authorization.AuthorizeAsync(user, null, MemorySmithPolicies.CanEditMemorySmith)).Succeeded;
    }

    internal static bool HasValidPercentEncoding(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }
}
