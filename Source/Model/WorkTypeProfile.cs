// WorkTypeProfile.cs
// Copyright (c) 2026 archdukejim

namespace WorkManager;

/// <summary>
/// What the game already knows about how fast a pawn does one kind of work: which stats scale its
/// throughput, and which body capacities it leans on.
/// </summary>
/// <remarks>
/// Stats and capacities are resolved by defName rather than through <see cref="StatDefOf"/> so a
/// stat that a given RimWorld version or DLC doesn't define is simply skipped instead of throwing
/// at type-load time.
/// </remarks>
public sealed class WorkTypeProfile
{
    /// <summary>Stats multiplied together to estimate output per hour. Missing stats are skipped.</summary>
    public List<StatDef> ThroughputStats { get; } = [];

    /// <summary>Capacity weights: how much losing this capacity costs this work type.</summary>
    public Dictionary<PawnCapacityDef, float> CapacityWeights { get; } = [];

    /// <summary>Work that moves across the map all day; a slow pawn drags the whole colony's
    /// throughput down doing it.</summary>
    public bool MovementBound { get; init; }

    private static readonly Dictionary<WorkTypeDef, WorkTypeProfile> Cache = [];

    public static WorkTypeProfile For(WorkTypeDef workType)
    {
        if (Cache.TryGetValue(workType, out var cached))
        {
            return cached;
        }

        var profile = Build(workType);
        Cache[workType] = profile;
        return profile;
    }

    /// <summary>Drops cached profiles; call when defs may have changed (game reload).</summary>
    public static void ClearCache() => Cache.Clear();

    private static StatDef? Stat(string defName) => DefDatabase<StatDef>.GetNamedSilentFail(defName);

    private static PawnCapacityDef? Capacity(string defName) =>
        DefDatabase<PawnCapacityDef>.GetNamedSilentFail(defName);

    private static WorkTypeProfile Build(WorkTypeDef workType)
    {
        var movementBound =
            workType.defName is "Hauling" or "Cleaning" or "Firefighter" or "BasicWorker";

        var profile = new WorkTypeProfile { MovementBound = movementBound };

        foreach (var statName in ThroughputStatNames(workType))
        {
            var stat = Stat(statName);
            if (stat != null)
            {
                profile.ThroughputStats.Add(stat);
            }
        }

        foreach (var (capacityName, weight) in CapacityWeightsFor(workType))
        {
            var capacity = Capacity(capacityName);
            if (capacity != null)
            {
                profile.CapacityWeights[capacity] = weight;
            }
        }

        return profile;
    }

    private static IEnumerable<string> ThroughputStatNames(WorkTypeDef workType)
    {
        // Named work types first: these have a stat that measures them directly.
        switch (workType.defName)
        {
            case "Mining":
                yield return "MiningSpeed";
                yield return "MiningYield";
                yield break;
            case "PlantCutting":
            case "Growing":
                yield return "PlantWorkSpeed";
                yield break;
            case "Construction":
                yield return "ConstructionSpeed";
                yield return "ConstructionSuccessChance";
                yield break;
            case "Cooking":
                yield return "CookSpeed";
                yield break;
            case "Research":
                yield return "ResearchSpeed";
                yield break;
            case "Doctor":
                yield return "MedicalTendSpeed";
                yield return "MedicalTendQuality";
                yield break;
            case "Hunting":
                yield return "ShootingAccuracyPawn";
                yield return "HuntingStealth";
                yield break;
            case "Handling":
                yield return "AnimalGatherSpeed";
                yield return "TameAnimalChance";
                yield break;
            case "Smithing":
            case "Tailoring":
            case "Art":
            case "Crafting":
                yield return "WorkSpeedGlobal";
                yield return "GeneralLaborSpeed";
                yield break;
            case "Hauling":
                yield return "MoveSpeed";
                yield return "CarryingCapacity";
                yield break;
            case "Cleaning":
            case "Firefighter":
            case "BasicWorker":
                yield return "MoveSpeed";
                yield return "WorkSpeedGlobal";
                yield break;
            case "Warden":
                yield return "NegotiationAbility";
                yield return "SocialImpact";
                yield break;
        }

        // Anything else, including work types added by other mods: general work speed is the
        // best estimate the game gives us.
        yield return "WorkSpeedGlobal";
        yield return "GeneralLaborSpeed";
    }

    private static IEnumerable<(string capacity, float weight)> CapacityWeightsFor(
        WorkTypeDef workType
    )
    {
        yield return ("Consciousness", 1f);

        switch (workType.defName)
        {
            case "Hauling":
            case "Cleaning":
            case "Firefighter":
            case "BasicWorker":
                // A pawn who can barely walk hauls barely anything; this is the case where
                // leaving them on the job actively costs the colony throughput.
                yield return ("Moving", 2f);
                yield return ("Manipulation", 0.5f);
                yield break;
            case "Mining":
            case "Construction":
            case "PlantCutting":
            case "Growing":
                yield return ("Manipulation", 1.5f);
                yield return ("Moving", 0.75f);
                yield break;
            case "Research":
            case "Art":
            case "Crafting":
            case "Smithing":
            case "Tailoring":
            case "Cooking":
                yield return ("Manipulation", 1.5f);
                yield return ("Sight", 1f);
                yield break;
            case "Doctor":
                yield return ("Manipulation", 1.5f);
                yield return ("Sight", 1.25f);
                yield break;
            case "Hunting":
                yield return ("Sight", 1.5f);
                yield return ("Manipulation", 1f);
                yield return ("Moving", 1f);
                yield break;
            case "Warden":
                yield return ("Talking", 1.5f);
                yield return ("Hearing", 0.75f);
                yield break;
            default:
                yield return ("Manipulation", 1f);
                yield return ("Moving", 0.5f);
                yield break;
        }
    }
}
