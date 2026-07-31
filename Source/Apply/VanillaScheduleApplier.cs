// VanillaScheduleApplier.cs
// Copyright (c) 2026 archdukejim

namespace WorkManager;

/// <summary>
/// Applies the plan by rewriting vanilla work priorities at the top of each hour: the scheduled
/// work goes to priority 1, everything else the pawn is willing to do drops behind it, and a pawn
/// with nothing scheduled gets their own priorities back.
/// </summary>
public sealed class VanillaScheduleApplier : IScheduleApplier
{
    public string Label => "WorkManager.Applier.Vanilla".Translate();

    public bool Available => true;

    private bool _warnedAboutManualPriorities;

    public void Apply(
        LaborPlan plan,
        int hour,
        BaselineStore baselines,
        Func<Pawn, EffectiveOptions> optionsFor
    )
    {
        EnsureManualPriorities();

        foreach (var pawn in plan.Pawns.ToList())
        {
            if (pawn?.workSettings == null || !pawn.workSettings.EverWork || pawn.Destroyed)
            {
                continue;
            }

            baselines.CaptureIfNew(pawn);

            var primary = plan.At(pawn, hour);
            var neverIdle = optionsFor(pawn).Flag(LaborOption.NeverIdle);

            foreach (var workType in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            {
                if (pawn.WorkTypeIsDisabled(workType))
                {
                    continue;
                }

                var baseline = baselines.PriorityFor(pawn, workType);
                var priority = SchedulePriorities.For(workType, primary, baseline, neverIdle);

                if (pawn.workSettings.GetPriority(workType) != priority)
                {
                    pawn.workSettings.SetPriority(workType, priority);
                }
            }
        }
    }

    public void Release(Pawn pawn, BaselineStore baselines) => baselines.Restore(pawn);

    /// <summary>
    /// Priorities do nothing unless manual priorities are switched on, so the manager switches them
    /// on rather than silently doing nothing.
    /// </summary>
    private void EnsureManualPriorities()
    {
        var playSettings = Current.Game?.playSettings;
        if (playSettings == null || playSettings.useWorkPriorities)
        {
            return;
        }

        playSettings.useWorkPriorities = true;
        foreach (var pawn in PawnsFinder.AllMaps_FreeColonists)
        {
            pawn.workSettings?.Notify_UseWorkPrioritiesChanged();
        }

        if (!_warnedAboutManualPriorities)
        {
            _warnedAboutManualPriorities = true;
            WorkManagerLog.Message(
                "Turned on manual work priorities; the labor manager needs them to assign work."
            );
        }
    }
}
