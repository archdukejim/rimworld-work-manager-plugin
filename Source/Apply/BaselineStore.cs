// BaselineStore.cs
// Copyright (c) 2026 archdukejim

namespace WorkManager;

/// <summary>
/// Each managed pawn's own work priorities, captured before the manager started rewriting them.
/// </summary>
/// <remarks>
/// This is what "falls back to a default priority mode" means in practice: when a pawn has used up
/// their scheduled hours, or the plan has nothing for them, their own priorities go back in place
/// rather than leaving them with an empty work tab. It's also what gets restored when a pawn stops
/// being managed or the job is deleted.
/// </remarks>
public sealed class BaselineStore : IExposable
{
    private Dictionary<Pawn, string> _encoded = [];

    public bool Has(Pawn pawn) => _encoded.ContainsKey(pawn);

    /// <summary>Records the pawn's current priorities, replacing anything already stored.</summary>
    public void Capture(Pawn pawn)
    {
        if (pawn.workSettings == null || !pawn.workSettings.EverWork)
        {
            return;
        }

        var parts = new List<string>();
        foreach (var workType in DefDatabase<WorkTypeDef>.AllDefsListForReading)
        {
            if (pawn.WorkTypeIsDisabled(workType))
            {
                continue;
            }
            parts.Add($"{workType.defName}:{pawn.workSettings.GetPriority(workType)}");
        }

        _encoded[pawn] = string.Join(";", parts);
    }

    /// <summary>Records priorities only if this pawn has never been captured.</summary>
    public void CaptureIfNew(Pawn pawn)
    {
        if (!Has(pawn))
        {
            Capture(pawn);
        }
    }

    public void Forget(Pawn pawn) => _encoded.Remove(pawn);

    /// <summary>The pawn's stored priority for a work type, or null when nothing was captured.</summary>
    public int? PriorityFor(Pawn pawn, WorkTypeDef workType)
    {
        if (!_encoded.TryGetValue(pawn, out var encoded))
        {
            return null;
        }

        foreach (var entry in encoded.Split(';'))
        {
            var split = entry.IndexOf(':');
            if (split <= 0 || entry.Substring(0, split) != workType.defName)
            {
                continue;
            }

            if (int.TryParse(entry.Substring(split + 1), out var priority))
            {
                return priority;
            }
        }

        return null;
    }

    /// <summary>Puts the pawn's own priorities back exactly as they were.</summary>
    public void Restore(Pawn pawn)
    {
        if (pawn.workSettings == null || !_encoded.TryGetValue(pawn, out var encoded))
        {
            return;
        }

        foreach (var entry in encoded.Split(';'))
        {
            var split = entry.IndexOf(':');
            if (split <= 0)
            {
                continue;
            }

            var workType = DefDatabase<WorkTypeDef>.GetNamedSilentFail(entry.Substring(0, split));
            if (
                workType == null
                || pawn.WorkTypeIsDisabled(workType)
                || !int.TryParse(entry.Substring(split + 1), out var priority)
            )
            {
                continue;
            }

            pawn.workSettings.SetPriority(workType, priority);
        }
    }

    public void ExposeData()
    {
        Scribe_Collections.Look(ref _encoded, "baselines", LookMode.Reference, LookMode.Value);
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            _encoded ??= [];
            foreach (var pawn in _encoded.Keys.Where(p => p == null).ToList())
            {
                _encoded.Remove(pawn);
            }
        }
    }
}
