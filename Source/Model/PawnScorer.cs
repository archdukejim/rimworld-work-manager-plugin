// PawnScorer.cs
// Copyright (c) 2026 archdukejim

namespace WorkManager;

/// <summary>Raw, un-normalised measures of what a pawn brings to one kind of work.</summary>
public readonly struct WorkAptitude
{
    /// <summary>Estimated output per hour, in whatever units that work type's stats are in. Only
    /// ever compared against other pawns doing the same work.</summary>
    public float Throughput { get; init; }

    /// <summary>How much the colony gains long-term by putting this pawn on this work: fast
    /// learners with room left to grow score highest.</summary>
    public float Growth { get; init; }

    /// <summary>False when the pawn can't do this work at all.</summary>
    public bool Capable { get; init; }

    /// <summary>Set when health is what's holding them back, for the UI to explain itself.</summary>
    public string? Impairment { get; init; }

    public static readonly WorkAptitude Incapable = new() { Capable = false };
}

/// <summary>
/// Scores pawns against work types using what the game already tracks: the work speed stats that
/// actually govern output, skill level, passion, and the body capacities the work leans on.
/// </summary>
public static class PawnScorer
{
    /// <summary>Below this capacity level, movement-bound work costs the colony more than it gains.</summary>
    private const float SevereImpairment = 0.6f;

    public static bool CanDo(Pawn pawn, WorkTypeDef workType) =>
        pawn.workSettings != null
        && pawn.workSettings.EverWork
        && !pawn.WorkTypeIsDisabled(workType);

    public static WorkAptitude Evaluate(Pawn pawn, WorkTypeDef workType, in EffectiveOptions options)
    {
        if (!CanDo(pawn, workType))
        {
            return WorkAptitude.Incapable;
        }

        var profile = WorkTypeProfile.For(workType);
        var throughput = StatFactor(pawn, profile) * SkillFactor(pawn, workType);
        string? impairment = null;

        if (options.Flag(LaborOption.HealthAware))
        {
            var (health, worst) = HealthFactor(pawn, profile);
            throughput *= health;
            if (health < SevereImpairment && worst != null)
            {
                impairment = worst.LabelCap;
            }
        }

        if (options.Flag(LaborOption.RespectPassions))
        {
            throughput *= PassionFactor(pawn, workType);
        }

        return new WorkAptitude
        {
            Throughput = Mathf.Max(throughput, 0.0001f),
            Growth = GrowthValue(pawn, workType),
            Capable = true,
            Impairment = impairment,
        };
    }

    /// <summary>
    /// The product of the stats that govern this work. Absolute scale doesn't matter: the planner
    /// normalises each work type's column before comparing across work types.
    /// </summary>
    private static float StatFactor(Pawn pawn, WorkTypeProfile profile)
    {
        var factor = 1f;
        foreach (var stat in profile.ThroughputStats)
        {
            factor *= Mathf.Max(pawn.GetStatValue(stat), 0.01f);
        }
        return factor;
    }

    /// <summary>
    /// Skill level as a throughput multiplier. An unskilled pawn isn't useless, they're just slow
    /// and sloppy, so this spans a third of a skilled pawn's output up to a little above it.
    /// </summary>
    private static float SkillFactor(Pawn pawn, WorkTypeDef workType)
    {
        if (pawn.skills == null || workType.relevantSkills.NullOrEmpty())
        {
            return 1f;
        }

        var total = 0f;
        var count = 0;
        foreach (var skill in workType.relevantSkills)
        {
            var record = pawn.skills.GetSkill(skill);
            if (record == null || record.TotallyDisabled)
            {
                continue;
            }
            total += record.Level;
            count++;
        }

        if (count == 0)
        {
            return 1f;
        }

        return Mathf.Lerp(0.35f, 1.35f, total / count / 20f);
    }

    /// <summary>
    /// Weighted capacity level, plus the capacity that's dragging hardest. Movement-bound work is
    /// judged harshly on Moving: a pawn with a bad leg hauling is the colony's throughput problem,
    /// not just their own.
    /// </summary>
    private static (float factor, PawnCapacityDef? worst) HealthFactor(
        Pawn pawn,
        WorkTypeProfile profile
    )
    {
        if (profile.CapacityWeights.Count == 0)
        {
            return (1f, null);
        }

        var weighted = 0f;
        var totalWeight = 0f;
        PawnCapacityDef? worst = null;
        var worstLevel = float.MaxValue;

        foreach (var (capacity, weight) in profile.CapacityWeights)
        {
            var level = pawn.health.capacities.GetLevel(capacity);
            weighted += level * weight;
            totalWeight += weight;
            if (level < worstLevel)
            {
                worstLevel = level;
                worst = capacity;
            }
        }

        var factor = totalWeight > 0f ? weighted / totalWeight : 1f;

        if (profile.MovementBound)
        {
            var moving = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Moving);
            if (moving < SevereImpairment)
            {
                // Squaring the shortfall is deliberate: a pawn at half speed doesn't cost half a
                // hauler, they cost a hauler plus the trips nobody else made in the meantime.
                factor *= moving * moving;
            }
        }

        return (Mathf.Clamp(factor, 0.01f, 1.5f), worst);
    }

    /// <summary>Passion is mostly a learning effect, so it only nudges present-day throughput.</summary>
    private static float PassionFactor(Pawn pawn, WorkTypeDef workType)
    {
        if (pawn.skills == null || workType.relevantSkills.NullOrEmpty())
        {
            return 1f;
        }

        var best = Passion.None;
        foreach (var skill in workType.relevantSkills)
        {
            var record = pawn.skills.GetSkill(skill);
            if (record != null && !record.TotallyDisabled && record.passion > best)
            {
                best = record.passion;
            }
        }

        return best switch
        {
            Passion.Major => 1.2f,
            Passion.Minor => 1.1f,
            _ => 1f,
        };
    }

    /// <summary>
    /// What the colony gets out of this pawn a season from now: their learn rate (which already
    /// folds in passion and age) against how much room the skill has left to grow.
    /// </summary>
    private static float GrowthValue(Pawn pawn, WorkTypeDef workType)
    {
        if (pawn.skills == null || workType.relevantSkills.NullOrEmpty())
        {
            return 0f;
        }

        var best = 0f;
        foreach (var skill in workType.relevantSkills)
        {
            var record = pawn.skills.GetSkill(skill);
            if (record == null || record.TotallyDisabled)
            {
                continue;
            }

            var headroom = Mathf.Clamp01((20f - record.Level) / 20f);
            var value = record.LearnRateFactor() * headroom;
            if (value > best)
            {
                best = value;
            }
        }

        return best;
    }
}
