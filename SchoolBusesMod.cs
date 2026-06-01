using System.Reflection;
using CitiesHarmony.API;
using ColossalFramework.UI;
using HarmonyLib;
using ICities;
using SchoolBuses.HarmonyPatches;
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

            var routeLen = (UIComponent)routing.AddSlider("Max pickup-loop length per route (m)", 800f, 4000f, 200f, s.MaxRouteLength,
                v => { Settings.Instance.MaxRouteLength = v; Settings.Save(); });
            routeLen.tooltip = "Per-route budget for driving BETWEEN pickups (the trunk to/from the\n"
                + "school is excluded). A school's stops split into several short one-bus routes so\n"
                + "no route's pickup loop exceeds this. Nearby neighbourhoods chain into one route;\n"
                + "spread-out ones split. Shorter = more, shorter routes (one bus keeps up better).";

            routing.AddSlider("Coverage warning threshold (%)", 30f, 95f, 5f, s.CoverageThreshold * 100f,
                v => { Settings.Instance.CoverageThreshold = v / 100f; Settings.Save(); });

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
