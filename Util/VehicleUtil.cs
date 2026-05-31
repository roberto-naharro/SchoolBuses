using ColossalFramework;
using UnityEngine;

namespace SchoolBuses.Util
{
    // Picks a sensible default vehicle for a generated school line and assigns it
    // through the *vanilla* per-line vehicle selection API — fully non-destructive:
    //
    //   TransportManager.AssignSelectedLineVehicle(lineID, vehicleInfo.m_prefabDataIndex)
    //
    // This is the same store (TransportManager.m_transportLineVehicleSelectionIndices)
    // the vanilla line vehicle-selector button writes, and that TransportLine.GetLineVehicle
    // reads at spawn time. So:
    //   • The player can change it with the vanilla selector at any time.
    //   • IPTE (which patches GetLineVehicle and keeps its own per-line list) simply takes
    //     over selection when active — our assignment is ignored, never broken.
    // We never patch vehicle spawning, so there is no conflict with IPTE/VehicleSelector.
    internal static class VehicleUtil
    {
        // Fallback when no bus prefab/capacity can be read (base-game bus is ~30).
        private const int DefaultBusCapacity = 30;

        // Passenger capacity of the bus a school line will run — the dedicated school bus if
        // one is available, otherwise any line bus. Used to size pickup clusters.
        internal static int GetSchoolBusCapacity()
        {
            VehicleInfo chosen = FindSchoolBusInfo() ?? FindAnyLineBus();
            BusAI ai = chosen != null ? chosen.m_vehicleAI as BusAI : null;
            int cap = ai != null ? ai.m_passengerCapacity : 0;
            if (cap > 0)
            {
                Log.DebugLog("Bus capacity: '" + chosen.name + "' carries " + cap + " passengers");
                return cap;
            }
            Log.DebugLog("Bus capacity: no bus prefab found; using default " + DefaultBusCapacity);
            return DefaultBusCapacity;
        }

        private static VehicleInfo FindAnyLineBus()
        {
            int count = PrefabCollection<VehicleInfo>.LoadedCount();
            for (uint i = 0; i < count; i++)
            {
                VehicleInfo info = PrefabCollection<VehicleInfo>.GetLoaded(i);
                if (IsLineBus(info))
                    return info;
            }
            return null;
        }

        // Assign a default "school bus"-style vehicle to the line if one is available.
        // No-op (line keeps the depot's random bus) when no matching asset is found.
        internal static void ApplyDefaultSchoolBus(ushort lineId)
        {
            VehicleInfo info = FindSchoolBusInfo();
            if (info == null)
            {
                Log.DebugLog("No school-bus asset found; line " + lineId + " keeps default bus");
                return;
            }
            Singleton<TransportManager>.instance.AssignSelectedLineVehicle(lineId, info.m_prefabDataIndex);
            Log.DebugLog("Assigned vehicle '" + info.name + "' to line " + lineId);
        }

        // Find a bus VehicleInfo whose name marks it as a school bus. Works for the
        // base/DLC school bus and for Workshop assets the player has subscribed to.
        internal static VehicleInfo FindSchoolBusInfo()
        {
            VehicleInfo firstBus = null;
            int count = PrefabCollection<VehicleInfo>.LoadedCount();
            for (uint i = 0; i < count; i++)
            {
                VehicleInfo info = PrefabCollection<VehicleInfo>.GetLoaded(i);
                if (!IsLineBus(info))
                    continue;
                if (firstBus == null)
                    firstBus = info;
                if (LooksLikeSchoolBus(info))
                    return info;
            }
            // No dedicated school bus available -> leave the line on the depot default
            // (return null) rather than forcing an arbitrary bus.
            return null;
        }

        private static bool IsLineBus(VehicleInfo info)
        {
            if (info == null || info.m_class == null)
                return false;
            if (info.m_class.m_service != ItemClass.Service.PublicTransport
                || info.m_class.m_subService != ItemClass.SubService.PublicTransportBus)
                return false;
            // Buses are VehicleType.Car in CS1; exclude trailers/other roles defensively.
            return info.m_vehicleType == VehicleInfo.VehicleType.Car
                && info.m_vehicleAI is BusAI;
        }

        private static bool LooksLikeSchoolBus(VehicleInfo info)
        {
            string name = info.name;
            if (string.IsNullOrEmpty(name))
                return false;
            name = name.ToLowerInvariant();
            return name.Contains("school");
        }
    }
}
