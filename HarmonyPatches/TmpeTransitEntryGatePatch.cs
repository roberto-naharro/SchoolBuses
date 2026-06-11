using System;
using System.Reflection;
using HarmonyLib;
using SchoolBuses.Util;

namespace SchoolBuses.HarmonyPatches
{
    // TM:PE replaces the game's pathfinder with its own CustomPathFind, so the school-line gate on
    // the vanilla PathFind never runs under it. This is a RUNTIME HARMONY PATCH on TM:PE's method —
    // we never edit TM:PE itself, need nothing from its authors, and its files stay untouched:
    // Harmony rewrites the method IN MEMORY at load, exactly like every patch we apply to the
    // game's own code. TM:PE's pathfinder is a faithful port that KEEPS the same seam
    // (source-verified against the TM:PE repo):
    //   TrafficManager.Custom.PathFinding.CustomPathFind
    //     .ProcessItemPublicTransport(..., ushort nextSegmentId, ref NetSegment nextSegment, ...)
    //   private uint calculating_;   // their equivalent of vanilla PathFind.m_calculating
    // We apply the SAME prefix to their method at level load — resolved entirely by reflection
    // (no hard dependency, any TM:PE edition whose member names match). Anything missing → log and
    // fall back to boarding-time rejection + eviction, exactly as before. The prefix can only ever
    // SKIP transit entry for school-line segments; every other TM:PE behaviour passes through.
    //
    // Our unit→citizen bookkeeping keeps working under TM:PE because Harmony postfixes always run:
    // CitizenAI.StartPathFind still assigns m_path (through TM:PE's own implementation), and
    // PathManager.ReleasePath still fires.
    internal static class TmpeTransitEntryGatePatch
    {
        private const string TypeName = "TrafficManager.Custom.PathFinding.CustomPathFind";

        // TM:PE prefix-replaces CitizenAI.StartPathFind and funnels ALL citizen path creation
        // (including Parking-AI walk legs created by its internal managers, which never touch the
        // vanilla method) through this manager. Without a postfix HERE, those paths get no owner
        // recorded → the gate fails open for them → adults still route onto school lines
        // (observed in-game: non-students waiting at school stops with the gate applied).
        private const string ExtManagerTypeName = "TrafficManager.Manager.Impl.ExtCitizenInstanceManager";

        private static bool _attempted;

        internal static void TryApply()
        {
            if (_attempted)
                return; // assemblies never unload — one attempt per game session is enough
            _attempted = true;

            try
            {
                Type pathFind = FindType(TypeName);
                if (pathFind == null)
                {
                    Log.DebugLog("TM:PE not present — school-line pathfind gate on vanilla only");
                    return;
                }

                MethodInfo target = AccessTools.Method(pathFind, "ProcessItemPublicTransport");
                FieldInfo unitField = AccessTools.Field(pathFind, "calculating_");
                if (target == null || unitField == null || unitField.FieldType != typeof(uint))
                {
                    Log.Warning("TM:PE detected but its pathfinder has an unexpected shape — "
                        + "school lines stay visible to its planner (boarding rejection still applies)");
                    return;
                }

                var harmony = new Harmony(HarmonyId.Value);
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(TmpeTransitEntryGatePatch), nameof(Prefix)));
                Log.Info("TM:PE detected — school-line pathfind gate applied to its custom pathfinder");

                // Ownership funnel: TM:PE creates citizen paths through its ExtCitizenInstanceManager
                // (the vanilla CitizenAI.StartPathFind is prefix-skipped, and Parking-AI legs call
                // this manager directly), so record unit→citizen here as well — otherwise those
                // paths have no owner and the gate fails open for them.
                Type extManager = FindType(ExtManagerTypeName);
                MethodInfo extStart = extManager != null
                    ? AccessTools.Method(extManager, "StartPathFind") : null;
                if (extStart == null)
                {
                    Log.Warning("TM:PE citizen path funnel not found — school-line hiding may not "
                        + "cover Parking-AI trips (boarding rejection still applies)");
                    return;
                }
                harmony.Patch(extStart,
                    postfix: new HarmonyMethod(typeof(TmpeTransitEntryGatePatch), nameof(RecordOwnerPostfix)));
                Log.Info("TM:PE citizen path funnel hooked — school-line hiding covers Parking-AI trips");

                // TM:PE's CustomPathManager declares `public new void ReleasePath` (method HIDING):
                // calls through a CustomPathManager-typed reference bypass the vanilla
                // PathManager.ReleasePath we postfix, so owners were never cleared for those units
                // → STALE classifications on recycled unit ids (observed in-game). Hook their
                // internal release to clear ours too.
                Type pathManager = FindType("TrafficManager.Custom.PathFinding.CustomPathManager");
                MethodInfo customRelease = pathManager != null
                    ? AccessTools.Method(pathManager, "CustomReleasePath") : null;
                if (customRelease != null)
                {
                    harmony.Patch(customRelease,
                        postfix: new HarmonyMethod(typeof(TmpeTransitEntryGatePatch), nameof(ClearOwnerPostfix)));
                    Log.Info("TM:PE custom path release hooked — ownership map stays clean");
                }
                else
                {
                    Log.Warning("TM:PE CustomReleasePath not found — recycled path units may carry "
                        + "stale owners (gate may misclassify rare trips)");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("TM:PE pathfind gate hookup failed (school lines stay visible to its "
                    + "planner): " + ex.Message);
            }
        }

        // Bound onto ExtCitizenInstanceManager.StartPathFind (TM:PE's citizen path funnel). Same
        // bookkeeping as our vanilla CitizenAI.StartPathFind postfix; `instanceData` matches their
        // parameter name, their Ext* struct parameters are simply not bound.
        private static void RecordOwnerPostfix(ref CitizenInstance instanceData, bool __result)
        {
            if (__result && instanceData.m_path != 0)
                Data.PathOwnership.Set(instanceData.m_path, instanceData.m_citizen);
        }

        // Bound onto CustomPathManager.CustomReleasePath(uint unit) — TM:PE's real release funnel.
        private static void ClearOwnerPostfix(uint unit)
        {
            Data.PathOwnership.Clear(unit);
        }

        // Bound by Harmony onto CustomPathFind.ProcessItemPublicTransport. Parameter names match
        // TM:PE's (release AND debug builds — extra debug-only leading args don't matter, Harmony
        // binds by name): `nextNodeId` is the stop node being expanded, `calculating_` the unit.
        private static bool Prefix(ushort nextNodeId, uint ___calculating_)
        {
            return TransitGate.Allow(nextNodeId, ___calculating_, true);
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(fullName, false);
                if (t != null)
                    return t;
            }
            return null;
        }
    }
}
