using HarmonyLib;
using SchoolBuses.Data;
using SchoolBuses.Util;

namespace SchoolBuses.HarmonyPatches
{
    // Keeps the registry clean when a line is deleted (by the player or the game).
    // Postfix so it fires after the line is actually released.
    [HarmonyPatch(typeof(TransportManager), "ReleaseLine")]
    internal static class ReleaseLinePatch
    {
        // Parameter name must match the game method's parameter ("lineID") — Harmony binds
        // injected originals by name.
        private static void Postfix(ushort lineID)
        {
            SchoolLineRegistry.Unregister(lineID);
            BoardingStats.Remove(lineID);
            RouteMetrics.Remove(lineID);
            LineFinalizer.Cancel(lineID);
        }
    }
}
