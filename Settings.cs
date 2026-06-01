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
        // it has at least MinClusterStudents students. Defaults (radius 400, min 8) are the values
        // the in-game experiment found best for real ridership across school sizes — see
        // .claude/plans/experiment_results.md.
        public float ClusterRadius = 400f;       // metres; max radius of a pickup neighbourhood
        public int MinClusterStudents = 8;       // a neighbourhood needs this many students for a stop
        public float CoverageThreshold = 0.70f;  // below this a line is flagged stale (health warning)

        // Scale the min-students-per-cluster by the school's actual capacity (read live, so it works
        // for MODDED schools of any size — not bucketed by vanilla 300/1000). Small schools then use a
        // lower min (their sparse neighbourhoods still get stops → coverage), big schools a higher one
        // (controls route count). effMin = clamp(round(capacity × CapacityMinFactor), 4, 14); factor
        // 0.008 calibrates a 1000-capacity school to min 8. When off, MinClusterStudents is used flat.
        public bool ScaleMinByCapacity = true;
        public float CapacityMinFactor = 0.008f;

        // Auto-regenerate (upkeep): when on, the mod periodically checks each school's mod-generated
        // routes and, if the student distribution has drifted so coverage falls below
        // MinCoverageTarget of its bus-needing students, automatically regenerates that school's
        // routes (same as the Regenerate button, using the CURRENT settings) so the stops follow the
        // students. Turn OFF to manage line regeneration entirely by hand.
        public bool AutoRegenerate = true;
        public float MinCoverageTarget = 0.30f; // coverage below this triggers an auto-regenerate (0 = never)

        // Catchment trim. Routes are normally bounded only by min-students; but if a school would
        // generate more than MaxRoutesPerSchool routes (when > 0), neighbourhoods farther than
        // MaxCatchmentDistance from the school are dropped (too far for a school bus) and it re-routes
        // — so only spread-out outliers are bounded, compact schools keep full coverage.
        // MaxRoutesPerSchool = 0 disables the trim entirely. MaxCatchmentDistance is the distance it
        // trims to (only used when the trigger fires).
        public float MaxCatchmentDistance = 2500f;

        // Auto-tune (grid search over radius × min). Default OFF: the experiment showed its proxy
        // fitness does not track real ridership, and the fixed defaults above beat it across school
        // sizes. Kept as an opt-in for users who want per-school adaptation; toggling it on disables
        // the two manual sliders.
        public bool DynamicStopCount = false;

        // Multi-route generation. A school's pickup clusters are swept into angular zones, each
        // becoming its own short one-bus loop (a big school = several short routes, like a real
        // district). The budget per route is the INTER-STOP pickup-loop length — the distance the
        // bus drives BETWEEN pickups — NOT the full loop: the trunk legs to/from the school are a
        // fixed access cost we exclude, so a far sector chains its nearby neighbourhoods into one
        // route instead of shattering into many one-stop routes. Clusters accrue along the sweep
        // until the next hop would exceed MaxRouteLength, then a new route starts. Near/dense
        // neighbourhoods chain more stops; spread-out ones split. Straight-line proxy. Sensible
        // range ≈ 800–4000 m; 2000 m ≈ 6–8 pickups when neighbourhoods are ~300 m apart.
        public float MaxRouteLength = 2000f; // metres of inter-stop pickup loop (trunk excluded)
        public int MaxRoutesPerSchool = 0;   // catchment-trim trigger, ROUTES PER 1000 CAPACITY (0 = never).
                                             // OFF by default: the trim cuts coverage→ridership; it's an
                                             // opt-in for budget-limited players who want fewer buses.

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
