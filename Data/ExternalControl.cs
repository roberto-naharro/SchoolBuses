using System.Collections.Generic;

namespace SchoolBuses.Data
{
    // Runtime controls a PARTNER MOD pushes into School Buses through the public SchoolBusBridge
    // API. The whole point: a partner (Real Time, TLM, …) drives our behaviour by CALLING US, so we
    // never reach into its internals (Dependency Inversion) and need no per-mod code.
    //
    // Two mutually-exclusive integration styles:
    //   • Simple  — a single service window via SetServiceHours(); used while external control is OFF.
    //   • Advanced — engage external control, then PauseSpawning/ResumeSpawning + supply toggles
    //     (global, or per-line when a line id is given). Lets a mod express any number of time
    //     ranges (just pause/resume whenever) and hand the whole fleet over (supply disabled).
    //
    // All state is in-memory only (never persisted): it takes precedence over the player's options
    // while active and falls back the moment it is cleared. Reads happen on the simulation thread
    // and are LOCK-FREE — volatile scalars, and copy-on-write maps for the per-line overrides (a
    // reader never sees a half-mutated map); the rare writes serialise on a single lock.
    internal static class ExternalControl
    {
        // ───────────────────────── service window (simple path) ─────────────────────────
        private sealed class Window
        {
            internal readonly float Start;
            internal readonly float End;
            internal Window(float start, float end) { Start = start; End = end; }
        }

        private static volatile Window _serviceWindow; // null = no external window in force

        internal static bool HasServiceHours => _serviceWindow != null;

        internal static bool TryGetServiceHours(out float start, out float end)
        {
            Window w = _serviceWindow;
            if (w == null)
            {
                start = 0f;
                end = 0f;
                return false;
            }
            start = w.Start;
            end = w.End;
            return true;
        }

        internal static void SetServiceHours(float start, float end) => _serviceWindow = new Window(start, end);
        internal static void ClearServiceHours() => _serviceWindow = null;

        // ──────────────────────── external control (advanced path) ───────────────────────
        private static volatile bool _externalControl;
        private static volatile bool _globalPaused;
        private static volatile bool _globalSupplyEnabled = true;

        // Per-line explicit overrides; a line ABSENT from the map follows the global value. Maps are
        // replaced wholesale (copy-on-write), so the volatile read below is always a complete map.
        private static volatile Dictionary<ushort, bool> _linePaused = new Dictionary<ushort, bool>();
        private static volatile Dictionary<ushort, bool> _lineSupply = new Dictionary<ushort, bool>();
        private static readonly object _writeLock = new object();

        // True while a partner mod holds spawn control (the master flag).
        internal static bool ExternalControlEngaged => _externalControl;
        internal static void SetExternalControl(bool engaged) => _externalControl = engaged;

        // Pause/resume (advanced). IsSpawningPaused is the EFFECTIVE value for a line: a per-line
        // override wins, otherwise the global flag.
        internal static bool GlobalPaused => _globalPaused;
        internal static void SetGlobalPaused(bool paused) => _globalPaused = paused;

        internal static void SetLinePaused(ushort lineId, bool paused)
        {
            if (lineId == 0)
                return;
            lock (_writeLock)
            {
                var copy = new Dictionary<ushort, bool>(_linePaused);
                copy[lineId] = paused;
                _linePaused = copy;
            }
        }

        internal static bool IsSpawningPaused(ushort lineId)
        {
            Dictionary<ushort, bool> map = _linePaused;
            bool v;
            if (lineId != 0 && map.TryGetValue(lineId, out v))
                return v;
            return _globalPaused;
        }

        // Vehicle supply (advanced). IsVehicleSupplyEnabled is the EFFECTIVE value for a line: a
        // per-line override wins, otherwise the global flag (default true = the school supplies it).
        internal static bool GlobalSupplyEnabled => _globalSupplyEnabled;
        internal static void SetGlobalSupplyEnabled(bool enabled) => _globalSupplyEnabled = enabled;

        internal static void SetLineSupplyEnabled(ushort lineId, bool enabled)
        {
            if (lineId == 0)
                return;
            lock (_writeLock)
            {
                var copy = new Dictionary<ushort, bool>(_lineSupply);
                copy[lineId] = enabled;
                _lineSupply = copy;
            }
        }

        internal static bool IsVehicleSupplyEnabled(ushort lineId)
        {
            Dictionary<ushort, bool> map = _lineSupply;
            bool v;
            if (lineId != 0 && map.TryGetValue(lineId, out v))
                return v;
            return _globalSupplyEnabled;
        }

        // ───────────────────────── panel placement (UI) ─────────────────────────
        // Lets a partner mod move School Buses' own side panels (the school-building routes panel and
        // the school-line panel) out of its UI. Independent of spawn control — pure presentation.
        //   side: 0 = School Buses' own automatic choice, 1 = force right of the info panel, 2 = left
        //   PanelTopOffset: extra pixels added to the panels' default vertical offset.
        private static volatile int _panelSide;       // 0 = auto, 1 = right, 2 = left
        private static volatile float _panelTopOffset; // extra px (default 0)

        internal static int PanelSide => _panelSide;
        internal static void SetPanelSide(int side) => _panelSide = side;

        internal static float PanelTopOffset => _panelTopOffset;
        internal static void SetPanelTopOffset(float pixels) => _panelTopOffset = pixels;
    }
}
