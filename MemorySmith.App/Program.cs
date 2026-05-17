using MemorySmith.App.Components;
using MemorySmith.App.Services;
using MemorySmith.Core.Indexing;
using MemorySmith.Storage;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using Serilog;
using System.Threading.RateLimiting;
using System.Text.Json.Serialization;

if (WindowsServiceCommands.TryHandle(args, out var serviceCommandExitCode))
{
    Environment.ExitCode = serviceCommandExitCode;
    return;
}

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "logs", "memorysmith-.log"), rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = builder.Configuration["MemorySmith:WindowsService:Name"] ?? WindowsServiceCommands.DefaultServiceName;
    });

    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();
    builder.Services.AddMudServices();

    builder.Services.Configure<MemorySmithOptions>(builder.Configuration.GetSection("MemorySmith"));
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/login";
            options.Cookie.Name = "MemorySmith.Auth";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.SlidingExpiration = true;
        });
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
    builder.Services.AddSingleton<MemoryApplicationService>();
    builder.Services.AddSingleton<MemoryMaintenanceTasks>();
    builder.Services.AddSingleton<OperationalDiagnosticsService>();
    builder.Services.AddHttpClient<OllamaChatProvider>();
    builder.Services.AddScoped<GitHubCopilotChatProvider>();
    builder.Services.AddScoped<IChatProvider>(sp => sp.GetRequiredService<OllamaChatProvider>());
    builder.Services.AddScoped<IChatProvider>(sp => sp.GetRequiredService<GitHubCopilotChatProvider>());
    builder.Services.AddScoped<IChatAgent, MemoryChatAgent>();

    var maintenanceEnabled = builder.Configuration.GetValue("MemorySmith:Maintenance:Enabled", true);
    if (maintenanceEnabled)
    {
        builder.Services.AddHostedService<MemoryMaintenanceService>();
    }

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter<MemoryChatMode>());
        });
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

    app.UseHttpsRedirection();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    var pagesPath = app.Configuration["MemorySmith:PagesPath"] ?? Path.Combine("..", "Data", "Pages");
    var pageAssetsPath = Path.GetFullPath(Path.Combine(pagesPath, "assets"));
    Directory.CreateDirectory(pageAssetsPath);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(pageAssetsPath),
        RequestPath = "/page-assets"
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
    private static void AddPermissionPolicy(AuthorizationOptions options, string name, MemorySmithPermission permission) =>
        options.AddPolicy(name, policy => policy.AddRequirements(new MemorySmithPermissionRequirement(permission)));
}