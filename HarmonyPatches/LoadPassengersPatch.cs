using ColossalFramework;
using HarmonyLib;
using SchoolBuses.Data;
using SchoolBuses.Util;
using UnityEngine;

namespace SchoolBuses.HarmonyPatches
{
    // Restricts boarding on school lines to eligible K–12 students (design §4.3, §5).
    //
    // For a non-school line the prefix returns true immediately => vanilla loop runs,
    // zero overhead. For a school line the prefix runs a faithful port of the vanilla
    // BusAI.LoadPassengers boarding loop (IL-verified, cs1/boarding.md) with one extra
    // gate inserted between TransportArriveAtSource and SetCurrentVehicle, then returns
    // false to skip the original.
    //
    // Coexistence: IPT's LoadPassengers patch is a postfix that diffs the vehicle's
    // passenger count before/after — it does NOT reimplement the loop — so it still
    // measures our boardings correctly. We run at Low priority so IPT's stat-capturing
    // prefix samples the pre-count before we board. Incompatible only with other mods
    // that *also* reimplement the loop and return false (e.g. Better Train Boarding).
    //
    // v1 scope: buses only. Adding TramAI/TrolleybusAI is a one-line patch each — the
    // boarding loop is identical — but is intentionally out of scope per design Q#1.
    [HarmonyPatch(typeof(BusAI), "LoadPassengers")]
    [HarmonyPriority(Priority.Low)]
    internal static class LoadPassengersPatch
    {
        // Grid constants from BusAI.LoadPassengers IL.
        private const int GridWidth = 0x870;       // 2160
        private const int GridMaxIndex = 0x86f;    // 2159
        private const float BoardRadiusSqr = 1024f; // ~32 m

        private static bool Prefix(ushort vehicleID, ref Vehicle data, ushort currentStop, ushort nextStop)
        {
            ushort lineId = data.m_transportLine;
            if (lineId == 0 || !SchoolLineRegistry.IsSchoolLine(lineId))
                return true; // vanilla path

            SchoolLineData line;
            if (!SchoolLineRegistry.TryGet(lineId, out line))
                return true;

            BoardEligible(vehicleID, ref data, currentStop, nextStop, ref line);
            return false; // skip original; we did the boarding
        }

        // Faithful port of BusAI.LoadPassengers with the eligibility gate added.
        private static void BoardEligible(
            ushort vehicleID, ref Vehicle data, ushort currentStop, ushort nextStop,
            ref SchoolLineData line)
        {
            if (currentStop == 0 || nextStop == 0)
                return;

            CitizenManager cm = Singleton<CitizenManager>.instance;
            NetManager nm = Singleton<NetManager>.instance;
            var nodes = nm.m_nodes.m_buffer;
            var instances = cm.m_instances.m_buffer;
            ushort[] grid = cm.m_citizenGrid;

            Vector3 stopPos = nodes[currentStop].m_position;
            Vector3 nextPos = nodes[nextStop].m_position;
            nodes[currentStop].m_maxWaitTime = 0;

            int minX = Mathf.Max((int)((stopPos.x - 32f) / 8f + 1080f), 0);
            int minZ = Mathf.Max((int)((stopPos.z - 32f) / 8f + 1080f), 0);
            int maxX = Mathf.Min((int)((stopPos.x + 32f) / 8f + 1080f), GridMaxIndex);
            int maxZ = Mathf.Min((int)((stopPos.z + 32f) / 8f + 1080f), GridMaxIndex);

            int tempCounter = nodes[currentStop].m_tempCounter;
            int transferSize = data.m_transferSize;
            bool full = false;

            for (int z = minZ; z <= maxZ && !full; z++)
            {
                for (int x = minX; x <= maxX && !full; x++)
                {
                    ushort instanceId = grid[z * GridWidth + x];
                    int guard = 0;
                    while (instanceId != 0)
                    {
                        ushort nextInstance = instances[instanceId].m_nextGridInstance;

                        if ((instances[instanceId].m_flags & CitizenInstance.Flags.WaitingTransport)
                            != CitizenInstance.Flags.None)
                        {
                            Vector3 targetPos = instances[instanceId].m_targetPos;
                            if ((targetPos - stopPos).sqrMagnitude < BoardRadiusSqr)
                            {
                                // ── eligibility gate (the only departure from vanilla) ──
                                // Checked before TransportArriveAtSource so an ineligible
                                // citizen is skipped exactly like a TransportArriveAtSource
                                // reject: no side effects, keep loading the rest.
                                bool eligible = CitizenEligibility.IsEligible(
                                    instances[instanceId].m_citizen,
                                    instances[instanceId].m_targetBuilding,
                                    currentStop,
                                    ref line);

                                if (eligible)
                                {
                                    CitizenInfo info = instances[instanceId].Info;
                                    if (info != null && info.m_citizenAI.TransportArriveAtSource(
                                            instanceId, ref instances[instanceId], stopPos, nextPos))
                                    {
                                        if (info.m_citizenAI.SetCurrentVehicle(
                                                instanceId, ref instances[instanceId], vehicleID, 0u, stopPos))
                                        {
                                            tempCounter++;
                                            transferSize++;
                                        }
                                        else
                                        {
                                            full = true; // vehicle full => stop loading everyone
                                        }
                                    }
                                }
                            }
                        }

                        instanceId = nextInstance;
                        if (++guard > 0x10000)
                        {
                            CODebugBase<LogChannel>.Error(LogChannel.Core,
                                "Invalid list detected!\n" + System.Environment.StackTrace);
                            break;
                        }
                        if (full)
                            break;
                    }
                }
            }

            nodes[currentStop].m_tempCounter = (ushort)Mathf.Min(tempCounter, 0xffff);
            data.m_transferSize = (ushort)transferSize;
        }
    }
}
