using MemorySmith.App.Components;
using MemorySmith.App.Services;
using MemorySmith.Core.Indexing;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using System.Diagnostics;
using System.Threading.RateLimiting;
using System.Text.Json.Serialization;
using System.Reflection;

if (WindowsServiceCommands.TryHandle(args, out var serviceCommandExitCode))
{
    Environment.ExitCode = serviceCommandExitCode;
    return;
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    if (string.Equals(builder.Environment.EnvironmentName, "LocalDevelopment", StringComparison.OrdinalIgnoreCase))
    {
        builder.WebHost.UseStaticWebAssets();
    }
    // Load optional local secrets file from the service working directory (survives publishes, gitignored in artifacts/)
    var secretsFile = Path.Combine(AppContext.BaseDirectory, "appsettings.Secrets.json");
    if (File.Exists(secretsFile))
        builder.Configuration.AddJsonFile(secretsFile, optional: true, reloadOnChange: false);
    var settingsOverrideFile = MemorySmithConfigurationPaths.ResolveSettingsOverridePath(builder.Configuration["MemorySmith:SettingsOverridePath"]);
    builder.Configuration.AddJsonFile(settingsOverrideFile, optional: true, reloadOnChange: true);
    builder.Host.UseSerilog((context, services, loggerConfiguration) =>
    {
        var loggingOptions = context.Configuration.GetSection("MemorySmith:Logging").Get<LoggingOptions>() ?? new LoggingOptions();
        var minimumLevel = ParseLogLevel(loggingOptions.MinimumLevel, LogEventLevel.Information);

        loggerConfiguration
            .MinimumLevel.Is(minimumLevel)
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "MemorySmith.App")
            .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName);

        if (loggingOptions.EnableConsole)
        {
            loggerConfiguration.WriteTo.Console();
        }

        if (loggingOptions.EnableStructuredFile)
        {
            var structuredFilePath = ResolveLogPath(loggingOptions.StructuredFilePath);
            var structuredFileDirectory = Path.GetDirectoryName(structuredFilePath);
            if (string.IsNullOrWhiteSpace(structuredFileDirectory))
            {
                structuredFileDirectory = AppContext.BaseDirectory;
                structuredFilePath = Path.Combine(structuredFileDirectory, Path.GetFileName(structuredFilePath));
            }

            Directory.CreateDirectory(structuredFileDirectory);
            loggerConfiguration.WriteTo.File(
                new CompactJsonFormatter(),
                structuredFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Math.Max(1, loggingOptions.StructuredFileRetainedDays),
                shared: true);
        }

        if (OperatingSystem.IsWindows() && loggingOptions.WindowsEventLogEnabled)
        {
            loggerConfiguration.WriteTo.EventLog(
                source: string.IsNullOrWhiteSpace(loggingOptions.WindowsEventLogSource) ? "MemorySmith.App" : loggingOptions.WindowsEventLogSource,
                manageEventSource: false,
                restrictedToMinimumLevel: LogEventLevel.Warning);
        }
    });
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = builder.Configuration["MemorySmith:WindowsService:Name"] ?? WindowsServiceCommands.DefaultServiceName;
    });

    var blazorMaximumReceiveMessageSize = builder.Configuration.GetValue<long?>("MemorySmith:Blazor:MaximumReceiveMessageSizeBytes") ?? 1024 * 1024;
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents()
        .AddHubOptions(options => options.MaximumReceiveMessageSize = blazorMaximumReceiveMessageSize);
    builder.Services.AddMudServices();

    builder.Services.Configure<MemorySmithOptions>(builder.Configuration.GetSection("MemorySmith"));
    builder.Services.AddSingleton<IPostConfigureOptions<MemorySmithOptions>, MemorySmithLocalDevelopmentPostConfigure>();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddCascadingAuthenticationState();
    var authProviders = builder.Configuration.GetSection("MemorySmith:Auth:Providers").Get<AuthProviderOptions>() ?? new AuthProviderOptions();
    var auth = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/login";
            options.Cookie.Name = "MemorySmith.Auth";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.SlidingExpiration = true;
        });
    if (authProviders.GitHub.Enabled && !string.IsNullOrEmpty(authProviders.GitHub.ClientId))
    {
        auth.AddOAuth("GitHub", options =>
        {
            options.ClientId = authProviders.GitHub.ClientId!;
            options.ClientSecret = authProviders.GitHub.ClientSecret ?? "";
            options.CallbackPath = new PathString("/signin-github");
            options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
            options.TokenEndpoint = "https://github.com/login/oauth/access_token";
            options.UserInformationEndpoint = "https://api.github.com/user";
            options.Scope.Add("read:user");
            options.Scope.Add("user:email");
            options.SaveTokens = true;
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.Events = new OAuthEvents
            {
                OnCreatingTicket = async ctx =>
                {
                    if (ctx.Identity == null) return;
                    // Fetch GitHub user profile
                    var req = new HttpRequestMessage(HttpMethod.Get, ctx.Options.UserInformationEndpoint);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    req.Headers.UserAgent.Add(new ProductInfoHeaderValue("MemorySmith", "1.0"));
                    var res = await ctx.Backchannel.SendAsync(req, ctx.HttpContext.RequestAborted);
                    res.EnsureSuccessStatusCode();
                    using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
                    var root = doc.RootElement;
                    var githubSubject = root.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : null;
                    var githubLogin = root.TryGetProperty("login", out var loginEl) ? loginEl.GetString() : null;
                    var githubEmail = root.TryGetProperty("email", out var emailEl) && emailEl.ValueKind == JsonValueKind.String ? emailEl.GetString() : null;
                    var githubDisplayName = root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(nameEl.GetString()) ? nameEl.GetString() : null;
                    if (githubSubject == null) return;
                    var db = ctx.HttpContext.RequestServices.GetRequiredService<IMemorySmithDatabase>();
                    var msOpts = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<MemorySmithOptions>>().Value;
                    var ct = ctx.HttpContext.RequestAborted;
                    var displayName = githubDisplayName ?? githubLogin ?? githubSubject;
                    var linkUserId = ctx.Properties?.Items.TryGetValue(MemorySmithAuthProperties.LinkUserId, out var requestedUserId) == true
                        ? requestedUserId
                        : null;
                    var link = await db.ProviderLinks.GetByProviderSubjectAsync(MemorySmithProviders.GitHub, githubSubject, ct);
                    string internalUserId;
                    if (link != null)
                    {
                        if (!string.IsNullOrWhiteSpace(linkUserId) && !string.Equals(link.UserId, linkUserId, StringComparison.Ordinal))
                        {
                            ctx.Fail("This GitHub account is already linked to another MemorySmith user.");
                            return;
                        }

                        internalUserId = link.UserId;
                    }
                    else if (!string.IsNullOrWhiteSpace(linkUserId))
                    {
                        var linkedUser = await db.Users.GetByIdAsync(linkUserId, ct);
                        if (linkedUser is null || linkedUser.IsDisabled)
                        {
                            ctx.Fail("The MemorySmith account for this link request is not available.");
                            return;
                        }

                        internalUserId = linkedUser.UserId;
                        await db.ProviderLinks.LinkAsync(new ProviderLink
                        {
                            LinkId = Guid.NewGuid().ToString("N"),
                            UserId = internalUserId,
                            ProviderName = MemorySmithProviders.GitHub,
                            ProviderSubject = githubSubject,
                            ProviderDisplayName = githubLogin ?? displayName,
                            ProviderEmail = githubEmail,
                            LinkedAtUtc = DateTime.UtcNow
                        }, ct);
                    }
                    else
                    {
                        internalUserId = Guid.NewGuid().ToString("N");
                        var now = DateTime.UtcNow;
                        await db.Users.CreateAsync(new UserAccount
                        {
                            UserId = internalUserId,
                            DisplayName = displayName,
                            NormalizedDisplayName = displayName.ToUpperInvariant(),
                            Email = githubEmail,
                            NormalizedEmail = githubEmail?.ToUpperInvariant(),
                            LocalPasswordEnabled = false,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now
                        }, ct);
                        await db.ProviderLinks.LinkAsync(new ProviderLink
                        {
                            LinkId = Guid.NewGuid().ToString("N"),
                            UserId = internalUserId,
                            ProviderName = MemorySmithProviders.GitHub,
                            ProviderSubject = githubSubject,
                            ProviderDisplayName = githubLogin ?? displayName,
                            ProviderEmail = githubEmail,
                            LinkedAtUtc = now
                        }, ct);
                        var isFirstAdmin = !await db.Users.HasAnyAdminAsync(ct);
                        var assignedRole = isFirstAdmin ? MemorySmithRoles.Admin : MemorySmithPermissionHandler.NormalizeAuthenticatedDefaultRole(msOpts.Auth.AuthenticatedDefaultRole);
                        await db.Roles.AssignRoleAsync(internalUserId, assignedRole, null, ct);
                    }
                    var roles = await db.Roles.GetRolesForUserAsync(internalUserId, ct);
                    var user = await db.Users.GetByIdAsync(internalUserId, ct);
                    if (user is null || user.IsDisabled)
                    {
                        ctx.Fail("The MemorySmith account is disabled or no longer exists.");
                        return;
                    }

                    var resolvedUser = user;
                    var loginAtUtc = DateTime.UtcNow;
                    resolvedUser.LastLoginAtUtc = loginAtUtc;
                    resolvedUser.UpdatedAtUtc = loginAtUtc;
                    await db.Users.UpdateAsync(resolvedUser, ct);
                    await db.LoginHistory.RecordAsync(new LoginHistoryEntry
                    {
                        LoginId = Guid.NewGuid().ToString("N"),
                        UserId = resolvedUser.UserId,
                        ProviderName = MemorySmithProviders.GitHub,
                        ProviderSubject = githubSubject,
                        OccurredAtUtc = loginAtUtc,
                        Succeeded = true,
                        RequestId = ctx.HttpContext.TraceIdentifier
                    }, ct);
                    ctx.Identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, internalUserId, ClaimValueTypes.String, ctx.Options.ClaimsIssuer));
                    ctx.Identity.AddClaim(new Claim(ClaimTypes.Name, resolvedUser.DisplayName, ClaimValueTypes.String, ctx.Options.ClaimsIssuer));
                    if (resolvedUser.Email is not null)
                        ctx.Identity.AddClaim(new Claim(ClaimTypes.Email, resolvedUser.Email, ClaimValueTypes.String, ctx.Options.ClaimsIssuer));
                    ctx.Identity.AddClaim(new Claim("provider", MemorySmithProviders.GitHub, ClaimValueTypes.String, ctx.Options.ClaimsIssuer));
                    ctx.Identity.AddClaim(new Claim("security_stamp", resolvedUser.SecurityStamp, ClaimValueTypes.String, ctx.Options.ClaimsIssuer));
                    foreach (var role in roles)
                        ctx.Identity.AddClaim(new Claim(ClaimTypes.Role, role.Name, ClaimValueTypes.String, ctx.Options.ClaimsIssuer));
                },
                OnRemoteFailure = ctx =>
                {
                    ctx.HandleResponse();
                    var returnUri = ctx.Properties?.RedirectUri;
                    var target = !string.IsNullOrWhiteSpace(returnUri) && returnUri.StartsWith("/profile", StringComparison.Ordinal)
                        ? $"/profile?error={Uri.EscapeDataString(ctx.Failure?.Message ?? "External sign-in failed.")}"
                        : "/login?error=1";
                    ctx.Response.Redirect(target);
                    return Task.CompletedTask;
                }
            };
        });
    }
    builder.Services.AddAuthorization(options =>
    {
        AddPermissionPolicy(options, MemorySmithPolicies.CanViewMemorySmith, MemorySmithPermission.View);
        AddPermissionPolicy(options, MemorySmithPolicies.CanEditMemorySmith, MemorySmithPermission.Edit);
        AddPermissionPolicy(options, MemorySmithPolicies.CanAdminMemorySmith, MemorySmithPermission.Admin);
        AddPermissionPolicy(options, MemorySmithPolicies.CanManageUsers, MemorySmithPermission.ManageUsers);
        AddPermissionPolicy(options, MemorySmithPolicies.CanManageSettings, MemorySmithPermission.ManageSettings);
        AddPermissionPolicy(options, MemorySmithPolicies.CanViewAudit, MemorySmithPermission.ViewAudit);
        AddPermissionPolicy(options, MemorySmithPolicies.CanRestoreHistory, MemorySmithPermission.RestoreHistory);
        AddPermissionPolicy(options, MemorySmithPolicies.CanReadSourceBundle, MemorySmithPermission.ReadSourceBundle);
        AddPermissionPolicy(options, MemorySmithPolicies.CanUseChat, MemorySmithPermission.UseChat);
        AddPermissionPolicy(options, MemorySmithPolicies.CanApproveAgentWrites, MemorySmithPermission.ApproveAgentWrites);
    });
    builder.Services.AddSingleton<IAuthorizationHandler, MemorySmithPermissionHandler>();
    builder.Services.AddRateLimiter(options =>
    {
        var auth = builder.Configuration.GetSection("MemorySmith:Auth:RateLimits").Get<AuthRateLimitOptions>() ?? new AuthRateLimitOptions();
        options.AddFixedWindowLimiter("login", limiter =>
        {
            limiter.PermitLimit = Math.Max(1, auth.LoginPermitLimit);
            limiter.Window = TimeSpan.FromMinutes(Math.Max(1, auth.LoginWindowMinutes));
            limiter.QueueLimit = 0;
            limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        });
    });

    var dataProtectionKeysPath = builder.Configuration["MemorySmith:DataProtectionKeysPath"] ?? Path.Combine("..", "Data", "Keys");
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(dataProtectionKeysPath)));

    builder.Services.AddSingleton<IDatabaseProviderFactory, DatabaseProviderFactory>();
    builder.Services.AddSingleton<IMemorySmithDatabase>(sp =>
    {
        var options = sp.GetRequiredService<IOptions<MemorySmithOptions>>().Value.Database;
        return sp.GetRequiredService<IDatabaseProviderFactory>().Create(options);
    });
    builder.Services.AddSingleton<ICurrentUserContext, HttpCurrentUserContext>();
    builder.Services.AddSingleton<AuditLogService>();
    builder.Services.AddSingleton<AdminSettingsService>();
    builder.Services.AddSingleton<ChatModelProfileService>();
    builder.Services.AddSingleton<VersionHistoryService>();
    builder.Services.AddScoped<MemorySmithLocalAuthService>();

    builder.Services.AddSingleton<StorageDiagnostics>();
    builder.Services.AddSingleton<IMemoryStore>(sp =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        var dataPath = configuration["MemorySmith:DataPath"] ?? Path.Combine("..", "Data", "Memories");
        return new FileMemoryStore(dataPath, sp.GetRequiredService<StorageDiagnostics>());
    });
    builder.Services.AddSingleton<IVarStore>(sp =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        var varsPath = configuration["MemorySmith:VarsPath"] ?? Path.Combine("..", "Data", "vars.json");
        return new FileVarStore(varsPath, sp.GetRequiredService<StorageDiagnostics>());
    });
    builder.Services.AddSingleton<FilePageService>(sp =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        var options = sp.GetRequiredService<IOptions<MemorySmithOptions>>().Value;
        var pagesPath = configuration["MemorySmith:PagesPath"] ?? Path.Combine("..", "Data", "Pages");
        return new FilePageService(pagesPath, options.Pages);
    });
    builder.Services.AddSingleton<ITaskService, FileTaskService>();
    builder.Services.AddSingleton<IPageService>(sp => new AuditedPageService(
        sp.GetRequiredService<FilePageService>(),
        sp.GetRequiredService<AuditLogService>(),
        sp.GetRequiredService<VersionHistoryService>()));
    builder.Services.AddSingleton<VarResolver>();
    builder.Services.AddSingleton<IEventStore>(sp =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        var eventLogPath = configuration["MemorySmith:EventLogPath"] ?? Path.Combine("..", "Data", "Events", "audit.log");
        return new FileEventStore(eventLogPath);
    });
    builder.Services.AddSingleton<MemoryIndex>();
    builder.Services.AddSingleton<ITextEmbeddingProvider, OnnxTextEmbeddingProvider>();
    builder.Services.AddSingleton<SemanticEmbeddingSearchService>();
    builder.Services.AddSingleton<BackgroundServiceTelemetryTracker>();
    builder.Services.AddSingleton<IMemoryChangePublisher, MemoryChangePublisher>();
    builder.Services.AddSingleton<TagPolicyService>();
    builder.Services.AddSingleton<MemoryDiagnosticsService>();
    builder.Services.AddSingleton<TagGovernanceService>();
    builder.Services.AddSingleton<MeasurementBaselineService>();
    builder.Services.AddSingleton<MemoryApplicationService>();
    builder.Services.AddSingleton<MemoryMaintenanceTasks>();
    builder.Services.AddSingleton<MaintenanceAgentConfigService>();
    builder.Services.AddSingleton<MaintenanceActiveRunStore>();
    builder.Services.AddSingleton<MaintenanceResourceProbe>();
    builder.Services.AddSingleton<MaintenanceDiffService>();
    builder.Services.AddSingleton<MaintenanceWritePermissionService>();
    builder.Services.AddSingleton<LoggingObservabilityService>();
    builder.Services.AddSingleton<IMaintenanceProposalStore, FileMaintenanceProposalStore>();
    builder.Services.AddSingleton<MaintenanceProposalWorkflow>();
    builder.Services.AddSingleton<MaintenanceTopicMapService>();
    builder.Services.AddScoped<MaintenanceAgentService>();
    builder.Services.AddSingleton<OperationalDiagnosticsService>();

    var telemetryOptions = builder.Configuration.GetSection("MemorySmith:Telemetry").Get<TelemetryOptions>() ?? new TelemetryOptions();
    if (telemetryOptions.Enabled)
    {
        var serviceVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        var openTelemetry = builder.Services.AddOpenTelemetry().ConfigureResource(resource =>
        {
            resource.Clear();
            resource.AddService(
                serviceName: string.IsNullOrWhiteSpace(telemetryOptions.ServiceName) ? "MemorySmith.App" : telemetryOptions.ServiceName,
                serviceVersion: serviceVersion,
                serviceInstanceId: Environment.MachineName);
        });

        if (telemetryOptions.TracingEnabled)
        {
            openTelemetry.WithTracing(tracing =>
            {
                tracing
                    .AddSource(MemorySmithTelemetry.ActivitySourceName)
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(Math.Clamp(telemetryOptions.TraceSamplingPercentage, 0, 100) / 100d)));

                if (telemetryOptions.AspNetCoreInstrumentationEnabled)
                {
                    tracing.AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = telemetryOptions.RecordExceptions;
                        options.Filter = context => !IsTelemetryPathExcluded(context.Request.Path, telemetryOptions.ExcludedRequestPathPrefixes);
                    });
                }

                if (telemetryOptions.HttpClientInstrumentationEnabled)
                {
                    tracing.AddHttpClientInstrumentation(options =>
                    {
                        options.RecordException = telemetryOptions.RecordExceptions;
                    });
                }

                if (telemetryOptions.ExporterEnabled)
                {
                    tracing.AddOtlpExporter(options => ConfigureOtlpExporter(options, telemetryOptions));
                }
            });
        }

        if (telemetryOptions.MetricsEnabled)
        {
            openTelemetry.WithMetrics(metrics =>
            {
                metrics.AddMeter(MemorySmithTelemetry.MeterName);

                if (telemetryOptions.AspNetCoreInstrumentationEnabled)
                {
                    metrics.AddAspNetCoreInstrumentation();
                }

                if (telemetryOptions.HttpClientInstrumentationEnabled)
                {
                    metrics.AddHttpClientInstrumentation();
                }

                if (telemetryOptions.RuntimeInstrumentationEnabled)
                {
                    metrics.AddRuntimeInstrumentation();
                }

                if (telemetryOptions.ExporterEnabled)
                {
                    metrics.AddOtlpExporter(options => ConfigureOtlpExporter(options, telemetryOptions));
                }
            });
        }
    }

    builder.Services.AddHttpClient<OllamaChatProvider>((sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<MemorySmithOptions>>().Value;
        var timeoutSeconds = Math.Clamp(options.Chat.RequestTimeoutSeconds, 10, 3600);
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    });
    builder.Services.AddScoped<GitHubCopilotChatProvider>();
    builder.Services.AddScoped<IChatProvider>(sp => sp.GetRequiredService<OllamaChatProvider>());
    builder.Services.AddScoped<IChatProvider>(sp => sp.GetRequiredService<GitHubCopilotChatProvider>());
    builder.Services.AddSingleton<ChatToolCatalog>();
    builder.Services.AddSingleton<ChatIntentInterceptor>();
    builder.Services.AddScoped<IChatAgent, MemoryChatAgent>();

    var maintenanceEnabled = builder.Configuration.GetValue("MemorySmith:Maintenance:Enabled", true);
    if (maintenanceEnabled)
    {
        builder.Services.AddHostedService<MemoryMaintenanceService>();
    }

    builder.Services.AddHostedService<MaintenanceAgentSchedulerService>();

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter<MemoryChatMode>());
        });
    builder.Services.AddProblemDetails();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var app = builder.Build();

    await app.Services.GetRequiredService<IMemorySmithDatabase>().InitializeAsync(CancellationToken.None);

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

            await context.Response.WriteAsJsonAsync(details);
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
                diagnosticContext.Set("TraceId", Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier);
                diagnosticContext.Set("RequestPath", httpContext.Request.Path.ToString());
                diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
                diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
                diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
                diagnosticContext.Set("CorrelationId", httpContext.TraceIdentifier);
            };
        });
    }

    app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Correlation-Id"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        await next();
    });

    app.UseHttpsRedirection();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    var pagesPath = app.Configuration["MemorySmith:PagesPath"] ?? Path.Combine("..", "Data", "Pages");
    var pageAssetsPath = Path.GetFullPath(Path.Combine(pagesPath, "assets"));
    Directory.CreateDirectory(pageAssetsPath);
    var contentTypeProvider = new FileExtensionContentTypeProvider();
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
        var canView = await CanViewPageAssetAsync(pages, normalizedAssetPath, httpContext.User, options.CurrentValue.Auth, authorization, cancellationToken);
        if (!canView)
        {
            return Results.NotFound();
        }

        return Results.File(
            resolvedAssetPath,
            contentTypeProvider.TryGetContentType(resolvedAssetPath, out var contentType) ? contentType : "application/octet-stream");
    });

    app.MapStaticAssets();
    app.MapControllers();
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program
{
    private static string ResolveLogPath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        return Path.Combine(AppContext.BaseDirectory, configuredPath);
    }

    private static LogEventLevel ParseLogLevel(string? rawLevel, LogEventLevel fallback)
    {
        if (Enum.TryParse<LogEventLevel>(rawLevel, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static void AddPermissionPolicy(AuthorizationOptions options, string name, MemorySmithPermission permission) =>
        options.AddPolicy(name, policy => policy.AddRequirements(new MemorySmithPermissionRequirement(permission)));

    private static bool IsTelemetryPathExcluded(PathString requestPath, IEnumerable<string> excludedPrefixes)
    {
        if (!requestPath.HasValue)
        {
            return false;
        }

        var value = requestPath.Value ?? string.Empty;
        return excludedPrefixes.Any(prefix =>
            !string.IsNullOrWhiteSpace(prefix)
            && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static void ConfigureOtlpExporter(OtlpExporterOptions options, TelemetryOptions telemetryOptions)
    {
        if (Uri.TryCreate(telemetryOptions.OtlpEndpoint, UriKind.Absolute, out var endpoint))
        {
            options.Endpoint = endpoint;
        }

        options.Protocol = telemetryOptions.OtlpProtocol.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;
    }

    private static string? ResolvePageAssetPath(string pageAssetsPath, string assetPath)
    {
        if (!HasValidPercentEncoding(assetPath))
        {
            return null;
        }

        var normalizedAssetPath = NormalizePageAssetRequestPath(assetPath);
        if (string.IsNullOrWhiteSpace(normalizedAssetPath) || normalizedAssetPath.Split('/').Any(segment => segment is ".." or "."))
        {
            return null;
        }

        var resolvedPath = Path.GetFullPath(Path.Combine(pageAssetsPath, normalizedAssetPath.Replace('/', Path.DirectorySeparatorChar)));
        var normalizedRoot = pageAssetsPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return resolvedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ? resolvedPath : null;
    }

    private static string NormalizePageAssetRequestPath(string assetPath) =>
        Uri.UnescapeDataString((assetPath ?? string.Empty).Replace('\\', '/').TrimStart('/'));

    private static async Task<bool> CanViewPageAssetAsync(
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

        return (await authorization.AuthorizeAsync(user, null, MemorySmithPolicies.CanEditMemorySmith)).Succeeded;
    }

    private static bool HasValidPercentEncoding(string value)
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
        }

        return true;
    }
}