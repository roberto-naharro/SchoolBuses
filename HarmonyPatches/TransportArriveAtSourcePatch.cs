using HarmonyLib;
using SchoolBuses.Data;
using SchoolBuses.Util;

namespace SchoolBuses.HarmonyPatches
{
    // The per-citizen boarding gate. Patches HumanAI.TransportArriveAtSource — the
    // override that actually runs for residents/tourists (CitizenAI declares it virtual;
    // HumanAI overrides it; the boarding loop calls it via callvirt). This is the exact
    // method the vanilla loop uses as its per-citizen skip-gate: returning false means
    // "not my source stop, keep loading everyone else".
    //
    // We only act while inside a school bus's LoadPassengers (SchoolBoardingContext.Active);
    // otherwise the prefix is a single bool read and a passthrough — zero behaviour change
    // for every other line, vehicle and citizen in the game.
    [HarmonyPatch(typeof(HumanAI), "TransportArriveAtSource")]
    internal static class TransportArriveAtSourcePatch
    {
        // Returning false from the prefix skips the original and forces __result = false,
        // i.e. the citizen is treated as not-at-their-source — the per-citizen skip-gate.
        private static bool Prefix(ref CitizenInstance citizenData, ref bool __result)
        {
            if (!SchoolBoardingContext.Active)
                return true; // not a school line: run vanilla decision

            bool eligible = CitizenEligibility.IsEligible(
                citizenData.m_citizen,
                citizenData.m_targetBuilding,
                SchoolBoardingContext.CurrentStop,
                ref SchoolBoardingContext.Line);

            if (eligible)
                return true; // eligible: let the normal arrival logic decide

            BoardingStats.RecordTurnedAway(SchoolBoardingContext.CurrentLine);
            SchoolBoardingContext.TurnedAwayThisCall++;
            __result = false; // ineligible: skip this citizen, keep loading the rest
            return false;
        }
    }
}
