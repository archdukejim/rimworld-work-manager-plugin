// ManagerTab_Labor.Schedule.cs
// Copyright (c) 2026 archdukejim

using static ColonyManagerRedux.Constants;

namespace WorkManager;

/// <summary>Demand rows and the schedule grid: the part of the tab that shows what the manager
/// decided and why.</summary>
public sealed partial class ManagerTab_Labor
{
    private const float PawnNameWidth = 90f;
    private const float ScheduleRowHeight = 18f;

    private static readonly Dictionary<WorkTypeDef, Color> WorkTypeColors = [];

    private float DrawDemands(ManagerJob_Labor job, Vector2 pos, float width)
    {
        var start = pos;

        if (_demandPreview.Count == 0)
        {
            var emptyText = "WorkManager.Demands.Empty".Translate();
            var emptyHeight = Text.CalcHeight(emptyText, width);
            GUI.color = Color.gray;
            Widgets.Label(new Rect(pos.x, pos.y, width, emptyHeight), emptyText);
            GUI.color = Color.white;
            pos.y += emptyHeight;
        }

        foreach (var demand in _demandPreview)
        {
            pos.y += DrawDemandRow(job, demand, new Rect(pos.x, pos.y, width, ListEntryHeight * 2));
        }

        var addRect = new Rect(pos.x, pos.y, width, ListEntryHeight).ContractedBy(2f);
        if (Widgets.ButtonText(addRect, "WorkManager.Demands.AddStanding".Translate()))
        {
            OpenStandingDemandMenu(job);
        }
        TooltipHandler.TipRegion(addRect, "WorkManager.Demands.AddStanding.Tip".Translate());
        pos.y += ListEntryHeight;

        return pos.y - start.y;
    }

    private float DrawDemandRow(ManagerJob_Labor job, LaborDemand demand, Rect rect)
    {
        Widgets.DrawHighlightIfMouseover(rect);

        var swatch = new Rect(rect.x, rect.y + 3f, 6f, rect.height - 6f);
        Widgets.DrawBoxSolid(swatch, ColorFor(demand.WorkType));

        var body = new Rect(
            swatch.xMax + Margin,
            rect.y,
            rect.width - swatch.width - Margin,
            rect.height
        );
        var topRow = new Rect(body.x, body.y, body.width, body.height / 2f);
        var bottomRow = new Rect(body.x, topRow.yMax, body.width, body.height / 2f);

        var labelRect = topRow.LeftPart(0.55f);
        Widgets.Label(labelRect, demand.Label);

        var statusRect = topRow.RightPart(0.43f);
        var previousFont = Text.Font;
        Text.Font = GameFont.Tiny;
        GUI.color = demand.Active ? Color.white : new Color(0.6f, 0.6f, 0.6f);
        Widgets.Label(statusRect, demand.StatusLine());
        GUI.color = Color.white;
        Text.Font = previousFont;

        TooltipHandler.TipRegion(
            rect,
            "WorkManager.Demands.Tip".Translate(
                demand.WorkType.gerundLabel.CapitalizeFirst(),
                demand.Weight.ToString("0.00"),
                demand.Active
                    ? "WorkManager.Log.Active".Translate()
                    : "WorkManager.Log.Satisfied".Translate()
            )
        );

        // Criticality on the left, worker bounds on the right.
        var config = demand.Config;
        var criticalityRect = bottomRow.LeftPart(0.55f);
        var criticality = config.Criticality;
        ManagerSettings_Labor.DrawLabelledSlider(
            criticalityRect,
            "WorkManager.Demands.Criticality".Translate(criticality.ToString("0.0")),
            "WorkManager.Demands.Criticality.Tip".Translate(),
            ref criticality,
            0f,
            3f
        );
        if (!Mathf.Approximately(criticality, config.Criticality))
        {
            config.Criticality = criticality;
            job.InvalidateApplication();
        }

        var boundsRect = bottomRow.RightPart(0.43f);
        var minRect = boundsRect.LeftHalf().ContractedBy(1f);
        var maxRect = boundsRect.RightHalf().ContractedBy(1f);

        if (
            Widgets.ButtonText(
                minRect,
                "WorkManager.Demands.MinWorkers".Translate(config.MinWorkers)
            )
        )
        {
            config.MinWorkers = NextBound(config.MinWorkers);
            job.InvalidateApplication();
        }
        TooltipHandler.TipRegion(minRect, "WorkManager.Demands.MinWorkers.Tip".Translate());

        if (
            Widgets.ButtonText(
                maxRect,
                config.MaxWorkers == 0
                    ? "WorkManager.Demands.MaxWorkersAny".Translate()
                    : "WorkManager.Demands.MaxWorkers".Translate(config.MaxWorkers)
            )
        )
        {
            config.MaxWorkers = NextBound(config.MaxWorkers);
            job.InvalidateApplication();
        }
        TooltipHandler.TipRegion(maxRect, "WorkManager.Demands.MaxWorkers.Tip".Translate());

        if (Event.current.type == EventType.MouseDown && Event.current.button == 1
            && rect.Contains(Event.current.mousePosition))
        {
            OpenDemandContextMenu(job, demand);
            Event.current.Use();
        }

        return rect.height;
    }

    /// <summary>Cycles a worker bound through 0-4, which covers every colony size worth clicking for.</summary>
    private static int NextBound(int current) => current >= 4 ? 0 : current + 1;

    private void OpenDemandContextMenu(ManagerJob_Labor job, LaborDemand demand)
    {
        var options = new List<FloatMenuOption>();
        var config = demand.Config;

        foreach (var pawn in job.ManagedPawns.OrderBy(p => p.LabelShortCap))
        {
            var pinned = config.PinnedPawns.Contains(pawn);
            options.Add(
                new FloatMenuOption(
                    pinned
                        ? "WorkManager.Demands.Unpin".Translate(pawn.LabelShortCap)
                        : "WorkManager.Demands.Pin".Translate(pawn.LabelShortCap),
                    () =>
                    {
                        if (pinned)
                        {
                            config.PinnedPawns.Remove(pawn);
                        }
                        else
                        {
                            config.PinnedPawns.Add(pawn);
                        }
                        job.InvalidateApplication();
                    }
                )
            );
        }

        options.Add(
            new FloatMenuOption(
                config.Enabled
                    ? "WorkManager.Demands.Disable".Translate()
                    : "WorkManager.Demands.Enable".Translate(),
                () =>
                {
                    config.Enabled = !config.Enabled;
                    job.InvalidateApplication();
                }
            )
        );

        if (config.Standing)
        {
            options.Add(
                new FloatMenuOption(
                    "WorkManager.Demands.RemoveStanding".Translate(),
                    () =>
                    {
                        job.Demands.RemoveStandingDemand(demand.WorkType);
                        _previewRefreshedTick = -1;
                    }
                )
            );
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }

    private void OpenStandingDemandMenu(ManagerJob_Labor job)
    {
        var existing = _demandPreview.Select(d => d.WorkType).ToHashSet();
        var options = new List<FloatMenuOption>();

        foreach (
            var workType in DefDatabase<WorkTypeDef>
                .AllDefsListForReading.Where(w => w.visible && !existing.Contains(w))
                .OrderByDescending(w => w.naturalPriority)
        )
        {
            options.Add(
                new FloatMenuOption(
                    workType.gerundLabel.CapitalizeFirst(),
                    () =>
                    {
                        job.Demands.AddStandingDemand(workType);
                        _previewRefreshedTick = -1;
                        job.InvalidateApplication();
                    }
                )
            );
        }

        if (options.Count == 0)
        {
            options.Add(
                new FloatMenuOption("WorkManager.Demands.NothingToAdd".Translate(), null)
            );
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }

    /// <summary>The schedule grid: one row per scheduled pawn, one cell per hour.</summary>
    private float DrawSchedule(ManagerJob_Labor job, Vector2 pos, float width)
    {
        var start = pos;
        var plan = job.Plan;
        var pawns = job
            .ManagedPawns.Where(p => plan.ScheduledHours(p) > 0)
            .OrderBy(p => p.LabelShortCap)
            .ToList();

        if (pawns.Count == 0)
        {
            var emptyText = "WorkManager.Schedule.Empty".Translate();
            var emptyHeight = Text.CalcHeight(emptyText, width);
            GUI.color = Color.gray;
            Widgets.Label(new Rect(pos.x, pos.y, width, emptyHeight), emptyText);
            GUI.color = Color.white;
            return emptyHeight;
        }

        var gridWidth = width - PawnNameWidth;
        var cellWidth = gridWidth / LaborPlan.HoursPerDay;
        var currentHour = GenLocalDate.HourOfDay((Map)Manager);

        // Hour ruler.
        var rulerRect = new Rect(pos.x + PawnNameWidth, pos.y, gridWidth, ScheduleRowHeight);
        var previousFont = Text.Font;
        Text.Font = GameFont.Tiny;
        GUI.color = Color.gray;
        for (var hour = 0; hour < LaborPlan.HoursPerDay; hour += 3)
        {
            var tickRect = new Rect(
                rulerRect.x + (hour * cellWidth),
                rulerRect.y,
                cellWidth * 3,
                rulerRect.height
            );
            Widgets.Label(tickRect, hour.ToString());
        }
        GUI.color = Color.white;
        Text.Font = previousFont;
        pos.y += ScheduleRowHeight;

        foreach (var pawn in pawns)
        {
            var rowRect = new Rect(pos.x, pos.y, width, ScheduleRowHeight);
            Widgets.DrawHighlightIfMouseover(rowRect);

            var nameRect = new Rect(pos.x, pos.y, PawnNameWidth, ScheduleRowHeight);
            Text.Font = GameFont.Tiny;
            Widgets.Label(nameRect, pawn.LabelShortCap);
            Text.Font = previousFont;

            for (var hour = 0; hour < LaborPlan.HoursPerDay; hour++)
            {
                var cellRect = new Rect(
                    pos.x + PawnNameWidth + (hour * cellWidth),
                    pos.y + 1f,
                    cellWidth - 1f,
                    ScheduleRowHeight - 2f
                );

                var workType = plan.At(pawn, hour);
                Widgets.DrawBoxSolid(
                    cellRect,
                    workType == null ? new Color(0.2f, 0.2f, 0.2f, 0.5f) : ColorFor(workType)
                );

                if (hour == currentHour)
                {
                    Widgets.DrawBox(cellRect, 1);
                }

                if (Mouse.IsOver(cellRect))
                {
                    TooltipHandler.TipRegion(
                        cellRect,
                        workType == null
                            ? "WorkManager.Schedule.CellIdle".Translate(hour)
                            : "WorkManager.Schedule.Cell".Translate(
                                hour,
                                workType.gerundLabel.CapitalizeFirst()
                            )
                    );
                }
            }

            if (Widgets.ButtonInvisible(nameRect))
            {
                _selectedPawn = _selectedPawn == pawn ? null : pawn;
            }

            var note = plan.NoteFor(pawn);
            if (note != null)
            {
                TooltipHandler.TipRegion(nameRect, plan.DaySummary(pawn) + "\n\n" + note);
            }
            else
            {
                TooltipHandler.TipRegion(nameRect, plan.DaySummary(pawn));
            }

            pos.y += ScheduleRowHeight;
        }

        return pos.y - start.y;
    }

    /// <summary>A stable colour per work type, derived from its defName so it never shifts.</summary>
    private static Color ColorFor(WorkTypeDef workType)
    {
        if (WorkTypeColors.TryGetValue(workType, out var color))
        {
            return color;
        }

        var hash = 0;
        foreach (var character in workType.defName)
        {
            hash = (hash * 31) + character;
        }

        color = Color.HSVToRGB(Mathf.Abs(hash % 360) / 360f, 0.55f, 0.75f);
        WorkTypeColors[workType] = color;
        return color;
    }
}
