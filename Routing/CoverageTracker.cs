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

        // Students this ONE line covers: home within `radius` of any of its stops AND not a walker
        // (within walking distance of the school — those need no bus, so they don't count as covered).
        internal static int CoveredCount(ushort lineId, ushort schoolId, List<ushort> homes, float radius)
        {
            List<Vector3> stops = GetStopPositions(lineId);
            if (stops.Count == 0 || homes.Count == 0)
                return 0;

            var buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            Vector3 schoolPos = EducationBuildingUtil.GetPosition(schoolId);
            float walkSqr = RoutePlanner.WalkToSchool * RoutePlanner.WalkToSchool;
            float radiusSqr = radius * radius;
            int covered = 0;
            foreach (ushort home in homes)
            {
                if (home == 0)
                    continue;
                Vector3 pos = buildings[home].m_position;
                if (RoadUtil.SqrDistance2D(pos, schoolPos) < walkSqr)
                    continue; // walker — not a bus rider
                if (WithinAnyStop(pos, stops, radiusSqr))
                    covered++;
            }
            return covered;
        }

        // Whole-SCHOOL coverage: the UNION of students covered by ANY of the school's lines (a
        // student counted once even if several routes reach them). Also reports `walkers` = students
        // living within walking distance of the school, who need no bus — so the caller can show
        // coverage against students who ACTUALLY need a bus (roster − walkers), not the raw roster
        // (otherwise near-school walkers drag the percentage down misleadingly).
        internal static void SchoolCoverage(ushort schoolId, List<ushort> lineIds, float radius,
            out int coveredUnion, out int roster, out int walkers)
        {
            var homes = EducationBuildingUtil.GetStudentHomeBuildings(schoolId);
            roster = homes.Count;
            coveredUnion = 0;
            walkers = 0;
            if (roster == 0)
                return;

            var buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            Vector3 schoolPos = EducationBuildingUtil.GetPosition(schoolId);
            float walkSqr = RoutePlanner.WalkToSchool * RoutePlanner.WalkToSchool;
            float radiusSqr = radius * radius;

            // Gather every stop of every line once, then classify each home at most once.
            var allStops = new List<Vector3>();
            foreach (ushort lineId in lineIds)
                allStops.AddRange(GetStopPositions(lineId));

            foreach (ushort home in homes)
            {
                if (home == 0)
                    continue;
                Vector3 pos = buildings[home].m_position;
                if (RoadUtil.SqrDistance2D(pos, schoolPos) < walkSqr)
                {
                    walkers++;
                    continue; // a walker is never counted as covered (else coverage can exceed 100%)
                }
                if (allStops.Count > 0 && WithinAnyStop(pos, allStops, radiusSqr))
                    coveredUnion++;
            }
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
