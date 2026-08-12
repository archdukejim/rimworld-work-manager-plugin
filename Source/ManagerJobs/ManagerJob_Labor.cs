// ManagerJob_Labor.cs
// Copyright (c) 2026 archdukejim

using ColonyManagerRedux;
using ilyvion.Laboratory;
using ilyvion.Laboratory.Coroutines;

namespace WorkManager;

/// <summary>
/// The labor manager job. Each time it runs it reads what the colony is short of, decides who
/// should be doing what and when, and installs an hour-by-hour schedule for every managed pawn.
/// </summary>
public sealed class ManagerJob_Labor : ManagerJob<ManagerSettings_Labor, ManagerJob_Labor.LaborWorkData>
{
    /// <summary>Carries the plan from the gather phase (which only reads) to the execute phase
    /// (which installs it).</summary>
    public sealed class LaborWorkData
    {
        public LaborPlanner.PlanResult? Result { get; set; }
    }

    private DemandModel _demands = new();
    private LaborOptionSet _colonyOptions = new LaborOptionSet().WithDefaults();
    private Dictionary<Pawn, LaborOptionSet> _pawnOptions = [];
    private List<Pawn> _excludedPawns = [];
    private BaselineStore _baselines = new();
    private LaborPlan _plan = new();
    private bool _preferEnhancedWorkTab = true;
    private int _lastAppliedHour = -1;

    private static readonly VanillaScheduleApplier Vanilla = new();
    private static readonly EnhancedWorkTabApplier EnhancedWorkTab = new();

    public ManagerJob_Labor(Manager manager)
        : base(manager)
    {
        // A plan covers a whole day, so rebuilding it daily is the natural rhythm. Shorten it on
        // the job itself to have the colony react to shortages faster.
        UpdateInterval = ColonyManagerRedux.UpdateInterval.Daily;
    }

    /// <summary>
    /// Seeds a brand-new job from the mod-settings defaults. PostMake runs only for freshly created
    /// jobs; a loaded job takes its values from <see cref="ExposeData"/> instead, so the two never
    /// fight over the same fields.
    /// </summary>
    public override void PostMake()
    {
        base.PostMake();

        var settings = ManagerSettings;
        if (settings == null)
        {
            return;
        }

        _demands.ResumeBelow = settings.DefaultResumeBelow;
        _colonyOptions.Set(LaborOption.MaxWorkHours, settings.DefaultMaxWorkHours);
        _preferEnhancedWorkTab = settings.PreferEnhancedWorkTab;
    }

    public DemandModel Demands => _demands;

    public LaborOptionSet ColonyOptions => _colonyOptions;

    public LaborPlan Plan => _plan;

    public BaselineStore Baselines => _baselines;

    /// <summary>Write schedules into Enhanced Work Tab when it's loaded, so the player can see them.</summary>
    public bool PreferEnhancedWorkTab
    {
        get => _preferEnhancedWorkTab;
        set => _preferEnhancedWorkTab = value;
    }

    public IScheduleApplier Applier =>
        _preferEnhancedWorkTab && EnhancedWorkTab.Available ? EnhancedWorkTab : Vanilla;

    public override string Label => "WorkManager.Labor.JobLabel".Translate();

    /// <summary>
    /// A labor plan is made of references to specific colonists, so it can't be exported to
    /// another colony the way a threshold job can.
    /// </summary>
    public override bool IsTransferable => false;

    /// <summary>This job schedules every kind of work rather than being one kind of work itself.</summary>
    public override WorkTypeDef? WorkTypeDef => null;

    public override IEnumerable<string> Targets =>
        _plan.IsEmpty
            ? [(string)"WorkManager.Labor.NoPlanYet".Translate()]
            : _plan.Pawns.Where(p => _plan.ScheduledHours(p) > 0).Select(p => p.LabelShortCap);

    /// <summary>Colonists on this map the job is allowed to schedule.</summary>
    public IEnumerable<Pawn> ManagedPawns =>
        ((Map)Manager).mapPawns.FreeColonists.Where(p => !_excludedPawns.Contains(p));

    public bool IsManagedPawn(Pawn pawn) => !_excludedPawns.Contains(pawn);

    public void SetPawnManaged(Pawn pawn, bool managed)
    {
        if (managed)
        {
            _excludedPawns.Remove(pawn);
        }
        else if (!_excludedPawns.Contains(pawn))
        {
            _excludedPawns.Add(pawn);
            // Hand them straight back to their own priorities rather than leaving them frozen in
            // whatever the last schedule set.
            Applier.Release(pawn, _baselines);
            _plan.Remove(pawn);
        }
    }

    /// <summary>The per-pawn override set, creating it on first use.</summary>
    public LaborOptionSet OverridesFor(Pawn pawn)
    {
        if (!_pawnOptions.TryGetValue(pawn, out var set))
        {
            set = new LaborOptionSet();
            _pawnOptions[pawn] = set;
        }
        return set;
    }

    public bool HasOverrides(Pawn pawn) =>
        _pawnOptions.TryGetValue(pawn, out var set) && set.Values.Count > 0;

    public void ClearOverrides(Pawn pawn) => _pawnOptions.Remove(pawn);

    public EffectiveOptions OptionsFor(Pawn pawn) =>
        new(_colonyOptions, _pawnOptions.GetValueOrDefault(pawn));

    public EffectiveOptions ColonyEffectiveOptions => new(_colonyOptions, null);

    protected override Coroutine GatherJobDataCoroutine(
        ManagerLog jobLog,
        AnyBoxed<LaborWorkData?> data
    )
    {
        var demands = _demands.Evaluate(Manager, this);
        _demands.Prune(
            Manager.JobTracker.Jobs.Where(j => j != this).Select(j => j.GetUniqueLoadID())
        );

        yield return ResumeImmediately.Singleton;

        var pawns = ManagedPawns.ToList();
        var result = LaborPlanner.Build(pawns, demands, ColonyEffectiveOptions, OptionsFor);

        data.Value = new LaborWorkData { Result = result };
    }

    protected override Coroutine ExecuteJobDataCoroutine(
        ManagerLog jobLog,
        LaborWorkData data,
        Boxed<bool> workDone
    )
    {
        var result = data.Result;
        if (result == null)
        {
            yield break;
        }

        _plan = result.Plan;
        _plan.Prune(IsManagedPawn);

        foreach (var line in result.LogLines)
        {
            jobLog.AddDetail(line);
        }

        jobLog.AddDetail(
            "WorkManager.Log.PlanInstalled".Translate(result.ScheduledPawns, result.ScheduledHours)
        );

        // Put the current hour into effect immediately rather than waiting for the clock to roll
        // over; the player just asked for this plan.
        _lastAppliedHour = -1;
        ApplyIfHourChanged();

        workDone.Value = result.ScheduledHours > 0;
    }

    /// <summary>
    /// Applies the plan's current hour if the clock has moved on since the last application.
    /// Called from the game component every few ticks.
    /// </summary>
    public void ApplyIfHourChanged()
    {
        if (_plan.IsEmpty || IsSuspended || !IsManaged)
        {
            return;
        }

        var map = (Map)Manager;
        var hour = GenLocalDate.HourOfDay(map);
        if (hour == _lastAppliedHour)
        {
            return;
        }

        _lastAppliedHour = hour;
        Applier.Apply(_plan, hour, _baselines, OptionsFor);
    }

    /// <summary>Forces the next tick to re-apply, e.g. after the player edits options.</summary>
    public void InvalidateApplication() => _lastAppliedHour = -1;

    public override void CleanUp(ManagerLog? jobLog = null)
    {
        foreach (var pawn in _plan.Pawns.ToList())
        {
            if (pawn is { Destroyed: false })
            {
                Applier.Release(pawn, _baselines);
            }
        }

        jobLog?.AddDetail("WorkManager.Log.PrioritiesRestored".Translate());
        _plan = new LaborPlan();
        _lastAppliedHour = -1;
    }

    public override void ExposeData()
    {
        base.ExposeData();

        Scribe_Deep.Look(ref _demands, "demands");
        Scribe_Deep.Look(ref _colonyOptions, "colonyOptions");
        Scribe_Deep.Look(ref _baselines, "baselines");
        Scribe_Deep.Look(ref _plan, "plan");
        Scribe_Collections.Look(ref _pawnOptions, "pawnOptions", LookMode.Reference, LookMode.Deep);
        Scribe_Collections.Look(ref _excludedPawns, "excludedPawns", LookMode.Reference);
        Scribe_Values.Look(ref _preferEnhancedWorkTab, "preferEnhancedWorkTab", true);

        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            _demands ??= new DemandModel();
            _colonyOptions ??= new LaborOptionSet().WithDefaults();
            _baselines ??= new BaselineStore();
            _plan ??= new LaborPlan();
            _pawnOptions ??= [];
            _excludedPawns ??= [];
            _excludedPawns.RemoveAll(p => p == null);
            foreach (var pawn in _pawnOptions.Keys.Where(p => p == null).ToList())
            {
                _pawnOptions.Remove(pawn);
            }
            _lastAppliedHour = -1;
        }
    }
}
