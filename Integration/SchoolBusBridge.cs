using ColossalFramework;
using SchoolBuses.Data;
using SchoolBuses.Util;

namespace SchoolBuses.Integration
{
    // Stable public API for sibling mods to query School Buses state.
    //
    // Intended consumer: Impatient Commuters, which calls IsProtectedRider via reflection
    // so it can exempt school students from its impatience/abandonment logic — a child
    // waiting for their assigned school bus should not give up and wander off.
    //
    // KEEP THIS SIGNATURE STABLE: it is bound by reflection
    // (Type "SchoolBuses.Integration.SchoolBusBridge, SchoolBuses",
    //  method "IsProtectedRider(ushort, ushort) : bool"). Allocation-free; safe per-frame.
    public static class SchoolBusBridge
    {
        // True if the waiting citizen is an eligible school student waiting at a stop that
        // belongs to a school line serving their school. Such riders should be left alone
        // (not made impatient / bored).
        public static bool IsProtectedRider(ushort citizenInstanceId, ushort stopNodeId)
        {
            if (citizenInstanceId == 0 || stopNodeId == 0)
                return false;

            ushort lineId = Singleton<NetManager>.instance.m_nodes.m_buffer[stopNodeId].m_transportLine;
            if (lineId == 0)
                return false;

            SchoolLineData line;
            if (!SchoolLineRegistry.TryGet(lineId, out line))
                return false;

            var inst = Singleton<CitizenManager>.instance.m_instances.m_buffer[citizenInstanceId];
            return CitizenEligibility.IsEligible(inst.m_citizen, inst.m_targetBuilding, stopNodeId, ref line);
        }

        // True if the stop node belongs to a registered school line (regardless of who waits).
        public static bool IsSchoolStop(ushort stopNodeId)
        {
            if (stopNodeId == 0)
                return false;
            ushort lineId = Singleton<NetManager>.instance.m_nodes.m_buffer[stopNodeId].m_transportLine;
            return lineId != 0 && SchoolLineRegistry.IsSchoolLine(lineId);
        }

        // Lets a consumer confirm the integration is present and which contract version it is.
        public const int ApiVersion = 1;
        public static int GetApiVersion() => ApiVersion;
    }
}
