// LaborPlanner.cs
// Copyright (c) 2026 archdukejim

namespace WorkManager;

/// <summary>
/// Turns demands and pawns into an hour-by-hour plan.
/// </summary>
/// <remarks>
/// Three passes, in order:
/// <list type="number">
/// <item>Coverage — demands with a minimum worker count get their pawns first, picking the
/// least-loaded eligible pawn each hour so the duty rotates instead of falling on one colonist.</item>
/// <item>Fill — the remaining hours go to whichever (pawn, demand) pairing buys the most, where a
/// demand's pull shrinks as work is assigned to it. That's what moves labor off wood once the wood
/// pile is covered and onto whatever is still short.</item>
/// <item>Smoothing — runs shorter than the minimum block are extended or dropped, so nobody spends
/// the day walking between jobs.</item>
/// </list>
/// </remarks>
public static class LaborPlanner
{
    /// <summary>Working state for one pawn during planning.</summary>
    private sealed class Candidate
    {
        public required Pawn Pawn { get; init; }
        public required PawnAvailability Availability { get; init; }
        public required EffectiveOptions Options { get; init; }

        public readonly Dictionary<WorkTypeDef, float> Score = [];
        public readonly Dictionary<WorkTypeDef, float> Throughput = [];
        public readonly WorkTypeDef?[] Row = new WorkTypeDef?[LaborPlan.HoursPerDay];
        public readonly bool[] Locked = new bool[LaborPlan.HoursPerDay];

        public int Assigned;
        public string? Impairment;

        public bool CanTake(int hour) =>
            Row[hour] == null
            && Availability.OpenHours[hour]
            && Assigned < Availability.MaxHours;

        public float ScoreFor(WorkTypeDef workType) =>
            Score.TryGetValue(workType, out var score) ? score : 0f;

        public void Take(int hour, WorkTypeDef workType, bool locked)
        {
            Row[hour] = workType;
            Locked[hour] = locked;
            Assigned++;
        }

        public void Release(int hour)
        {
            if (Row[hour] != null)
            {
                Row[hour] = null;
                Assigned--;
            }
        }
    }

    public sealed class PlanResult
    {
        public required LaborPlan Plan { get; init; }
        public required List<string> LogLines { get; init; }
        public required int ScheduledPawns { get; init; }
        public required int ScheduledHours { get; init; }
    }

    /// <summary>Bonus applied to staying on the same work as the previous hour.</summary>
    private const float ContinuityBonus = 1.3f;

    /// <summary>How fast a demand's pull falls off as pawn-hours are committed to it.</summary>
    private const float SaturationRate = 0.3f;

    public static PlanResult Build(
        IReadOnlyList<Pawn> pawns,
        IReadOnlyList<LaborDemand> demands,
        EffectiveOptions colonyOptions,
        Func<Pawn, EffectiveOptions> optionsFor
    )
    {
        var log = new List<string>();
        var plan = new LaborPlan();

        var candidates = BuildCandidates(pawns, optionsFor, log);
        var workTypes = demands.Select(d => d.WorkType).Distinct().ToList();

        ScoreCandidates(candidates, workTypes);

        if (colonyOptions.Flag(LaborOption.KeepCriticalManned))
        {
            CoveragePass(candidates, demands, log);
        }

        FillPass(candidates, demands);
        SmoothingPass(candidates);

        var scheduledPawns = 0;
        var scheduledHours = 0;

        foreach (var candidate in candidates)
        {
            plan.EnsureRow(candidate.Pawn);
            for (var hour = 0; hour < LaborPlan.HoursPerDay; hour++)
            {
                plan.Set(candidate.Pawn, hour, candidate.Row[hour]);
            }

            var hours = candidate.Row.Count(w => w != null);
            if (hours > 0)
            {
                scheduledPawns++;
                scheduledHours += hours;
            }

            var note = BuildNote(candidate, hours);
            if (note != null)
            {
                plan.SetNote(candidate.Pawn, note);
            }
        }

        plan.MarkGenerated();

        foreach (var demand in demands)
        {
            log.Add(
                "WorkManager.Log.DemandLine".Translate(
                    demand.Label,
                    demand.WorkType.gerundLabel.CapitalizeFirst(),
                    demand.Weight.ToString("0.00"),
                    demand.Active
                        ? "WorkManager.Log.Active".Translate()
                        : "WorkManager.Log.Satisfied".Translate(),
                    demand.StatusLine()
                )
            );
        }

        return new PlanResult
        {
            Plan = plan,
            LogLines = log,
            ScheduledPawns = scheduledPawns,
            ScheduledHours = scheduledHours,
        };
    }

    private static List<Candidate> BuildCandidates(
        IReadOnlyList<Pawn> pawns,
        Func<Pawn, EffectiveOptions> optionsFor,
        List<string> log
    )
    {
        var candidates = new List<Candidate>();

        foreach (var pawn in pawns.OrderBy(p => p.thingIDNumber))
        {
            var options = optionsFor(pawn);
            var availability = AvailabilityModel.For(pawn, options);

            if (availability.Unavailable)
            {
                if (availability.Note != null)
                {
                    log.Add(
                        "WorkManager.Log.PawnSkipped".Translate(
                            pawn.LabelShortCap,
                            availability.Note
                        )
                    );
                }
                continue;
            }

            candidates.Add(
                new Candidate
                {
                    Pawn = pawn,
                    Availability = availability,
                    Options = options,
                }
            );
        }

        return candidates;
    }

    /// <summary>
    /// Fills in each candidate's blended score per work type. Throughput and growth are normalised
    /// within a work type before blending, so "best miner" and "best researcher" are on the same
    /// scale and a demand's weight is what decides between them.
    /// </summary>
    private static void ScoreCandidates(List<Candidate> candidates, List<WorkTypeDef> workTypes)
    {
        foreach (var workType in workTypes)
        {
            var aptitudes = new Dictionary<Candidate, WorkAptitude>();
            var maxThroughput = 0f;
            var maxGrowth = 0f;

            foreach (var candidate in candidates)
            {
                var options = candidate.Options;
                var aptitude = PawnScorer.Evaluate(candidate.Pawn, workType, options);
                if (!aptitude.Capable)
                {
                    continue;
                }

                aptitudes[candidate] = aptitude;
                maxThroughput = Mathf.Max(maxThroughput, aptitude.Throughput);
                maxGrowth = Mathf.Max(maxGrowth, aptitude.Growth);

                if (aptitude.Impairment != null)
                {
                    candidate.Impairment ??= aptitude.Impairment;
                }
            }

            foreach (var (candidate, aptitude) in aptitudes)
            {
                var throughput = maxThroughput > 0f ? aptitude.Throughput / maxThroughput : 0f;
                var growth = maxGrowth > 0f ? aptitude.Growth / maxGrowth : 0f;
                var bias = candidate.Options.Get(LaborOption.GrowthBias);

                candidate.Throughput[workType] = throughput;
                candidate.Score[workType] = Mathf.Lerp(throughput, growth, bias);
            }
        }
    }

    /// <summary>
    /// Keeps minimum-staffed demands manned every hour, handing each hour to whoever has the
    /// fewest hours so far. That rotation is what stops a single researcher being scheduled for a
    /// 24-hour shift while everyone else idles.
    /// </summary>
    private static void CoveragePass(
        List<Candidate> candidates,
        IReadOnlyList<LaborDemand> demands,
        List<string> log
    )
    {
        var covered = new Dictionary<LaborDemand, int>();

        for (var hour = 0; hour < LaborPlan.HoursPerDay; hour++)
        {
            foreach (var demand in demands.Where(d => d.Config.MinWorkers > 0))
            {
                var assignedThisHour = candidates.Count(c => c.Row[hour] == demand.WorkType);
                var needed = demand.Config.MinWorkers - assignedThisHour;

                for (var i = 0; i < needed; i++)
                {
                    var pick = candidates
                        .Where(c => Eligible(c, demand, hour, assignedThisHour + i))
                        .OrderBy(c => c.Assigned)
                        .ThenByDescending(c => c.ScoreFor(demand.WorkType))
                        .ThenBy(c => c.Pawn.thingIDNumber)
                        .FirstOrDefault();

                    if (pick == null)
                    {
                        break;
                    }

                    pick.Take(hour, demand.WorkType, locked: true);
                    covered[demand] = covered.GetValueOrDefault(demand) + 1;
                }
            }
        }

        foreach (var (demand, hours) in covered)
        {
            log.Add(
                "WorkManager.Log.CoverageLine".Translate(
                    demand.WorkType.gerundLabel.CapitalizeFirst(),
                    demand.Config.MinWorkers,
                    hours
                )
            );
        }
    }

    /// <summary>
    /// Greedy hour-by-hour fill. Every hour, the highest-value (pawn, demand) pairing is taken
    /// until nothing is left worth assigning; a demand's remaining work shrinks as pawns are put on
    /// it, which is what moves the next pawn onto the next-most-starved resource.
    /// </summary>
    private static void FillPass(List<Candidate> candidates, IReadOnlyList<LaborDemand> demands)
    {
        var workersThisHour = new Dictionary<WorkTypeDef, int>();

        for (var hour = 0; hour < LaborPlan.HoursPerDay; hour++)
        {
            // Counted once per hour and kept up to date as assignments are made, rather than
            // recounted for every candidate/demand pairing.
            workersThisHour.Clear();
            foreach (var candidate in candidates)
            {
                var assigned = candidate.Row[hour];
                if (assigned != null)
                {
                    workersThisHour[assigned] = workersThisHour.GetValueOrDefault(assigned) + 1;
                }
            }

            while (true)
            {
                Candidate? bestCandidate = null;
                LaborDemand? bestDemand = null;
                var bestValue = 0f;

                foreach (var candidate in candidates)
                {
                    if (!candidate.CanTake(hour))
                    {
                        continue;
                    }

                    foreach (var demand in demands)
                    {
                        if (demand.RemainingWork <= 0f || demand.Weight <= 0f)
                        {
                            continue;
                        }

                        if (
                            !Eligible(
                                candidate,
                                demand,
                                hour,
                                workersThisHour.GetValueOrDefault(demand.WorkType)
                            )
                        )
                        {
                            continue;
                        }

                        var value = Value(candidate, demand, hour);
                        if (value <= 0f)
                        {
                            continue;
                        }

                        if (
                            value > bestValue
                            || (
                                Mathf.Approximately(value, bestValue)
                                && bestCandidate != null
                                && candidate.Pawn.thingIDNumber < bestCandidate.Pawn.thingIDNumber
                            )
                        )
                        {
                            bestValue = value;
                            bestCandidate = candidate;
                            bestDemand = demand;
                        }
                    }
                }

                if (bestCandidate == null || bestDemand == null)
                {
                    break;
                }

                bestCandidate.Take(hour, bestDemand.WorkType, locked: false);
                workersThisHour[bestDemand.WorkType] =
                    workersThisHour.GetValueOrDefault(bestDemand.WorkType) + 1;
                bestDemand.AssignedHours++;
                bestDemand.RemainingWork -= Mathf.Max(
                    0.25f,
                    bestCandidate.Throughput.GetValueOrDefault(bestDemand.WorkType)
                );
            }
        }
    }

    /// <summary>
    /// What putting this pawn on this demand for this hour is worth: the demand's pull, scaled by
    /// how good the pawn is at it, damped by how much labor the demand has already been given, and
    /// bonused for continuing what they were doing the hour before.
    /// </summary>
    private static float Value(Candidate candidate, LaborDemand demand, int hour)
    {
        var score = candidate.ScoreFor(demand.WorkType);
        if (score <= 0f)
        {
            return 0f;
        }

        // Diminishing returns: the fifth pawn-hour poured into wood is worth less than the first,
        // which is what spreads labor across everything that's short instead of dogpiling one row.
        var saturation = 1f / (1f + (SaturationRate * demand.AssignedHours));
        var value = demand.Weight * score * saturation;

        if (hour > 0 && candidate.Row[hour - 1] == demand.WorkType)
        {
            value *= ContinuityBonus;
        }

        return value;
    }

    /// <summary>
    /// Removes assignment runs shorter than the pawn's minimum block, first by trying to grow them
    /// into adjacent open hours, then by handing the hours to the run before them, and failing both
    /// by dropping them back to the pawn's baseline priorities.
    /// </summary>
    private static void SmoothingPass(List<Candidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            var minBlock = Mathf.Max(1, candidate.Options.Int(LaborOption.MinBlockHours));
            if (minBlock <= 1)
            {
                continue;
            }

            var hour = 0;
            while (hour < LaborPlan.HoursPerDay)
            {
                var workType = candidate.Row[hour];
                if (workType == null)
                {
                    hour++;
                    continue;
                }

                var start = hour;
                while (hour < LaborPlan.HoursPerDay && candidate.Row[hour] == workType)
                {
                    hour++;
                }

                var length = hour - start;
                if (length >= minBlock)
                {
                    continue;
                }

                if (GrowRun(candidate, start, hour, workType, minBlock))
                {
                    continue;
                }

                // A locked run is there to keep critical work manned; a stubby one is still better
                // than an unmanned hour.
                if (Enumerable.Range(start, length).Any(h => candidate.Locked[h]))
                {
                    continue;
                }

                var previous = start > 0 ? candidate.Row[start - 1] : null;
                for (var h = start; h < hour; h++)
                {
                    candidate.Release(h);
                    if (previous != null && candidate.CanTake(h))
                    {
                        candidate.Take(h, previous, locked: false);
                    }
                }
            }
        }
    }

    /// <summary>Tries to pad a too-short run out to the minimum block using adjacent open hours.</summary>
    private static bool GrowRun(
        Candidate candidate,
        int start,
        int end,
        WorkTypeDef workType,
        int minBlock
    )
    {
        var length = end - start;

        var after = end;
        while (length < minBlock && after < LaborPlan.HoursPerDay && candidate.CanTake(after))
        {
            candidate.Take(after, workType, locked: false);
            after++;
            length++;
        }

        var before = start - 1;
        while (length < minBlock && before >= 0 && candidate.CanTake(before))
        {
            candidate.Take(before, workType, locked: false);
            before--;
            length++;
        }

        return length >= minBlock;
    }

    private static bool Eligible(
        Candidate candidate,
        LaborDemand demand,
        int hour,
        int workersThisHour
    )
    {
        if (!candidate.CanTake(hour))
        {
            return false;
        }

        if (candidate.ScoreFor(demand.WorkType) <= 0f)
        {
            return false;
        }

        var pinned = demand.Config.PinnedPawns;
        if (pinned.Count > 0 && !pinned.Contains(candidate.Pawn))
        {
            return false;
        }

        if (demand.Config.MaxWorkers > 0 && workersThisHour >= demand.Config.MaxWorkers)
        {
            return false;
        }

        return true;
    }

    private static string? BuildNote(Candidate candidate, int hours)
    {
        var parts = new List<string>();

        if (candidate.Availability.Note != null)
        {
            parts.Add(candidate.Availability.Note);
        }

        if (candidate.Impairment != null)
        {
            parts.Add("WorkManager.Plan.Impaired".Translate(candidate.Impairment));
        }

        if (hours == 0)
        {
            parts.Add("WorkManager.Plan.NothingToDo".Translate());
        }
        else if (hours >= candidate.Availability.MaxHours)
        {
            parts.Add("WorkManager.Plan.AtCap".Translate(candidate.Availability.MaxHours));
        }

        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }
}
