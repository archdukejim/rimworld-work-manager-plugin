// DemandModel.cs
// Copyright (c) 2026 archdukejim

using ColonyManagerRedux;

namespace WorkManager;

/// <summary>
/// Turns the stock targets already configured on the other manager tabs into a per-work-type pull,
/// and remembers enough history to tell whether each stockpile is actually keeping up.
/// </summary>
/// <remarks>
/// The hysteresis lives here. A demand latches on when its stock falls to
/// <see cref="ResumeBelow"/> of target and only latches off once it reaches target again, so labor
/// doesn't flip between wood and steel every time a single log is burned. Between those two points
/// the previous decision stands.
/// </remarks>
public sealed class DemandModel : IExposable
{
    /// <summary>Fraction of target a stockpile has to fall to before labor is pulled back onto it.</summary>
    public float ResumeBelow = 0.85f;

    /// <summary>Sampling state per source job, keyed by the job's stable load id.</summary>
    private Dictionary<string, DemandState> _states = [];

    private Dictionary<WorkTypeDef, DemandConfig> _configs = [];

    /// <summary>Player settings per work type. Rows appear here the first time a work type is seen.</summary>
    public IReadOnlyDictionary<WorkTypeDef, DemandConfig> Configs => _configs;

    public DemandConfig ConfigFor(WorkTypeDef workType)
    {
        if (!_configs.TryGetValue(workType, out var config))
        {
            config = new DemandConfig();
            _configs[workType] = config;
        }
        return config;
    }

    public void AddStandingDemand(WorkTypeDef workType)
    {
        var config = ConfigFor(workType);
        config.Standing = true;
        if (config.MinWorkers < 1)
        {
            config.MinWorkers = 1;
        }
    }

    public void RemoveStandingDemand(WorkTypeDef workType)
    {
        if (_configs.TryGetValue(workType, out var config))
        {
            config.Standing = false;
        }
    }

    /// <summary>
    /// Reads every managed threshold job on the map plus the player's standing rows, and returns
    /// the current pull per demand. Updates the rate samples and hysteresis latches as it goes,
    /// unless <paramref name="mutate"/> is false — the UI redraws many times a second and must not
    /// move the latches by looking at them.
    /// </summary>
    public List<LaborDemand> Evaluate(Manager manager, ManagerJob self, bool mutate = true)
    {
        var demands = new List<LaborDemand>();
        var now = Find.TickManager.TicksGame;
        var seenWorkTypes = new HashSet<WorkTypeDef>();

        foreach (var job in manager.JobTracker.Jobs)
        {
            if (job == self || !job.IsManaged || job.IsSuspended)
            {
                continue;
            }

            var workType = job.WorkTypeDef;
            if (workType == null || job.Trigger is not Trigger_Threshold threshold)
            {
                continue;
            }

            var config = ConfigFor(workType);
            if (!config.Enabled || config.Criticality <= 0f)
            {
                continue;
            }

            var key = job.GetUniqueLoadID();
            var current = threshold.GetCurrentCount();
            var target = threshold.TargetCount;
            var state = StateFor(key);
            var fill = target > 0 ? Mathf.Clamp01((float)current / target) : 1f;
            var rate = mutate ? state.Sample(current, now) : state.RatePerDay;
            var active = mutate ? state.UpdateLatch(fill, ResumeBelow) : state.Active;

            demands.Add(
                new LaborDemand
                {
                    WorkType = workType,
                    SourceJobId = key,
                    Label = job.Label + ": " + job.TargetsLabel,
                    Current = current,
                    Target = target,
                    RatePerDay = rate,
                    Active = active,
                    Urgency = Urgency(fill, rate, target, active),
                    Config = config,
                    RemainingWork = WorkWanted(current, target),
                }
            );
            seenWorkTypes.Add(workType);
        }

        // Standing rows: work the colony wants covered even though nothing stockpiles it.
        foreach (var (workType, config) in _configs)
        {
            if (!config.Standing || !config.Enabled || config.Criticality <= 0f)
            {
                continue;
            }

            demands.Add(
                new LaborDemand
                {
                    WorkType = workType,
                    SourceJobId = null,
                    Label = workType.gerundLabel.CapitalizeFirst(),
                    Current = 0,
                    Target = 0,
                    RatePerDay = 0f,
                    Active = true,
                    // A standing demand's pull is entirely the player's criticality setting; its
                    // real teeth are MinWorkers, which the planner satisfies before anything else.
                    Urgency = 0.35f,
                    Config = config,
                    // A standing demand wants its minimum staffing covered for the day and no
                    // more; without a bound it would soak up every hour nothing else claimed.
                    RemainingWork =
                        PawnAvailability.HoursPerDay * Mathf.Max(1, config.MinWorkers),
                }
            );
        }

        demands.SortByDescending(d => d.Weight);
        return demands;
    }

    /// <summary>
    /// How hard to pull labor toward a demand. An active demand pulls in proportion to how far
    /// below target it is; a satisfied one still pulls a little if it's draining fast enough to
    /// drop back through the resume threshold within a day.
    /// </summary>
    private float Urgency(float fill, float ratePerDay, int target, bool active)
    {
        if (active)
        {
            return Mathf.Clamp01(1f - fill);
        }

        if (target <= 0 || ratePerDay >= 0f)
        {
            return 0f;
        }

        var projectedFill = fill + (ratePerDay / target);
        return Mathf.Clamp01(ResumeBelow - projectedFill);
    }

    /// <summary>
    /// The shortfall expressed as pawn-hours, so the planner can stop assigning once a demand is
    /// covered. One unit of an average worker's output is treated as ten items an hour, which is
    /// only ever used as a relative measure between demands.
    /// </summary>
    private static float WorkWanted(int current, int target) =>
        target <= current ? 0f : (target - current) / 10f;

    private DemandState StateFor(string key)
    {
        if (!_states.TryGetValue(key, out var state))
        {
            state = new DemandState();
            _states[key] = state;
        }
        return state;
    }

    /// <summary>Forgets sampling state for jobs that no longer exist.</summary>
    public void Prune(IEnumerable<string> liveJobIds)
    {
        var live = new HashSet<string>(liveJobIds);
        var dead = _states.Keys.Where(k => !live.Contains(k)).ToList();
        foreach (var key in dead)
        {
            _states.Remove(key);
        }
    }

    public void ExposeData()
    {
        Scribe_Values.Look(ref ResumeBelow, "resumeBelow", 0.85f);
        Scribe_Collections.Look(ref _states, "states", LookMode.Value, LookMode.Deep);
        Scribe_Collections.Look(ref _configs, "configs", LookMode.Def, LookMode.Deep);

        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            _states ??= [];
            _configs ??= [];
            // A def that vanished with a mod would otherwise resurrect as a null key.
            foreach (var key in _configs.Keys.Where(k => k == null).ToList())
            {
                _configs.Remove(key);
            }
        }
    }
}

/// <summary>Per-source sampling and latch state. Split out so it scribes cleanly.</summary>
public sealed class DemandState : IExposable
{
    private int _lastCount = -1;
    private int _lastTick = -1;
    private float _ratePerDay;
    private bool _active = true;

    /// <summary>Last smoothed change per day, without taking a new sample.</summary>
    public float RatePerDay => _ratePerDay;

    /// <summary>Current latch state, without moving it.</summary>
    public bool Active => _active;

    /// <summary>
    /// Records a stock reading and returns the smoothed change per day. Readings closer together
    /// than a quarter day are ignored, since the manager runs far more often than stock moves.
    /// </summary>
    public float Sample(int count, int tick)
    {
        const int QuarterDay = GenDate.TicksPerDay / 4;

        if (_lastTick < 0)
        {
            _lastCount = count;
            _lastTick = tick;
            return 0f;
        }

        var elapsed = tick - _lastTick;
        if (elapsed < QuarterDay)
        {
            return _ratePerDay;
        }

        var perDay = (count - _lastCount) * (float)GenDate.TicksPerDay / elapsed;
        // Exponential smoothing: one bad hunt shouldn't read as a famine.
        _ratePerDay = Mathf.Lerp(_ratePerDay, perDay, 0.5f);
        _lastCount = count;
        _lastTick = tick;
        return _ratePerDay;
    }

    /// <summary>Applies the hysteresis band and returns whether this demand is latched on.</summary>
    public bool UpdateLatch(float fill, float resumeBelow)
    {
        if (_active)
        {
            if (fill >= 1f)
            {
                _active = false;
            }
        }
        else if (fill <= resumeBelow)
        {
            _active = true;
        }
        return _active;
    }

    public void ExposeData()
    {
        Scribe_Values.Look(ref _lastCount, "lastCount", -1);
        Scribe_Values.Look(ref _lastTick, "lastTick", -1);
        Scribe_Values.Look(ref _ratePerDay, "ratePerDay");
        Scribe_Values.Look(ref _active, "active", true);
    }
}
