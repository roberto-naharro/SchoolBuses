using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace SchoolBuses.Util
{
    // RouteBuilder.CloseLoop already closes the ring at build time (append-AddStop on the first
    // stop), so the line is structurally complete immediately. What remains is COMMITTING the
    // stop-to-stop bus path-finds: the game does that in TransportManager.UpdateLinesNow(), which
    // it only calls from the simulation step — frozen while the player is paused (and players lay
    // out lines paused). Path-finding itself runs on worker threads regardless of pause, so the
    // results are ready; they just never get applied to the line while paused.
    //
    // So for a short window after a line is built we call UpdateLinesNow() every frame (from
    // SchoolStopManager.OnUpdate, which ticks even while paused) to commit those paths. No stop
    // moving — re-snapping a stop with MoveStop could re-open the freshly closed ring.
    internal static class LineFinalizer
    {
        private struct Pending
        {
            public ushort LineId;
            public int Frame; // counts up since scheduling
        }

        private const int DoneFrame = 480; // ~8s window to let the path-finds finish & commit

        private static readonly List<Pending> Items = new List<Pending>();
        private static readonly object Sync = new object();

        internal static void Schedule(ushort lineId)
        {
            lock (Sync)
            {
                Items.RemoveAll(p => p.LineId == lineId);
                Items.Add(new Pending { LineId = lineId, Frame = 0 });
            }
            Log.DebugLog("Finalize: committing paths for line " + lineId + " while paused");
        }

        internal static void Cancel(ushort lineId)
        {
            lock (Sync) Items.RemoveAll(p => p.LineId == lineId);
        }

        internal static void Clear()
        {
            lock (Sync) Items.Clear();
        }

        // Called once per rendered frame (including while paused). Cheap no-op when idle.
        internal static void Tick()
        {
            lock (Sync)
            {
                if (Items.Count == 0)
                    return;

                TransportManager tm = Singleton<TransportManager>.instance;
                var lines = tm.m_lines.m_buffer;

                // Apply any path-finds that have finished (the sim step that normally does this is
                // frozen while paused).
                tm.UpdateLinesNow();

                for (int i = Items.Count - 1; i >= 0; i--)
                {
                    Pending p = Items[i];

                    if ((lines[p.LineId].m_flags & TransportLine.Flags.Created) == TransportLine.Flags.None)
                    {
                        Items.RemoveAt(i); // line deleted while pending
                        continue;
                    }

                    p.Frame++;
                    if (p.Frame >= DoneFrame)
                    {
                        bool complete = (lines[p.LineId].m_flags & TransportLine.Flags.Complete)
                            != TransportLine.Flags.None;
                        Log.DebugLog("Finalize: line " + p.LineId + " committed — Complete=" + complete
                            + " length=" + Mathf.RoundToInt(lines[p.LineId].m_totalLength) + "m");
                        Items.RemoveAt(i);
                    }
                    else
                    {
                        Items[i] = p;
                    }
                }
            }
        }
    }
}
