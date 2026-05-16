using MemorySmith.App.Components;
using MemorySmith.App.Services;
using MemorySmith.Core.Indexing;
using MemorySmith.Storage;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting.WindowsServices;
using MudBlazor.Services;
using Serilog;
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
    builder.Services.AddSingleton<IPageService>(sp =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        var pagesPath = configuration["MemorySmith:PagesPath"] ?? Path.Combine("..", "Data", "Pages");
        return new FilePageService(pagesPath);
    });
    builder.Services.AddSingleton<VarResolver>();
    builder.Services.AddSingleton<IEventStore>(sp =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        var eventLogPath = configuration["MemorySmith:EventLogPath"] ?? Path.Combine("..", "Data", "Events", "audit.log");
        return new FileEventStore(eventLogPath);
    });
    builder.Services.AddSingleton<MemoryIndex>();
    builder.Services.AddSingleton<BackgroundServiceTelemetryTracker>();
    builder.Services.AddSingleton<IMemoryChangePublisher, MemoryChangePublisher>();
    builder.Services.AddSingleton<MemoryApplicationService>();
    builder.Services.AddSingleton<MemoryMaintenanceTasks>();
    builder.Services.AddSingleton<OperationalDiagnosticsService>();
    builder.Services.AddHttpClient<OllamaChatProvider>();
    builder.Services.AddScoped<IChatProvider, OllamaChatProvider>();
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

    app.UseMiddleware<MemorySmithRequestGuardMiddleware>();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseHttpsRedirection();
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

public partial class Program { }