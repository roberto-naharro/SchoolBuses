using System.Collections.Generic;
using System.IO;
using ColossalFramework;
using SchoolBuses.Util;

namespace SchoolBuses.Data
{
    // Static, save-scoped store of which transport lines are school lines.
    //
    // Read on the boarding hot path (BusAI.LoadPassengers) so IsSchoolLine must be
    // cheap and allocation-free. Mutations happen on the simulation thread (line
    // creation, flagging, deletion); a lock guards the dictionary because the boarding
    // loop runs on the simulation thread too but UI reads can come from the main thread.
    public static class SchoolLineRegistry
    {
        private const byte SaveVersion = 1;

        private static readonly Dictionary<ushort, SchoolLineData> Lines =
            new Dictionary<ushort, SchoolLineData>();
        private static readonly object Sync = new object();

        // Lock-free line→school mirror for PATHFIND WORKER THREADS (the transit-entry gate runs
        // there; a Dictionary read racing a sim-thread write is not safe enough on that path).
        // Single-word array reads/writes are atomic; 0 = not a school line. Kept in sync with
        // Lines by every mutator below.
        private static readonly ushort[] SchoolByLine = new ushort[TransportManager.MAX_LINE_COUNT];

        // School served by this line, or 0. Safe from any thread.
        public static ushort SchoolOfLineFast(ushort lineId)
        {
            return lineId < SchoolByLine.Length ? SchoolByLine[lineId] : (ushort)0;
        }

        // Diagnostics: how many lines the lock-free mirror currently maps (compare with Count to
        // detect a mirror that fell out of sync with the dictionary).
        public static int MirrorCountDebug()
        {
            int n = 0;
            for (int i = 0; i < SchoolByLine.Length; i++)
                if (SchoolByLine[i] != 0)
                    n++;
            return n;
        }

        public static int CountDebug()
        {
            return Lines.Count;
        }

        // Cheap hot-path guard so per-frame scanners can skip entirely when no school
        // lines exist. Volatile int is enough (monotonic-ish; exactness not required).
        public static bool AnyLines => Lines.Count > 0;

        public static bool IsSchoolLine(ushort lineId)
        {
            // No lock on the read: Dictionary reads are safe against concurrent reads,
            // and the worst case of a torn read during a rare write is a single
            // mis-boarded citizen for one frame — acceptable and self-correcting.
            return Lines.ContainsKey(lineId);
        }

        public static bool TryGet(ushort lineId, out SchoolLineData data)
        {
            return Lines.TryGetValue(lineId, out data);
        }

        public static void Register(ushort lineId, SchoolLineData data)
        {
            lock (Sync)
            {
                Lines[lineId] = data;
                if (lineId < SchoolByLine.Length)
                    SchoolByLine[lineId] = data.SchoolBuildingId;
            }
            Log.DebugLog("Registered school line " + lineId + " school=" + data.SchoolBuildingId
                + " stop=" + data.SchoolStopNode + " generated=" + data.ModGenerated);
        }

        public static void Unregister(ushort lineId)
        {
            lock (Sync)
            {
                if (Lines.Remove(lineId))
                    Log.DebugLog("Unregistered school line " + lineId);
                if (lineId < SchoolByLine.Length)
                    SchoolByLine[lineId] = 0;
            }
        }

        // Snapshot of every registered school line id (used for periodic health logging).
        public static List<ushort> GetAllLineIds()
        {
            lock (Sync)
            {
                return new List<ushort>(Lines.Keys);
            }
        }

        // All school lines bound to a given school building (used by the building panel).
        public static List<ushort> GetLinesForSchool(ushort buildingId)
        {
            var result = new List<ushort>();
            lock (Sync)
            {
                foreach (var kv in Lines)
                {
                    if (kv.Value.SchoolBuildingId == buildingId)
                        result.Add(kv.Key);
                }
            }
            return result;
        }

        public static void Clear()
        {
            lock (Sync)
            {
                Lines.Clear();
                System.Array.Clear(SchoolByLine, 0, SchoolByLine.Length);
            }
        }

        // Drop entries whose line no longer exists (e.g. deleted while another mod
        // bypassed our ReleaseLine patch). Called lazily on load.
        public static void PruneDeadLines()
        {
            var lines = Singleton<TransportManager>.instance.m_lines.m_buffer;
            lock (Sync)
            {
                var dead = new List<ushort>();
                foreach (var kv in Lines)
                {
                    if (kv.Key >= lines.Length
                        || (lines[kv.Key].m_flags & TransportLine.Flags.Created) == TransportLine.Flags.None)
                        dead.Add(kv.Key);
                }
                foreach (var id in dead)
                {
                    Lines.Remove(id);
                    if (id < SchoolByLine.Length)
                        SchoolByLine[id] = 0;
                }
                if (dead.Count > 0)
                    Log.Info("Pruned " + dead.Count + " stale school-line entries");
            }
        }

        // ── Persistence (binary blob stored via ISerializableData) ──────────────

        public static byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(SaveVersion);
                lock (Sync)
                {
                    w.Write(Lines.Count);
                    foreach (var kv in Lines)
                    {
                        w.Write(kv.Key);
                        w.Write(kv.Value.SchoolBuildingId);
                        w.Write(kv.Value.SchoolStopNode);
                        w.Write(kv.Value.ModGenerated);
                    }
                }
                return ms.ToArray();
            }
        }

        public static void Deserialize(byte[] data)
        {
            Clear();
            if (data == null || data.Length == 0)
                return;

            using (var ms = new MemoryStream(data))
            using (var r = new BinaryReader(ms))
            {
                byte version = r.ReadByte();
                if (version != SaveVersion)
                {
                    Log.Warning("Unknown registry save version " + version + " — ignoring");
                    return;
                }
                int count = r.ReadInt32();
                lock (Sync)
                {
                    for (int i = 0; i < count; i++)
                    {
                        ushort lineId = r.ReadUInt16();
                        var entry = new SchoolLineData(r.ReadUInt16(), r.ReadUInt16(), r.ReadBoolean());
                        Lines[lineId] = entry;
                        if (lineId < SchoolByLine.Length)
                            SchoolByLine[lineId] = entry.SchoolBuildingId;
                    }
                }
                Log.Info("Loaded " + count + " school line(s) from save");
            }
        }
    }
}
