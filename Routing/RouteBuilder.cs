using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.Math;
using SchoolBuses.Data;
using SchoolBuses.Util;
using UnityEngine;

namespace SchoolBuses.Routing
{
    // Creates a bus TransportLine from an ordered list of stop positions and binds it
    // to a school in the registry (design §7 step 4). Must run on the simulation thread.
    internal static class RouteBuilder
    {
        // Result of a build attempt.
        internal struct Result
        {
            public bool Success;
            public ushort LineId;
            public bool NoDepot;   // line created but idle (no bus depot serves the area)
            public string Error;
        }

        // orderedStops[0] is the school (terminal); the rest are neighbourhood stops.
        internal static Result Build(ushort schoolId, List<Vector3> orderedStops)
        {
            var result = new Result();

            if (orderedStops == null || orderedStops.Count < 2)
            {
                result.Error = "Not enough stops to build a line";
                return result;
            }

            TransportInfo busInfo = FindBusTransportInfo();
            if (busInfo == null)
            {
                result.Error = "Bus TransportInfo prefab not found";
                return result;
            }

            TransportManager tm = Singleton<TransportManager>.instance;
            ushort lineId;
            Randomizer randomizer = Singleton<SimulationManager>.instance.m_randomizer;
            if (!tm.CreateLine(out lineId, ref randomizer, busInfo, true))
            {
                result.Error = "CreateLine failed (line limit reached?)";
                return result;
            }

            // Bind the line to the source school building (matches vanilla TransportTool).
            tm.m_lines.m_buffer[lineId].m_building = schoolId;

            int index = 0;
            for (int i = 0; i < orderedStops.Count; i++)
            {
                Vector3 pos = orderedStops[i];
                if (!tm.m_lines.m_buffer[lineId].CanAddStop(lineId, index, pos))
                {
                    Log.DebugLog("Skipping unreachable stop " + i + " at " + pos);
                    continue;
                }
                if (tm.m_lines.m_buffer[lineId].AddStop(lineId, index, pos, false))
                    index++;
            }

            if (index < 2)
            {
                tm.ReleaseLine(lineId);
                result.Error = "Could not place enough reachable stops";
                return result;
            }

            // The first stop added (index 0) is the school terminal — it is the head
            // of the line's stop chain.
            ushort schoolStopNode = tm.m_lines.m_buffer[lineId].m_stops;

            SchoolLineRegistry.Register(lineId,
                new SchoolLineData(schoolId, schoolStopNode, true));

            result.Success = true;
            result.LineId = lineId;
            result.NoDepot = !HasBusDepotInArea();
            return result;
        }

        private static TransportInfo FindBusTransportInfo()
        {
            // Prefer iterating loaded prefabs so a renamed/DLC bus still matches.
            int count = PrefabCollection<TransportInfo>.LoadedCount();
            for (uint i = 0; i < count; i++)
            {
                TransportInfo info = PrefabCollection<TransportInfo>.GetLoaded(i);
                if (info != null && info.m_transportType == TransportInfo.TransportType.Bus
                    && info.m_class != null
                    && info.m_class.m_subService == ItemClass.SubService.PublicTransportBus)
                    return info;
            }
            // Fall back to the base-game prefab name.
            return PrefabCollection<TransportInfo>.FindLoaded("Bus");
        }

        // Best-effort: is there at least one bus depot anywhere in the city? If not,
        // a created line will sit idle. We only surface this as a soft warning.
        private static bool HasBusDepotInArea()
        {
            var buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            for (int i = 1; i < buildings.Length; i++)
            {
                if ((buildings[i].m_flags & Building.Flags.Created) == Building.Flags.None)
                    continue;
                BuildingInfo info = buildings[i].Info;
                if (info != null && info.m_class != null
                    && info.m_class.m_service == ItemClass.Service.PublicTransport
                    && info.m_class.m_subService == ItemClass.SubService.PublicTransportBus
                    && info.m_buildingAI is DepotAI)
                    return true;
            }
            return false;
        }
    }
}
