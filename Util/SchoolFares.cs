using ColossalFramework;
using SchoolBuses.Data;

namespace SchoolBuses.Util
{
    // School transport is a school service, not paid transit — students ride free. The game's own
    // "free" representation is a zero per-line ticket price: BusAI.GetTicketPrice returns
    // TransportLine.m_ticketPrice RAW when the vehicle is on a line (IL-verified), and
    // HumanAI.EnterVehicle only charges income when GetTicketPrice != 0. So no patch is needed for
    // fares — we just write the field. Maintenance is handled by LineMaintenancePatch.
    // All writes run on the simulation thread.
    internal static class SchoolFares
    {
        internal static void ApplyFree(ushort lineId)
        {
            if (lineId == 0 || !Settings.Instance.FreeSchoolTransport)
                return;
            Singleton<TransportManager>.instance.m_lines.m_buffer[lineId].m_ticketPrice = 0;
        }

        // Back to the transport type's default price (used when a line stops being a school line
        // or the user turns the feature off). Any custom price the player had set before flagging
        // is not remembered — the vanilla default is the sane fallback.
        internal static void RestoreDefault(ushort lineId)
        {
            if (lineId == 0)
                return;
            var lines = Singleton<TransportManager>.instance.m_lines.m_buffer;
            TransportInfo info = lines[lineId].Info;
            if (info == null)
                return;
            lines[lineId].m_ticketPrice = (ushort)info.m_ticketPrice;
        }

        // Sweep every registered school line (existing saves, or the feature being toggled).
        internal static void ApplyAll()
        {
            foreach (ushort lineId in SchoolLineRegistry.GetAllLineIds())
                ApplyFree(lineId);
        }

        internal static void RestoreAll()
        {
            foreach (ushort lineId in SchoolLineRegistry.GetAllLineIds())
                RestoreDefault(lineId);
        }
    }
}
