using ColossalFramework;
using SchoolBuses.Util;

namespace SchoolBuses.Routing
{
    public enum HealthStatus
    {
        Ok,
        Incomplete,     // a stop can't be reached by road — route won't run as a loop
        StaleCoverage,  // students have drifted out of range
        NoVehicles,     // line has no buses running (often: no depot serves it)
        Blocked,        // buses are stuck in traffic
    }

    // Read-only health evaluation for a school line — no patches, no mutation, so it is
    // fully compatible with everything. Signals are inspired by TransportTool's LineIssues
    // (blocked vehicles via Vehicle.m_blockCounter) but limited to version-stable fields.
    public struct LineHealthResult
    {
        public float Coverage;        // [0..1]
        public int ActiveVehicles;
        public HealthStatus Status;
        public string Message;
        public bool IsProblem => Status != HealthStatus.Ok;
    }

    internal static class LineHealth
    {
        // m_blockCounter is a byte (0..255); high values mean a vehicle is wedged.
        private const int BlockThreshold = 192;

        internal static LineHealthResult Evaluate(
            ushort lineId, ushort schoolId, float coverageRadius, float coverageThreshold)
        {
            var result = new LineHealthResult();
            result.Coverage = CoverageTracker.ComputeCoverage(lineId, schoolId, coverageRadius);

            int blocked;
            result.ActiveVehicles = CountVehicles(lineId, out blocked);

            bool complete = Singleton<TransportManager>.instance.m_lines.m_buffer[lineId].Complete;

            if (!complete)
            {
                result.Status = HealthStatus.Incomplete;
                result.Message = "Route incomplete — drag the stop nearest the school onto "
                    + "the school stop to close the loop.";
            }
            else if (result.ActiveVehicles == 0)
            {
                result.Status = HealthStatus.NoVehicles;
                result.Message = "No buses running — check for a bus depot nearby.";
            }
            else if (blocked > 0)
            {
                result.Status = HealthStatus.Blocked;
                result.Message = blocked == 1
                    ? "A bus is stuck in traffic."
                    : blocked + " buses are stuck in traffic.";
            }
            else if (result.Coverage < coverageThreshold)
            {
                result.Status = HealthStatus.StaleCoverage;
                result.Message = "Coverage dropped — students have moved. Regenerate to refresh.";
            }
            else
            {
                result.Status = HealthStatus.Ok;
                result.Message = string.Empty;
            }
            return result;
        }

        private static int CountVehicles(ushort lineId, out int blocked)
        {
            blocked = 0;
            var lines = Singleton<TransportManager>.instance.m_lines.m_buffer;
            var vehicles = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;

            int count = 0;
            ushort vid = lines[lineId].m_vehicles;
            int guard = 0;
            while (vid != 0)
            {
                count++;
                if (vehicles[vid].m_blockCounter >= BlockThreshold)
                    blocked++;
                vid = vehicles[vid].m_nextLineVehicle;
                if (++guard > 16384)
                    break;
            }
            return count;
        }
    }
}
