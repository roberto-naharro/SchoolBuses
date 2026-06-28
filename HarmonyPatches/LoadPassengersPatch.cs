using HarmonyLib;
using SchoolBuses.Data;
using SchoolBuses.Routing;
using SchoolBuses.Util;

namespace SchoolBuses.HarmonyPatches
{
    // Marks "we are inside a school bus's boarding loop" so the HumanAI
    // TransportArriveAtSource patch can veto ineligible riders (design §4.3, §5).
    //
    // This prefix returns void => the original BusAI.LoadPassengers (or whoever else
    // patched it) runs unchanged. We do NOT reimplement or skip the loop, so we never
    // collide with mods that do (IPT, EBS, Better Train Boarding). The actual filtering
    // happens one level down, inside TransportArriveAtSource, which both vanilla and EBS
    // call per citizen. A mod that boards while bypassing TransportArriveAtSource (BTB)
    // simply isn't filtered on that line — graceful degradation, never double-boarding.
    //
    // Priority.First so the context is set before any lower-priority prefix (EBS/IPT/BTB)
    // runs its own boarding pass and triggers TransportArriveAtSource.
    //
    // v1 scope: buses only. TramAI/TrolleybusAI would be one extra [HarmonyPatch] each.
    [HarmonyPatch(typeof(BusAI), "LoadPassengers")]
    [HarmonyPriority(Priority.First)]
    internal static class LoadPassengersPatch
    {
        private static void Prefix(ref Vehicle data, ushort currentStop)
        {
            ushort lineId = data.m_transportLine;
            SchoolLineData line;
            if (lineId != 0 && SchoolLineRegistry.TryGet(lineId, out line))
                // Evaluate the service state once per boarding call (shared by every citizen the
                // loop considers): when this line is closed, the gate refuses all boarding so the bus
                // empties.
                SchoolBoardingContext.Set(lineId, currentStop, line, !SchoolDepot.ServiceOpenNow(lineId));
            else
                SchoolBoardingContext.Clear();
        }

        // Always runs (Harmony fires postfixes even when a prefix skipped the original),
        // so the context never leaks past this LoadPassengers call.
        private static void Postfix(ushort vehicleID, ushort currentStop)
        {
            if (Log.DebugEnabled && SchoolBoardingContext.Active
                && (SchoolBoardingContext.ServedThisCall > 0 || SchoolBoardingContext.TurnedAwayThisCall > 0))
            {
                Log.DebugLog("Boarding: line " + SchoolBoardingContext.CurrentLine
                    + " stop " + currentStop + " vehicle " + vehicleID
                    + " — boarded " + SchoolBoardingContext.ServedThisCall + " eligible, skipped "
                    + SchoolBoardingContext.TurnedAwayThisCall + " ineligible");
            }
            SchoolBoardingContext.Clear();
        }
    }
}
