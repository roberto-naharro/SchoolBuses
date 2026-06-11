namespace SchoolBuses.Data
{
    // Which CITIZEN a path unit is being computed for. The pathfinder itself has no idea who it
    // is routing (a PathUnit carries no citizen id), but the transit-entry gate needs to know so
    // it can hide school lines from non-students. Filled by a postfix on CitizenAI.StartPathFind
    // (sim thread), read by the gate on PATHFIND WORKER THREADS, cleared on PathManager.ReleasePath
    // (unit ids are recycled — a stale entry must never classify someone else's path).
    //
    // A flat array indexed by unit id: single-word reads/writes are atomic, so no locking; the
    // worst possible race is one path evaluated with a just-changed owner — harmless and
    // self-correcting on the next re-path. 0 = unknown owner (gate fails open to vanilla).
    public static class PathOwnership
    {
        private static readonly uint[] Owner = new uint[PathManager.MAX_PATHUNIT_COUNT];

        public static void Set(uint unit, uint citizenId)
        {
            if (unit < Owner.Length)
                Owner[unit] = citizenId;
        }

        public static void Clear(uint unit)
        {
            if (unit < Owner.Length)
                Owner[unit] = 0;
        }

        public static uint Get(uint unit)
        {
            return unit < Owner.Length ? Owner[unit] : 0;
        }
    }
}
