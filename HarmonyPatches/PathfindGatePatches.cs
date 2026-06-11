using ColossalFramework;
using HarmonyLib;
using SchoolBuses.Data;
using UnityEngine;

namespace SchoolBuses.HarmonyPatches
{
    // SCHOOL LINES DO NOT EXIST FOR NON-STUDENTS (pathfinding-level exclusion).
    //
    // Boarding-time rejection + eviction still let adults WALK to a school stop, wait, get turned
    // away and re-path — sometimes oscillating onto the same line and despawning. The proper fix
    // is to make the route planner never offer the line to them at all.
    //
    // IL-verified design (deliberately the narrowest possible patch surface):
    //  • PathFind.ProcessItemPublicTransport is the SINGLE method through which a pedestrian path
    //    can step onto a transit line (ProcessItemMain calls it only at stop nodes, i.e. nodes
    //    with NetNode.m_lane != 0). A plain PREFIX there — no transpiler, ProcessItemMain
    //    untouched — can veto the entry, mirroring the method's own first gate
    //    (segment.m_flags & m_disableMask → return).
    //  • The unit being computed is PathFind.m_calculating (set right before
    //    PathFindImplementation; ProcessItem* run on the same worker thread).
    //  • Who the unit belongs to: CitizenAI.StartPathFind (the 7-arg funnel every resident /
    //    tourist trip goes through) assigns citizenData.m_path on success — a postfix records
    //    unit → citizen in PathOwnership; PathManager.ReleasePath clears it (ids are recycled).
    //
    // Fail-open everywhere: unknown unit owner, unregistered line, feature off → vanilla.
    // Thread-safety: gate reads only lock-free single-word state (SchoolOfLineFast mirror,
    // PathOwnership array, citizen buffer fields — same class of reads the pathfinder itself does).
    // TM:PE: replaces the whole pathfinder, so the gate simply never runs — graceful fallback to
    // boarding-time rejection + eviction (unchanged). No other known mod touches these methods
    // (checked TLM/IPT/EBS/BTB/RealTime/GameAnarchy reference sources).

    // Record which citizen each path unit is for.
    [HarmonyPatch(typeof(CitizenAI), "StartPathFind",
        new[] { typeof(ushort), typeof(CitizenInstance), typeof(Vector3), typeof(Vector3),
                typeof(VehicleInfo), typeof(bool), typeof(bool) },
        new[] { ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal,
                ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal })]
    internal static class CitizenStartPathFindPatch
    {
        private static void Postfix(ref CitizenInstance citizenData, bool __result)
        {
            if (__result && citizenData.m_path != 0)
                PathOwnership.Set(citizenData.m_path, citizenData.m_citizen);
        }
    }

    // Path unit ids are recycled — drop the owner when a path is released so a reused id can
    // never inherit someone else's classification (e.g. a bus's own path unit).
    [HarmonyPatch(typeof(PathManager), "ReleasePath")]
    internal static class ReleasePathPatch
    {
        private static void Postfix(uint unit)
        {
            PathOwnership.Clear(unit);
        }
    }

    // The shared gate decision, used by the vanilla prefix below AND by TmpeGateBridge (the same
    // prefix applied dynamically to TM:PE's CustomPathFind.ProcessItemPublicTransport).
    internal static class TransitGate
    {
        // Debug telemetry (Interlocked — the gate runs on pathfind worker threads). Read+reset by
        // the periodic report in SchoolStopManager. DbgEntries counts EVERY invocation (proves the
        // prefix fires at all); DbgChecks counts school-line segments recognised (an entries
        // stream with zero checks = line→school resolution broken, not the patch); unknownOwner
        // that never drains = some path-creation entry point is not hooked.
        internal static int DbgEntries, DbgEntriesTmpe, DbgLineSeen, DbgChecks,
            DbgAllowedStudents, DbgBlocked, DbgUnknownOwner;
        internal static volatile int DbgLastLine; // last nonzero line id the gate resolved

        // True = run the original (line visible); false = skip (line does not exist for this trip).
        // `stopNodeId` is the STOP NODE being expanded (both pathfinders pass it as a parameter) —
        // the same node our boarding/eviction code resolves lines from. (First version read
        // m_transportLine off the transit lane's segment start node instead: telemetry showed that
        // is NEVER set — 200k gate entries, zero line hits — so the gate was silently transparent.)
        internal static bool Allow(ushort stopNodeId, uint pathUnit, bool fromTmpe)
        {
            bool dbg = Util.Log.DebugEnabled;
            if (dbg)
            {
                if (fromTmpe)
                    System.Threading.Interlocked.Increment(ref DbgEntriesTmpe);
                else
                    System.Threading.Interlocked.Increment(ref DbgEntries);
            }

            if (!Settings.Instance.Enabled || !Settings.Instance.HideLinesFromNonStudents)
                return true;
            if (!SchoolLineRegistry.AnyLines)
                return true;

            ushort lineId = Singleton<NetManager>.instance.m_nodes
                .m_buffer[stopNodeId].m_transportLine;
            if (dbg && lineId != 0)
            {
                System.Threading.Interlocked.Increment(ref DbgLineSeen);
                DbgLastLine = lineId;
            }
            ushort schoolId = SchoolLineRegistry.SchoolOfLineFast(lineId);
            if (schoolId == 0)
                return true; // not a school line → vanilla

            if (dbg)
                System.Threading.Interlocked.Increment(ref DbgChecks);

            uint citizenId = PathOwnership.Get(pathUnit);
            if (citizenId == 0)
            {
                if (dbg)
                    System.Threading.Interlocked.Increment(ref DbgUnknownOwner);
                return true; // unknown requester (not a tracked citizen trip) → vanilla
            }

            bool allow = IsStudentOf(citizenId, schoolId);
            if (dbg)
            {
                if (allow)
                    System.Threading.Interlocked.Increment(ref DbgAllowedStudents);
                else
                    System.Threading.Interlocked.Increment(ref DbgBlocked);
            }
            return allow;
        }

        // Pathfind-time eligibility: enrolled K–12 student of THIS school (no stop/direction
        // terms — both school-bound and home-bound planning are legitimate). Mirrors
        // CitizenEligibility; reads are lock-free citizen-buffer fields.
        private static bool IsStudentOf(uint citizenId, ushort schoolId)
        {
            var citizens = Singleton<CitizenManager>.instance.m_citizens.m_buffer;
            if ((citizens[citizenId].m_flags & Citizen.Flags.Student) == Citizen.Flags.None)
                return false;
            Citizen.AgeGroup age = Citizen.GetAgeGroup(citizens[citizenId].m_age);
            if (age != Citizen.AgeGroup.Child && age != Citizen.AgeGroup.Teen)
                return false;
            return citizens[citizenId].m_workBuilding == schoolId;
        }
    }

    // The gate on the VANILLA pathfinder. (TM:PE's replacement pathfinder gets the same prefix
    // via Integration/TmpeGateBridge, resolved by reflection at level load.)
    [HarmonyPatch(typeof(PathFind), "ProcessItemPublicTransport")]
    internal static class TransitEntryGatePatch
    {
        // `targetNode` = the stop node being expanded (2nd parameter of the original).
        private static bool Prefix(ushort targetNode, uint ___m_calculating)
        {
            return TransitGate.Allow(targetNode, ___m_calculating, false);
        }
    }
}
