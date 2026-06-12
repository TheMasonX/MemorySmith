namespace MemorySmith.App.Hosting;

using MemorySmith.App.Services;

/// <summary>
/// Maintenance-agent wiring: configuration, resource probing, the proposal store/workflow
/// cluster, topic maps, and the background maintenance + scheduler hosted services.
/// Extracted from Program.cs (TSK-0282).
/// </summary>
public static class MemorySmithMaintenanceSetup
{
    public static WebApplicationBuilder AddMemorySmithMaintenance(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<MaintenanceAgentConfigService>();
        builder.Services.AddSingleton<MaintenanceActiveRunStore>();
        builder.Services.AddSingleton<MaintenanceResourceProbe>();
        builder.Services.AddSingleton<MaintenanceDiffService>();
        builder.Services.AddSingleton<MaintenanceWritePermissionService>();
        builder.Services.AddSingleton<IMaintenanceProposalStore, FileMaintenanceProposalStore>();
        builder.Services.AddSingleton<MaintenanceProposalWorkflow>();
        builder.Services.AddSingleton<MaintenanceTopicMapService>();
        builder.Services.AddScoped<MaintenanceAgentService>();

        var maintenanceEnabled = builder.Configuration.GetValue("MemorySmith:Maintenance:Enabled", true);
        if (maintenanceEnabled)
        {
            builder.Services.AddHostedService<MemoryMaintenanceService>();
        }

        builder.Services.AddHostedService<MaintenanceAgentSchedulerService>();
        return builder;
    }
}
