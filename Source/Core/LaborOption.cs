// LaborOption.cs
// Copyright (c) 2026 archdukejim

namespace WorkManager;

/// <summary>
/// Every knob the labor manager exposes. Each one is set for the colony on the job, and can be
/// overridden for a single pawn; see <see cref="LaborOptionSet"/>.
/// </summary>
public enum LaborOption
{
    /// <summary>0 = assign whoever produces the most right now, 1 = assign to grow skills.</summary>
    GrowthBias,

    /// <summary>Let passion feed into the assignment score.</summary>
    RespectPassions,

    /// <summary>Let health and capacity losses steer work away from what they'd slow down.</summary>
    HealthAware,

    /// <summary>Hand scheduled hours back to recreation when mood drops.</summary>
    WellBeingCare,

    /// <summary>Hard cap on scheduled work hours per day. Beyond it, a pawn falls back to their
    /// baseline priorities rather than being left with nothing to do.</summary>
    MaxWorkHours,

    /// <summary>Shortest run of hours the planner will assign to one work type.</summary>
    MinBlockHours,

    /// <summary>Only schedule hours the pawn's own timetable marks as work.</summary>
    RespectTimetable,

    /// <summary>Outside their scheduled block, leave a pawn's baseline priorities in place so
    /// they always have something to fall back on.</summary>
    NeverIdle,

    /// <summary>Mood fraction below which well-being care starts pulling hours.</summary>
    MoodFloor,

    /// <summary>Hours handed back to the pawn when they're under the mood floor.</summary>
    WellBeingHours,

    /// <summary>Honour the per-demand minimum worker count, rotating pawns through it so
    /// critical work stays manned around the clock.</summary>
    KeepCriticalManned,
}

/// <summary>How a <see cref="LaborOption"/>'s stored float should be read and edited.</summary>
public enum LaborOptionKind
{
    /// <summary>Stored as 0 or 1.</summary>
    Toggle,

    /// <summary>Stored as a whole number in [Min, Max].</summary>
    Integer,

    /// <summary>Stored as a 0..1 fraction, shown as a percentage.</summary>
    Fraction,
}

/// <summary>Static description of one option: how to store it, its bounds, and its default.</summary>
public sealed class LaborOptionMeta
{
    public LaborOption Option { get; }
    public LaborOptionKind Kind { get; }
    public float Default { get; }
    public float Min { get; }
    public float Max { get; }

    public string LabelKey => "WorkManager.Option." + Option;
    public string TipKey => "WorkManager.Option." + Option + ".Tip";

    public LaborOptionMeta(
        LaborOption option,
        LaborOptionKind kind,
        float @default,
        float min = 0f,
        float max = 1f
    )
    {
        Option = option;
        Kind = kind;
        Default = @default;
        Min = min;
        Max = max;
    }

    public float Clamp(float value) =>
        Kind == LaborOptionKind.Integer
            ? Mathf.Clamp(Mathf.Round(value), Min, Max)
            : Mathf.Clamp(value, Min, Max);

    /// <summary>Renders a stored value the way it should read in the UI.</summary>
    public string Format(float value) =>
        Kind switch
        {
            LaborOptionKind.Toggle => value > 0.5f
                ? "WorkManager.Common.On".Translate()
                : "WorkManager.Common.Off".Translate(),
            LaborOptionKind.Integer => Mathf.RoundToInt(value).ToString(),
            _ => value.ToStringPercent(),
        };
}

/// <summary>The option catalogue. Adding a knob here makes it show up in the UI, in the per-pawn
/// override list, and in save data, with no further wiring.</summary>
public static class LaborOptions
{
    public static readonly IReadOnlyList<LaborOptionMeta> All =
    [
        new(LaborOption.GrowthBias, LaborOptionKind.Fraction, 0.25f),
        new(LaborOption.RespectPassions, LaborOptionKind.Toggle, 1f),
        new(LaborOption.HealthAware, LaborOptionKind.Toggle, 1f),
        new(LaborOption.WellBeingCare, LaborOptionKind.Toggle, 1f),
        new(LaborOption.MaxWorkHours, LaborOptionKind.Integer, 10f, 1f, 16f),
        new(LaborOption.MinBlockHours, LaborOptionKind.Integer, 2f, 1f, 8f),
        new(LaborOption.RespectTimetable, LaborOptionKind.Toggle, 1f),
        new(LaborOption.NeverIdle, LaborOptionKind.Toggle, 1f),
        new(LaborOption.MoodFloor, LaborOptionKind.Fraction, 0.4f),
        new(LaborOption.WellBeingHours, LaborOptionKind.Integer, 2f, 0f, 8f),
        new(LaborOption.KeepCriticalManned, LaborOptionKind.Toggle, 1f),
    ];

    private static readonly Dictionary<LaborOption, LaborOptionMeta> ByOption = All.ToDictionary(
        m => m.Option
    );

    public static LaborOptionMeta Meta(LaborOption option) => ByOption[option];

    public static float DefaultOf(LaborOption option) => ByOption[option].Default;
}

/// <summary>
/// A sparse set of option values. The job holds one of these as the colony-wide setting; each
/// managed pawn may hold another whose entries win over the colony's.
/// </summary>
public sealed class LaborOptionSet : IExposable
{
    private Dictionary<LaborOption, float> _values = [];

    public IReadOnlyDictionary<LaborOption, float> Values => _values;

    public bool Has(LaborOption option) => _values.ContainsKey(option);

    /// <summary>The stored value, or <paramref name="fallback"/> when this set doesn't set it.</summary>
    public float Get(LaborOption option, float fallback) =>
        _values.TryGetValue(option, out var value) ? value : fallback;

    public void Set(LaborOption option, float value) =>
        _values[option] = LaborOptions.Meta(option).Clamp(value);

    public void Clear(LaborOption option) => _values.Remove(option);

    public void ClearAll() => _values.Clear();

    public LaborOptionSet Clone()
    {
        var clone = new LaborOptionSet();
        foreach (var (option, value) in _values)
        {
            clone._values[option] = value;
        }
        return clone;
    }

    /// <summary>Fills in every option that isn't already set, so a set can stand on its own.</summary>
    public LaborOptionSet WithDefaults()
    {
        foreach (var meta in LaborOptions.All)
        {
            if (!_values.ContainsKey(meta.Option))
            {
                _values[meta.Option] = meta.Default;
            }
        }
        return this;
    }

    public void ExposeData() =>
        Scribe_Collections.Look(ref _values, "values", LookMode.Value, LookMode.Value);
}

/// <summary>
/// The options actually in force for one pawn: the colony set with that pawn's overrides applied,
/// falling back to the catalogue defaults for anything neither one sets.
/// </summary>
public readonly struct EffectiveOptions
{
    private readonly LaborOptionSet _colony;
    private readonly LaborOptionSet? _pawn;

    public EffectiveOptions(LaborOptionSet colony, LaborOptionSet? pawn)
    {
        _colony = colony;
        _pawn = pawn;
    }

    public float Get(LaborOption option) =>
        _pawn != null && _pawn.Has(option)
            ? _pawn.Get(option, LaborOptions.DefaultOf(option))
            : _colony.Get(option, LaborOptions.DefaultOf(option));

    public bool Flag(LaborOption option) => Get(option) > 0.5f;

    public int Int(LaborOption option) => Mathf.RoundToInt(Get(option));

    /// <summary>True when this pawn overrides the colony's value for <paramref name="option"/>.</summary>
    public bool IsOverridden(LaborOption option) => _pawn != null && _pawn.Has(option);
}
