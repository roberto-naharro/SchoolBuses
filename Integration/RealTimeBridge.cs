using System;
using System.Reflection;
using SchoolBuses.Util;

namespace SchoolBuses.Integration
{
    // Reads the Real Time mod's live school hours, by reflection, so that when Real Time is present
    // it is the SOURCE OF TRUTH for *when* a school line should run: the player sets school hours in
    // one place (Real Time) instead of duplicating them in our options. We only READ Real Time's
    // config — we never write it, and we take no hard dependency (detection by assembly name, so it
    // survives type/namespace renames across versions). When Real Time is absent, the mod falls back
    // to its own service-hour option.
    //
    // Access path (verified against Real Time source):
    //   RealTime.Core.RealTimeMod.configProvider   (static field, ConfigurationProvider<RealTimeConfig>)
    //     .Configuration                            (property -> RealTimeConfig)
    //       .SchoolBegin / .SchoolEnd               (float hours, e.g. 8.0 / 14.0)
    internal static class RealTimeBridge
    {
        private const string AssemblyName = "RealTime";

        private static bool _resolved;
        private static bool _available;
        private static FieldInfo _configProviderField;
        private static PropertyInfo _configurationProp;
        private static PropertyInfo _schoolBeginProp;
        private static PropertyInfo _schoolEndProp;

        // True if Real Time is installed and we managed to bind its config accessors. Cheap after
        // the first call (cached); safe to call every tick.
        internal static bool IsPresent
        {
            get
            {
                Resolve();
                return _available;
            }
        }

        // Real Time's current school start/end hours (0–24). Returns false if Real Time is absent or
        // its config isn't ready yet (e.g. before a save loads), in which case the caller uses its
        // own configured window.
        internal static bool TryGetSchoolHours(out float begin, out float end)
        {
            begin = 0f;
            end = 0f;
            Resolve();
            if (!_available)
                return false;
            try
            {
                object provider = _configProviderField.GetValue(null);
                if (provider == null)
                    return false;
                object config = _configurationProp.GetValue(provider, null);
                if (config == null)
                    return false;
                begin = (float)_schoolBeginProp.GetValue(config, null);
                end = (float)_schoolEndProp.GetValue(config, null);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("RealTimeBridge: reading school hours failed: " + ex.Message);
                return false;
            }
        }

        private static void Resolve()
        {
            if (_resolved)
                return;
            _resolved = true;
            try
            {
                Type modType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != AssemblyName)
                        continue;
                    modType = asm.GetType("RealTime.Core.RealTimeMod", false);
                    if (modType != null)
                        break;
                }
                if (modType == null)
                {
                    Log.Info("Real Time not detected — using the mod's own service-hour option");
                    return;
                }

                _configProviderField = modType.GetField("configProvider",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (_configProviderField == null)
                    return;
                _configurationProp = _configProviderField.FieldType.GetProperty("Configuration");
                if (_configurationProp == null)
                    return;
                Type configType = _configurationProp.PropertyType;
                _schoolBeginProp = configType.GetProperty("SchoolBegin");
                _schoolEndProp = configType.GetProperty("SchoolEnd");
                if (_schoolBeginProp == null || _schoolEndProp == null)
                    return;

                _available = true;
                Log.Info("Real Time detected — school lines will follow its school hours");
            }
            catch (Exception ex)
            {
                Log.Warning("Real Time detection failed: " + ex.Message);
                _available = false;
            }
        }
    }
}
