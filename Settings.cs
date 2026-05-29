using System;
using System.IO;
using System.Xml.Serialization;
using ColossalFramework.IO;
using SchoolBuses.Util;

namespace SchoolBuses
{
    // Global (not per-save) mod options, persisted as XML next to the game config —
    // same discipline as ImpatientCommuters' Settings.cs. The per-save school-line
    // bindings live separately in SchoolLineRegistry.
    [XmlRoot("SchoolBusesSettings")]
    public class Settings
    {
        private const string FileName = "SchoolBusesSettings.xml";

        private static Settings _instance;
        public static Settings Instance => _instance ?? (_instance = Load());

        public bool Enabled = true;

        // Route generation tunables (design Q#6/#7).
        public float ClusterRadius = 300f;     // metres; cluster radius and stop coverage radius
        public int MaxStops = 10;               // including the school terminal stop
        public float CoverageThreshold = 0.70f; // below this a line is flagged stale

        public bool DebugLogging =
#if DEBUG
            true;
#else
            false;
#endif

        private static string FilePath => Path.Combine(DataLocation.localApplicationData, FileName);

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    using (var sr = new StreamReader(FilePath))
                    {
                        var ser = new XmlSerializer(typeof(Settings));
                        var loaded = (Settings)ser.Deserialize(sr);
                        Log.DebugEnabled = loaded.DebugLogging;
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to load settings: " + ex.Message);
            }
            return new Settings();
        }

        public static void Save()
        {
            try
            {
                using (var sw = new StreamWriter(FilePath))
                {
                    var ser = new XmlSerializer(typeof(Settings));
                    ser.Serialize(sw, Instance);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to save settings: " + ex.Message);
            }
        }
    }
}
