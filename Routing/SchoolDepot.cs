using ColossalFramework;
using SchoolBuses.Data;
using SchoolBuses.Util;
using UnityEngine;

namespace SchoolBuses.Routing
{
    // SCHOOL-AS-DEPOT: the school spawns and owns its lines' buses, so no bus depot is needed
    // (the most common "why are my routes idle" gotcha) and buses thematically live at the school.
    //
    // How vanilla supplies a line: TransportLine.SimulationStep posts a TransferManager offer
    // (reason = TransportInfo.m_vehicleReason, offer.TransportLine = lineId); a DepotAI answers it
    // in StartTransfer → CreateVehicle: CalculateSpawnPosition → VehicleManager.CreateVehicle →
    // VehicleAI.SetSource(depot) → VehicleAI.StartTransfer(offer) (IL-verified). We replay exactly
    // those steps with the SCHOOL as the source building, while DepotStartTransferPatch stops city
    // depots from also serving school lines (no double supply). "Return to depot" then drives the
    // bus back to the school. Deterministic one-bus-per-route even without IPTE.
    //
    // Must run on the simulation thread.
    internal static class SchoolDepot
    {
        // Check cadence: all school lines, once every 512 sim frames (spawning is rare; a fresh
        // line waits at most ~8 in-game minutes for its bus).
        private const uint TickMask = 0x1FF;

        internal static void Tick(uint frameIndex)
        {
            if (!Settings.Instance.SpawnFromSchool)
                return;
            if ((frameIndex & TickMask) != 0)
                return;

            TransportManager tm = Singleton<TransportManager>.instance;
            var lines = tm.m_lines.m_buffer;

            foreach (ushort lineId in SchoolLineRegistry.GetAllLineIds())
            {
                SchoolLineData data;
                if (!SchoolLineRegistry.TryGet(lineId, out data) || !data.ModGenerated)
                    continue; // manual lines keep their vanilla depot supply
                if ((lines[lineId].m_flags & TransportLine.Flags.Created) == TransportLine.Flags.None)
                    continue;
                if (!lines[lineId].Complete)
                    continue; // ring not closed — spawning would fail
                if (LineFinalizer.IsPending(lineId))
                    continue; // CloseLoop sets Complete at build time, but the stop-to-stop paths
                              // are still committing (LineFinalizer window) — wait, else the bus
                              // lands on a line it cannot path along yet
                if (!ActiveNow(ref lines[lineId]))
                    continue; // line disabled for the current period (the player's day/night
                              // toggle): the game despawns its bus then, and we must NOT resurrect
                              // it — otherwise a bus keeps circling a school closed for the night
                              // (e.g. with the Real Time mod). Vanilla day/night despawn handles
                              // the existing bus; this just stops the respawn.
                if (lines[lineId].CountVehicles(lineId) > 0)
                    continue; // already supplied (one bus per route)

                TrySpawn(lineId, data.SchoolBuildingId, lines);
            }
        }

        // Whether the line should be running RIGHT NOW, applying the same day/night gate vanilla
        // TransportLine.SimulationStep uses: it is OFF when the period-appropriate disabled flag is
        // set (DisabledNight at night, DisabledDay by day). Lets a player schedule a school line to
        // day-only (vanilla line panel), and school-as-depot stops fighting the night shutdown.
        private static bool ActiveNow(ref TransportLine line)
        {
            TransportLine.Flags disabledNow = Singleton<SimulationManager>.instance.m_isNightTime
                ? TransportLine.Flags.DisabledNight
                : TransportLine.Flags.DisabledDay;
            return (line.m_flags & disabledNow) == TransportLine.Flags.None;
        }

        private static void TrySpawn(ushort lineId, ushort schoolId, TransportLine[] lines)
        {
            if (schoolId == 0)
                return;
            var buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            if ((buildings[schoolId].m_flags & Building.Flags.Created) == Building.Flags.None)
                return; // school demolished — the depot block also lifts, vanilla depots take over

            TransportInfo lineInfo = lines[lineId].Info;
            if (lineInfo == null)
                return;

            // Same vehicle resolution as DepotAI: the line's selected vehicle first (we set the
            // school bus there), then any matching bus prefab.
            VehicleInfo vehicleInfo = lines[lineId].GetLineVehicle(lineId)
                ?? VehicleUtil.FindSchoolBusInfo();
            if (vehicleInfo == null)
                return;

            BuildingInfo schoolInfo = buildings[schoolId].Info;
            if (schoolInfo == null || schoolInfo.m_buildingAI == null)
                return;

            SimulationManager sm = Singleton<SimulationManager>.instance;
            Vector3 spawnPos, spawnTarget;
            schoolInfo.m_buildingAI.CalculateSpawnPosition(schoolId, ref buildings[schoolId],
                ref sm.m_randomizer, vehicleInfo, out spawnPos, out spawnTarget);

            VehicleManager vm = Singleton<VehicleManager>.instance;
            ushort vehicleId;
            if (!vm.CreateVehicle(out vehicleId, ref sm.m_randomizer, vehicleInfo, spawnPos,
                    lineInfo.m_vehicleReason, false, true))
            {
                Log.Warning("SchoolDepot: CreateVehicle failed for line " + lineId
                    + " (vehicle limit reached?)");
                return;
            }

            var vehicles = vm.m_vehicles.m_buffer;
            var offer = default(TransferManager.TransferOffer);
            offer.TransportLine = lineId;

            vehicleInfo.m_vehicleAI.SetSource(vehicleId, ref vehicles[vehicleId], schoolId);
            vehicleInfo.m_vehicleAI.StartTransfer(vehicleId, ref vehicles[vehicleId],
                lineInfo.m_vehicleReason, offer);

            Log.Info("SchoolDepot: spawned bus " + vehicleId + " for line " + lineId
                + " from school " + schoolId);
        }
    }
}
