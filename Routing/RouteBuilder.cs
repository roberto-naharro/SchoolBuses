using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.Math;
using SchoolBuses.Data;
using SchoolBuses.Integration;
using SchoolBuses.Util;
using UnityEngine;

namespace SchoolBuses.Routing
{
    // Creates a bus TransportLine from an ordered list of stop positions and binds it
    // to a school in the registry (design §7 step 4). Must run on the simulation thread.
    internal static class RouteBuilder
    {
        // Per-line budget for generated lines: minimal (5%). We deliberately do NOT compute a
        // target vehicle count — at 5% the vanilla formula runs ~1 bus on a typical school
        // loop, and the in-game budget slider stays fully editable for the player to add more.
        private const ushort SchoolLineBudget = 5;

        // Result of a build attempt.
        internal struct Result
        {
            public bool Success;
            public ushort LineId;
            public bool NoDepot;   // line created but idle (no bus depot serves the area)
            public string Error;
            public int RoutesBuilt; // for a multi-route set: how many lines were created
        }

        // orderedStops[0] is the school (terminal); the rest are neighbourhood stops.
        // routeNumber > 0 appends " - n" to the generated name (one of a school's several routes);
        // 0 means a lone route (no suffix).
        internal static Result Build(ushort schoolId, List<Vector3> orderedStops, int routeNumber)
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

            // NOTE: do NOT set m_lines[lineId].m_building — that marks the line as
            // building-owned and makes it non-editable in the transport tool. We track the
            // school in our own registry instead, so the line stays a normal editable line.

            // Default the line to school-bus yellow (player can still recolour it). Setting
            // m_color + the CustomColor flag is the direct equivalent of the vanilla
            // SetLineColor coroutine. Skipped when Transport Lines Manager is present — it owns
            // line colour, so we don't fight its colour management.
            if (!TlmBridge.IsPresent)
            {
                tm.m_lines.m_buffer[lineId].m_color = new Color32(253, 218, 36, 255);
                tm.m_lines.m_buffer[lineId].m_flags |= TransportLine.Flags.CustomColor;
            }

            // Minimal per-line budget (~1 bus); slider stays editable.
            tm.m_lines.m_buffer[lineId].m_budget = SchoolLineBudget;

            Log.DebugLog("Created line " + lineId + " for school " + schoolId
                + "; adding " + orderedStops.Count + " stops (closed loop)");

            int index = AddStops(tm, lineId, orderedStops);
            if (index < 2)
            {
                tm.ReleaseLine(lineId);
                result.Error = "Could not place enough reachable stops";
                return result;
            }

            // Close the ring (creates the last→school closing segment + sets Complete).
            CloseLoop(tm, lineId);

            // The first stop added (index 0) is the school terminal — it is the head
            // of the line's stop chain.
            ushort schoolStopNode = tm.m_lines.m_buffer[lineId].m_stops;
            Log.DebugLog("Placed " + index + " reachable stops on line " + lineId
                + "; school stop node = " + schoolStopNode);

            SchoolLineRegistry.Register(lineId,
                new SchoolLineData(schoolId, schoolStopNode, true));

            // Default the line to a school-bus model via the vanilla per-line selector
            // (non-destructive; overridable by vanilla tools or IPTE).
            VehicleUtil.ApplyDefaultSchoolBus(lineId);

            // Pin to exactly one bus via IPT if installed (deterministic regardless of line
            // length); the m_budget above already yields ~1 bus when IPT is absent.
            IpteBridge.TrySetVehicleCount(lineId, 1);

            // Name it "<school> - <street in front of the school>" (+ " - n" for a numbered route).
            // Skipped when Transport Lines Manager is present — it owns line naming (auto-name), so
            // we leave the name to TLM rather than have the two write over each other.
            if (!TlmBridge.IsPresent)
                ApplyGeneratedName(lineId, schoolId, routeNumber);

            // Auto-close the loop: re-trigger path computation over the next several seconds
            // (CS1 fails the first pass before the stops settle — same as a manual stop drag).
            LineFinalizer.Schedule(lineId);

            result.Success = true;
            result.LineId = lineId;
            // With school-as-depot the school supplies the bus, so a missing city depot is fine.
            result.NoDepot = !Settings.Instance.SpawnFromSchool && !HasBusDepotInArea();
            return result;
        }

        // Rebuilds a generated line's stops IN PLACE: clears the existing stops and adds the
        // new ones without releasing the line. This keeps the line's identity — its name,
        // colour, chosen vehicle, budget and its entry in the transport line list — so a
        // Regenerate refreshes the route without resetting everything (and without the empty
        // line-list rows that deleting+recreating produced).
        internal static Result RebuildStops(ushort lineId, ushort schoolId, List<Vector3> orderedStops)
        {
            var result = new Result();

            if (orderedStops == null || orderedStops.Count < 2)
            {
                result.Error = "Not enough stops to rebuild the line";
                return result;
            }

            TransportManager tm = Singleton<TransportManager>.instance;
            if ((tm.m_lines.m_buffer[lineId].m_flags & TransportLine.Flags.Created) == TransportLine.Flags.None)
            {
                result.Error = "Line no longer exists";
                return result;
            }

            // Remove every existing stop (repeatedly drop the head). We're inside a single
            // simulation action, so the game won't process this line mid-rebuild.
            int removeGuard = 0;
            while (tm.m_lines.m_buffer[lineId].m_stops != 0 && removeGuard++ < 512)
            {
                if (!tm.m_lines.m_buffer[lineId].RemoveStop(lineId, 0))
                    break;
            }
            Log.DebugLog("Regenerate in place: cleared stops on line " + lineId
                + "; adding " + orderedStops.Count + " new stops");

            int index = AddStops(tm, lineId, orderedStops);
            if (index < 2)
            {
                result.Error = "Could not place enough reachable stops";
                return result;
            }

            CloseLoop(tm, lineId);

            ushort schoolStopNode = tm.m_lines.m_buffer[lineId].m_stops;
            SchoolLineRegistry.Register(lineId,
                new SchoolLineData(schoolId, schoolStopNode, true));
            Log.DebugLog("Regenerated line " + lineId + " in place with " + index
                + " stops; school stop node = " + schoolStopNode);

            IpteBridge.TrySetVehicleCount(lineId, 1);
            LineFinalizer.Schedule(lineId);

            result.Success = true;
            result.LineId = lineId;
            // With school-as-depot the school supplies the bus, so a missing city depot is fine.
            result.NoDepot = !Settings.Instance.SpawnFromSchool && !HasBusDepotInArea();
            return result;
        }

        // Closes the ring the way the transport tool does: an APPEND AddStop (index -1) that
        // lands on the first stop. CS1 only creates the last→first closing segment and sets
        // Flags.Complete when a stop is added within 2.5 m of the first stop in append mode
        // (verified in TransportLine.AddStop IL: the closing branch requires index == -1 so its
        // internal loc.0 == 0 gate passes, plus SqrMagnitude(pos - firstStopPos) < 6.25). We add
        // pickups with explicit indices, so the ring stays OPEN until this call — which is why
        // generated lines never closed and had to be finished by hand (drag last stop onto first).
        private static void CloseLoop(TransportManager tm, ushort lineId)
        {
            ushort first = tm.m_lines.m_buffer[lineId].m_stops;
            if (first == 0)
                return;

            Vector3 firstPos = Singleton<NetManager>.instance.m_nodes.m_buffer[first].m_position;
            bool ok = tm.m_lines.m_buffer[lineId].AddStop(lineId, -1, firstPos, false);
            bool complete = (tm.m_lines.m_buffer[lineId].m_flags & TransportLine.Flags.Complete)
                != TransportLine.Flags.None;
            Log.DebugLog("Closed loop on line " + lineId
                + " via append-AddStop at first stop -> ok=" + ok + " Complete=" + complete);
        }

        // Adds the ordered stops to a line, skipping any the game reports unreachable.
        // Returns the number actually placed.
        private static int AddStops(TransportManager tm, ushort lineId, List<Vector3> orderedStops)
        {
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
            return index;
        }

        // "<school name> - <street name>", e.g. "Elementary School - Pine Avenue", with " - n"
        // appended when the school has several numbered routes. Falls back to just the school name
        // if no street can be found.
        private static void ApplyGeneratedName(ushort lineId, ushort schoolId, int routeNumber)
        {
            string name = BuildGeneratedName(schoolId);
            if (routeNumber > 0)
                name = name + " - " + routeNumber;
            if (string.IsNullOrEmpty(name))
                return;

            TransportManager tm = Singleton<TransportManager>.instance;
            // SetLineName is a coroutine (writes through InstanceManager); drain it here on the
            // simulation thread so the name applies immediately.
            var e = tm.SetLineName(lineId, name);
            int guard = 0;
            if (e != null)
                while (e.MoveNext() && guard++ < 1000) { }
            Log.DebugLog("Named line " + lineId + " \"" + name + "\"");
        }

        private static string BuildGeneratedName(ushort schoolId)
        {
            BuildingManager bm = Singleton<BuildingManager>.instance;
            string school = bm.GetBuildingName(schoolId, InstanceID.Empty);
            if (string.IsNullOrEmpty(school))
                school = "School";

            Vector3 pos = bm.m_buildings.m_buffer[schoolId].m_position;
            ushort seg = RoadUtil.FindNearestRoadSegment(pos, 200f);
            string street = seg != 0
                ? Singleton<NetManager>.instance.GetSegmentName(seg)
                : null;

            return string.IsNullOrEmpty(street) ? school : school + " - " + street;
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
