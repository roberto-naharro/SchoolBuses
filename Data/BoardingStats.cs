using System.Collections.Generic;

namespace SchoolBuses.Data
{
    // Per-line tallies of eligible students boarded vs ineligible riders turned away,
    // accumulated since the game was loaded. Purely observational (filled from a
    // postfix and the eligibility veto); never affects behaviour. Not persisted —
    // these are a "this session" feedback number for the panels.
    public static class BoardingStats
    {
        public struct Counts
        {
            public int Served;
            public int TurnedAway;
        }

        private static readonly Dictionary<ushort, Counts> Stats = new Dictionary<ushort, Counts>();
        private static readonly object Sync = new object();

        public static void RecordServed(ushort lineId)
        {
            lock (Sync)
            {
                Counts c;
                Stats.TryGetValue(lineId, out c);
                c.Served++;
                Stats[lineId] = c;
            }
        }

        public static void RecordTurnedAway(ushort lineId)
        {
            lock (Sync)
            {
                Counts c;
                Stats.TryGetValue(lineId, out c);
                c.TurnedAway++;
                Stats[lineId] = c;
            }
        }

        public static Counts Get(ushort lineId)
        {
            lock (Sync)
            {
                Counts c;
                Stats.TryGetValue(lineId, out c);
                return c;
            }
        }

        public static void Clear()
        {
            lock (Sync)
            {
                Stats.Clear();
            }
        }

        public static void Remove(ushort lineId)
        {
            lock (Sync)
            {
                Stats.Remove(lineId);
            }
        }
    }
}
