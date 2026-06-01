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
        // The generation knobs for one build: normally the user's Settings, but the experiment
        // harness overrides them per school with an assigned combo.
        private struct BuildParams
        {
            public float Radius;
            public int Min;
            public bool Dynamic;
            public float Pickup;
            public int MaxRoutes; // 0 = uncapped
            public int Combo;     // 0 = normal generation
        }

        private static BuildParams SettingsParams()
        {
            return new BuildParams
            {
                Radius = Settings.Instance.ClusterRadius,
                Min = Settings.Instance.MinClusterStudents,
                Dynamic = Settings.Instance.DynamicStopCount,
                Pickup = Settings.Instance.MaxRouteLength,
                MaxRoutes = Settings.Instance.MaxRoutesPerSchool,
                Combo = 0,
            };
        }

        // Generate the full set of routes for a school. `onComplete` runs on the main thread.
        internal static void Generate(ushort schoolId, Action<RouteBuilder.Result> onComplete)
        {
            Singleton<SimulationManager>.instance.AddAction(() =>
            {
                RouteBuilder.Result result = BuildSet(schoolId, SettingsParams());
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
                RouteBuilder.Result result = BuildSet(schoolId, SettingsParams());
                MainThread(() => onComplete(result));
            });
        }

        // EXPERIMENT: generate routes for every school that has none yet, assigning each the next
        // combo from `Experiment` (so one session measures many settings). `onComplete(summary)`
        // runs on the main thread.
        internal static void GenerateAllSchoolsExperiment(Action<string> onComplete)
        {
            Singleton<SimulationManager>.instance.AddAction(() =>
            {
                // Fixed sample: first N elementary + first M high schools by building id (so the
                // SAME schools every run). Only those with no routes yet.
                var elem = new List<ushort>();
                var high = new List<ushort>();
                foreach (ushort s in EducationBuildingUtil.AllSchools())
                {
                    if (Data.SchoolLineRegistry.GetLinesForSchool(s).Count > 0)
                        continue;
                    if (EducationBuildingUtil.IsHighSchool(s))
                        high.Add(s);
                    else
                        elem.Add(s);
                }

                // First N elementary + first M high schools, ALL with the SAME setting this run
                // (one-setting-per-run → the same school across runs is a clean comparison).
                var sample = new List<ushort>();
                for (int i = 0; i < Experiment.Elementary && i < elem.Count; i++)
                    sample.Add(elem[i]);
                for (int i = 0; i < Experiment.HighSchools && i < high.Count; i++)
                    sample.Add(high[i]);

                Log.Info("EXP run R" + Experiment.RunId + ": r" + Mathf.RoundToInt(Experiment.Radius)
                    + " m" + Experiment.Min + " p" + Mathf.RoundToInt(Experiment.Pickup)
                    + " applied to all sampled schools");

                int builtSchools = 0, totalRoutes = 0;
                foreach (ushort schoolId in sample)
                {
                    var p = new BuildParams
                    {
                        Radius = Experiment.Radius, Min = Experiment.Min, Dynamic = false,
                        Pickup = Experiment.Pickup, MaxRoutes = 0, Combo = Experiment.RunId,
                    };
                    Log.Info("EXP assign: " + (EducationBuildingUtil.IsHighSchool(schoolId) ? "HIGH" : "elem")
                        + " school " + schoolId + " (cap " + EducationBuildingUtil.GetStudentCapacity(schoolId)
                        + ") -> run R" + Experiment.RunId + " (r" + Mathf.RoundToInt(Experiment.Radius)
                        + " m" + Experiment.Min + " p" + Mathf.RoundToInt(Experiment.Pickup) + ")");
                    RouteBuilder.Result r = BuildSet(schoolId, p);
                    if (r.Success)
                    {
                        builtSchools++;
                        totalRoutes += r.RoutesBuilt;
                    }
                }
                string msg = builtSchools + " schools (" + elem.Count + " elem/" + high.Count
                    + " high available), " + totalRoutes + " routes built";
                Log.Info("EXP run complete: " + msg + " — ensure depot capacity for the fleet");
                ExperimentClock.Start(); // 15-min countdown → auto-stop marker + "close game" popup
                MainThread(() => onComplete(msg));
            });
        }

        // Stop creating new lines this far below CS1's 256 transport-line limit, so a city-wide
        // generate degrades gracefully (reports how many schools it skipped) instead of failing.
        private const int LineLimitGuard = 248;

        // Clamps for capacity-scaled min — small schools still get stops; huge modded schools don't
        // get an absurd min.
        private const int MinFloor = 4;
        private const int MinCeil = 14;

        // The min-students-per-cluster to actually use for a school: either the flat setting, or —
        // when ScaleMinByCapacity is on and we're not in auto-tune — scaled by the school's LIVE
        // capacity (modded-safe; capacity ≤ 0 falls back to the flat min).
        private static int EffectiveMin(ushort schoolId, BuildParams p)
        {
            if (!p.Dynamic && Settings.Instance.ScaleMinByCapacity)
            {
                int cap = EducationBuildingUtil.GetStudentCapacity(schoolId);
                if (cap > 0)
                    return Mathf.Clamp(Mathf.RoundToInt(cap * Settings.Instance.CapacityMinFactor), MinFloor, MinCeil);
            }
            return p.Min;
        }

        // USER FEATURE: route the WHOLE city in one go with the player's current settings — first
        // DELETES all existing school routes, then generates a fresh set for every school that has
        // students. Guarded against the line limit. No experiment combos/countdown. Caller must
        // confirm first (the options-menu button shows a warning modal).
        internal static void GenerateAllSchools(Action<string> onComplete)
        {
            Singleton<SimulationManager>.instance.AddAction(() =>
            {
                TransportManager tm = Singleton<TransportManager>.instance;

                int removed = 0;
                foreach (ushort lineId in Data.SchoolLineRegistry.GetAllLineIds())
                {
                    Data.SchoolLineData data;
                    if (!Data.SchoolLineRegistry.TryGet(lineId, out data) || !data.ModGenerated)
                        continue;
                    tm.ReleaseLine(lineId);
                    removed++;
                }

                int builtSchools = 0, totalRoutes = 0, skippedLimit = 0, skippedEmpty = 0;
                foreach (ushort schoolId in EducationBuildingUtil.AllSchools())
                {
                    if (tm.m_lineCount >= LineLimitGuard)
                    {
                        skippedLimit++;
                        continue;
                    }
                    RouteBuilder.Result r = BuildSet(schoolId, SettingsParams());
                    if (r.Success)
                    {
                        builtSchools++;
                        totalRoutes += r.RoutesBuilt;
                    }
                    else
                    {
                        skippedEmpty++;
                    }
                }

                string msg = "deleted " + removed + " old, built " + totalRoutes + " route(s) for "
                    + builtSchools + " school(s)"
                    + (skippedEmpty > 0 ? " (" + skippedEmpty + " had no usable roster)" : "")
                    + (skippedLimit > 0 ? " — STOPPED at the line limit, " + skippedLimit + " school(s) skipped" : "");
                Log.Info("Generate-all-schools: " + msg);
                MainThread(() => onComplete(msg));
            });
        }

        // EXPERIMENT: wipe every mod-generated school route in the city (reset between rounds).
        internal static void DeleteAllSchools(Action<int> onComplete)
        {
            Singleton<SimulationManager>.instance.AddAction(() =>
            {
                TransportManager tm = Singleton<TransportManager>.instance;
                int removed = 0;
                foreach (ushort lineId in Data.SchoolLineRegistry.GetAllLineIds())
                {
                    Data.SchoolLineData data;
                    if (!Data.SchoolLineRegistry.TryGet(lineId, out data) || !data.ModGenerated)
                        continue;
                    tm.ReleaseLine(lineId);
                    removed++;
                }
                Log.Info("Deleted ALL school routes: " + removed + " line(s)");
                MainThread(() => onComplete(removed));
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
        private static RouteBuilder.Result BuildSet(ushort schoolId, BuildParams p)
        {
            try
            {
                if (!EducationBuildingUtil.IsSchool(schoolId))
                    return new RouteBuilder.Result { Error = "Not a K–12 school" };

                List<ushort> homes = EducationBuildingUtil.GetStudentHomeBuildings(schoolId);
                Log.DebugLog("Roster scan: school " + schoolId + " has " + homes.Count + " student home positions");
                if (homes.Count == 0)
                    return new RouteBuilder.Result { Error = "School has no enrolled students yet" };

                int effMin = EffectiveMin(schoolId, p);

                Vector3 schoolPos;
                List<RoutePlanner.ClusterPoint> clusters;
                Data.RouteMetrics.GenRecord baseMetrics;
                string planError;
                if (!RoutePlanner.PlanClusters(schoolId, homes,
                        p.Radius, effMin, p.Dynamic,
                        out schoolPos, out clusters, out baseMetrics, out planError))
                    return new RouteBuilder.Result { Error = planError ?? "Could not plan a usable route" };

                baseMetrics.PickupLoop = p.Pickup;
                baseMetrics.Combo = p.Combo;

                // Zone with NO hard cap — route count is meant to emerge from min + (conditionally)
                // the catchment distance below.
                List<List<RoutePlanner.ClusterPoint>> zones = RouteZoner.Partition(
                    schoolPos, clusters, p.Pickup, 0);
                if (zones.Count == 0)
                    return new RouteBuilder.Result { Error = "Could not plan a usable route" };

                // Catchment trim — ONLY kicks in for spread-out schools that exceed the route
                // trigger: drop neighbourhoods beyond MaxCatchmentDistance and re-zone, so compact
                // schools keep full coverage and only the far-flung outliers are bounded. The trigger
                // is CAPACITY-SCALED (MaxRoutesPerSchool = routes per 1000 capacity, clamped 3..16) so
                // it fits a small school (~3) and a big one (~10) alike — modded-safe.
                int triggerPer1000 = Settings.Instance.MaxRoutesPerSchool;
                float catchDist = Settings.Instance.MaxCatchmentDistance;
                int trigger = 0;
                if (triggerPer1000 > 0)
                    trigger = baseMetrics.Capacity > 0
                        ? Mathf.Clamp(Mathf.RoundToInt(baseMetrics.Capacity * triggerPer1000 / 1000f), 3, 16)
                        : triggerPer1000;
                if (trigger > 0 && catchDist > 0f && zones.Count > trigger)
                {
                    float maxSqr = catchDist * catchDist;
                    var near = new List<RoutePlanner.ClusterPoint>(clusters.Count);
                    foreach (RoutePlanner.ClusterPoint c in clusters)
                        if (RoadUtil.SqrDistance2D(c.Pos, schoolPos) <= maxSqr)
                            near.Add(c);

                    if (near.Count >= 1 && near.Count < clusters.Count)
                    {
                        int before = zones.Count;
                        clusters = near;
                        zones = RouteZoner.Partition(schoolPos, clusters, p.Pickup, 0);
                        int covNow = 0;
                        foreach (RoutePlanner.ClusterPoint c in clusters) covNow += c.Students;
                        baseMetrics.Covered = covNow;
                        baseMetrics.Coverage = baseMetrics.Considered > 0 ? (float)covNow / baseMetrics.Considered : 0f;
                        baseMetrics.Stops = clusters.Count;
                        Log.Info("Catchment trim: school " + schoolId + " had " + before + " routes > max "
                            + trigger + " -> kept neighbourhoods within " + Mathf.RoundToInt(catchDist)
                            + "m -> " + zones.Count + " routes");
                    }
                }

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

                Log.Info("GEN SET summary: school " + schoolId
                    + (p.Combo > 0 ? " [combo " + p.Combo + "]" : "")
                    + " -> built " + built + "/" + zones.Count
                    + " route(s) from " + clusters.Count + " clusters | "
                    + baseMetrics.Considered + " students considered, " + baseMetrics.Excluded
                    + " walk-excluded, cap " + baseMetrics.Capacity
                    + " | radius " + Mathf.RoundToInt(baseMetrics.Radius) + "m, min " + baseMetrics.MinStudents
                    + ", pickup-loop " + Mathf.RoundToInt(p.Pickup) + "m, " + baseMetrics.Covered + " covered ("
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
