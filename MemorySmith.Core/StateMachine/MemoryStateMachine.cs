using MemorySmith.Core.Models;

namespace MemorySmith.Core.StateMachine;

public class MemoryStateMachine
{
    private const double WorkingThreshold = 1.0;
    private const double CoreThreshold = 2.0;
    private const double DeprecationThreshold = 0.2;

    public (MemoryStatus NewStatus, MemoryEvent? Event) Evaluate(MemoryRecord record, bool allowDeprecation = true)
    {
        var score = MemoryScorer.Score(record);
        var original = record.Status;
        MemoryStatus newStatus = original;

        if (allowDeprecation && score < DeprecationThreshold && original != MemoryStatus.Deprecated)
        {
            newStatus = MemoryStatus.Deprecated;
        }
        else if (original == MemoryStatus.Unconsolidated && score >= WorkingThreshold)
        {
            newStatus = MemoryStatus.Working;
        }
        else if (original == MemoryStatus.Working && score >= CoreThreshold)
        {
            newStatus = MemoryStatus.Core;
        }

        MemoryEvent? evt = null;
        if (newStatus != original)
        {
            evt = new MemoryEvent
            {
                MemoryId = record.Id,
                Action = $"Transition",
                Details = $"{original} → {newStatus} (score={score:F3})"
            };
        }

        return (newStatus, evt);
    }
}
