using System;
using System.Collections.Generic;
using ColossalFramework;
using SchoolBuses.Util;
using UnityEngine;

namespace SchoolBuses.Routing
{
    // Orchestrates one-click route generation: roster scan → cluster → SWEEP into zones → build
    // one short one-bus loop per zone, all on the simulation thread, then marshals the aggregate
    // result back to the main thread for the UI. A small school yields one route; a big one yields
    // several short routes (like a real district), each pinned to one bus.
    internal static class RouteGenerator
    {
        // Generate the full set of routes for a school. `onComplete` runs on the main thread.
        internal static void Generate(ushort schoolId, Action<RouteBuilder.Result> onComplete)
        {
            Singleton<SimulationManager>.instance.AddAction(() =>
            {
                RouteBuilder.Result result = BuildSet(schoolId);
                MainThread(() => onComplete(result));
            });
        }

        // Regenerate a school: DELETE all of its existing mod-generated lines, then rebuild the
        // whole set from the current roster (the route count may change as the city grows/shrinks,
        // so an in-place per-line rebuild no longer fits). Releasing a line fires our ReleaseLine
        // patch, which unregisters it and clears its metrics/finaliser state.
        internal static void RegenerateSchool(ushort schoolId, Action<RouteBuilder.Result> onComplete)
        {
            Singleton<SimulationManager>.instance.AddAction(() =>
            {
                int removed = ReleaseSchoolLines(schoolId);
                Log.DebugLog("Regenerate school " + schoolId + ": released " + removed + " existing line(s)");
                RouteBuilder.Result result = BuildSet(schoolId);
                MainThread(() => onComplete(result));
            });
        }

        // Delete a school's routes without rebuilding. `onComplete(removedCount)` runs on the main thread.
        internal static void DeleteSchool(ushort schoolId, Action<int> onComplete)
        {
            Singleton<SimulationManager>.instance.AddAction(() =>
            {
                int removed = ReleaseSchoolLines(schoolId);
                Log.Info("Deleted " + removed + " route(s) for school " + schoolId);
                MainThread(() => onComplete(removed));
            });
        }

        // Release every mod-generated line bound to this school. Returns how many were released.
        private static int ReleaseSchoolLines(ushort schoolId)
        {
            TransportManager tm = Singleton<TransportManager>.instance;
            int removed = 0;
            foreach (ushort lineId in Data.SchoolLineRegistry.GetLinesForSchool(schoolId))
            {
                Data.SchoolLineData data;
                if (!Data.SchoolLineRegistry.TryGet(lineId, out data) || !data.ModGenerated)
                    continue;
                tm.ReleaseLine(lineId); // ReleaseLinePatch unregisters + clears BoardingStats/RouteMetrics/Finalizer
                removed++;
            }
            return removed;
        }

        // Plan the school's clusters once, sweep them into zones, and build one route per zone.
        private static RouteBuilder.Result BuildSet(ushort schoolId)
        {
            try
            {
                if (!EducationBuildingUtil.IsSchool(schoolId))
                    return new RouteBuilder.Result { Error = "Not a K–12 school" };

                List<ushort> homes = EducationBuildingUtil.GetStudentHomeBuildings(schoolId);
                Log.DebugLog("Roster scan: school " + schoolId + " has " + homes.Count + " student home positions");
                if (homes.Count == 0)
                    return new RouteBuilder.Result { Error = "School has no enrolled students yet" };

                Vector3 schoolPos;
                List<RoutePlanner.ClusterPoint> clusters;
                Data.RouteMetrics.GenRecord baseMetrics;
                string planError;
                if (!RoutePlanner.PlanClusters(schoolId, homes,
                        Settings.Instance.ClusterRadius,
                        Settings.Instance.MinClusterStudents,
                        Settings.Instance.DynamicStopCount,
                        out schoolPos, out clusters, out baseMetrics, out planError))
                    return new RouteBuilder.Result { Error = planError ?? "Could not plan a usable route" };

                List<List<RoutePlanner.ClusterPoint>> zones = RouteZoner.Partition(
                    schoolPos, clusters,
                    Settings.Instance.MaxRouteLength,
                    Settings.Instance.MaxRoutesPerSchool);
                if (zones.Count == 0)
                    return new RouteBuilder.Result { Error = "Could not plan a usable route" };

                bool numbered = zones.Count > 1; // a lone route gets no " - n" suffix
                int considered = baseMetrics.Considered;
                int built = 0;
                bool anyNoDepot = false;
                ushort firstLine = 0;
                string lastError = null;

                for (int i = 0; i < zones.Count; i++)
                {
                    List<RoutePlanner.ClusterPoint> zone = zones[i];
                    var stops = new List<Vector3>(zone.Count);
                    int zoneStudents = 0;
                    foreach (RoutePlanner.ClusterPoint c in zone)
                    {
                        stops.Add(c.Pos);
                        zoneStudents += c.Students;
                    }

                    List<Vector3> ordered = RoutePlanner.OrderZone(schoolPos, stops);
                    int routeNumber = numbered ? i + 1 : 0;
                    RouteBuilder.Result r = RouteBuilder.Build(schoolId, ordered, routeNumber);

                    if (r.Success)
                    {
                        built++;
                        anyNoDepot |= r.NoDepot;
                        if (firstLine == 0)
                            firstLine = r.LineId;

                        // Tag this LINE with its own zone's covered/stops (the search params are
                        // shared school-wide), so per-route usage can be correlated later.
                        Data.RouteMetrics.GenRecord m = baseMetrics;
                        m.Stops = zone.Count;
                        m.Covered = zoneStudents;
                        m.Coverage = considered > 0 ? (float)zoneStudents / considered : 0f;

                        float access, interStop;
                        MeasureLengths(ordered, out access, out interStop);
                        RecordAndSummariseRoute(r, m, schoolId, routeNumber, zones.Count, access, interStop);
                    }
                    else
                    {
                        lastError = r.Error;
                        Log.Warning("Route " + (i + 1) + "/" + zones.Count + " for school " + schoolId
                            + " failed: " + r.Error);
                    }
                }

                if (built == 0)
                    return new RouteBuilder.Result { Error = lastError ?? "No routes could be built" };

                Log.Info("GEN SET summary: school " + schoolId + " -> built " + built + "/" + zones.Count
                    + " route(s) from " + clusters.Count + " clusters | "
                    + baseMetrics.Considered + " students considered, " + baseMetrics.Excluded
                    + " walk-excluded, cap " + baseMetrics.Capacity
                    + " | radius " + Mathf.RoundToInt(baseMetrics.Radius) + "m, min " + baseMetrics.MinStudents
                    + ", " + baseMetrics.Covered + " covered ("
                    + Mathf.RoundToInt(baseMetrics.Coverage * 100f) + "%)"
                    + (anyNoDepot ? " | NO BUS DEPOT in area" : ""));

                return new RouteBuilder.Result
                {
                    Success = true,
                    LineId = firstLine,
                    NoDepot = anyNoDepot,
                    RoutesBuilt = built,
                };
            }
            catch (Exception ex)
            {
                Log.Error("Route generation failed: " + ex);
                return new RouteBuilder.Result { Error = "Internal error (see log)" };
            }
        }

        // Store one route's generation parameters on its line and emit one human-readable summary.
        // access = school→first + last→school (the fixed trunk); interStop = driving between pickups
        // (the part the route-length budget actually caps).
        private static void RecordAndSummariseRoute(RouteBuilder.Result result,
            Data.RouteMetrics.GenRecord metrics, ushort schoolId, int routeNumber, int totalRoutes,
            float access, float interStop)
        {
            Data.RouteMetrics.Record(result.LineId, metrics);

            string which = totalRoutes > 1 ? " route " + routeNumber + "/" + totalRoutes : "";
            string fitness = float.IsNaN(metrics.Fitness) ? "n/a" : metrics.Fitness.ToString("0.000");
            Log.Info("GEN summary: line " + result.LineId + " school " + schoolId + which
                + " (" + (metrics.Dynamic ? "auto" : "manual") + ") | "
                + metrics.Stops + " pickup stops, " + metrics.Covered + " covered ("
                + Mathf.RoundToInt(metrics.Coverage * 100f) + "% of considered) | access "
                + Mathf.RoundToInt(access) + "m, pickup-loop " + Mathf.RoundToInt(interStop) + "m"
                + " | radius " + Mathf.RoundToInt(metrics.Radius) + "m, min " + metrics.MinStudents
                + ", fitness " + fitness + (result.NoDepot ? " | NO BUS DEPOT" : ""));
        }

        // Split an ordered stop loop [school, s0..sn] into the trunk access length (school→s0 +
        // sn→school) and the inter-stop pickup-loop length (Σ s_i → s_{i+1}). Straight-line.
        private static void MeasureLengths(List<Vector3> ordered, out float access, out float interStop)
        {
            access = 0f;
            interStop = 0f;
            if (ordered == null || ordered.Count < 2)
                return;

            Vector3 school = ordered[0];
            access = RoadUtil.Distance2D(school, ordered[1])
                + RoadUtil.Distance2D(ordered[ordered.Count - 1], school);
            for (int i = 1; i < ordered.Count - 1; i++)
                interStop += RoadUtil.Distance2D(ordered[i], ordered[i + 1]);
        }

        private static void MainThread(Action action)
        {
            Singleton<SimulationManager>.instance.m_ThreadingWrapper.QueueMainThread(action);
        }
    }
}
