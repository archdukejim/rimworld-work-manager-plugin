// LaborPlan.cs
// Copyright (c) 2026 archdukejim

using System.Text;

namespace WorkManager;

/// <summary>
/// An hour-by-hour work assignment for every managed pawn: what each of them is meant to be doing
/// at each hour of the day, plus why the planner shaped it that way.
/// </summary>
public sealed class LaborPlan : IExposable
{
    public const int HoursPerDay = PawnAvailability.HoursPerDay;

    private Dictionary<Pawn, WorkTypeDef?[]> _slots = [];
    private Dictionary<Pawn, string> _notes = [];
    private int _generatedTick = -1;

    // Scribe staging: a jagged def array per pawn doesn't round-trip through Scribe_Collections,
    // so each pawn's day is saved as one delimited string of defNames.
    private List<Pawn> _scribePawns = [];
    private List<string> _scribeRows = [];

    public int GeneratedTick => _generatedTick;

    public bool IsEmpty => _slots.Count == 0;

    public IEnumerable<Pawn> Pawns => _slots.Keys;

    public IReadOnlyDictionary<Pawn, string> Notes => _notes;

    public void MarkGenerated() => _generatedTick = Find.TickManager.TicksGame;

    public WorkTypeDef? At(Pawn pawn, int hour) =>
        _slots.TryGetValue(pawn, out var row) ? row[WrapHour(hour)] : null;

    public void Set(Pawn pawn, int hour, WorkTypeDef? workType)
    {
        if (!_slots.TryGetValue(pawn, out var row))
        {
            row = new WorkTypeDef?[HoursPerDay];
            _slots[pawn] = row;
        }
        row[WrapHour(hour)] = workType;
    }

    public WorkTypeDef?[] Row(Pawn pawn) =>
        _slots.TryGetValue(pawn, out var row) ? row : new WorkTypeDef?[HoursPerDay];

    public void EnsureRow(Pawn pawn)
    {
        if (!_slots.ContainsKey(pawn))
        {
            _slots[pawn] = new WorkTypeDef?[HoursPerDay];
        }
    }

    public int ScheduledHours(Pawn pawn) => Row(pawn).Count(w => w != null);

    /// <summary>How many pawns are on <paramref name="workType"/> at <paramref name="hour"/>.</summary>
    public int WorkerCount(WorkTypeDef workType, int hour)
    {
        var count = 0;
        foreach (var row in _slots.Values)
        {
            if (row[WrapHour(hour)] == workType)
            {
                count++;
            }
        }
        return count;
    }

    public void SetNote(Pawn pawn, string note) => _notes[pawn] = note;

    public string? NoteFor(Pawn pawn) => _notes.TryGetValue(pawn, out var note) ? note : null;

    public void Remove(Pawn pawn)
    {
        _slots.Remove(pawn);
        _notes.Remove(pawn);
    }

    /// <summary>Drops pawns who have left the colony, died, or stopped being managed.</summary>
    public void Prune(Func<Pawn, bool> stillManaged)
    {
        foreach (var pawn in _slots.Keys.ToList())
        {
            if (pawn == null)
            {
                _slots.Remove(pawn!);
                continue;
            }

            if (pawn.Destroyed || !stillManaged(pawn))
            {
                Remove(pawn);
            }
        }
    }

    /// <summary>A compact "6-10 Mining, 10-14 Hauling" summary of one pawn's day.</summary>
    public string DaySummary(Pawn pawn)
    {
        var row = Row(pawn);
        var summary = new StringBuilder();
        var hour = 0;

        while (hour < HoursPerDay)
        {
            var workType = row[hour];
            if (workType == null)
            {
                hour++;
                continue;
            }

            var start = hour;
            while (hour < HoursPerDay && row[hour] == workType)
            {
                hour++;
            }

            if (summary.Length > 0)
            {
                summary.Append(", ");
            }
            summary.Append($"{start}h-{hour}h {workType.gerundLabel ?? workType.labelShort ?? workType.label}");
        }

        return summary.Length > 0 ? summary.ToString() : "WorkManager.Plan.NoHours".Translate();
    }

    private static int WrapHour(int hour) => ((hour % HoursPerDay) + HoursPerDay) % HoursPerDay;

    public void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving)
        {
            _scribePawns = [];
            _scribeRows = [];
            foreach (var (pawn, row) in _slots)
            {
                if (pawn == null)
                {
                    continue;
                }
                _scribePawns.Add(pawn);
                _scribeRows.Add(string.Join("|", row.Select(w => w?.defName ?? "-")));
            }
        }

        Scribe_Values.Look(ref _generatedTick, "generatedTick", -1);
        Scribe_Collections.Look(ref _scribePawns, "planPawns", LookMode.Reference);
        Scribe_Collections.Look(ref _scribeRows, "planRows", LookMode.Value);
        Scribe_Collections.Look(ref _notes, "notes", LookMode.Reference, LookMode.Value);

        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            _slots = [];
            _notes ??= [];
            _scribePawns ??= [];
            _scribeRows ??= [];

            for (var i = 0; i < _scribePawns.Count && i < _scribeRows.Count; i++)
            {
                var pawn = _scribePawns[i];
                if (pawn == null)
                {
                    continue;
                }

                var row = new WorkTypeDef?[HoursPerDay];
                var parts = _scribeRows[i].Split('|');
                for (var hour = 0; hour < HoursPerDay && hour < parts.Length; hour++)
                {
                    row[hour] =
                        parts[hour] == "-"
                            ? null
                            : DefDatabase<WorkTypeDef>.GetNamedSilentFail(parts[hour]);
                }
                _slots[pawn] = row;
            }

            _scribePawns.Clear();
            _scribeRows.Clear();
        }
    }
}
