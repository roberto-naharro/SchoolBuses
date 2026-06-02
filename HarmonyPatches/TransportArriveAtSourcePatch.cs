using HarmonyLib;
using SchoolBuses.Data;
using SchoolBuses.Util;

namespace SchoolBuses.HarmonyPatches
{
    // The per-citizen boarding gate. Patches HumanAI.TransportArriveAtSource — the override
    // that actually runs for residents/tourists (CitizenAI declares it virtual; HumanAI
    // overrides it; the boarding loop calls it per waiting citizen via callvirt).
    //
    // We run as a POSTFIX (not a prefix): vanilla TransportArriveAtSource is given this hop's
    // current+next stop, so it returns TRUE only for a citizen whose path actually boards THIS
    // line here — a city-line waiter at a shared stop returns false. That lets us act ONLY on
    // genuine would-be boarders of this school line: if such a citizen is ineligible we veto the
    // boarding (set __result=false) AND make them give up (BoredOfWaiting), so they re-route
    // instead of waiting for a school bus that will never take them. Because we key off vanilla's
    // own true/false, eviction is unambiguous even when the school line shares a stop with another
    // line — no node-line guessing. Inactive context (any non-school line) = no-op.
    [HarmonyPatch(typeof(HumanAI), "TransportArriveAtSource")]
    internal static class TransportArriveAtSourcePatch
    {
        private static void Postfix(ref CitizenInstance citizenData, ref bool __result)
        {
            if (!__result || !SchoolBoardingContext.Active)
                return; // not boarding this hop, or not a school line: leave it alone

            bool eligible = CitizenEligibility.IsEligible(
                citizenData.m_citizen,
                citizenData.m_targetBuilding,
                SchoolBoardingContext.CurrentStop,
                ref SchoolBoardingContext.Line);

            if (eligible)
                return;

            // Genuine but ineligible would-be boarder of this school line.
            BoardingStats.RecordTurnedAway(SchoolBoardingContext.CurrentLine);
            SchoolBoardingContext.TurnedAwayThisCall++;
            __result = false; // refuse the boarding (same effect as "not at source")

            // Evict: make them stop waiting for a bus that will never take them, so they re-route.
            if ((citizenData.m_flags & CitizenInstance.Flags.BoredOfWaiting) == CitizenInstance.Flags.None)
            {
                citizenData.m_flags |= CitizenInstance.Flags.BoredOfWaiting;
                citizenData.m_waitCounter = byte.MaxValue;
            }
        }
    }
}
