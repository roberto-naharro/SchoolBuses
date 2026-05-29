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
                    Lines.Remove(id);
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
                    }
                }
                Log.Info("Loaded " + count + " school line(s) from save");
            }
        }
    }
}
