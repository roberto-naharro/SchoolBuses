using System;
using System.Collections.Generic;
using ColossalFramework;
using SchoolBuses.Util;
using UnityEngine;

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
                RouteBuilder.Result result = BuildNew(schoolId);
                MainThread(() => onComplete(result));
            });
        }

        // Refresh an existing mod-generated line from the current roster. We rebuild the
        // line's stops IN PLACE (clear + re-add) rather than deleting and recreating it, so
        // the line keeps its name, colour, vehicle, budget and its row in the transport line
        // list. Non-mod-generated lines are left untouched.
        internal static void Regenerate(ushort lineId, ushort schoolId, Action<RouteBuilder.Result> onComplete)
        {
            Singleton<SimulationManager>.instance.AddAction(() =>
            {
                Data.SchoolLineData existing;
                bool owned = Data.SchoolLineRegistry.TryGet(lineId, out existing) && existing.ModGenerated;

                RouteBuilder.Result result = owned
                    ? RebuildInPlace(lineId, schoolId)
                    : BuildNew(schoolId);

                MainThread(() => onComplete(result));
            });
        }

        // Roster scan + clustering → ordered stop list (incl. the school). Returns null and
        // sets `error` if no usable route can be planned.
        private static List<Vector3> PlanForSchool(ushort schoolId, out string error,
            out Data.RouteMetrics.GenRecord metrics)
        {
            error = null;
            metrics = default(Data.RouteMetrics.GenRecord);

            if (!EducationBuildingUtil.IsSchool(schoolId))
            {
                error = "Not a K–12 school";
                return null;
            }

            List<ushort> homes = EducationBuildingUtil.GetStudentHomeBuildings(schoolId);
            Log.DebugLog("Roster scan: school " + schoolId + " has " + homes.Count + " student home positions");
            if (homes.Count == 0)
            {
                error = "School has no enrolled students yet";
                return null;
            }

            var stops = RoutePlanner.PlanStops(
                schoolId, homes,
                Settings.Instance.ClusterRadius,
                Settings.Instance.MinClusterStudents,
                Settings.Instance.DynamicStopCount,
                out metrics);

            Log.DebugLog("Planner produced " + (stops == null ? 0 : stops.Count) + " ordered stops (incl. school)");
            if (stops == null || stops.Count < 2)
            {
                error = "Could not plan a usable route";
                return null;
            }
            return stops;
        }

        private static RouteBuilder.Result BuildNew(ushort schoolId)
        {
            try
            {
                string error;
                Data.RouteMetrics.GenRecord metrics;
                List<Vector3> stops = PlanForSchool(schoolId, out error, out metrics);
                if (stops == null)
                    return new RouteBuilder.Result { Error = error };

                RouteBuilder.Result result = RouteBuilder.Build(schoolId, stops);
                Log.DebugLog("RouteBuilder: success=" + result.Success + " line=" + result.LineId
                    + " noDepot=" + result.NoDepot + (result.Error != null ? " error=" + result.Error : ""));
                RecordAndSummarise(result, metrics, schoolId, "generate");
                return result;
            }
            catch (Exception ex)
            {
                Log.Error("Route generation failed: " + ex);
                return new RouteBuilder.Result { Error = "Internal error (see log)" };
            }
        }

        private static RouteBuilder.Result RebuildInPlace(ushort lineId, ushort schoolId)
        {
            try
            {
                string error;
                Data.RouteMetrics.GenRecord metrics;
                List<Vector3> stops = PlanForSchool(schoolId, out error, out metrics);
                if (stops == null)
                    return new RouteBuilder.Result { Error = error };

                RouteBuilder.Result result = RouteBuilder.RebuildStops(lineId, schoolId, stops);
                Log.DebugLog("RouteBuilder.RebuildStops: success=" + result.Success + " line=" + result.LineId
                    + " noDepot=" + result.NoDepot + (result.Error != null ? " error=" + result.Error : ""));
                RecordAndSummarise(result, metrics, schoolId, "regenerate");
                return result;
            }
            catch (Exception ex)
            {
                Log.Error("Route regeneration failed: " + ex);
                return new RouteBuilder.Result { Error = "Internal error (see log)" };
            }
        }

        // Store the generation parameters on the line (so usage can be correlated later) and emit
        // one consolidated, human-readable summary line for the test logs.
        private static void RecordAndSummarise(RouteBuilder.Result result,
            Data.RouteMetrics.GenRecord metrics, ushort schoolId, string action)
        {
            if (!result.Success)
                return;

            Data.RouteMetrics.Record(result.LineId, metrics);

            string mode = metrics.Dynamic ? "auto" : "manual";
            string fitness = float.IsNaN(metrics.Fitness) ? "n/a" : metrics.Fitness.ToString("0.000");
            Log.Info("GEN summary [" + action + "]: line " + result.LineId + " school " + schoolId
                + " (" + mode + ") | " + metrics.Considered + " students considered, "
                + metrics.Excluded + " walk-excluded, cap " + metrics.Capacity
                + " | radius " + Mathf.RoundToInt(metrics.Radius) + "m, min " + metrics.MinStudents
                + " -> " + metrics.Stops + " pickup stops, " + metrics.Covered + " covered ("
                + Mathf.RoundToInt(metrics.Coverage * 100f) + "%), fitness " + fitness
                + (result.NoDepot ? " | NO BUS DEPOT in area" : ""));
        }

        private static void MainThread(Action action)
        {
            Singleton<SimulationManager>.instance.m_ThreadingWrapper.QueueMainThread(action);
        }
    }
}
