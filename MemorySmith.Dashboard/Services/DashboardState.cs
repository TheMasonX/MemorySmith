using MemorySmith.Core.Models;

namespace MemorySmith.Dashboard.Services;

public class DashboardState
{
    public StatsSnapshot? CurrentStats { get; private set; }
    public IReadOnlyList<BackgroundServiceTelemetry> CurrentServiceTelemetry { get; private set; } = [];
    public MemoryMetadata? SelectedMemory { get; private set; }

    public event Action? StateChanged;

    public void SetStats(StatsSnapshot? stats)
    {
        CurrentStats = stats;
        StateChanged?.Invoke();
    }

    public void SetServiceTelemetry(IReadOnlyList<BackgroundServiceTelemetry> telemetry)
    {
        CurrentServiceTelemetry = telemetry;
        StateChanged?.Invoke();
    }

    public void SetSelectedMemory(MemoryMetadata? selectedMemory)
    {
        SelectedMemory = selectedMemory;
        StateChanged?.Invoke();
    }
}
