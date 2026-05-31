using System.Collections.Generic;
using ColossalFramework;
using ICities;
using SchoolBuses.Data;
using SchoolBuses.Util;
using UnityEngine;

namespace SchoolBuses
{
    // Auto-discovered sim-thread worker. Two jobs, both scoped to school-line stops:
    //
    //  1. EVICTION — regular commuters pathfind to a school bus stop (the game's router
    //     treats a school line as an ordinary bus line), then can never board because of
    //     our filter, so they pile up dozens deep. We make each ineligible waiter give up
    //     and re-route, exactly like Impatient Commuters does: set BoredOfWaiting and max
    //     out m_waitCounter (the vanilla "tired of waiting" trigger).
    //
    //  2. REPORTING — while scanning, tally per-stop eligible/ineligible waiters and log a
    //     snapshot each full pass (debug only) so you can see who is waiting where.
    //
    // Stepped like Impatient Commuters: 1/16 of the instance buffer per frame.
    public class SchoolStopManager : ThreadingExtensionBase
    {
        private const CitizenInstance.Flags WaitingFlags =
            CitizenInstance.Flags.OnPath | CitizenInstance.Flags.WaitingTransport;

        private const int StepMask = 0xF;
        private const int StepSize = CitizenManager.MAX_INSTANCE_COUNT / (StepMask + 1);

        private CitizenInstance[] _instances;
        private NetNode[] _nodes;
        private NetSegment[] _segments;
        private PathUnit[] _pathUnits;
        private bool _initialized;

        // Per-stop tallies accumulated across one full 16-step pass (debug reporting only).
        // Report a stop snapshot only every Nth full pass to avoid flooding the log.
        private const int ReportEveryPasses = 24; // a full pass ≈ 16 frames

        private readonly Dictionary<ushort, int> _eligibleAt = new Dictionary<ushort, int>();
        private readonly Dictionary<ushort, int> _ineligibleAt = new Dictionary<ushort, int>();
        private int _evictedThisPass;
        private int _passCounter;

        // Usage-rate tracking: served count + frame index at the previous report, so we can log
        // boardings-per-time per line — the real signal for whether a generated route is used.
        private readonly Dictionary<ushort, int> _lastServed = new Dictionary<ushort, int>();
        private uint _lastReportFrame;

        // Warm-up handling: a freshly generated line spends its first while with no bus spawned
        // yet and the roster still settling, so early boardings are noise. We ignore each line's
        // first WarmupFrames, then measure a clean cumulative rate from that baseline onward.
        private const uint WarmupFrames = 12000; // ~the initial settling period (tunable)
        private readonly Dictionary<ushort, uint> _firstFrame = new Dictionary<ushort, uint>();
        private readonly Dictionary<ushort, uint> _warmBaseFrame = new Dictionary<ushort, uint>();
        private readonly Dictionary<ushort, int> _warmBaseServed = new Dictionary<ushort, int>();

        private void EnsureInitialized()
        {
            if (_initialized)
                return;
            _instances = Singleton<CitizenManager>.instance.m_instances.m_buffer;
            _nodes = Singleton<NetManager>.instance.m_nodes.m_buffer;
            _segments = Singleton<NetManager>.instance.m_segments.m_buffer;
            _pathUnits = Singleton<PathManager>.instance.m_pathUnits.m_buffer;
            _initialized = true;
        }

        // Runs every rendered frame, INCLUDING while the simulation is paused (unlike
        // OnBeforeSimulationFrame). Path-finding still resolves while paused, so this is where we
        // re-snap freshly built lines to close their loops — matching how a manually drawn line
        // completes while paused. Cheap no-op once nothing is pending.
        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            if (!Settings.Instance.Enabled)
                return;
            LineFinalizer.Tick();
        }

        public override void OnBeforeSimulationFrame()
        {
            if (!Settings.Instance.Enabled || !SchoolLineRegistry.AnyLines)
                return;

            EnsureInitialized();

            bool evict = Settings.Instance.EvictIneligibleRiders;
            bool report = Log.DebugEnabled;
            if (!evict && !report)
                return;

            uint step = Singleton<SimulationManager>.instance.m_currentFrameIndex & (uint)StepMask;
            uint start = step * (uint)StepSize;
            uint end = start + (uint)StepSize;

            for (uint i = start; i < end; i++)
            {
                ref CitizenInstance inst = ref _instances[i];
                if (inst.m_path == 0 || (inst.m_flags & WaitingFlags) != WaitingFlags)
                    continue;

                ushort nodeId = GetStopNode(ref inst);
                if (nodeId == 0)
                    continue;

                ushort lineId = _nodes[nodeId].m_transportLine;
                if (lineId == 0)
                    continue;

                SchoolLineData line;
                if (!SchoolLineRegistry.TryGet(lineId, out line))
                    continue; // not a school stop — leave it to the game / Impatient Commuters

                bool eligible = CitizenEligibility.IsEligible(
                    inst.m_citizen, inst.m_targetBuilding, nodeId, ref line);

                if (report)
                    Tally(eligible ? _eligibleAt : _ineligibleAt, nodeId);

                if (!eligible && evict
                    && (inst.m_flags & CitizenInstance.Flags.BoredOfWaiting) == 0)
                {
                    inst.m_flags |= CitizenInstance.Flags.BoredOfWaiting;
                    inst.m_waitCounter = byte.MaxValue;
                    _evictedThisPass++;
                }
            }

            // End of a full pass → emit a throttled snapshot and reset.
            if (report && step == StepMask)
            {
                bool emit = (++_passCounter % ReportEveryPasses) == 0;
                ReportAndReset(emit);
            }
        }

        private static void Tally(Dictionary<ushort, int> map, ushort nodeId)
        {
            int n;
            map.TryGetValue(nodeId, out n);
            map[nodeId] = n + 1;
        }

        private void ReportAndReset(bool emit)
        {
            if (emit)
            {
                // Definitive per-line completion + USAGE snapshot (independent of who is waiting),
                // so the log always shows whether each generated loop closed and how much it's
                // actually used (boardings per 1000 sim frames since the last report).
                var lines = Singleton<TransportManager>.instance.m_lines.m_buffer;
                uint nowFrame = Singleton<SimulationManager>.instance.m_currentFrameIndex;
                uint elapsed = _lastReportFrame == 0 ? 0 : nowFrame - _lastReportFrame;
                foreach (ushort lineId in SchoolLineRegistry.GetAllLineIds())
                {
                    if (lineId == 0 || lineId >= lines.Length)
                        continue;

                    var counts = Data.BoardingStats.Get(lineId);
                    int prevServed;
                    _lastServed.TryGetValue(lineId, out prevServed);
                    int delta = counts.Served - prevServed;
                    _lastServed[lineId] = counts.Served;
                    float perK = elapsed > 0 ? 1000f * delta / elapsed : 0f;

                    // School capacity (max students) normalises usage so lines of different-sized
                    // schools are comparable — the fair fitness signal for tuning the weights.
                    Data.SchoolLineData sd;
                    int cap = SchoolLineRegistry.TryGet(lineId, out sd)
                        ? EducationBuildingUtil.GetStudentCapacity(sd.SchoolBuildingId) : 0;
                    float normUse = cap > 0 ? perK / cap * 100f : 0f; // boardings/1k frames per 100 capacity

                    // Steady-state usage: ignore each line's warm-up, then a clean cumulative
                    // rate (boardings/1k frames) measured from the post-warm-up baseline — far
                    // less noisy than the per-report delta and the number worth tuning against.
                    uint firstSeen;
                    if (!_firstFrame.TryGetValue(lineId, out firstSeen))
                    {
                        firstSeen = nowFrame;
                        _firstFrame[lineId] = firstSeen;
                    }
                    string steadyStr;
                    if (nowFrame - firstSeen < WarmupFrames)
                    {
                        steadyStr = " steady=warming";
                    }
                    else
                    {
                        uint baseFrame;
                        if (!_warmBaseFrame.TryGetValue(lineId, out baseFrame))
                        {
                            _warmBaseFrame[lineId] = nowFrame;
                            _warmBaseServed[lineId] = counts.Served;
                            steadyStr = " steady=baseline";
                        }
                        else
                        {
                            int baseServed;
                            _warmBaseServed.TryGetValue(lineId, out baseServed);
                            uint span = nowFrame - baseFrame;
                            float steady = span > 0 ? 1000f * (counts.Served - baseServed) / span : 0f;
                            steadyStr = " steady=" + steady.ToString("0.00") + "/1k";
                        }
                    }

                    // Echo how the line was generated so params↔usage read on one line.
                    Data.RouteMetrics.GenRecord gen;
                    string genStr = Data.RouteMetrics.TryGet(lineId, out gen)
                        ? " | built: " + (gen.Dynamic ? "auto" : "manual") + " r" + Mathf.RoundToInt(gen.Radius)
                            + " m" + gen.MinStudents + " " + gen.Stops + "stops cov"
                            + Mathf.RoundToInt(gen.Coverage * 100f) + "% fit"
                            + (float.IsNaN(gen.Fitness) ? "n/a" : gen.Fitness.ToString("0.00"))
                        : "";

                    Log.DebugLog("Line health: line " + lineId
                        + (lines[lineId].Complete ? " COMPLETE" : " INCOMPLETE")
                        + " length=" + Mathf.RoundToInt(lines[lineId].m_totalLength) + "m"
                        + " stops=" + CountStops(lineId, lines)
                        + " | usage: served=" + counts.Served + " (+" + delta + ", "
                        + perK.ToString("0.0") + "/1k frames)" + steadyStr
                        + " turnedAway=" + counts.TurnedAway
                        + " cap=" + cap + " normUse=" + normUse.ToString("0.00")
                        + genStr);
                }
                _lastReportFrame = nowFrame;
            }

            if (emit)
            {
                var stops = new HashSet<ushort>();
                foreach (var k in _eligibleAt.Keys) stops.Add(k);
                foreach (var k in _ineligibleAt.Keys) stops.Add(k);

                var lines = Singleton<TransportManager>.instance.m_lines.m_buffer;
                foreach (ushort nodeId in stops)
                {
                    int elig, inelig;
                    _eligibleAt.TryGetValue(nodeId, out elig);
                    _ineligibleAt.TryGetValue(nodeId, out inelig);
                    ushort lineId = _nodes[nodeId].m_transportLine;
                    Log.DebugLog("Stop status: line " + lineId
                        + (lineId != 0 && lines[lineId].Complete ? " (complete)" : " (INCOMPLETE)")
                        + " stop " + nodeId + " — "
                        + elig + " students, " + inelig + " non-students waiting");
                }
                if (_evictedThisPass > 0)
                    Log.DebugLog("Evicted " + _evictedThisPass + " ineligible rider(s) since last report");
            }

            _eligibleAt.Clear();
            _ineligibleAt.Clear();
            if (emit)
                _evictedThisPass = 0;
        }

        // Number of stops in a line's chain (walks m_stops via GetNextStop, guarded).
        private static int CountStops(ushort lineId, TransportLine[] lines)
        {
            ushort first = lines[lineId].m_stops;
            if (first == 0)
                return 0;
            int n = 0;
            ushort node = first;
            do
            {
                n++;
                node = TransportLine.GetNextStop(node);
            } while (node != first && node != 0 && n < 256);
            return n;
        }

        // Stop node the citizen is currently waiting at (from their path), same derivation
        // Impatient Commuters uses.
        private ushort GetStopNode(ref CitizenInstance inst)
        {
            uint pathId = inst.m_path;
            if (pathId == 0)
                return 0;
            PathUnit.Position pos = _pathUnits[pathId].GetPosition(inst.m_pathPositionIndex >> 1);
            return _segments[pos.m_segment].m_startNode;
        }
    }
}
