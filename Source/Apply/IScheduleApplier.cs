// IScheduleApplier.cs
// Copyright (c) 2026 archdukejim

namespace WorkManager;

/// <summary>Puts a <see cref="LaborPlan"/> into effect.</summary>
public interface IScheduleApplier
{
    /// <summary>Name shown on the tab so the player knows which path is in use.</summary>
    string Label { get; }

    /// <summary>False when whatever this applier needs isn't loaded.</summary>
    bool Available { get; }

    /// <summary>
    /// Applies the plan. Hour-based appliers use <paramref name="hour"/>; appliers that can write a
    /// whole day at once ignore it and write all 24 hours.
    /// </summary>
    void Apply(LaborPlan plan, int hour, BaselineStore baselines, Func<Pawn, EffectiveOptions> optionsFor);

    /// <summary>Hands a pawn back to their own priorities.</summary>
    void Release(Pawn pawn, BaselineStore baselines);
}

/// <summary>Shared priority arithmetic. Kept in one place so both appliers agree on what a
/// scheduled hour actually means in work-tab terms.</summary>
public static class SchedulePriorities
{
    /// <summary>Priority given to the work a pawn is scheduled for.</summary>
    public const int Primary = 1;

    /// <summary>Highest priority anything else is allowed to hold while a pawn is on a schedule,
    /// so the scheduled work always wins the pawn's attention.</summary>
    public const int BackgroundFloor = 2;

    /// <summary>Vanilla's lowest selectable priority. Mods that widen the range still accept these.</summary>
    public const int Lowest = 4;

    /// <summary>
    /// What priority <paramref name="workType"/> should hold for a pawn scheduled onto
    /// <paramref name="primary"/> this hour.
    /// </summary>
    public static int For(WorkTypeDef workType, WorkTypeDef? primary, int? baseline, bool neverIdle)
    {
        if (primary != null && workType == primary)
        {
            return Primary;
        }

        // A work type the player switched off stays off; the manager schedules work, it doesn't
        // override a decision that this pawn must never do this.
        if (baseline is 0 or null)
        {
            return 0;
        }

        if (!neverIdle)
        {
            return 0;
        }

        return primary == null
            ? baseline.Value
            : Mathf.Clamp(Mathf.Max(baseline.Value, BackgroundFloor), BackgroundFloor, Lowest);
    }
}
