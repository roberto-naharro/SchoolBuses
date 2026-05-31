using System.Collections.Generic;
using ColossalFramework;
using SchoolBuses.Util;
using UnityEngine;

namespace SchoolBuses.Routing
{
    // Turns a set of student home positions into an ordered list of stop positions:
    // greedy radius clustering (dropping clusters too small to be worth a detour),
    // road-snapping, then a nearest-neighbour tour refined by a CLOSED-LOOP 2-opt that
    // includes the return-to-school edge. Optimising the closing edge keeps the last stop
    // near the school, so the loop closes with a short, drivable hop — which is what makes
    // CS1 mark the line "complete" instead of leaving a gap back to the school.
    internal static class RoutePlanner
    {
        // Auto-tune runs a small parameter search ("several passes"): it tries a grid of
        // (cluster radius × minimum students per cluster), scores each clustering with a weighted
        // fitness (coverage up, stops down, sensible stop size), and keeps the best. No fixed
        // stop cap — the fitness, the walk-to-stop catchment and the grid bound it.
        // DynHardMaxStops only rejects absurd configs.
        private const float PeakTravelFraction = 0.5f; // ~half a school's students en route at peak
        private const int DynHardMaxStops = 24;         // reject configs with more stops than this

        // Weighted fitness for a candidate clustering (all terms normalised to ~[0,1]):
        //   score = Wcoverage·coverage − Wstops·(stops/max) − WperStop·perStopError
        // The proxy we can compute at generation time. The REAL objective is how much the line
        // gets used (boardings per time) — these weights are placeholders to be tuned against
        // that measured usage later (see usage logging in SchoolStopManager / BoardingStats).
        private const float WCoverage = 1.00f;  // serve as many students as possible (dominant)
        private const float WStops = 0.45f;     // penalise long routes (too many stops is bad)
        private const float WPerStop = 0.20f;   // nudge stop sizes toward a sensible bus-load

        // Students whose home is within WalkToSchool of the school just walk — no stop for them.
        private const float WalkToSchool = 350f;
        private const float SnapSearchRadius = 200f;

        // Search grid. Radii top out around the distance a student will actually walk to a stop
        // (≈500 m), so the catchment never assumes an unrealistic walk.
        private static readonly float[] SearchRadii = { 180f, 240f, 300f, 360f, 420f, 500f };
        private static readonly int[] SearchMinStudents = { 5, 8, 12, 16, 22, 30, 40 };

        private struct Cluster
        {
            public Vector3 Centroid;
            public int Students;
        }

        // Returns the ordered stop list: school, s0, s1, …, sn (the game closes sn → school).
        // The closed-loop 2-opt keeps sn near the school so that final hop is short.
        // index 0 is always the school (the school stop). Returns null if nothing usable.
        internal static List<Vector3> PlanStops(
            ushort schoolId,
            List<ushort> studentHomes,
            float clusterRadius,
            int minClusterStudents,
            bool dynamicStopCount,
            out Data.RouteMetrics.GenRecord metrics)
        {
            metrics = new Data.RouteMetrics.GenRecord
            {
                SchoolId = schoolId,
                Dynamic = dynamicStopCount,
                Fitness = float.NaN,
                Capacity = EducationBuildingUtil.GetStudentCapacity(schoolId),
            };

            // Snap the school stop onto the road at the school's frontage. Using the raw
            // building centre often snaps to an awkward point far from the road, which is a
            // common cause of the closing hop (last stop → school) being unpathable.
            Vector3 schoolBuildingPos = EducationBuildingUtil.GetPosition(schoolId);
            Vector3 schoolPos = schoolBuildingPos;
            bool schoolSnapped;
            Vector3 snappedSchool = RoadUtil.SnapToRoad(schoolPos, 200f, out schoolSnapped);
            if (schoolSnapped)
                schoolPos = snappedSchool;

            // One point per student (homes repeat for siblings), so cluster sizes are student
            // counts. Drop students who live within walking distance of the school — they walk,
            // so building a pickup stop for them is wasted (and would crowd stops near the
            // school).
            int walkers;
            var studentPositions = StudentHomePositions(studentHomes, schoolBuildingPos, out walkers);
            metrics.Excluded = walkers;
            metrics.Considered = studentPositions.Count;
            if (studentPositions.Count == 0)
                return null;

            // Group students into neighbourhoods and choose which get a stop. Auto-tune searches
            // a grid of (radius × min-students) for the best-scoring clustering; manual mode uses
            // the two sliders directly.
            float chosenRadius;
            int chosenMin;
            float fitness;
            List<Cluster> clusters;
            if (dynamicStopCount)
            {
                clusters = OptimiseClustering(studentPositions, out chosenRadius, out chosenMin, out fitness);
            }
            else
            {
                clusters = SelectClusters(BuildClusters(studentPositions, clusterRadius), minClusterStudents);
                chosenRadius = clusterRadius;
                chosenMin = minClusterStudents;
                fitness = float.NaN;
            }
            if (clusters.Count == 0)
                return null;

            int covered = SumStudents(clusters);
            metrics.Radius = chosenRadius;
            metrics.MinStudents = chosenMin;
            metrics.Stops = clusters.Count;
            metrics.Covered = covered;
            metrics.Coverage = studentPositions.Count > 0 ? (float)covered / studentPositions.Count : 0f;
            metrics.Fitness = fitness;

            if (!dynamicStopCount)
                Log.DebugLog("Clustering: " + studentPositions.Count + " students -> kept "
                    + clusters.Count + " pickup clusters (radius " + Mathf.RoundToInt(clusterRadius)
                    + "m, min " + minClusterStudents + " students each) — " + DescribeSizes(clusters));

            // Snap each cluster centroid onto a road.
            var snapped = new List<Vector3>(clusters.Count);
            foreach (Cluster c in clusters)
            {
                bool found;
                Vector3 p = RoadUtil.SnapToRoad(c.Centroid, SnapSearchRadius, out found);
                snapped.Add(found ? p : c.Centroid);
            }

            // Order the pickups as a loop through the school: NN seed + closed-loop 2-opt. The
            // 2-opt includes the return-to-school edge, so the last pickup ends up near the
            // school and the ring (closed by RouteBuilder.CloseLoop) has a short final hop. No
            // separate "approach" stop is added — that left an unwanted stop right by the school.
            List<Vector3> ordered = NearestNeighbourTour(schoolPos, snapped);
            TwoOpt(schoolPos, ordered);

            var result = new List<Vector3>(ordered.Count + 1);
            result.Add(schoolPos);
            result.AddRange(ordered);
            return result;
        }

        // One position per student (no de-duplication): two students sharing a home produce
        // two identical points, so a cluster's point-count is its student-count. Identical
        // points always fall into the same cluster, and the running-mean centroid is pulled
        // toward the denser buildings. Students within WalkToSchool of the school are dropped
        // (they walk).
        private static List<Vector3> StudentHomePositions(List<ushort> homes, Vector3 schoolPos, out int walkers)
        {
            var buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            var positions = new List<Vector3>(homes.Count);
            float walkSqr = WalkToSchool * WalkToSchool;
            walkers = 0;
            foreach (ushort id in homes)
            {
                if (id == 0)
                    continue;
                Vector3 p = buildings[id].m_position;
                if (RoadUtil.SqrDistance2D(p, schoolPos) < walkSqr)
                {
                    walkers++;
                    continue;
                }
                positions.Add(p);
            }
            if (walkers > 0)
                Log.DebugLog("Excluded " + walkers + " student(s) within "
                    + Mathf.RoundToInt(WalkToSchool) + "m of the school (they walk)");
            return positions;
        }

        // Greedy radius clustering with NO cap: a home joins the nearest cluster within radius,
        // otherwise it seeds a new one. The number of clusters therefore reflects the actual
        // spread of the school's students. Centroids are running means; each keeps a home count
        // so we can rank and prune them afterwards.
        private static List<Cluster> BuildClusters(List<Vector3> points, float radius)
        {
            var clusters = new List<Cluster>();
            float radiusSqr = radius * radius;

            foreach (Vector3 p in points)
            {
                int best = -1;
                float bestSqr = radiusSqr;
                for (int i = 0; i < clusters.Count; i++)
                {
                    float d = RoadUtil.SqrDistance2D(p, clusters[i].Centroid);
                    if (d < bestSqr)
                    {
                        bestSqr = d;
                        best = i;
                    }
                }

                if (best < 0)
                {
                    clusters.Add(new Cluster { Centroid = p, Students = 1 });
                }
                else
                {
                    Cluster c = clusters[best];
                    c.Students++;
                    c.Centroid = c.Centroid + (p - c.Centroid) / c.Students;
                    clusters[best] = c;
                }
            }
            return clusters;
        }

        // Keeps every neighbourhood with at least minStudents students; the rest are too
        // sparse to be worth a stop (those students walk / use a nearby stop). No upper cap —
        // the count follows the radius and the threshold. If nothing clears the bar, keep the
        // single largest neighbourhood so a line still gets built.
        private static List<Cluster> SelectClusters(List<Cluster> clusters, int minStudents)
        {
            var kept = new List<Cluster>();
            foreach (Cluster c in clusters)
            {
                if (c.Students >= minStudents)
                    kept.Add(c);
            }

            if (kept.Count == 0 && clusters.Count > 0)
            {
                int largest = 0;
                for (int i = 1; i < clusters.Count; i++)
                {
                    if (clusters[i].Students > clusters[largest].Students)
                        largest = i;
                }
                kept.Add(clusters[largest]);
            }

            // Largest neighbourhoods first (cosmetic; the tour re-orders anyway).
            kept.Sort((a, b) => b.Students.CompareTo(a.Students));
            return kept;
        }

        // Auto-tune: search a grid of (radius × min-students), score each clustering with the
        // weighted fitness, and keep the highest-scoring one. The "several passes" optimisation —
        // cheap (a few dozen clustering passes over a few hundred points).
        private static List<Cluster> OptimiseClustering(List<Vector3> studentPositions,
            out float chosenRadius, out int chosenMin, out float fitness)
        {
            int students = studentPositions.Count;
            int capacity = VehicleUtil.GetSchoolBusCapacity();
            int idealPerStop = Mathf.Max(1, Mathf.RoundToInt(capacity / PeakTravelFraction));

            List<Cluster> best = null;
            float bestScore = float.NegativeInfinity;
            float bestCoverage = 0f;
            float bestPerStopErr = 0f;
            float bestRadius = 0f;
            int bestMin = 0;
            int evaluated = 0;

            foreach (float r in SearchRadii)
            {
                List<Cluster> natural = BuildClusters(studentPositions, r);
                foreach (int m in SearchMinStudents)
                {
                    List<Cluster> kept = FilterAndSort(natural, m);
                    if (kept.Count == 0 || kept.Count > DynHardMaxStops)
                        continue;
                    evaluated++;

                    int covered = SumStudents(kept);
                    float coverage = students > 0 ? (float)covered / students : 0f;
                    int stops = kept.Count;
                    float meanPerStop = covered / (float)stops;
                    float perStopErr = Mathf.Abs(meanPerStop - idealPerStop) / idealPerStop;

                    float score = Fitness(coverage, stops, perStopErr);
                    if (score > bestScore)
                    {
                        best = kept;
                        bestScore = score;
                        bestCoverage = coverage;
                        bestPerStopErr = perStopErr;
                        bestRadius = r;
                        bestMin = m;
                    }
                }
            }

            // Fallback: degenerate input (everyone clustered into one blob, etc.).
            if (best == null)
            {
                best = SelectClusters(BuildClusters(studentPositions, 300f), 1);
                bestRadius = 300f;
                bestMin = 1;
                bestScore = float.NaN;
            }

            chosenRadius = bestRadius;
            chosenMin = bestMin;
            fitness = bestScore;

            Log.DebugLog("Auto-tune search: " + students + " students, capacity " + capacity
                + " (ideal ~" + idealPerStop + "/stop), " + evaluated + " configs -> best score "
                + bestScore.ToString("0.000") + ": " + best.Count + " stops, "
                + Mathf.RoundToInt(bestCoverage * 100f) + "% covered, radius "
                + Mathf.RoundToInt(bestRadius) + "m, min " + bestMin + " students");
            // Fitness breakdown so a test reader can see WHY this config won.
            float stopsNorm = best.Count / (float)DynHardMaxStops;
            float errTerm = Mathf.Min(bestPerStopErr, 1f);
            Log.DebugLog("Auto-tune fitness: coverage " + bestCoverage.ToString("0.00")
                + "→+" + (WCoverage * bestCoverage).ToString("0.000")
                + " | stops " + best.Count + "/" + DynHardMaxStops
                + "→-" + (WStops * stopsNorm).ToString("0.000")
                + " | sizeErr " + bestPerStopErr.ToString("0.00")
                + "→-" + (WPerStop * errTerm).ToString("0.000")
                + " | total " + bestScore.ToString("0.000"));
            Log.DebugLog("Auto-tune: kept " + best.Count + " clusters — " + DescribeSizes(best));
            return best;
        }

        // Weighted fitness (higher is better). Coverage rewarded, stop count and stop-size error
        // penalised. A single objective so the weights can be tuned against measured ridership.
        private static float Fitness(float coverage, int stops, float perStopErr)
        {
            float stopsNorm = stops / (float)DynHardMaxStops;
            float err = Mathf.Min(perStopErr, 1f);
            return WCoverage * coverage - WStops * stopsNorm - WPerStop * err;
        }

        // Clusters with at least minStudents, largest-first (no keep-largest fallback — the
        // search wants honest empties so it can reject a bad config).
        private static List<Cluster> FilterAndSort(List<Cluster> clusters, int minStudents)
        {
            var kept = new List<Cluster>();
            foreach (Cluster c in clusters)
            {
                if (c.Students >= minStudents)
                    kept.Add(c);
            }
            kept.Sort((a, b) => b.Students.CompareTo(a.Students));
            return kept;
        }

        private static int SumStudents(List<Cluster> clusters)
        {
            int s = 0;
            foreach (Cluster c in clusters)
                s += c.Students;
            return s;
        }

        private static int NearestIndex(Vector3 p, List<Vector3> points)
        {
            int best = 0;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                float d = RoadUtil.SqrDistance2D(p, points[i]);
                if (d < bestSqr)
                {
                    bestSqr = d;
                    best = i;
                }
            }
            return best;
        }

        // Compact summary of a cluster set for the logs: per-cluster student counts plus
        // mean/min/max and the total students covered. Lets a test run show at a glance whether
        // stop sizes line up with the bus capacity target.
        private static string DescribeSizes(List<Cluster> clusters)
        {
            if (clusters.Count == 0)
                return "sizes=[]";

            int min = int.MaxValue, max = 0, sum = 0;
            var sb = new System.Text.StringBuilder("sizes=[");
            for (int i = 0; i < clusters.Count; i++)
            {
                int n = clusters[i].Students;
                if (i > 0) sb.Append(',');
                sb.Append(n);
                if (n < min) min = n;
                if (n > max) max = n;
                sum += n;
            }
            sb.Append("] mean=").Append(Mathf.RoundToInt(sum / (float)clusters.Count));
            sb.Append(" min=").Append(min).Append(" max=").Append(max);
            sb.Append(" covered=").Append(sum);
            return sb.ToString();
        }

        private static List<Vector3> NearestNeighbourTour(Vector3 start, List<Vector3> stops)
        {
            var remaining = new List<Vector3>(stops);
            var tour = new List<Vector3>(stops.Count);
            Vector3 current = start;
            while (remaining.Count > 0)
            {
                int best = NearestIndex(current, remaining);
                current = remaining[best];
                tour.Add(current);
                remaining.RemoveAt(best);
            }
            return tour;
        }

        // Closed-loop 2-opt on the cycle school → s0 → … → sn → school. Reverses segments
        // while it shortens total cycle length, INCLUDING the sn → school closing edge, so
        // the last stop is pulled back near the school for a short, drivable closing hop.
        private static void TwoOpt(Vector3 school, List<Vector3> tour)
        {
            int n = tour.Count;
            if (n < 3)
                return;

            bool improved = true;
            int safety = 0;
            while (improved && safety++ < 50)
            {
                improved = false;
                for (int i = 0; i < n - 1; i++)
                {
                    for (int k = i + 1; k < n; k++)
                    {
                        Vector3 a = (i == 0) ? school : tour[i - 1];
                        Vector3 b = tour[i];
                        Vector3 c = tour[k];
                        // Closing the loop: the node after the last stop is the school.
                        Vector3 d = (k == n - 1) ? school : tour[k + 1];

                        float before = RoadUtil.Distance2D(a, b) + RoadUtil.Distance2D(c, d);
                        float after = RoadUtil.Distance2D(a, c) + RoadUtil.Distance2D(b, d);
                        if (after + 0.1f < before)
                        {
                            tour.Reverse(i, k - i + 1);
                            improved = true;
                        }
                    }
                }
            }
        }
    }
}
