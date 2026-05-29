using System.Collections.Generic;
using ColossalFramework;
using SchoolBuses.Util;
using UnityEngine;

namespace SchoolBuses.Routing
{
    // Turns a set of student home positions into an ordered list of stop positions
    // (design §7 steps 2–3): greedy radius clustering, road-snapping, then a
    // nearest-neighbour tour refined by 2-opt. School is the terminal stop.
    internal static class RoutePlanner
    {
        // Build ordered stop positions. The returned list starts AND conceptually
        // ends at the school (the line is a loop, so we add the school once at index 0
        // and let the game close the loop back to it). Returns null if no usable stops.
        internal static List<Vector3> PlanStops(
            ushort schoolId,
            List<ushort> studentHomes,
            float clusterRadius,
            int maxStops)
        {
            Vector3 schoolPos = EducationBuildingUtil.GetPosition(schoolId);

            var homePositions = UniqueHomePositions(studentHomes);
            if (homePositions.Count == 0)
                return null;

            // Reserve one stop for the school itself.
            int maxClusters = Mathf.Max(1, maxStops - 1);
            List<Vector3> clusters = Cluster(homePositions, clusterRadius, maxClusters);

            // Snap each cluster centroid onto a road.
            var snapped = new List<Vector3>(clusters.Count);
            foreach (Vector3 c in clusters)
            {
                bool found;
                Vector3 p = RoadUtil.SnapToRoad(c, clusterRadius, out found);
                snapped.Add(found ? p : c);
            }

            // Order: NN tour from the school, then 2-opt.
            List<Vector3> ordered = NearestNeighbourTour(schoolPos, snapped);
            TwoOpt(schoolPos, ordered);

            // Prepend the school as the terminal stop.
            var result = new List<Vector3>(ordered.Count + 1);
            result.Add(schoolPos);
            result.AddRange(ordered);
            return result;
        }

        private static List<Vector3> UniqueHomePositions(List<ushort> homes)
        {
            var seen = new HashSet<ushort>();
            var buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            var positions = new List<Vector3>();
            foreach (ushort id in homes)
            {
                if (id == 0 || !seen.Add(id))
                    continue;
                positions.Add(buildings[id].m_position);
            }
            return positions;
        }

        // Greedy radius clustering with a hard cap. Centroids are running means.
        private static List<Vector3> Cluster(List<Vector3> points, float radius, int maxClusters)
        {
            var centroids = new List<Vector3>();
            var counts = new List<int>();
            float radiusSqr = radius * radius;

            foreach (Vector3 p in points)
            {
                int best = -1;
                float bestSqr = radiusSqr;
                for (int i = 0; i < centroids.Count; i++)
                {
                    float d = RoadUtil.SqrDistance2D(p, centroids[i]);
                    if (d < bestSqr)
                    {
                        bestSqr = d;
                        best = i;
                    }
                }

                if (best < 0 && centroids.Count < maxClusters)
                {
                    centroids.Add(p);
                    counts.Add(1);
                }
                else
                {
                    if (best < 0)
                        best = NearestCentroid(p, centroids); // cap reached: fold into nearest
                    int n = counts[best] + 1;
                    centroids[best] = centroids[best] + (p - centroids[best]) / n;
                    counts[best] = n;
                }
            }
            return centroids;
        }

        private static int NearestCentroid(Vector3 p, List<Vector3> centroids)
        {
            int best = 0;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < centroids.Count; i++)
            {
                float d = RoadUtil.SqrDistance2D(p, centroids[i]);
                if (d < bestSqr)
                {
                    bestSqr = d;
                    best = i;
                }
            }
            return best;
        }

        private static List<Vector3> NearestNeighbourTour(Vector3 start, List<Vector3> stops)
        {
            var remaining = new List<Vector3>(stops);
            var tour = new List<Vector3>(stops.Count);
            Vector3 current = start;
            while (remaining.Count > 0)
            {
                int best = NearestCentroid(current, remaining);
                current = remaining[best];
                tour.Add(current);
                remaining.RemoveAt(best);
            }
            return tour;
        }

        // 2-opt on the open path school → tour. Reverses segments while it shortens
        // the total length (school → s0 → … → sn). Instant for the ≤~10 stops we use.
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
                        Vector3 d = (k == n - 1) ? c : tour[k + 1];

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
