// EnhancedWorkTabApplier.cs
// Copyright (c) 2026 archdukejim

using System.Reflection;

namespace WorkManager;

/// <summary>
/// Writes the whole day's schedule into Enhanced Work Tab's hour-aware priorities, so the plan is
/// visible in the work tab itself instead of only taking effect as the clock rolls over.
/// </summary>
/// <remarks>
/// Enhanced Work Tab isn't a build dependency, so this binds by reflection and refuses to run
/// unless it can identify the parameters by name. Guessing at an argument order would silently
/// write a garbage schedule, which is worse than falling back to the vanilla applier.
/// </remarks>
public sealed class EnhancedWorkTabApplier : IScheduleApplier
{
    private const string ComponentTypeName = "EnhancedWorkTab.EnhancedWorkTabGameComponent";
    private const string SetMethodName = "SetWorkTypePriority";

    private static bool _resolved;
    private static Type? _componentType;
    private static MethodInfo? _setMethod;
    private static int _pawnArg = -1;
    private static int _workTypeArg = -1;
    private static int _hourArg = -1;
    private static int _priorityArg = -1;
    private static string? _unavailableReason;

    public string Label => "WorkManager.Applier.EnhancedWorkTab".Translate();

    public bool Available
    {
        get
        {
            Resolve();
            return _setMethod != null && Component() != null;
        }
    }

    /// <summary>Why the integration is off, for the tab to show. Null when it's working.</summary>
    public static string? UnavailableReason
    {
        get
        {
            Resolve();
            return _unavailableReason;
        }
    }

    public void Apply(
        LaborPlan plan,
        int hour,
        BaselineStore baselines,
        Func<Pawn, EffectiveOptions> optionsFor
    )
    {
        var component = Component();
        if (_setMethod == null || component == null)
        {
            return;
        }

        var arguments = new object[4];

        foreach (var pawn in plan.Pawns.ToList())
        {
            if (pawn?.workSettings == null || !pawn.workSettings.EverWork || pawn.Destroyed)
            {
                continue;
            }

            baselines.CaptureIfNew(pawn);
            var neverIdle = optionsFor(pawn).Flag(LaborOption.NeverIdle);

            for (var planHour = 0; planHour < LaborPlan.HoursPerDay; planHour++)
            {
                var primary = plan.At(pawn, planHour);

                foreach (var workType in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                {
                    if (pawn.WorkTypeIsDisabled(workType))
                    {
                        continue;
                    }

                    var baseline = baselines.PriorityFor(pawn, workType);
                    var priority = SchedulePriorities.For(workType, primary, baseline, neverIdle);

                    arguments[_pawnArg] = pawn;
                    arguments[_workTypeArg] = workType;
                    arguments[_hourArg] = planHour;
                    arguments[_priorityArg] = priority;

                    try
                    {
                        _setMethod.Invoke(component, arguments);
                    }
                    catch (Exception exception)
                    {
                        _setMethod = null;
                        _unavailableReason =
                            "Enhanced Work Tab rejected a schedule write: " + exception.Message;
                        WorkManagerLog.Warning(_unavailableReason);
                        return;
                    }
                }
            }
        }
    }

    public void Release(Pawn pawn, BaselineStore baselines) => baselines.Restore(pawn);

    private static object? Component()
    {
        Resolve();
        return _componentType == null ? null : Current.Game?.GetComponent(_componentType);
    }

    private static void Resolve()
    {
        if (_resolved)
        {
            return;
        }
        _resolved = true;

        _componentType = GenTypes.GetTypeInAnyAssembly(ComponentTypeName);
        if (_componentType == null)
        {
            _unavailableReason = "Enhanced Work Tab is not loaded.";
            return;
        }

        foreach (
            var method in _componentType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
        )
        {
            if (method.Name != SetMethodName)
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length != 4)
            {
                continue;
            }

            var pawnArg = -1;
            var workTypeArg = -1;
            var hourArg = -1;
            var priorityArg = -1;

            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var name = parameter.Name?.ToLowerInvariant() ?? string.Empty;

                if (parameter.ParameterType == typeof(Pawn))
                {
                    pawnArg = i;
                }
                else if (parameter.ParameterType == typeof(WorkTypeDef))
                {
                    workTypeArg = i;
                }
                else if (parameter.ParameterType == typeof(int) && name.Contains("hour"))
                {
                    hourArg = i;
                }
                else if (parameter.ParameterType == typeof(int) && name.Contains("priorit"))
                {
                    priorityArg = i;
                }
            }

            if (pawnArg < 0 || workTypeArg < 0 || hourArg < 0 || priorityArg < 0)
            {
                continue;
            }

            _setMethod = method;
            _pawnArg = pawnArg;
            _workTypeArg = workTypeArg;
            _hourArg = hourArg;
            _priorityArg = priorityArg;
            return;
        }

        _unavailableReason =
            $"Found {ComponentTypeName} but no {SetMethodName}(pawn, workType, hour, priority) "
            + "whose parameters could be identified by name; not risking a wrong argument order.";
        WorkManagerLog.Warning(_unavailableReason);
    }
}
