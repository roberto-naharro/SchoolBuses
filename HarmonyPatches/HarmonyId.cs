namespace SchoolBuses.HarmonyPatches
{
    // Single source of truth for the Harmony instance id, used by both
    // PatchAll/UnpatchAll and the dynamic patches.
    internal static class HarmonyId
    {
        internal const string Value = "com.github.roberto-naharro.schoolbuses";
    }
}
