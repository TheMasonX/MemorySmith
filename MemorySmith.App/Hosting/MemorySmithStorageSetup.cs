namespace MemorySmith.App.Hosting;

using MemorySmith.App.Services;
using MemorySmith.Core.Indexing;
using MemorySmith.Storage;
using Microsoft.Extensions.Options;

/// <summary>
/// Persistence wiring: the SQLite metadata database, file-backed memory/page/var/event stores,
/// the audited page-service decorator, and the audit/history/settings services that write to
/// them. Extracted from Program.cs (TSK-0282).
/// </summary>
public static class MemorySmithStorageSetup
{
    public static WebApplicationBuilder AddMemorySmithStorage(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IDatabaseProviderFactory, DatabaseProviderFactory>();
        builder.Services.AddSingleton<IMemorySmithDatabase>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MemorySmithOptions>>().Value.Database;
            return sp.GetRequiredService<IDatabaseProviderFactory>().Create(options);
        });
        builder.Services.AddSingleton<ICurrentUserContext, HttpCurrentUserContext>();
        builder.Services.AddSingleton<AuditLogService>();
        builder.Services.AddSingleton<AdminSettingsService>();
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
        return builder;
    }
}
