using HarmonyLib;
using SchoolBuses.Data;

namespace SchoolBuses.HarmonyPatches
{
    // Keeps the registry clean when a line is deleted (by the player or the game).
    // Postfix so it fires after the line is actually released.
    [HarmonyPatch(typeof(TransportManager), "ReleaseLine")]
    internal static class ReleaseLinePatch
    {
        private static void Postfix(ushort line)
        {
            SchoolLineRegistry.Unregister(line);
        }
    }
}
