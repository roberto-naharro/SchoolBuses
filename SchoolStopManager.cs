using System;
using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.UI;
using ICities;
using SchoolBuses.Data;
using SchoolBuses.Routing;
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

        // A line whose live covered-students has fallen below this is "obsolete" (its catchment
        // drifted away under the game) — flagged so its usage sample can be excluded from weight
        // tuning rather than wrongly blaming the design. See the route-fitness roadmap.
        private const int ObsoleteCoverFloor = 6;

        // Per-school usage accumulator for the SCHOOL health aggregate (the multi-route set studied
        // "as one line"). Built fresh each report.
        private sealed class SchoolAgg
        {
            public int Routes;
            public int Stops;
            public int Served;
            public int ServedDelta;
            public int TurnedAway;
            public float SteadySum; // Σ post-warm-up steady rates (boardings/1k frames); warming lines contribute 0
            public int Capacity;
        }

        // Auto-regenerate upkeep: periodically re-check coverage and regenerate a school whose routes
        // have drifted below the target. Scan cadence + per-school cooldown (a regenerated school
        // needs time to settle and we must not thrash an inherently-sparse one).
        private const uint AutoRegenScanInterval = 16384;
        private const uint AutoRegenCooldown = 65536;
        private uint _lastAutoRegenScan;
        private readonly Dictionary<ushort, uint> _lastSchoolRegen = new Dictionary<ushort, uint>();

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
        // Set when the experiment countdown elapses (main thread) so the next sim-thread report
        // emits a final, marked SCHOOL health snapshot.
        private volatile bool _forceFinalReport;

        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            if (!Settings.Instance.Enabled)
                return;
            LineFinalizer.Tick();
            if (ExperimentClock.Tick(realTimeDelta))
                OnExperimentTimeout();
        }

        private void OnExperimentTimeout()
        {
            Log.Info("===== EXPERIMENT COMPLETE: 15 min elapsed — final SCHOOL health below is the result. "
                + "Close the game and send output_log.txt. =====");
            _forceFinalReport = true;
            try
            {
                var panel = UIView.library.ShowModal<ExceptionPanel>("ExceptionPanel");
                if (panel != null)
                    panel.SetMessage("School Buses — experiment finished",
                        "15 minutes elapsed. The final measurements are now in the log.\n\n"
                        + "Please CLOSE THE GAME and send output_log.txt.", false);
            }
            catch (Exception e)
            {
                Log.Warning("Could not show experiment popup: " + e.Message);
            }
        }

        public override void OnBeforeSimulationFrame()
        {
            if (!Settings.Instance.Enabled || !SchoolLineRegistry.AnyLines)
                return;

            EnsureInitialized();

            // Auto-regenerate upkeep runs regardless of eviction/reporting (once per full pass; it
            // self-throttles to AutoRegenScanInterval).
            uint frameNow = Singleton<SimulationManager>.instance.m_currentFrameIndex;
            if ((frameNow & (uint)StepMask) == StepMask)
                AutoRegenScan(frameNow);

            // Evicting non-students from a school stop is the defining behaviour of a school line
            // (otherwise it's just a normal line — the player can untick the school-line flag for
            // that). So it's always on; only the optional debug reporting is conditional.
            const bool evict = true;
            bool report = Log.DebugEnabled;

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

            // End of a full pass → emit a throttled snapshot and reset. A pending experiment
            // timeout forces an immediate final emit, marked so the result is easy to find.
            if (report && step == StepMask)
            {
                bool force = _forceFinalReport;
                bool emit = force || (++_passCounter % ReportEveryPasses) == 0;
                ReportAndReset(emit);
                if (force)
                {
                    _forceFinalReport = false;
                    Log.Info("===== END OF EXPERIMENT — final measurements above =====");
                }
            }
        }

        // Upkeep: when the student distribution drifts so a school's routes fall below the coverage
        // target, regenerate that school (same as the Regenerate button, current settings) so its
        // stops follow the students. Disabled by Settings.AutoRegenerate for fully manual control.
        // Throttled by scan interval + per-school cooldown to avoid thrashing.
        private void AutoRegenScan(uint nowFrame)
        {
            if (!Settings.Instance.AutoRegenerate)
                return;
            float target = Settings.Instance.MinCoverageTarget;
            if (target <= 0f)
                return;
            if (_lastAutoRegenScan != 0 && nowFrame - _lastAutoRegenScan < AutoRegenScanInterval)
                return;
            _lastAutoRegenScan = nowFrame;

            float radius = Settings.Instance.ClusterRadius;
            var schools = new HashSet<ushort>();
            foreach (ushort lineId in SchoolLineRegistry.GetAllLineIds())
            {
                Data.SchoolLineData d;
                if (SchoolLineRegistry.TryGet(lineId, out d) && d.ModGenerated)
                    schools.Add(d.SchoolBuildingId);
            }

            foreach (ushort schoolId in schools)
            {
                uint last;
                if (_lastSchoolRegen.TryGetValue(schoolId, out last) && nowFrame - last < AutoRegenCooldown)
                    continue;

                var lines = SchoolLineRegistry.GetLinesForSchool(schoolId);
                int coveredUnion, roster, walkers;
                Routing.CoverageTracker.SchoolCoverage(schoolId, lines, radius, out coveredUnion, out roster, out walkers);
                int needBus = Mathf.Max(0, roster - walkers);
                if (needBus == 0)
                    continue;
                float frac = (float)coveredUnion / needBus;
                if (frac >= target)
                    continue;

                _lastSchoolRegen[schoolId] = nowFrame;
                Log.Info("Auto-regenerate: school " + schoolId + " coverage " + Mathf.RoundToInt(frac * 100f)
                    + "% < target " + Mathf.RoundToInt(target * 100f) + "% — regenerating routes");
                Routing.RouteGenerator.RegenerateSchool(schoolId, r => { });
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
                float radius = Settings.Instance.ClusterRadius;
                var homesCache = new Dictionary<ushort, List<ushort>>();
                var agg = new Dictionary<ushort, SchoolAgg>();

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
                    bool known = SchoolLineRegistry.TryGet(lineId, out sd);
                    ushort schoolId = known ? sd.SchoolBuildingId : (ushort)0;
                    int cap = known ? EducationBuildingUtil.GetStudentCapacity(schoolId) : 0;
                    float normUse = cap > 0 ? perK / cap * 100f : 0f; // boardings/1k frames per 100 capacity

                    // LIVE coverage: students this line currently reaches (re-measured now, not the
                    // generation-time figure) — the demand-robust denominator for capture rate.
                    List<ushort> homes = null;
                    if (known && !homesCache.TryGetValue(schoolId, out homes))
                    {
                        homes = EducationBuildingUtil.GetStudentHomeBuildings(schoolId);
                        homesCache[schoolId] = homes;
                    }
                    int liveCov = homes != null ? CoverageTracker.CoveredCount(lineId, schoolId, homes, radius) : 0;
                    bool obsolete = liveCov < ObsoleteCoverFloor;

                    // Steady-state usage: ignore each line's warm-up, then a clean cumulative
                    // rate (boardings/1k frames) measured from the post-warm-up baseline — far
                    // less noisy than the per-report delta and the number worth tuning against.
                    // Anchor the warm-up to the line's FIRST ACTUAL BOARDING, not its creation — with
                    // few depots a line can sit busless for a long time, and counting that idle period
                    // would poison the steady rate. Until it serves anyone it shows steady=nobus.
                    string steadyStr;
                    float steadyRate = float.NaN; // boardings/1k frames once past warm-up
                    uint firstSeen;
                    bool started = _firstFrame.TryGetValue(lineId, out firstSeen);
                    if (!started && counts.Served > 0)
                    {
                        firstSeen = nowFrame;
                        _firstFrame[lineId] = firstSeen;
                        started = true;
                    }
                    if (!started)
                    {
                        steadyStr = " steady=nobus"; // never carried anyone yet (likely waiting for a bus)
                    }
                    else if (nowFrame - firstSeen < WarmupFrames)
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
                            steadyRate = span > 0 ? 1000f * (counts.Served - baseServed) / span : 0f;
                            steadyStr = " steady=" + steadyRate.ToString("0.00") + "/1k";
                        }
                    }

                    // CAPTURE RATE = steady boardings per covered student — the demand-robust signal
                    // the optimiser tunes against (how well the route serves who it reaches).
                    string captureStr;
                    if (float.IsNaN(steadyRate))
                        captureStr = " capture=warming";
                    else if (liveCov > 0)
                        captureStr = " capture=" + (steadyRate / liveCov).ToString("0.000");
                    else
                        captureStr = " capture=n/a";

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
                        + (obsolete ? " OBSOLETE" : "")
                        + " length=" + Mathf.RoundToInt(lines[lineId].m_totalLength) + "m"
                        + " stops=" + CountStops(lineId, lines)
                        + " | usage: served=" + counts.Served + " (+" + delta + ", "
                        + perK.ToString("0.0") + "/1k frames)" + steadyStr
                        + " turnedAway=" + counts.TurnedAway
                        + " liveCov=" + liveCov + captureStr
                        + " cap=" + cap + " normUse=" + normUse.ToString("0.00")
                        + genStr);

                    // Accumulate into the per-school aggregate (study the route set "as one line").
                    if (known)
                    {
                        SchoolAgg a;
                        if (!agg.TryGetValue(schoolId, out a))
                        {
                            a = new SchoolAgg { Capacity = cap };
                            agg[schoolId] = a;
                        }
                        a.Routes++;
                        a.Stops += CountStops(lineId, lines);
                        a.Served += counts.Served;
                        a.ServedDelta += delta;
                        a.TurnedAway += counts.TurnedAway;
                        if (!float.IsNaN(steadyRate))
                            a.SteadySum += steadyRate;
                    }
                }

                // SCHOOL health: the multi-route set as a whole — union coverage, summed ridership,
                // and capture rate against students who need a bus. This is the per-school datum the
                // optimiser ultimately maximises.
                foreach (var kv in agg)
                {
                    ushort schoolId = kv.Key;
                    SchoolAgg a = kv.Value;
                    var schoolLines = SchoolLineRegistry.GetLinesForSchool(schoolId);
                    int covUnion, roster, walkers;
                    CoverageTracker.SchoolCoverage(schoolId, schoolLines, radius, out covUnion, out roster, out walkers);
                    int needBus = Mathf.Max(0, roster - walkers);
                    int covPct = needBus > 0 ? Mathf.RoundToInt(100f * covUnion / needBus) : 0;
                    string capture = covUnion > 0 ? (a.SteadySum / covUnion).ToString("0.000") : "n/a";
                    string perRoute = a.Routes > 0 ? (a.SteadySum / a.Routes).ToString("0.00") : "n/a";

                    // Echo the build params (shared across the school's routes) so combo↔capture
                    // read on one line — the join the experiment analysis needs.
                    Data.RouteMetrics.GenRecord g;
                    string built = schoolLines.Count > 0 && Data.RouteMetrics.TryGet(schoolLines[0], out g)
                        ? " | built: combo" + g.Combo + " r" + Mathf.RoundToInt(g.Radius) + " m" + g.MinStudents
                            + " pickup" + Mathf.RoundToInt(g.PickupLoop) + (g.Dynamic ? " auto" : "")
                        : "";

                    Log.DebugLog("SCHOOL health: school " + schoolId + " | routes=" + a.Routes
                        + " stops=" + a.Stops + " cap=" + a.Capacity
                        + " | served=" + a.Served + " (+" + a.ServedDelta + ", steady "
                        + a.SteadySum.ToString("0.00") + "/1k) turnedAway=" + a.TurnedAway
                        + " | cov " + covUnion + "/" + needBus + " need-bus (" + covPct + "%) walk=" + walkers
                        + " | capture=" + capture + " perRoute=" + perRoute + built);
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
