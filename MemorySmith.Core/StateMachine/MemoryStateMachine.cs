using MemorySmith.Core.Models;

namespace MemorySmith.Core.StateMachine;

public class MemoryStateMachine
{
    private const double WorkingThreshold = 0.81;
    private const double CoreThreshold = 1.62;
    public const double DeprecationThreshold = 0.16;

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
        // Demotion: Core records that drop below the Core threshold fall back to Working
        else if (original == MemoryStatus.Core && score < CoreThreshold)
        {
            newStatus = MemoryStatus.Working;
        }
        // Re-promotion: Deprecated records that recover above the Deprecation threshold
        // return to Working (must be >= WorkingThreshold to avoid churn near the boundary)
        else if (original == MemoryStatus.Deprecated && score >= WorkingThreshold)
        {
            newStatus = MemoryStatus.Working;
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
