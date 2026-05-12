using MemorySmith.Core.Indexing;
using MemorySmith.Storage;
using MemorySmith.Worker.Hubs;
using MemorySmith.Worker.Services;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;
using System.Threading.RateLimiting;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/memorysmith-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // Storage
    var dataPath = builder.Configuration["DataPath"]
        ?? Path.Combine(Directory.GetCurrentDirectory(), "..", "Data", "Memories");
    var eventLogPath = builder.Configuration["EventLogPath"]
        ?? Path.Combine(Directory.GetCurrentDirectory(), "..", "Data", "Events", "audit.log");
    
    builder.Services.AddSingleton<IMemoryStore>(_ => new FileMemoryStore(dataPath));
    builder.Services.AddSingleton<IEventStore>(_ => new FileEventStore(eventLogPath));
    builder.Services.AddSingleton<MemoryIndex>();
    builder.Services.AddSingleton<BackgroundServiceTelemetryTracker>();

    // Background services
    builder.Services.AddHostedService<TriageService>();
    builder.Services.AddHostedService<ConsolidationService>();
    builder.Services.AddHostedService<IndexingService>();
    builder.Services.AddHostedService<StatsBroadcastService>();

    // API
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // SignalR
    builder.Services.AddSignalR();

    // Health checks
    builder.Services.AddHealthChecks();

    // Response caching
    builder.Services.AddResponseCaching();

    // Rate limiting
    builder.Services.AddRateLimiter(opt =>
    {
        opt.AddFixedWindowLimiter("fixed", o =>
        {
            o.PermitLimit = 100;
            o.Window = TimeSpan.FromMinutes(1);
        });
        opt.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    });

    // CORS — allow Dashboard origins
    var dashboardOrigin = builder.Configuration["DashboardOrigin"] ?? "https://localhost:7188";
    builder.Services.AddCors(options =>
        options.AddPolicy("Dashboard", policy =>
            policy.WithOrigins(dashboardOrigin, "http://localhost:5079", "https://localhost:7188")
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials()));

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseHttpsRedirection();
    app.UseCors("Dashboard");
    app.UseResponseCaching();
    app.UseRateLimiter();

    app.MapControllers();
    app.MapHub<DashboardHub>("/hubs/dashboard");
    app.MapHealthChecks("/api/health/live");
    app.MapHealthChecks("/api/health/ready");

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
