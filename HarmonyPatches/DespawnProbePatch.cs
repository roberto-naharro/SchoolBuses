using ColossalFramework;
using HarmonyLib;
using SchoolBuses.Data;
using SchoolBuses.Util;

namespace SchoolBuses.HarmonyPatches
{
    // DIAGNOSTIC (debug-logging only): catches citizens being RELEASED (despawned) while they were
    // waiting for a school line, and says who they were and whether their path was tracked by
    // PathOwnership. This pins down the despawn reports: are they at school stops at all, are they
    // students or adults, and if adults — why did the pathfind gate not stop their path (owner=0
    // means some path-creation entry point is not hooked; owner!=0 means the gate itself let it
    // through). Zero work when debug logging is off or no school lines exist.
    [HarmonyPatch(typeof(CitizenManager), "ReleaseCitizenInstance")]
    internal static class DespawnProbePatch
    {
        private static int _total;

        private static void Prefix(CitizenManager __instance, ushort instance)
        {
            if (!Log.DebugEnabled || !SchoolLineRegistry.AnyLines)
                return;

            ref CitizenInstance inst = ref __instance.m_instances.m_buffer[instance];
            if ((inst.m_flags & CitizenInstance.Flags.WaitingTransport) == CitizenInstance.Flags.None)
                return;
            if (inst.m_path == 0)
                return;

            // Same stop/line derivation as the eviction scan: the current hop's segment; the line
            // they intend to ride is the next stop's transport line.
            var pathUnits = Singleton<PathManager>.instance.m_pathUnits.m_buffer;
            var segments = Singleton<NetManager>.instance.m_segments.m_buffer;
            var nodes = Singleton<NetManager>.instance.m_nodes.m_buffer;

            PathUnit.Position p = pathUnits[inst.m_path].GetPosition(inst.m_pathPositionIndex >> 1);
            if (p.m_segment == 0)
                return;
            ushort lineId = nodes[segments[p.m_segment].m_endNode].m_transportLine;
            if (lineId == 0)
                lineId = nodes[segments[p.m_segment].m_startNode].m_transportLine;
            if (!SchoolLineRegistry.IsSchoolLine(lineId))
                return;

            uint citizenId = inst.m_citizen;
            var citizens = Singleton<CitizenManager>.instance.m_citizens.m_buffer;
            bool student = citizenId != 0
                && (citizens[citizenId].m_flags & Citizen.Flags.Student) != Citizen.Flags.None;

            int n = ++_total;
            Log.DebugLog("DESPAWN at school line " + lineId + ": instance " + instance
                + " citizen " + citizenId + (student ? " STUDENT" : " non-student")
                + " path " + inst.m_path + " owner=" + PathOwnership.Get(inst.m_path)
                + " flags=" + inst.m_flags + " (total " + n + ")");
        }
    }
}
