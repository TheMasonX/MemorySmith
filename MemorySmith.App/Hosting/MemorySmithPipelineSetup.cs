namespace MemorySmith.App.Hosting;

using MemorySmith.App.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using System.Diagnostics;

/// <summary>
/// The HTTP request pipeline in its required order: request guard, dev tooling, the
/// problem+json exception handler, Serilog request logging, the security-headers middleware,
/// then HTTPS/rate-limiting/auth. Extracted from Program.cs (TSK-0282) — the exception handler,
/// request logging, and security headers were all among the blocks the June 4 reconstruction
/// silently dropped. Order is load-bearing; change it only deliberately.
/// </summary>
public static class MemorySmithPipelineSetup
{
    public static WebApplication UseMemorySmithPipeline(this WebApplication app)
    {
        app.UseMiddleware<MemorySmithRequestGuardMiddleware>();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseExceptionHandler(exceptionApp =>
        {
            exceptionApp.Run(async context =>
            {
                var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
                var exception = exceptionFeature?.Error;
                var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

                Log.Error(exception, "Unhandled request failure {Method} {Path} TraceId={TraceId}", context.Request.Method, context.Request.Path, traceId);

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/problem+json";

                var details = new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "An unexpected error occurred.",
                    Detail = "Use the traceId when reporting this issue.",
                    Instance = context.Request.Path
                };
                details.Extensions["traceId"] = traceId;

                await context.Response.WriteAsJsonAsync(details, options: null, contentType: "application/problem+json");
            });
        });

        var loggingSettings = app.Configuration.GetSection("MemorySmith:Logging").Get<LoggingOptions>() ?? new LoggingOptions();
        if (loggingSettings.RequestLoggingEnabled)
        {
            app.UseSerilogRequestLogging(options =>
            {
                options.GetLevel = (_, elapsed, ex) =>
                {
                    if (ex is not null)
                    {
                        return LogEventLevel.Error;
                    }

                    if (elapsed >= loggingSettings.SlowRequestThresholdMs)
                    {
                        return LogEventLevel.Warning;
                    }

                    return LogEventLevel.Information;
                };

                options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
                {
                    var correlationId = RequestMetadata.ResolveCorrelationId(httpContext);
                    diagnosticContext.Set("TraceId", correlationId);
                    diagnosticContext.Set("RequestPath", httpContext.Request.Path.ToString());
                    diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
                    diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
                    diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
                    diagnosticContext.Set("CorrelationId", correlationId);
                };
            });
        }

        app.Use(async (context, next) =>
        {
            var runtimeSettings = context.RequestServices.GetRequiredService<IOptionsMonitor<MemorySmithOptions>>().CurrentValue;
            if (runtimeSettings.ContentSecurityPolicyEnabled && !string.IsNullOrWhiteSpace(runtimeSettings.ContentSecurityPolicy))
            {
                context.Response.Headers["Content-Security-Policy"] = runtimeSettings.ContentSecurityPolicy;
            }

            if (runtimeSettings.XContentTypeOptionsEnabled && !string.IsNullOrWhiteSpace(runtimeSettings.XContentTypeOptions))
            {
                context.Response.Headers["X-Content-Type-Options"] = runtimeSettings.XContentTypeOptions;
            }

            if (runtimeSettings.ReferrerPolicyEnabled && !string.IsNullOrWhiteSpace(runtimeSettings.ReferrerPolicy))
            {
                context.Response.Headers["Referrer-Policy"] = runtimeSettings.ReferrerPolicy;
            }

            if (runtimeSettings.XFrameOptionsEnabled && !string.IsNullOrWhiteSpace(runtimeSettings.XFrameOptions))
            {
                context.Response.Headers["X-Frame-Options"] = runtimeSettings.XFrameOptions;
            }

            if (runtimeSettings.PermissionsPolicyEnabled && !string.IsNullOrWhiteSpace(runtimeSettings.PermissionsPolicy))
            {
                context.Response.Headers["Permissions-Policy"] = runtimeSettings.PermissionsPolicy;
            }

            context.Response.Headers["X-Correlation-Id"] = RequestMetadata.ResolveCorrelationId(context);
            await next();
        });

        app.UseHttpsRedirection();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        return app;
    }
}
