using System;
using System.Collections.Generic;
using ColossalFramework;
using SchoolBuses.Util;

namespace SchoolBuses.Routing
{
    // Orchestrates one-click route generation: roster scan → plan → build, all on the
    // simulation thread, then marshals the result back to the main thread for the UI.
    internal static class RouteGenerator
    {
        // Generate a fresh line for a school. `onComplete` runs on the main thread.
        internal static void Generate(ushort schoolId, Action<RouteBuilder.Result> onComplete)
        {
            Singleton<SimulationManager>.instance.AddAction(() =>
            {
                RouteBuilder.Result result = BuildOnSimThread(schoolId);
                MainThread(() => onComplete(result));
            });
        }

        // Release an existing mod-generated line and build a replacement from the
        // current roster (design §8 Regenerate). Non-mod-generated lines are left
        // untouched (we only rebuild lines we own).
        internal static void Regenerate(ushort lineId, ushort schoolId, Action<RouteBuilder.Result> onComplete)
        {
            Singleton<SimulationManager>.instance.AddAction(() =>
            {
                Data.SchoolLineData existing;
                if (Data.SchoolLineRegistry.TryGet(lineId, out existing) && existing.ModGenerated)
                {
                    Singleton<TransportManager>.instance.ReleaseLine(lineId);
                    // ReleaseLine postfix unregisters it.
                }
                RouteBuilder.Result result = BuildOnSimThread(schoolId);
                MainThread(() => onComplete(result));
            });
        }

        private static RouteBuilder.Result BuildOnSimThread(ushort schoolId)
        {
            try
            {
                if (!EducationBuildingUtil.IsSchool(schoolId))
                    return new RouteBuilder.Result { Error = "Not a K–12 school" };

                List<ushort> homes = EducationBuildingUtil.GetStudentHomeBuildings(schoolId);
                if (homes.Count == 0)
                    return new RouteBuilder.Result { Error = "School has no enrolled students yet" };

                var stops = RoutePlanner.PlanStops(
                    schoolId, homes,
                    Settings.Instance.ClusterRadius,
                    Settings.Instance.MaxStops);

                if (stops == null || stops.Count < 2)
                    return new RouteBuilder.Result { Error = "Could not plan a usable route" };

                return RouteBuilder.Build(schoolId, stops);
            }
            catch (Exception ex)
            {
                Log.Error("Route generation failed: " + ex);
                return new RouteBuilder.Result { Error = "Internal error (see log)" };
            }
        }

        private static void MainThread(Action action)
        {
            Singleton<SimulationManager>.instance.m_ThreadingWrapper.QueueMainThread(action);
        }
    }
}
