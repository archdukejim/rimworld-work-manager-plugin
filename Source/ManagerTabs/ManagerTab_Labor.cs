// ManagerTab_Labor.cs
// Copyright (c) 2026 archdukejim

using ColonyManagerRedux;
using static ColonyManagerRedux.Constants;

namespace WorkManager;

/// <summary>
/// The labor tab. Left column configures the plan; right column shows what the colony is short of
/// and the schedule that came out of it.
/// </summary>
public sealed partial class ManagerTab_Labor : ManagerTab<ManagerJob_Labor, ManagerSettings_Labor>
{
    private const string OptionsColumn = "Labor.Options";
    private const string DetailColumn = "Labor.Detail";

    /// <summary>The pawn whose overrides the options column is editing, or null for the colony.</summary>
    private Pawn? _selectedPawn;

    private List<LaborDemand> _demandPreview = [];
    private int _previewRefreshedTick = -1;

    public ManagerTab_Labor(Manager manager)
        : base(manager) { }

    public ManagerJob_Labor SelectedLaborJob => SelectedJob!;

    protected override void DoMainContent(Rect rect)
    {
        Widgets.DrawMenuSection(rect);

        var optionsRect = new Rect(
            rect.xMin,
            rect.yMin,
            (rect.width * 0.5f) - Margin,
            rect.height - Margin - ButtonSize.y
        );
        var detailRect = new Rect(
            optionsRect.xMax + Margin,
            rect.yMin,
            rect.width - optionsRect.width - Margin,
            rect.height - Margin - ButtonSize.y
        );
        var buttonRect = new Rect(
            rect.xMax - ButtonSize.x,
            rect.yMax - ButtonSize.y,
            ButtonSize.x - Margin,
            ButtonSize.y - Margin
        );

        RefreshPreview();

        Widgets_Section.BeginSectionColumn(
            optionsRect,
            OptionsColumn,
            out var position,
            out var width
        );
        DrawSection(
            OptionsColumn,
            "Status",
            ref position,
            width,
            DrawStatus,
            "WorkManager.Section.Status".Translate()
        );
        DrawSection(
            OptionsColumn,
            "Hysteresis",
            ref position,
            width,
            DrawHysteresis,
            "WorkManager.Section.Hysteresis".Translate()
        );
        DrawSection(
            OptionsColumn,
            "Options",
            ref position,
            width,
            DrawOptions,
            "WorkManager.Section.Options".Translate()
        );
        DrawSection(
            OptionsColumn,
            "Pawns",
            ref position,
            width,
            DrawPawnList,
            "WorkManager.Section.Pawns".Translate()
        );
        Widgets_Section.EndSectionColumn(OptionsColumn, position);

        Widgets_Section.BeginSectionColumn(detailRect, DetailColumn, out position, out width);
        DrawSection(
            DetailColumn,
            "Demands",
            ref position,
            width,
            DrawDemands,
            "WorkManager.Section.Demands".Translate()
        );
        DrawSection(
            DetailColumn,
            "Schedule",
            ref position,
            width,
            DrawSchedule,
            "WorkManager.Section.Schedule".Translate()
        );
        Widgets_Section.EndSectionColumn(DetailColumn, position);

        DrawManageButton(buttonRect);
    }

    private void DrawManageButton(Rect buttonRect)
    {
        var job = SelectedLaborJob;

        if (!job.IsManaged)
        {
            if (Widgets.ButtonText(buttonRect, "ColonyManagerRedux.Common.Manage".Translate()))
            {
                job.IsManaged = true;
                Manager.JobTracker.Add(job);
                Refresh();
            }
            return;
        }

        if (Widgets.ButtonText(buttonRect, "ColonyManagerRedux.Common.Delete".Translate()))
        {
            // Deleting has to hand every managed pawn back their own priorities, which CleanUp does.
            Manager.JobTracker.Delete(job);
            Selected = MakeNewJob();
            Refresh();
        }
    }

    /// <summary>
    /// Re-reads the demand rows for display roughly once a second. This is the read-only path: it
    /// never advances the rate samples or the hysteresis latches.
    /// </summary>
    private void RefreshPreview()
    {
        var now = Find.TickManager.TicksGame;
        if (_previewRefreshedTick > 0 && now - _previewRefreshedTick < 60)
        {
            return;
        }

        _previewRefreshedTick = now;
        _demandPreview = SelectedLaborJob.Demands.Evaluate(Manager, SelectedLaborJob, mutate: false);
    }

    private float DrawStatus(ManagerJob_Labor job, Vector2 pos, float width)
    {
        var start = pos;
        var rowRect = new Rect(pos.x, pos.y, width, ListEntryHeight);

        var generated = job.Plan.GeneratedTick;
        Widgets.Label(
            rowRect,
            generated < 0
                ? "WorkManager.Status.NoPlan".Translate()
                : "WorkManager.Status.PlanAge".Translate(
                    (Find.TickManager.TicksGame - generated).ToStringTicksToPeriod()
                )
        );
        pos.y += ListEntryHeight;

        rowRect.y = pos.y;
        Widgets.Label(rowRect, "WorkManager.Status.Applier".Translate(job.Applier.Label));
        if (EnhancedWorkTabApplier.UnavailableReason is string reason)
        {
            TooltipHandler.TipRegion(rowRect, reason);
        }
        pos.y += ListEntryHeight;

        rowRect.y = pos.y;
        var useEnhanced = job.PreferEnhancedWorkTab;
        Widgets.CheckboxLabeled(
            rowRect,
            "WorkManager.Status.UseEnhancedWorkTab".Translate(),
            ref useEnhanced
        );
        TooltipHandler.TipRegion(rowRect, "WorkManager.Status.UseEnhancedWorkTab.Tip".Translate());
        if (useEnhanced != job.PreferEnhancedWorkTab)
        {
            job.PreferEnhancedWorkTab = useEnhanced;
            job.InvalidateApplication();
        }
        pos.y += ListEntryHeight;

        rowRect.y = pos.y;
        var planNowRect = rowRect.LeftHalf().ContractedBy(2f);
        var baselineRect = rowRect.RightHalf().ContractedBy(2f);

        if (Widgets.ButtonText(planNowRect, "WorkManager.Status.PlanNow".Translate()))
        {
            // Untouch makes the manager treat the job as overdue, so it runs on the next pass.
            job.Untouch();
        }
        TooltipHandler.TipRegion(planNowRect, "WorkManager.Status.PlanNow.Tip".Translate());

        if (Widgets.ButtonText(baselineRect, "WorkManager.Status.RecaptureBaseline".Translate()))
        {
            foreach (var pawn in job.ManagedPawns)
            {
                job.Baselines.Capture(pawn);
            }
            job.InvalidateApplication();
        }
        TooltipHandler.TipRegion(
            baselineRect,
            "WorkManager.Status.RecaptureBaseline.Tip".Translate()
        );
        pos.y += ListEntryHeight;

        return pos.y - start.y;
    }

    private float DrawHysteresis(ManagerJob_Labor job, Vector2 pos, float width)
    {
        var start = pos;
        var rowRect = new Rect(pos.x, pos.y, width, ListEntryHeight);

        ManagerSettings_Labor.DrawLabelledSlider(
            rowRect,
            "WorkManager.Hysteresis.ResumeBelow".Translate(
                job.Demands.ResumeBelow.ToStringPercent()
            ),
            "WorkManager.Hysteresis.ResumeBelow.Tip".Translate(),
            ref job.Demands.ResumeBelow,
            0.1f,
            1f
        );
        pos.y += ListEntryHeight;

        var explanationRect = new Rect(pos.x, pos.y, width, ListEntryHeight * 1.5f);
        var previous = Text.Font;
        Text.Font = GameFont.Tiny;
        GUI.color = Color.gray;
        Widgets.Label(
            explanationRect,
            "WorkManager.Hysteresis.Explanation".Translate(
                job.Demands.ResumeBelow.ToStringPercent()
            )
        );
        GUI.color = Color.white;
        Text.Font = previous;
        pos.y += explanationRect.height;

        return pos.y - start.y;
    }

    /// <summary>
    /// The option rows. When a pawn is selected in the pawn list, each row also gets a scope
    /// control so the value can be pinned to that pawn instead of the colony.
    /// </summary>
    private float DrawOptions(ManagerJob_Labor job, Vector2 pos, float width)
    {
        var start = pos;

        if (_selectedPawn != null && !job.ManagedPawns.Contains(_selectedPawn))
        {
            _selectedPawn = null;
        }

        var headerRect = new Rect(pos.x, pos.y, width, ListEntryHeight);
        Widgets.Label(
            headerRect,
            _selectedPawn == null
                ? "WorkManager.Options.EditingColony".Translate()
                : "WorkManager.Options.EditingPawn".Translate(_selectedPawn.LabelShortCap)
        );
        if (
            _selectedPawn != null
            && Widgets.ButtonText(
                headerRect.RightPart(0.35f),
                "WorkManager.Options.BackToColony".Translate()
            )
        )
        {
            _selectedPawn = null;
        }
        pos.y += ListEntryHeight;

        foreach (var meta in LaborOptions.All)
        {
            pos.y += DrawOptionRow(job, meta, new Rect(pos.x, pos.y, width, ListEntryHeight));
        }

        if (_selectedPawn != null && job.HasOverrides(_selectedPawn))
        {
            var clearRect = new Rect(pos.x, pos.y, width, ListEntryHeight).ContractedBy(2f);
            if (Widgets.ButtonText(clearRect, "WorkManager.Options.ClearOverrides".Translate()))
            {
                job.ClearOverrides(_selectedPawn);
            }
            pos.y += ListEntryHeight;
        }

        return pos.y - start.y;
    }

    private float DrawOptionRow(ManagerJob_Labor job, LaborOptionMeta meta, Rect rowRect)
    {
        var options = _selectedPawn == null
            ? job.ColonyEffectiveOptions
            : job.OptionsFor(_selectedPawn);
        var overridden = _selectedPawn != null && options.IsOverridden(meta.Option);
        var value = options.Get(meta.Option);

        Widgets.DrawHighlightIfMouseover(rowRect);
        TooltipHandler.TipRegion(rowRect, meta.TipKey.Translate());

        var controlRect = rowRect;

        // Per-pawn rows get a scope toggle: colony value, or this pawn's own.
        if (_selectedPawn != null)
        {
            var scopeRect = rowRect.RightPart(0.18f);
            controlRect = new Rect(
                rowRect.x,
                rowRect.y,
                rowRect.width - scopeRect.width - Margin,
                rowRect.height
            );

            if (
                Widgets.ButtonText(
                    scopeRect.ContractedBy(1f),
                    overridden
                        ? "WorkManager.Options.ScopePawn".Translate()
                        : "WorkManager.Options.ScopeColony".Translate()
                )
            )
            {
                if (overridden)
                {
                    job.OverridesFor(_selectedPawn).Clear(meta.Option);
                }
                else
                {
                    job.OverridesFor(_selectedPawn).Set(meta.Option, value);
                }
                job.InvalidateApplication();
                return rowRect.height;
            }
        }

        var changed = DrawOptionControl(meta, controlRect, ref value);

        if (changed)
        {
            if (_selectedPawn != null)
            {
                // Editing a value while a pawn is selected means "for this pawn", so the row
                // becomes an override rather than silently discarding the edit.
                job.OverridesFor(_selectedPawn).Set(meta.Option, value);
            }
            else
            {
                job.ColonyOptions.Set(meta.Option, value);
            }
            job.InvalidateApplication();
        }

        return rowRect.height;
    }

    /// <summary>Draws the right control for an option's kind. Returns true when the value moved.</summary>
    private static bool DrawOptionControl(LaborOptionMeta meta, Rect rect, ref float value)
    {
        var label = meta.LabelKey.Translate();

        if (meta.Kind == LaborOptionKind.Toggle)
        {
            var on = value > 0.5f;
            var before = on;
            Widgets.CheckboxLabeled(rect, label, ref on);
            if (on == before)
            {
                return false;
            }
            value = on ? 1f : 0f;
            return true;
        }

        var before2 = value;
        ManagerSettings_Labor.DrawLabelledSlider(
            rect,
            label + ": " + meta.Format(value),
            string.Empty,
            ref value,
            meta.Min,
            meta.Max
        );
        value = meta.Clamp(value);
        return !Mathf.Approximately(before2, value);
    }

    private float DrawPawnList(ManagerJob_Labor job, Vector2 pos, float width)
    {
        var start = pos;

        foreach (var pawn in ((Map)Manager).mapPawns.FreeColonists.OrderBy(p => p.LabelShortCap))
        {
            var rowRect = new Rect(pos.x, pos.y, width, ListEntryHeight);
            Widgets.DrawHighlightIfMouseover(rowRect);
            if (_selectedPawn == pawn)
            {
                Widgets.DrawHighlightSelected(rowRect);
            }

            var checkRect = rowRect.LeftPart(0.08f);
            var managed = job.IsManagedPawn(pawn);
            var before = managed;
            Widgets.Checkbox(checkRect.position, ref managed, checkRect.width);
            if (managed != before)
            {
                job.SetPawnManaged(pawn, managed);
            }

            var labelRect = new Rect(
                checkRect.xMax + Margin,
                rowRect.y,
                rowRect.width - checkRect.width - Margin,
                rowRect.height
            );
            var suffix = job.HasOverrides(pawn) ? " *" : string.Empty;
            var hours = job.Plan.ScheduledHours(pawn);
            Widgets.Label(
                labelRect,
                "WorkManager.Pawns.Row".Translate(pawn.LabelShortCap + suffix, hours)
            );

            var note = job.Plan.NoteFor(pawn);
            if (note != null)
            {
                TooltipHandler.TipRegion(rowRect, note);
            }

            if (Widgets.ButtonInvisible(labelRect))
            {
                _selectedPawn = _selectedPawn == pawn ? null : pawn;
            }

            pos.y += ListEntryHeight;
        }

        return pos.y - start.y;
    }

    public override void PreOpen() => Refresh();

    protected override void Refresh()
    {
        _previewRefreshedTick = -1;
        if (_selectedPawn != null && _selectedPawn.Destroyed)
        {
            _selectedPawn = null;
        }
    }
}
