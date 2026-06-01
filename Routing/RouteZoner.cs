using System.Collections.Generic;
using SchoolBuses.Util;
using UnityEngine;

namespace SchoolBuses.Routing
{
    // Partitions a school's pickup clusters into compact, NON-OVERLAPPING angular sectors — the
    // classic Sweep algorithm for vehicle routing (Gillett & Miller, 1974). Each sector becomes its
    // own short one-bus loop, so a big school is covered by several tidy routes that don't crisscross
    // the same roads, instead of one giant loop a single bus can't keep up with.
    //
    // The per-route budget is the INTER-STOP pickup-loop length — the distance the bus drives
    // *between* a route's pickups — NOT the full loop. The trunk legs school→first-stop and
    // last-stop→school are a fixed access cost we deliberately EXCLUDE: a sector far from the school
    // shouldn't be shattered into many one-stop routes just because each cluster's round trip is
    // long (that produced 20 one-bus routes for one school). So neighbourhoods that are close TO
    // EACH OTHER chain into one route however far the whole sector sits from school; the split
    // happens when consecutive pickups grow too far apart. K is an OUTPUT of this.
    internal static class RouteZoner
    {
        // Absolute safety ceiling so a dense cluster of near stops can't make an unwieldy route even
        // under the length cap. Not a tuning knob — the length budget is the real control.
        private const int HardMaxStops = 14;

        // Returns one list of clusters per route. When maxRoutes > 0 and more wedges than that form,
        // the lowest-ridership (fewest-students) wedges are dropped — partial coverage, logged.
        internal static List<List<RoutePlanner.ClusterPoint>> Partition(
            Vector3 school,
            List<RoutePlanner.ClusterPoint> clusters,
            float maxRouteLength,
            int maxRoutes)
        {
            var zones = new List<List<RoutePlanner.ClusterPoint>>();
            if (clusters == null || clusters.Count == 0)
                return zones;

            int n = clusters.Count;
            float maxLen = Mathf.Max(1f, maxRouteLength);

            if (n == 1)
            {
                zones.Add(new List<RoutePlanner.ClusterPoint>(clusters));
                Log.DebugLog("Zoner: 1 cluster -> 1 route");
                return zones;
            }

            // Sort clusters by polar angle around the school (the "sweep"); start at the widest
            // angular gap so a dense neighbourhood isn't split across the 0/2π seam.
            var sorted = new List<RoutePlanner.ClusterPoint>(clusters);
            sorted.Sort((a, b) => Angle(school, a).CompareTo(Angle(school, b)));
            int start = LargestGapStart(school, sorted);

            // Sweep, accumulating into the current zone while the INTER-STOP pickup-loop length
            // (sum of hops between consecutive pickups — school trunk legs excluded) stays within
            // budget. Open a new zone when the next hop would blow the budget, or the hard stop
            // ceiling is hit. Excluding the trunk is what stops a far sector being shattered into
            // many one-stop routes.
            var current = new List<RoutePlanner.ClusterPoint>();
            Vector3 last = Vector3.zero;
            float interStop = 0f; // Σ hops between this zone's pickups so far

            for (int i = 0; i < n; i++)
            {
                RoutePlanner.ClusterPoint c = sorted[(start + i) % n];

                if (current.Count == 0)
                {
                    current.Add(c);
                    last = c.Pos;
                    interStop = 0f;
                    continue;
                }

                float hop = RoadUtil.Distance2D(last, c.Pos);
                if (interStop + hop > maxLen || current.Count >= HardMaxStops)
                {
                    zones.Add(current);
                    current = new List<RoutePlanner.ClusterPoint> { c };
                    last = c.Pos;
                    interStop = 0f;
                }
                else
                {
                    current.Add(c);
                    last = c.Pos;
                    interStop += hop;
                }
            }
            if (current.Count > 0)
                zones.Add(current);

            // Fleet cap: keep the most-served wedges, drop the rest (partial coverage).
            int dropped = 0;
            if (maxRoutes > 0 && zones.Count > maxRoutes)
            {
                zones.Sort((a, b) => Students(b).CompareTo(Students(a)));
                for (int i = zones.Count - 1; i >= maxRoutes; i--)
                {
                    dropped += zones[i].Count;
                    zones.RemoveAt(i);
                }
            }

            Log.DebugLog("Zoner: " + n + " clusters, maxPickupLoop " + Mathf.RoundToInt(maxLen)
                + "m -> " + zones.Count + " route(s) [" + DescribeZones(school, zones) + "]"
                + (dropped > 0 ? "; fleet capped at " + maxRoutes + ", dropped " + dropped + " clusters" : ""));
            return zones;
        }

        private static float Angle(Vector3 school, RoutePlanner.ClusterPoint c)
        {
            return Mathf.Atan2(c.Pos.z - school.z, c.Pos.x - school.x);
        }

        // Index (into the angle-sorted list) of the cluster just AFTER the widest empty angular
        // wedge — a natural seam to start the sweep so wedges follow the actual neighbourhoods.
        private static int LargestGapStart(Vector3 school, List<RoutePlanner.ClusterPoint> sorted)
        {
            int n = sorted.Count;
            if (n < 2)
                return 0;

            float maxGap = -1f;
            int start = 0;
            for (int i = 0; i < n; i++)
            {
                float a1 = Angle(school, sorted[i]);
                float a2 = Angle(school, sorted[(i + 1) % n]);
                float gap = a2 - a1;
                if (gap < 0f)
                    gap += 2f * Mathf.PI;
                if (gap > maxGap)
                {
                    maxGap = gap;
                    start = (i + 1) % n;
                }
            }
            return start;
        }

        private static int Students(List<RoutePlanner.ClusterPoint> zone)
        {
            int s = 0;
            for (int i = 0; i < zone.Count; i++)
                s += zone[i].Students;
            return s;
        }

        // Per-zone "stops×students" for the log, e.g. "6st/142, 4st/96".
        private static string DescribeZones(Vector3 school, List<List<RoutePlanner.ClusterPoint>> zones)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < zones.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(zones[i].Count).Append("st/").Append(Students(zones[i]));
            }
            return sb.ToString();
        }
    }
}
