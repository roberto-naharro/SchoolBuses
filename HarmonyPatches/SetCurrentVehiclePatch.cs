using HarmonyLib;
using SchoolBuses.Data;

namespace SchoolBuses.HarmonyPatches
{
    // Observe-only: counts eligible students who actually board a school bus, for the
    // "served this session" panel stat. Postfix on HumanAI.SetCurrentVehicle — it never
    // changes the result or any behaviour, so it composes with every other mod.
    //
    // While a school bus's LoadPassengers loop is running (SchoolBoardingContext.Active),
    // ineligible citizens were already vetoed by TransportArriveAtSourcePatch, so any
    // successful SetCurrentVehicle here is an eligible student boarding this line.
    [HarmonyPatch(typeof(HumanAI), "SetCurrentVehicle")]
    internal static class SetCurrentVehiclePatch
    {
        private static void Postfix(bool __result)
        {
            if (__result && SchoolBoardingContext.Active)
            {
                BoardingStats.RecordServed(SchoolBoardingContext.CurrentLine);
                SchoolBoardingContext.ServedThisCall++;
            }
        }
    }
}
