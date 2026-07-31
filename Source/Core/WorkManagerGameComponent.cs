// WorkManagerGameComponent.cs
// Copyright (c) 2026 archdukejim

using ColonyManagerRedux;

namespace WorkManager;

/// <summary>
/// Drives the clock side of the mod: the manager job builds a plan on its own schedule, but the
/// plan has to be pushed onto pawns as each hour comes around.
/// </summary>
public sealed class WorkManagerGameComponent : GameComponent
{
    /// <summary>Hours are 2500 ticks, so checking four times a minute of game time is plenty.</summary>
    private const int CheckInterval = 250;

    public WorkManagerGameComponent(Game game)
        : base() { }

    public override void GameComponentTick()
    {
        if (Find.TickManager.TicksGame % CheckInterval != 0)
        {
            return;
        }

        foreach (var map in Find.Maps)
        {
            var manager = Manager.For(map);
            if (manager == null)
            {
                continue;
            }

            foreach (var job in manager.JobTracker.JobsOfType<ManagerJob_Labor>())
            {
                try
                {
                    job.ApplyIfHourChanged();
                }
                catch (Exception exception)
                {
                    // A throwing applier would otherwise repeat every 250 ticks forever.
                    job.IsSuspended = true;
                    job.CausedException = exception;
                    WorkManagerLog.Error(
                        $"Suspended the labor job on {map} after it threw while applying a "
                            + $"schedule: {exception}"
                    );
                }
            }
        }
    }

    /// <summary>Work type profiles cache defs, which are rebuilt between games.</summary>
    public override void LoadedGame() => WorkTypeProfile.ClearCache();

    public override void StartedNewGame() => WorkTypeProfile.ClearCache();
}
