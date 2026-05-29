namespace SchoolBuses.Data
{
    // Per-line school binding. Stored in SchoolLineRegistry keyed by lineID and
    // persisted with the save game. A line is a "school line" iff it has an entry
    // in the registry; mod removal => missing key => vanilla boarding (graceful).
    public struct SchoolLineData
    {
        // The Education (L1/L2) building this line serves.
        public ushort SchoolBuildingId;

        // The line stop node that sits at the school. Homebound students may only
        // board here (the §5 boarding gate "from-school" case is stop-aware).
        public ushort SchoolStopNode;

        // True when the line was created by Generate Route (so Regenerate knows it
        // owns the whole line and may release/rebuild it). False when the player
        // manually flagged a hand-made line.
        public bool ModGenerated;

        public SchoolLineData(ushort schoolBuildingId, ushort schoolStopNode, bool modGenerated)
        {
            SchoolBuildingId = schoolBuildingId;
            SchoolStopNode = schoolStopNode;
            ModGenerated = modGenerated;
        }
    }
}
