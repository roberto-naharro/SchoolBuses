using ColossalFramework;
using SchoolBuses.Data;

namespace SchoolBuses.Util
{
    // Day-only school service. Schools (with Real Time) close at night, but a school line runs
    // 24/7 by default, so its bus keeps circling a closed school. The vanilla per-line "no night
    // service" flag (TransportLine.Flags.DisabledNight) already stops a line at night — the game
    // despawns its bus and SchoolDepot no longer resurrects it (ActiveNow check) — this just sets
    // or clears that flag in bulk from the option so the player doesn't have to toggle every line.
    // All writes run on the simulation thread.
    internal static class SchoolSchedule
    {
        internal static void Apply(ushort lineId)
        {
            if (lineId == 0)
                return;
            var lines = Singleton<TransportManager>.instance.m_lines.m_buffer;
            if (Settings.Instance.DayOnlyService)
                lines[lineId].m_flags |= TransportLine.Flags.DisabledNight;
            else
                lines[lineId].m_flags &= ~TransportLine.Flags.DisabledNight;
        }

        internal static void ApplyAll()
        {
            foreach (ushort lineId in SchoolLineRegistry.GetAllLineIds())
                Apply(lineId);
        }
    }
}
