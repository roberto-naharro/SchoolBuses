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

        // Route generation tunables. The stop count emerges from these two knobs — homes
        // within ClusterRadius form one neighbourhood, and a neighbourhood only gets a stop if
        // it has at least MinClusterStudents students.
        public float ClusterRadius = 400f;       // metres; max radius of a pickup neighbourhood
        public int MinClusterStudents = 10;      // a neighbourhood needs this many students for a stop
        public float CoverageThreshold = 0.70f;  // below this a line is flagged stale (health warning)

        // When on, the generator auto-targets a sensible number of stops from the school's
        // student count and the bus's passenger capacity (scaled because students don't all
        // travel at once), keeping the densest neighbourhoods. The two manual sliders below are
        // disabled while this is on.
        public bool DynamicStopCount = true;

        // Make ineligible commuters who pathfind to a school stop give up and re-route,
        // instead of piling up waiting for a bus that will never pick them up.
        public bool EvictIneligibleRiders = true;

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
