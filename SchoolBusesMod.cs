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
            HarmonyHelper.EnsureHarmonyInstalled();
            HarmonyHelper.DoOnHarmonyReady(() =>
            {
                try
                {
                    var harmony = new Harmony(HarmonyId.Value);
                    harmony.PatchAll(Assembly.GetExecutingAssembly());
                    Log.Info("Harmony patches applied");
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

            var routing = helper.AddGroup("Route generation");

            routing.AddSlider("Stop coverage / cluster radius (m)", 100f, 600f, 25f, s.ClusterRadius,
                v => { Settings.Instance.ClusterRadius = v; Settings.Save(); });

            routing.AddSlider("Maximum stops per route", 3f, 20f, 1f, s.MaxStops,
                v => { Settings.Instance.MaxStops = UnityEngine.Mathf.RoundToInt(v); Settings.Save(); });

            routing.AddSlider("Coverage warning threshold (%)", 30f, 95f, 5f, s.CoverageThreshold * 100f,
                v => { Settings.Instance.CoverageThreshold = v / 100f; Settings.Save(); });

            var dbg = helper.AddGroup("Debug");
            dbg.AddCheckbox("Enable debug logging", s.DebugLogging,
                v => { Settings.Instance.DebugLogging = v; Log.DebugEnabled = v; Settings.Save(); });
        }
    }
}
