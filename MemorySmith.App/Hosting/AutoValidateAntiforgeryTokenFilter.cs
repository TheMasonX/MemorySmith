using System.Reflection;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MemorySmith.App.Hosting;

/// <summary>
/// Global MVC authorization filter that validates antiforgery tokens on every
/// state-changing (POST, PUT, PATCH, DELETE) request, unless the action or
/// controller carries <see cref="IgnoreAntiforgeryTokenAttribute"/>.
///
/// This replaces the built-in <c>AutoValidateAntiforgeryTokenAttribute</c> which
/// requires MVC View Features services that <c>AddControllers()</c> alone does
/// not register.
/// </summary>
public sealed class AutoValidateAntiforgeryTokenFilter : IAsyncAuthorizationFilter
{
    private static readonly HashSet<string> SafeMethods =
        ["GET", "HEAD", "OPTIONS", "TRACE"];

    private readonly IAntiforgery _antiforgery;

    public AutoValidateAntiforgeryTokenFilter(IAntiforgery antiforgery)
    {
        _antiforgery = antiforgery;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var httpMethod = context.HttpContext.Request.Method;
        if (SafeMethods.Contains(httpMethod))
        {
            return;
        }

        // Check if the action or its controller has [IgnoreAntiforgeryToken]
        var controllerType = context.ActionDescriptor is ControllerActionDescriptor cad
            ? cad.ControllerTypeInfo
            : null;
        if (controllerType is not null)
        {
            var hasIgnoreAttribute =
                // Check method-level
                context.ActionDescriptor is ControllerActionDescriptor methodCad &&
                methodCad.MethodInfo.GetCustomAttributes(typeof(IgnoreAntiforgeryTokenAttribute), inherit: true).Any()
                ||
                // Check controller-level
                controllerType.GetCustomAttributes(typeof(IgnoreAntiforgeryTokenAttribute), inherit: true).Any();

            if (hasIgnoreAttribute)
            {
                return;
            }
        }

        try
        {
            await _antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status400BadRequest);
        }
    }
}
