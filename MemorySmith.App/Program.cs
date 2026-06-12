using MemorySmith.App.Components;
using MemorySmith.App.Hosting;
using MemorySmith.App.Services;
using MemorySmith.Storage;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using Serilog;
using System.Text.Json.Serialization;

// Composition root (TSK-0282 / Audit #9). Every concern lives in a named module under
// MemorySmith.App/Hosting — registration groups, the pipeline, and content endpoints are each
// one visible call below, so dropping any of them is a one-line diff (the June 4 reconstruction
// silently lost ~16 inline blocks; this shape makes that failure class structurally impossible).
// Module order mirrors the original inline order; pipeline order is load-bearing.

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
    builder.AddMemorySmithConfigurationLayers();   // secrets + admin settings-override file
    builder.UseMemorySmithSerilog();               // console / structured file / Windows event log
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

    builder.AddMemorySmithSecurity();              // cookie + GitHub OAuth, policies, rate limits, data protection
    builder.AddMemorySmithStorage();               // SQLite metadata DB, file stores, audit/history/settings
    builder.AddMemorySmithCore();                  // memory domain, embeddings, code search, tasks, diagnostics
    builder.AddMemorySmithTelemetry();             // OpenTelemetry tracing/metrics (opt-in)
    builder.AddMemorySmithChat();                  // chat providers, tool catalog, agent sessions
    builder.AddMemorySmithMaintenance();           // maintenance agent + background services

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

    app.UseMemorySmithPipeline();                  // guard, exception handler, request logging, security headers, auth
    app.MapMemorySmithContentEndpoints();          // task attachments + page assets

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

// Required so WebApplicationFactory<Program> can reference the entry point from the test project.
public partial class Program;
