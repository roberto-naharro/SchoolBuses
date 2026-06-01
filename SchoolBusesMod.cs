using System.Reflection;
using CitiesHarmony.API;
using ColossalFramework.UI;
using HarmonyLib;
using ICities;
using SchoolBuses.HarmonyPatches;
using SchoolBuses.Routing;
using SchoolBuses.Util;

namespace SchoolBuses
{
    // Entry point. IUserMod is discovered by the game; Harmony patches are applied on
    // enable and removed on disable (CitiesHarmony ensures Harmony is installed).
    public class SchoolBusesMod : IUserMod
    {
        public string Name => "School Buses";
        public string Description => "Makes school bus lines carry only students to and from their school, with one-click route generation.";

        public void OnEnabled()
        {
            // Apply the saved debug-logging preference immediately so startup logs honour it.
            Log.DebugEnabled = Settings.Instance.DebugLogging;
            HarmonyHelper.EnsureHarmonyInstalled();
            HarmonyHelper.DoOnHarmonyReady(() =>
            {
                try
                {
                    var harmony = new Harmony(HarmonyId.Value);
                    harmony.PatchAll(Assembly.GetExecutingAssembly());
                    Log.Info("Harmony patches applied (debug logging "
                        + (Log.DebugEnabled ? "ON" : "OFF") + ")");
                }
                catch (System.Exception ex)
                {
                    Log.Error("PatchAll failed: " + ex);
                }
            });
        }

        public void OnDisabled()
        {
            if (HarmonyHelper.IsHarmonyInstalled)
            {
                new Harmony(HarmonyId.Value).UnpatchAll(HarmonyId.Value);
                Log.Info("Harmony patches removed");
            }
        }

        public void OnSettingsUI(UIHelperBase helper)
        {
            Settings s = Settings.Instance;

            var general = helper.AddGroup("School Buses");
            general.AddCheckbox("Enable mod", s.Enabled,
                v => { Settings.Instance.Enabled = v; Settings.Save(); });

            var evict = (UIComponent)general.AddCheckbox(
                "Send non-students away from school stops", s.EvictIneligibleRiders,
                v => { Settings.Instance.EvictIneligibleRiders = v; Settings.Save(); });
            evict.tooltip =
                "Commuters who can't board a school bus will give up and find another route\n"
                + "instead of piling up at the stop forever.";

            var routing = helper.AddGroup("Route generation");

            // Declared first so the checkbox handler (below) can enable/disable them; assigned
            // when the sliders are created just after.
            UIComponent radius = null;
            UIComponent minStudents = null;

            var dynamic = (UIComponent)routing.AddCheckbox(
                "Auto-tune number of stops", s.DynamicStopCount,
                v =>
                {
                    Settings.Instance.DynamicStopCount = v;
                    Settings.Save();
                    SetManualKnobsEnabled(radius, minStudents, !v);
                });
            dynamic.tooltip =
                "Size the route automatically from the school's student count and the bus's\n"
                + "passenger capacity (accounting for students not all travelling at once).\n"
                + "Turn off for manual control with the two sliders below.";

            radius = (UIComponent)routing.AddSlider("Maximum cluster radius (m)", 150f, 800f, 25f, s.ClusterRadius,
                v => { Settings.Instance.ClusterRadius = v; Settings.Save(); });
            radius.tooltip = "Student homes within this distance of each other form one pickup\n"
                + "neighbourhood (one stop). Larger = fewer, bigger stops.";

            minStudents = (UIComponent)routing.AddSlider("Minimum students per cluster", 1f, 40f, 1f, s.MinClusterStudents,
                v => { Settings.Instance.MinClusterStudents = UnityEngine.Mathf.RoundToInt(v); Settings.Save(); });
            minStudents.tooltip = "A neighbourhood only gets a stop if at least this many students\n"
                + "live in it. Higher = fewer stops (small clusters are skipped).";

            // Reflect the saved state: the manual knobs are greyed out while auto-tune is on.
            SetManualKnobsEnabled(radius, minStudents, !s.DynamicStopCount);

            var scaleMin = (UIComponent)routing.AddCheckbox("Scale min-students by school capacity", s.ScaleMinByCapacity,
                v => { Settings.Instance.ScaleMinByCapacity = v; Settings.Save(); });
            scaleMin.tooltip = "When on, the 'minimum students per cluster' is set per school from its capacity\n"
                + "(small schools use fewer so they still get stops; big schools use more to limit buses).\n"
                + "Works for modded school sizes. Set the 1000-capacity reference below.";

            var capMin = (UIComponent)routing.AddSlider("Min students per cluster at 1000 capacity", 2f, 20f, 1f, s.CapacityMinFactor * 1000f,
                v => { Settings.Instance.CapacityMinFactor = v / 1000f; Settings.Save(); });
            capMin.tooltip = "Reference for capacity scaling: a 1000-capacity school uses this many students\n"
                + "per cluster; others scale linearly (clamped 4–14). Only used when scaling is on.";

            var autoRegen = (UIComponent)routing.AddCheckbox("Auto-regenerate routes when coverage drifts", s.AutoRegenerate,
                v => { Settings.Instance.AutoRegenerate = v; Settings.Save(); });
            autoRegen.tooltip = "Periodically re-check each school: if students have moved so its routes now cover\n"
                + "fewer than the target below, automatically regenerate that school (current settings),\n"
                + "so stops follow the students. Turn OFF to regenerate lines only by hand.";

            var covTarget = (UIComponent)routing.AddSlider("Auto-regenerate below coverage (%)", 0f, 80f, 5f, s.MinCoverageTarget * 100f,
                v => { Settings.Instance.MinCoverageTarget = v / 100f; Settings.Save(); });
            covTarget.tooltip = "Coverage of bus-needing students below which auto-regenerate kicks in for a school.\n"
                + "0 = never. Only used when 'Auto-regenerate' above is on.";

            var routeLen = (UIComponent)routing.AddSlider("Max pickup-loop length per route (m)", 800f, 4000f, 200f, s.MaxRouteLength,
                v => { Settings.Instance.MaxRouteLength = v; Settings.Save(); });
            routeLen.tooltip = "Per-route budget for driving BETWEEN pickups (the trunk to/from the\n"
                + "school is excluded). A school's stops split into several short one-bus routes so\n"
                + "no route's pickup loop exceeds this. Nearby neighbourhoods chain into one route;\n"
                + "spread-out ones split. Shorter = more, shorter routes (one bus keeps up better).";

            var maxRoutes = (UIComponent)routing.AddSlider("Trim routes above (per 1000 capacity, 0 = never)", 0f, 20f, 1f, s.MaxRoutesPerSchool,
                v => { Settings.Instance.MaxRoutesPerSchool = UnityEngine.Mathf.RoundToInt(v); Settings.Save(); });
            maxRoutes.tooltip = "If a school exceeds this many routes (SCALED by its capacity, clamped 3–16:\n"
                + "so ~10 for a 1000-capacity school, ~3 for a 300 one), drop neighbourhoods beyond the\n"
                + "catchment distance below and re-route. Bounds only spread-out outliers; compact\n"
                + "schools keep full coverage. 0 = never trim (route count follows min-students).";

            var catchment = (UIComponent)routing.AddSlider("Catchment distance when trimming (m)", 1000f, 5000f, 250f, s.MaxCatchmentDistance,
                v => { Settings.Instance.MaxCatchmentDistance = v; Settings.Save(); });
            catchment.tooltip = "Used only when the route trigger above fires: neighbourhoods farther than this from\n"
                + "the school are dropped (too far for a school bus). Ignored if the trigger is 0.";

            var coverage = (UIComponent)routing.AddSlider("Coverage warning threshold (%)", 30f, 95f, 5f, s.CoverageThreshold * 100f,
                v => { Settings.Instance.CoverageThreshold = v / 100f; Settings.Save(); });

            // Restore the recommended (experiment-tuned) defaults. Setting each control's value fires
            // its handler, which writes Settings + saves.
            routing.AddButton("Restore recommended defaults", () =>
            {
                var dyn = dynamic as UICheckBox;
                if (dyn != null) dyn.isChecked = false;
                var rad = radius as UISlider;
                if (rad != null) rad.value = 400f;
                var min = minStudents as UISlider;
                if (min != null) min.value = 8f;
                var rl = routeLen as UISlider;
                if (rl != null) rl.value = 2000f;
                var sc = scaleMin as UICheckBox;
                if (sc != null) sc.isChecked = true;
                var cm = capMin as UISlider;
                if (cm != null) cm.value = 8f;
                var ar = autoRegen as UICheckBox;
                if (ar != null) ar.isChecked = true;
                var ct = covTarget as UISlider;
                if (ct != null) ct.value = 30f;
                var mr = maxRoutes as UISlider;
                if (mr != null) mr.value = 0f; // trim off by default
                var ca = catchment as UISlider;
                if (ca != null) ca.value = 2500f;
                var cov = coverage as UISlider;
                if (cov != null) cov.value = 70f;
                Settings.Save();
                Log.Info("Settings restored to recommended defaults (auto-tune off, r400, scale-min on @8/1000, p2000)");
            });

            // City-wide actions: route or clear every school in one click.
            var city = helper.AddGroup("City-wide routes");
            city.AddButton("Generate routes for ALL schools", () =>
            {
                ConfirmPanel.ShowModal("Generate routes for all schools",
                    "This DELETES every existing school bus route and rebuilds a fresh set for "
                    + "EVERY school in the city.\n\n"
                    + "Be aware:\n"
                    + "(1) It can create a lot of lines (possibly near the game's ~256 transport-line "
                    + "limit; generation stops before the limit and tells you how many were skipped).\n"
                    + "(2) Each route runs ONE bus (without enough bus depots, many routes will sit idle).\n"
                    + "(3) Creating this many lines at once can briefly slow the game.\n\n"
                    + "Continue?",
                    (comp, ret) =>
                    {
                        if (ret != 1)
                            return;
                        Log.Info("Generate-all-schools: starting…");
                        RouteGenerator.GenerateAllSchools(msg => Log.Info("Generate-all-schools: " + msg));
                    });
            });
            city.AddButton("Delete ALL school routes", () =>
            {
                RouteGenerator.DeleteAllSchools(n => Log.Info("Deleted " + n + " school route(s)"));
            });

            // Parameter-tuning experiment (advanced/dev): samples schools, assigns one setting per
            // run, 15-min auto-stop; the agent reads SCHOOL health capture from the log. Requires
            // debug logging on.
            var exp = helper.AddGroup("Experiment (parameter tuning — advanced)");
            exp.AddButton("Run experiment across sampled schools", () =>
            {
                Log.Info("EXP: starting experiment run…");
                RouteGenerator.GenerateAllSchoolsExperiment(msg => Log.Info("EXP result: " + msg));
            });

            var dbg = helper.AddGroup("Debug");
            dbg.AddCheckbox("Enable debug logging", s.DebugLogging,
                v => { Settings.Instance.DebugLogging = v; Log.DebugEnabled = v; Settings.Save(); });
        }

        // Grey out / re-enable the manual cluster sliders (used while auto-tune is on).
        private static void SetManualKnobsEnabled(UIComponent radius, UIComponent minStudents, bool enabled)
        {
            SetKnobEnabled(radius, enabled);
            SetKnobEnabled(minStudents, enabled);
        }

        private static void SetKnobEnabled(UIComponent c, bool enabled)
        {
            if (c == null)
                return;
            c.isEnabled = enabled;
            c.opacity = enabled ? 1f : 0.35f;
            if (c.parent != null)
                c.parent.opacity = enabled ? 1f : 0.35f; // dim the whole row (label + slider)
        }
    }
}
