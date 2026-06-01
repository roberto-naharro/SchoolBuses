using SchoolBuses.Util;
using UnityEngine;

namespace SchoolBuses.Routing
{
    // A real-time countdown for an experiment run: started when the user clicks "generate all
    // schools (experiment)", it elapses after ExperimentMinutes of wall-clock time (counts even
    // while paused — it's a watch, not sim time). When it fires, the manager logs an END marker +
    // a final SCHOOL health snapshot and pops up a "close the game" dialog, so the user knows the
    // data is collected. Started/stopped from the sim thread, ticked from the main thread (OnUpdate).
    internal static class ExperimentClock
    {
        private const float DurationSeconds = 15f * 60f;

        private static volatile bool _active;
        private static float _elapsed;

        internal static bool Active => _active;
        internal static int RemainingSeconds => _active ? Mathf.Max(0, (int)(DurationSeconds - _elapsed)) : 0;

        internal static void Start()
        {
            _elapsed = 0f;
            _active = true;
            Log.Info("EXP countdown started — auto-stop in " + (int)(DurationSeconds / 60f) + " minutes");
        }

        internal static void Stop()
        {
            _active = false;
        }

        // Returns true exactly once, on the tick where the window elapses.
        internal static bool Tick(float realSeconds)
        {
            if (!_active)
                return false;
            _elapsed += realSeconds;
            if (_elapsed >= DurationSeconds)
            {
                _active = false;
                return true;
            }
            return false;
        }
    }
}
