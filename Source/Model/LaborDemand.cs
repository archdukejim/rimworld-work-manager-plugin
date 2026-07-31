// LaborDemand.cs
// Copyright (c) 2026 archdukejim

namespace WorkManager;

/// <summary>
/// One thing the colony wants kept in stock, and how badly it wants it right now. Threshold
/// demands are read off the other manager tabs; standing demands are rows the player adds here
/// for work that has no stockpile target (research, doctoring, cleaning).
/// </summary>
public sealed class LaborDemand
{
    /// <summary>The work that satisfies this demand.</summary>
    public required WorkTypeDef WorkType { get; init; }

    /// <summary>Stable key of the manager job this was read from, or null for a standing demand.</summary>
    public string? SourceJobId { get; init; }

    /// <summary>What to call this row in the UI.</summary>
    public required string Label { get; init; }

    /// <summary>Current stock, and the target set on the source tab. Both zero for standing demands.</summary>
    public int Current { get; init; }

    public int Target { get; init; }

    /// <summary>Stock as a fraction of target; 1 when there's no target to fall short of.</summary>
    public float Fill => Target > 0 ? Mathf.Clamp01((float)Current / Target) : 1f;

    /// <summary>Measured change in stock per day, signed. Negative means it's draining.</summary>
    public float RatePerDay { get; init; }

    /// <summary>True while the hysteresis latch says this demand is being actively worked.</summary>
    public bool Active { get; init; }

    /// <summary>0..1 pull before the player's criticality multiplier is applied.</summary>
    public float Urgency { get; init; }

    /// <summary>The player's per-row settings.</summary>
    public required DemandConfig Config { get; init; }

    /// <summary>What the planner actually sorts on.</summary>
    public float Weight => Urgency * Config.Criticality;

    /// <summary>How much work this demand still wants, in pawn-hours of an average worker. Used to
    /// stop pouring labor into something that's already covered.</summary>
    public float RemainingWork { get; set; }

    /// <summary>Pawn-hours the planner has already committed to this demand, which is what makes
    /// its pull fall off as it gets staffed.</summary>
    public float AssignedHours { get; set; }

    public string StatusLine()
    {
        if (Target > 0)
        {
            return "WorkManager.Demand.StatusThreshold".Translate(
                Current,
                Target,
                Fill.ToStringPercent(),
                RatePerDay.ToString("0.#")
            );
        }
        return "WorkManager.Demand.StatusStanding".Translate(Config.MinWorkers);
    }
}

/// <summary>Per-demand settings the player controls on the labor tab.</summary>
public sealed class DemandConfig : IExposable
{
    /// <summary>Multiplier on this demand's pull. 0 takes it out of the plan entirely.</summary>
    public float Criticality = 1f;

    /// <summary>Keep at least this many pawns on this work every hour, rotating between them.</summary>
    public int MinWorkers;

    /// <summary>Never put more than this many pawns on it at once; 0 means no limit.</summary>
    public int MaxWorkers;

    /// <summary>When non-empty, only these pawns are ever scheduled onto this work.</summary>
    public List<Pawn> PinnedPawns = [];

    /// <summary>A standing demand exists because the player added it, not because a manager job
    /// asked for stock. It survives even when no threshold references its work type.</summary>
    public bool Standing;

    public bool Enabled = true;

    public void ExposeData()
    {
        Scribe_Values.Look(ref Criticality, "criticality", 1f);
        Scribe_Values.Look(ref MinWorkers, "minWorkers");
        Scribe_Values.Look(ref MaxWorkers, "maxWorkers");
        Scribe_Values.Look(ref Standing, "standing");
        Scribe_Values.Look(ref Enabled, "enabled", true);
        Scribe_Collections.Look(ref PinnedPawns, "pinnedPawns", LookMode.Reference);
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            PinnedPawns ??= [];
            PinnedPawns.RemoveAll(p => p == null);
        }
    }

    public DemandConfig Clone() =>
        new()
        {
            Criticality = Criticality,
            MinWorkers = MinWorkers,
            MaxWorkers = MaxWorkers,
            PinnedPawns = [.. PinnedPawns],
            Standing = Standing,
            Enabled = Enabled,
        };
}
