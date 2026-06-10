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

            var schoolDepot = (UIComponent)general.AddCheckbox("Buses spawn from the school (no depot needed)",
                s.SpawnFromSchool,
                v => { Settings.Instance.SpawnFromSchool = v; Settings.Save(); });
            schoolDepot.tooltip = "Generated school routes get their bus from the school itself, like a real\n"
                + "school: it spawns there and parks back there. No bus depot required.\n"
                + "Turn off to supply school lines from your city's bus depots instead.";

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

            radius = (UIComponent)AddValueSlider(routing,"Cluster radius (m)", 150f, 800f, 25f, s.ClusterRadius,
                v => { Settings.Instance.ClusterRadius = v; Settings.Save(); });
            radius.tooltip = "Student homes within this distance of each other form one pickup\n"
                + "neighbourhood (one stop). Larger = fewer, bigger stops.";
            routing.AddSpace(6);

            minStudents = (UIComponent)AddValueSlider(routing,"Min students / cluster", 1f, 40f, 1f, s.MinClusterStudents,
                v => { Settings.Instance.MinClusterStudents = UnityEngine.Mathf.RoundToInt(v); Settings.Save(); });
            minStudents.tooltip = "A neighbourhood only gets a stop if at least this many students\n"
                + "live in it. Higher = fewer stops (small clusters are skipped).";
            routing.AddSpace(6);

            // Reflect the saved state: the manual knobs are greyed out while auto-tune is on.
            SetManualKnobsEnabled(radius, minStudents, !s.DynamicStopCount);

            var scaleMin = (UIComponent)routing.AddCheckbox("Scale min by capacity", s.ScaleMinByCapacity,
                v => { Settings.Instance.ScaleMinByCapacity = v; Settings.Save(); });
            scaleMin.tooltip = "When on, the 'minimum students per cluster' is set per school from its capacity\n"
                + "(small schools use fewer so they still get stops; big schools use more to limit buses).\n"
                + "Works for modded school sizes. Set the 1000-capacity reference below.";

            var capMin = (UIComponent)AddValueSlider(routing,"Students / cluster @ 1000 cap", 2f, 20f, 1f, s.CapacityMinFactor * 1000f,
                v => { Settings.Instance.CapacityMinFactor = v / 1000f; Settings.Save(); });
            capMin.tooltip = "Reference for capacity scaling: a 1000-capacity school uses this many students\n"
                + "per cluster; others scale linearly (clamped 4–14). Only used when scaling is on.";
            routing.AddSpace(6);

            var autoRegen = (UIComponent)routing.AddCheckbox("Auto-regenerate on coverage drift", s.AutoRegenerate,
                v => { Settings.Instance.AutoRegenerate = v; Settings.Save(); });
            autoRegen.tooltip = "Periodically re-check each school: if students have moved so its routes now cover\n"
                + "fewer than the target below, automatically regenerate that school (current settings),\n"
                + "so stops follow the students. Turn OFF to regenerate lines only by hand.";

            var covTarget = (UIComponent)AddValueSlider(routing,"Auto-regen below coverage (%)", 0f, 80f, 5f, s.MinCoverageTarget * 100f,
                v => { Settings.Instance.MinCoverageTarget = v / 100f; Settings.Save(); });
            covTarget.tooltip = "Coverage of bus-needing students below which auto-regenerate kicks in for a school.\n"
                + "0 = never. Only used when 'Auto-regenerate' above is on.";
            routing.AddSpace(6);

            var routeLen = (UIComponent)AddValueSlider(routing,"Max pickup-loop / route (m)", 800f, 4000f, 200f, s.MaxRouteLength,
                v => { Settings.Instance.MaxRouteLength = v; Settings.Save(); });
            routeLen.tooltip = "Per-route budget for driving BETWEEN pickups (the trunk to/from the\n"
                + "school is excluded). A school's stops split into several short one-bus routes so\n"
                + "no route's pickup loop exceeds this. Nearby neighbourhoods chain into one route;\n"
                + "spread-out ones split. Shorter = more, shorter routes (one bus keeps up better).";
            routing.AddSpace(6);

            var maxRoutes = (UIComponent)AddValueSlider(routing,"Trim routes above (per 1000 cap)", 0f, 20f, 1f, s.MaxRoutesPerSchool,
                v => { Settings.Instance.MaxRoutesPerSchool = UnityEngine.Mathf.RoundToInt(v); Settings.Save(); });
            maxRoutes.tooltip = "If a school exceeds this many routes (SCALED by its capacity, clamped 3–16:\n"
                + "so ~10 for a 1000-capacity school, ~3 for a 300 one), drop neighbourhoods beyond the\n"
                + "catchment distance below and re-route. Bounds only spread-out outliers; compact\n"
                + "schools keep full coverage. 0 = never trim (route count follows min-students).";
            routing.AddSpace(6);

            var catchment = (UIComponent)AddValueSlider(routing,"Catchment distance (m)", 1000f, 5000f, 250f, s.MaxCatchmentDistance,
                v => { Settings.Instance.MaxCatchmentDistance = v; Settings.Save(); });
            catchment.tooltip = "Used only when the route trigger above fires: neighbourhoods farther than this from\n"
                + "the school are dropped (too far for a school bus). Ignored if the trigger is 0.";
            routing.AddSpace(6);

            var coverage = (UIComponent)AddValueSlider(routing,"Coverage warning threshold (%)", 30f, 95f, 5f, s.CoverageThreshold * 100f,
                v => { Settings.Instance.CoverageThreshold = v / 100f; Settings.Save(); });
            routing.AddSpace(6);

            // A touch smaller so the longer rows fit comfortably.
            foreach (UIComponent ctl in new[] { radius, minStudents, scaleMin, capMin, autoRegen,
                covTarget, routeLen, maxRoutes, catchment, coverage, dynamic })
                ShrinkLabel(ctl);

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
                    "Deletes all existing school routes and rebuilds a set for every school.\n\n"
                    + "It may create many lines (stops before the ~256 limit), each needs a bus "
                    + "(too few depots = idle routes), and can briefly slow the game.\n\n"
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

        // UIHelper sliders don't render their value, so wire the current value into the row's label
        // ("Label: 42") and keep it updated as the slider moves. Returns the slider as a UIComponent.
        private static UISlider AddValueSlider(UIHelperBase group, string label, float min, float max,
            float step, float value, OnValueChanged onChange)
        {
            UILabel title = null;
            var slider = (UISlider)group.AddSlider(label, min, max, step, value, v =>
            {
                if (title != null)
                    title.text = label + ": " + UnityEngine.Mathf.RoundToInt(v);
                onChange(v);
            });
            title = FindRowLabel(slider);
            if (title != null)
                title.text = label + ": " + UnityEngine.Mathf.RoundToInt(value);
            return slider;
        }

        private static UILabel FindRowLabel(UIComponent slider)
        {
            if (slider == null || slider.parent == null)
                return null;
            foreach (UIComponent child in slider.parent.components)
            {
                var label = child as UILabel;
                if (label != null)
                    return label;
            }
            return null;
        }

        // Shrink a control's label a touch so the longer option rows fit. A checkbox carries its
        // label as a child; a slider's label is a sibling in its row panel — so scan both. Best-effort.
        private const float OptionLabelScale = 0.85f;
        private static void ShrinkLabel(UIComponent c)
        {
            if (c == null)
                return;
            ScaleChildLabels(c);
            ScaleChildLabels(c.parent);
        }

        private static void ScaleChildLabels(UIComponent parent)
        {
            if (parent == null)
                return;
            foreach (UIComponent child in parent.components)
            {
                var label = child as UILabel;
                if (label != null)
                    label.textScale = OptionLabelScale;
            }
        }
    }
}
