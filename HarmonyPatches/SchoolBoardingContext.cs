using System;
using SchoolBuses.Data;

namespace SchoolBuses.HarmonyPatches
{
    // Thread-local bridge between the BusAI.LoadPassengers patch (which knows the line)
    // and the HumanAI.TransportArriveAtSource patch (which decides per citizen but has
    // no line/vehicle id). LoadPassengers runs the boarding loop synchronously on the
    // simulation thread, so for the duration of one LoadPassengers call every
    // TransportArriveAtSource it triggers sees the correct context.
    //
    // [ThreadStatic] keeps it correct even if the game ever boards on multiple threads.
    internal static class SchoolBoardingContext
    {
        [ThreadStatic] internal static bool Active;
        [ThreadStatic] internal static ushort CurrentLine;
        [ThreadStatic] internal static ushort CurrentStop;
        [ThreadStatic] internal static SchoolLineData Line;

        // Per-LoadPassengers-call tallies, used only for the optional debug summary.
        [ThreadStatic] internal static int ServedThisCall;
        [ThreadStatic] internal static int TurnedAwayThisCall;

        internal static void Set(ushort lineId, ushort currentStop, SchoolLineData line)
        {
            Active = true;
            CurrentLine = lineId;
            CurrentStop = currentStop;
            Line = line;
            ServedThisCall = 0;
            TurnedAwayThisCall = 0;
        }

        internal static void Clear()
        {
            Active = false;
        }
    }
}
