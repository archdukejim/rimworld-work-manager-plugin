// PawnAvailability.cs
// Copyright (c) 2026 archdukejim

namespace WorkManager;

/// <summary>When a pawn can be scheduled, how many of those hours they should actually be worked,
/// and why that number isn't the full day.</summary>
public sealed class PawnAvailability
{
    public const int HoursPerDay = 24;

    public required Pawn Pawn { get; init; }

    /// <summary>Hours the pawn's timetable leaves open for work.</summary>
    public required bool[] OpenHours { get; init; }

    /// <summary>Hard cap on scheduled hours, after well-being has taken its cut.</summary>
    public required int MaxHours { get; init; }

    /// <summary>Hours well-being took off the cap, if any.</summary>
    public int HoursGivenBack { get; init; }

    /// <summary>Player-facing explanation of the cap, or null when nothing unusual is going on.</summary>
    public string? Note { get; init; }

    /// <summary>True when the pawn shouldn't be scheduled at all right now.</summary>
    public bool Unavailable => MaxHours <= 0;

    public int OpenHourCount => OpenHours.Count(h => h);
}

/// <summary>
/// Works out each pawn's schedulable hours from their own timetable, then takes hours back off the
/// cap when their mood says they need them more than the colony needs the work.
/// </summary>
public static class AvailabilityModel
{
    /// <summary>Fallback waking hours when a pawn has no timetable of their own.</summary>
    private const int DefaultWakeHour = 6;
    private const int DefaultSleepHour = 22;

    public static PawnAvailability For(Pawn pawn, in EffectiveOptions options)
    {
        var open = OpenHours(pawn, options.Flag(LaborOption.RespectTimetable));
        var maxHours = Mathf.Min(options.Int(LaborOption.MaxWorkHours), CountTrue(open));

        if (!Schedulable(pawn))
        {
            return new PawnAvailability
            {
                Pawn = pawn,
                OpenHours = open,
                MaxHours = 0,
                Note = "WorkManager.Availability.NotSchedulable".Translate(),
            };
        }

        var givenBack = 0;
        string? note = null;

        if (options.Flag(LaborOption.WellBeingCare))
        {
            var mood = pawn.needs?.mood?.CurLevelPercentage ?? 1f;
            var floor = options.Get(LaborOption.MoodFloor);
            if (mood < floor)
            {
                // Scale the cut by how far under the floor they are, so a pawn on the edge of a
                // break gets the full allowance and a mildly grumpy one gets an hour.
                var shortfall = floor > 0f ? Mathf.Clamp01((floor - mood) / floor) : 1f;
                givenBack = Mathf.CeilToInt(options.Int(LaborOption.WellBeingHours) * shortfall);
                maxHours = Mathf.Max(0, maxHours - givenBack);
                note = "WorkManager.Availability.MoodCut".Translate(
                    mood.ToStringPercent(),
                    givenBack
                );
            }
        }

        return new PawnAvailability
        {
            Pawn = pawn,
            OpenHours = open,
            MaxHours = maxHours,
            HoursGivenBack = givenBack,
            Note = note,
        };
    }

    /// <summary>A pawn who can't take orders right now shouldn't be in the plan at all.</summary>
    private static bool Schedulable(Pawn pawn) =>
        pawn.Spawned
        && !pawn.Dead
        && !pawn.Downed
        && !pawn.InMentalState
        && !pawn.IsPrisoner
        && pawn.workSettings != null
        && pawn.workSettings.EverWork;

    private static bool[] OpenHours(Pawn pawn, bool respectTimetable)
    {
        var open = new bool[PawnAvailability.HoursPerDay];
        var timetable = pawn.timetable;

        for (var hour = 0; hour < PawnAvailability.HoursPerDay; hour++)
        {
            if (!respectTimetable)
            {
                open[hour] = hour is >= DefaultWakeHour and < DefaultSleepHour;
                continue;
            }

            if (timetable == null)
            {
                open[hour] = hour is >= DefaultWakeHour and < DefaultSleepHour;
                continue;
            }

            var assignment = timetable.GetAssignment(hour);
            open[hour] =
                assignment == TimeAssignmentDefOf.Work || assignment == TimeAssignmentDefOf.Anything;
        }

        return open;
    }

    private static int CountTrue(bool[] values)
    {
        var count = 0;
        foreach (var value in values)
        {
            if (value)
            {
                count++;
            }
        }
        return count;
    }
}
