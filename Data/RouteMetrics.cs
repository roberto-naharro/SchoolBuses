using System.Collections.Generic;

namespace SchoolBuses.Data
{
    // Per-line record of HOW a line was generated (the parameters and the proxy scores the
    // search settled on). Kept in a side store — not persisted — so the periodic usage report
    // can echo "this line was built with these params, here's how much it's now used". That
    // params→usage pairing is the data we need to later tune the fitness weights.
    public static class RouteMetrics
    {
        public struct GenRecord
        {
            public ushort SchoolId;
            public bool Dynamic;     // auto-tune vs manual
            public float Radius;     // chosen cluster radius (m)
            public int MinStudents;  // chosen min students per cluster
            public int Stops;        // pickup clusters kept (excludes school + approach stop)
            public int Considered;   // students after walk-to-school exclusion
            public int Excluded;     // students dropped as "walk to school"
            public int Covered;      // students inside a kept cluster
            public float Coverage;   // Covered / Considered
            public float Fitness;    // winning weighted score (NaN for manual mode)
            public int Capacity;     // school max students at generation time
        }

        private static readonly Dictionary<ushort, GenRecord> Records = new Dictionary<ushort, GenRecord>();
        private static readonly object Sync = new object();

        public static void Record(ushort lineId, GenRecord record)
        {
            lock (Sync) Records[lineId] = record;
        }

        public static bool TryGet(ushort lineId, out GenRecord record)
        {
            lock (Sync) return Records.TryGetValue(lineId, out record);
        }

        public static void Remove(ushort lineId)
        {
            lock (Sync) Records.Remove(lineId);
        }

        public static void Clear()
        {
            lock (Sync) Records.Clear();
        }
    }
}
