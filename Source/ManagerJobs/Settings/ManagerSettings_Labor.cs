// ManagerSettings_Labor.cs
// Copyright (c) 2026 archdukejim

using ColonyManagerRedux;
using static ColonyManagerRedux.Constants;

namespace WorkManager;

/// <summary>Defaults applied to new labor jobs, edited from Colony Manager Redux's settings.</summary>
public sealed class ManagerSettings_Labor : ManagerSettings
{
    /// <summary>Fraction of target a stockpile falls to before labor is pulled back onto it.</summary>
    public float DefaultResumeBelow = 0.85f;

    /// <summary>Default hard cap on scheduled work hours per pawn per day.</summary>
    public int DefaultMaxWorkHours = 10;

    /// <summary>Write schedules into Enhanced Work Tab when it's present.</summary>
    public bool PreferEnhancedWorkTab = true;

    public override void DoTabContents(Rect rect)
    {
        var panelRect = new Rect(rect.xMin, rect.yMin, rect.width, rect.height - Margin);

        Widgets_Section.BeginSectionColumn(
            panelRect,
            "Labor.Settings",
            out var position,
            out var width
        );
        Widgets_Section.Section(
            ref position,
            width,
            DrawDefaults,
            "WorkManager.Settings.Defaults".Translate()
        );
        Widgets_Section.EndSectionColumn("Labor.Settings", position);
    }

    private float DrawDefaults(Vector2 pos, float width)
    {
        var start = pos;
        var rowRect = new Rect(pos.x, pos.y, width, ListEntryHeight);

        DrawLabelledSlider(
            rowRect,
            "WorkManager.Settings.ResumeBelow".Translate(DefaultResumeBelow.ToStringPercent()),
            "WorkManager.Settings.ResumeBelow.Tip".Translate(),
            ref DefaultResumeBelow,
            0.1f,
            1f
        );
        pos.y += ListEntryHeight;

        rowRect.y = pos.y;
        var hours = (float)DefaultMaxWorkHours;
        DrawLabelledSlider(
            rowRect,
            "WorkManager.Settings.MaxWorkHours".Translate(DefaultMaxWorkHours),
            "WorkManager.Settings.MaxWorkHours.Tip".Translate(),
            ref hours,
            1f,
            16f
        );
        DefaultMaxWorkHours = Mathf.RoundToInt(hours);
        pos.y += ListEntryHeight;

        rowRect.y = pos.y;
        Utilities.DrawToggle(
            rowRect,
            "WorkManager.Settings.PreferEnhancedWorkTab".Translate(),
            "WorkManager.Settings.PreferEnhancedWorkTab.Tip".Translate(),
            ref PreferEnhancedWorkTab
        );
        pos.y += ListEntryHeight;

        return pos.y - start.y;
    }

    /// <summary>A label on the left, a slider on the right, in one list row.</summary>
    internal static void DrawLabelledSlider(
        Rect rect,
        string label,
        string tooltip,
        ref float value,
        float min,
        float max
    )
    {
        var labelRect = rect.LeftPart(0.6f);
        var sliderRect = rect.RightPart(0.37f);
        sliderRect.height = rect.height * 0.6f;
        sliderRect.y += rect.height * 0.2f;

        // Drop a label that would otherwise wrap onto a second line down to the tiny font, so it
        // stays on one line inside its row instead of spilling over the control beneath it.
        var previousFont = Text.Font;
        if (Text.CalcSize(label).x > labelRect.width)
        {
            Text.Font = GameFont.Tiny;
        }
        var labelSize = Text.CalcSize(label);
        var centeredLabel = new Rect(
            labelRect.x,
            labelRect.y + ((labelRect.height - labelSize.y) / 2f),
            labelRect.width,
            labelSize.y
        );
        Widgets.Label(centeredLabel, label);
        Text.Font = previousFont;

        if (!tooltip.NullOrEmpty())
        {
            TooltipHandler.TipRegion(rect, tooltip);
        }
        value = Widgets.HorizontalSlider(sliderRect, value, min, max);
    }

    public override void ExposeData()
    {
        base.ExposeData();

        Scribe_Values.Look(ref DefaultResumeBelow, "defaultResumeBelow", 0.85f);
        Scribe_Values.Look(ref DefaultMaxWorkHours, "defaultMaxWorkHours", 10);
        Scribe_Values.Look(ref PreferEnhancedWorkTab, "preferEnhancedWorkTab", true);
    }
}
