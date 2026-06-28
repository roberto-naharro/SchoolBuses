using ColossalFramework;
using SchoolBuses.Data;
using SchoolBuses.Integration;
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

        // School-as-depot — and with it our own service-hours spawn/despawn — is active only when
        // the feature is on AND Transport Lines Manager is NOT present (auto-detection fallback, kept
        // until TLM calls SetVehicleSupplyEnabled(false) itself). Per the TLM author: when TLM is
        // enabled it owns the vehicle count, spawning and despawning, so we must not also spawn or
        // they fight. Our students-only boarding rule still applies on those lines either way.
        internal static bool Active
        {
            get { return Settings.Instance.SpawnFromSchool && !TlmBridge.IsPresent; }
        }

        // Whether THE SCHOOL supplies a specific line right now: the feature is active AND a partner
        // mod has not disabled supply for it (SchoolBusBridge.SetVehicleSupplyEnabled). When false,
        // city depots / a line manager serve the line instead.
        internal static bool SuppliesLine(ushort lineId)
        {
            return Active && Data.ExternalControl.IsVehicleSupplyEnabled(lineId);
        }

        internal static void Tick(uint frameIndex)
        {
            if (!Active)
                return;
            if ((frameIndex & TickMask) != 0)
                return;

            TransportManager tm = Singleton<TransportManager>.instance;
            var lines = tm.m_lines.m_buffer;

            foreach (ushort lineId in SchoolLineRegistry.GetAllLineIds())
            {
                SchoolLineData data;
                if (!SchoolLineRegistry.TryGet(lineId, out data))
                    continue; // the school is the depot for EVERY school line (generated or manual)
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
                              // it — otherwise a bus keeps circling a school closed for the night.
                              // Vanilla day/night despawn handles the existing bus; we just stop the
                              // respawn.

                if (!SpawningOpenNow(lineId))
                {
                    // Line is CLOSED right now — outside the service window, paused by a partner mod,
                    // or its supply handed to depots/TLM. SOFT end-of-service: we do NOT yank a bus
                    // off the road (that vanishes any students aboard). The boarding gate stops a
                    // closed line taking on new riders — including at the school stop — so a running
                    // bus just finishes its loop, drops everyone, and we release it ONLY once it is
                    // EMPTY and back at the school. We also don't respawn until it reopens.
                    if (lines[lineId].m_vehicles != 0)
                        ReleaseFinishedVehicles(lineId, data.SchoolStopNode, lines);
                    continue;
                }

                // Behave as a NORMAL DEPOT: keep the line topped up to its target vehicle count,
                // spawning from the school. The target is vanilla CalculateTargetVehicleCount (which
                // the line budget — and IPTE/TLM when present — drive), so a generated short route
                // sits at ~1 bus while a hand-built manual line gets as many buses as its budget
                // asks for. We only ADD buses; the game/over-budget logic retires any surplus.
                int target = lines[lineId].CalculateTargetVehicleCount();
                int have = lines[lineId].CountVehicles(lineId);
                int spawnGuard = 0;
                while (have < target && spawnGuard++ < 32)
                {
                    if (!TrySpawn(lineId, data.SchoolBuildingId, lines))
                        break; // spawn failed (vehicle limit, no spawn point) — try again next tick
                    have++;
                }
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

        // Whether a line should be SPAWNING (and topped up) right now — used by Tick. A line is
        // closed when its supply has been handed to depots/TLM, when a partner mod has paused it
        // (advanced external control, global or per-line), or when it is outside the service window
        // (simple path / player option). Closed → wind the bus down (soft despawn), don't respawn.
        internal static bool SpawningOpenNow(ushort lineId)
        {
            if (!Data.ExternalControl.IsVehicleSupplyEnabled(lineId))
                return false; // supply handed over — drain our buses so depots/TLM can take over
            if (Data.ExternalControl.ExternalControlEngaged)
                return !Data.ExternalControl.IsSpawningPaused(lineId);
            return WithinServiceHours();
        }

        // Whether the boarding gate should let new riders onto a line right now. Public so a CLOSED
        // line refuses boarders and its winding-down bus can empty out. We only gate boarding on
        // lines WE actually run: if the feature is off, TLM is present, or supply for the line was
        // handed over, boarding is not ours to block (return true). Day/night is a separate per-line
        // gate (ActiveNow).
        internal static bool ServiceOpenNow(ushort lineId)
        {
            if (!SuppliesLine(lineId))
                return true; // not our fleet → don't gate its boarding by our schedule
            if (Data.ExternalControl.ExternalControlEngaged)
                return !Data.ExternalControl.IsSpawningPaused(lineId);
            return WithinServiceHours();
        }

        // Whether school buses may run RIGHT NOW under the service window. The window comes from a
        // PARTNER MOD if one is driving it (e.g. Real Time pushes its hours via
        // SchoolBusBridge.SetServiceHours — we never read its internals), otherwise from the player's
        // own start/end option. Either way the comparison is against the game clock
        // (SimulationManager.m_currentGameTime — the clock Real Time slows), so it tracks school
        // hours. No window in force (no override, RestrictServiceHours off) = run any time of day.
        private static bool WithinServiceHours()
        {
            float start, end;
            if (Data.ExternalControl.TryGetServiceHours(out start, out end))
            {
                // A partner mod owns the window — use it verbatim.
            }
            else if (Settings.Instance.RestrictServiceHours)
            {
                start = Settings.Instance.ServiceStartHour;
                end = Settings.Instance.ServiceEndHour;
            }
            else
            {
                return true; // no window in force
            }

            System.DateTime now = Singleton<SimulationManager>.instance.m_currentGameTime;
            float hour = now.Hour + now.Minute / 60f;
            return HourInWindow(hour, start, end);
        }

        // True if `hour` (0–24) falls in [start, end), with both wrapped into 0–24 and the window
        // allowed to cross midnight (start > end). start == end means the window is always open.
        private static bool HourInWindow(float hour, float start, float end)
        {
            start = Mod24(start);
            end = Mod24(end);
            if (Mathf.Approximately(start, end))
                return true;
            return start < end
                ? hour >= start && hour < end
                : hour >= start || hour < end; // window wraps past midnight (unusual for schools)
        }

        private static float Mod24(float h)
        {
            h %= 24f;
            return h < 0f ? h + 24f : h;
        }

        // SOFT end-of-service: release a school line's bus ONLY once it is EMPTY (every student
        // dropped off) and has returned to — or is heading back to — the school. A bus still
        // carrying students keeps running and dropping them at their stops; the boarding gate keeps
        // it from taking on new riders while closed, so it empties within its loop and finishes the
        // run at the school instead of vanishing kids mid-route. Capture next-in-chain before any
        // release, since the chain changes underneath us.
        private static void ReleaseFinishedVehicles(ushort lineId, ushort schoolStopNode, TransportLine[] lines)
        {
            VehicleManager vm = Singleton<VehicleManager>.instance;
            var vehicles = vm.m_vehicles.m_buffer;
            ushort v = lines[lineId].m_vehicles;
            int guard = 0;
            while (v != 0 && guard++ < 16384)
            {
                ushort next = vehicles[v].m_nextLineVehicle;
                bool empty = vehicles[v].m_transferSize == 0;
                bool atSchool = (vehicles[v].m_flags & Vehicle.Flags.GoingBack) != (Vehicle.Flags)0
                    || (schoolStopNode != 0 && vehicles[v].m_targetBuilding == schoolStopNode);
                if (empty && atSchool)
                {
                    vm.ReleaseVehicle(v);
                    Log.Info("SchoolDepot: line " + lineId
                        + " closed — empty bus " + v + " finished its run at the school, released");
                }
                v = next;
            }
        }

        // Spawns ONE bus for the line from the school (replays the DepotAI supply sequence with the
        // school as source). Returns true on success, false if it could not spawn (so the caller's
        // top-up loop stops and retries next tick).
        private static bool TrySpawn(ushort lineId, ushort schoolId, TransportLine[] lines)
        {
            if (schoolId == 0)
                return false;
            var buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            if ((buildings[schoolId].m_flags & Building.Flags.Created) == Building.Flags.None)
                return false; // school demolished — the depot block also lifts, vanilla depots take over

            TransportInfo lineInfo = lines[lineId].Info;
            if (lineInfo == null)
                return false;

            // Same vehicle resolution as DepotAI: the line's selected vehicle first (we set the
            // school bus there), then any matching bus prefab.
            VehicleInfo vehicleInfo = lines[lineId].GetLineVehicle(lineId)
                ?? VehicleUtil.FindSchoolBusInfo();
            if (vehicleInfo == null)
                return false;

            BuildingInfo schoolInfo = buildings[schoolId].Info;
            if (schoolInfo == null || schoolInfo.m_buildingAI == null)
                return false;

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
                return false;
            }

            var vehicles = vm.m_vehicles.m_buffer;
            var offer = default(TransferManager.TransferOffer);
            offer.TransportLine = lineId;

            vehicleInfo.m_vehicleAI.SetSource(vehicleId, ref vehicles[vehicleId], schoolId);
            vehicleInfo.m_vehicleAI.StartTransfer(vehicleId, ref vehicles[vehicleId],
                lineInfo.m_vehicleReason, offer);

            Log.Info("SchoolDepot: spawned bus " + vehicleId + " for line " + lineId
                + " from school " + schoolId);
            return true;
        }
    }
}
