using System.Collections.Generic;
using ColossalFramework;
using SchoolBuses.Util;
using UnityEngine;

namespace SchoolBuses.Routing
{
    // Computes how well a school line still covers its school's current roster
    // (design §8). On-demand (called when the building panel refreshes), not a
    // background ticker — the roster walk is cheap and only runs for an open panel.
    internal static class CoverageTracker
    {
        // Fraction [0..1] of enrolled students whose home is within `radius` of any
        // stop on the line. Returns 1 when the school has no students.
        internal static float ComputeCoverage(ushort lineId, ushort schoolId, float radius)
        {
            var homes = EducationBuildingUtil.GetStudentHomeBuildings(schoolId);
            if (homes.Count == 0)
                return 1f;

            List<Vector3> stops = GetStopPositions(lineId);
            if (stops.Count == 0)
                return 0f;

            var buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            float radiusSqr = radius * radius;
            int covered = 0;
            foreach (ushort home in homes)
            {
                if (home == 0)
                    continue;
                Vector3 pos = buildings[home].m_position;
                if (WithinAnyStop(pos, stops, radiusSqr))
                    covered++;
            }
            return (float)covered / homes.Count;
        }

        private static bool WithinAnyStop(Vector3 pos, List<Vector3> stops, float radiusSqr)
        {
            for (int i = 0; i < stops.Count; i++)
            {
                if (RoadUtil.SqrDistance2D(pos, stops[i]) <= radiusSqr)
                    return true;
            }
            return false;
        }

        internal static List<Vector3> GetStopPositions(ushort lineId)
        {
            var positions = new List<Vector3>();
            TransportManager tm = Singleton<TransportManager>.instance;
            var nodes = Singleton<NetManager>.instance.m_nodes.m_buffer;

            ushort first = tm.m_lines.m_buffer[lineId].m_stops;
            if (first == 0)
                return positions;

            ushort stop = first;
            int guard = 0;
            do
            {
                positions.Add(nodes[stop].m_position);
                stop = TransportLine.GetNextStop(stop);
                if (++guard > 32768)
                    break;
            }
            while (stop != first && stop != 0);

            return positions;
        }
    }
}
