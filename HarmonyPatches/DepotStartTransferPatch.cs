using HarmonyLib;
using SchoolBuses.Data;

namespace SchoolBuses.HarmonyPatches
{
    // School-as-depot, supply side: city bus depots must NOT answer a school line's vehicle
    // request, or the line would get buses from both the depot and the school (SchoolDepot).
    // DepotAI.StartTransfer is the single place a depot supplies a line (the offer carries the
    // line id), so when the offer targets one of our mod-generated school lines we skip the
    // original — the depot ignores it and SchoolDepot spawns from the school instead.
    //
    // Scoped: anything that isn't a registered, mod-generated school line (with its school still
    // standing and the feature enabled) runs vanilla. Manual school lines keep depot supply.
    [HarmonyPatch(typeof(DepotAI), "StartTransfer")]
    internal static class DepotStartTransferPatch
    {
        private static bool Prefix(TransferManager.TransferReason reason, TransferManager.TransferOffer offer)
        {
            if (!Settings.Instance.Enabled || !Settings.Instance.SpawnFromSchool)
                return true;

            ushort lineId = offer.TransportLine;
            if (lineId == 0)
                return true;

            SchoolLineData data;
            if (!SchoolLineRegistry.TryGet(lineId, out data) || !data.ModGenerated)
                return true;

            // If the school was demolished the school can't supply the line any more — let the
            // depot serve it rather than leaving it permanently busless.
            var buildings = ColossalFramework.Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            if (data.SchoolBuildingId == 0
                || (buildings[data.SchoolBuildingId].m_flags & Building.Flags.Created) == Building.Flags.None)
                return true;

            return false; // school line: the school is the depot — skip the city depot's supply
        }
    }
}
