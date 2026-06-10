using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.UI;
using SchoolBuses.Data;
using SchoolBuses.Routing;
using SchoolBuses.Util;
using UnityEngine;

namespace SchoolBuses.UI
{
    // Surface A — a right-docked panel on the city-service info panel, shown only for
    // K–12 schools. Lists the school's bus lines with live coverage, offers one-click
    // Generate Route, and a per-line Regenerate when coverage drifts.
    public class SchoolBuildingPanelExtender : MonoBehaviour
    {
        private const float Width = 320f;
        private const float Pad = 10f;
        private const float TitleBarOffset = 60f; // align dock with the building panel title bar
        private const int RefreshEveryTicks = 120; // ~2 s at 60 fps

        private const float ListTop = 94f;   // below title + enrolled + 2-line coverage label
        private const float RowHeight = 46f;
        private const float RowGap = 4f;
        private const int MaxVisibleRows = 4; // scroll past this many routes
        private const float ListHeight = MaxVisibleRows * (RowHeight + RowGap);

        private bool _initialized;
        private WorldInfoPanel _wip;
        private MonoBehaviour _wipScript;

        private UIPanel _panel;
        private UILabel _title;
        private UILabel _enrolled;
        private UILabel _coverageLabel;
        private UIScrollablePanel _lineList;
        private UIScrollbar _scrollbar;
        private UIButton _generateButton;
        private UIButton _regenButton;
        private UIButton _deleteButton;
        private UILabel _statusLabel;

        private readonly List<UIButton> _rows = new List<UIButton>();
        private ushort _cachedBuilding;
        private int _cachedLineCount = -1;
        private int _tick;
        private bool _busy;
        private bool _regenArmed;  // first Regenerate click arms; second confirms (delete-all + rebuild)
        private bool _deleteArmed; // first Delete click arms; second confirms (delete, no rebuild)

        private void Update()
        {
            if (!_initialized)
            {
                TryInit();
                return;
            }
            if (_wip == null || !_wip.component.isVisible)
            {
                if (_panel.isVisible) _panel.Hide();
                return;
            }

            ushort buildingId = CurrentBuilding();
            if (buildingId == 0 || !EducationBuildingUtil.IsSchool(buildingId))
            {
                if (_panel.isVisible) _panel.Hide();
                return;
            }

            if (!_panel.isVisible) _panel.Show();
            // CityServiceWorldInfoPanel's component origin sits above its visible window,
            // so a child docked at y=0 floats too high. Offset down to align with the
            // building panel's title bar (IPT uses the same workaround on this panel).
            // Falls back to the LEFT side, clamped on screen, when the right side would clip
            // (UI Resolution / panel near the screen edge).
            PanelUtil.DockBeside(_panel, _wip.component,
                _wip.component.width + 1f, -_panel.width - 1f, TitleBarOffset);

            int lineCount = SchoolLineRegistry.GetLinesForSchool(buildingId).Count;
            bool buildingChanged = buildingId != _cachedBuilding;
            bool changed = buildingChanged || lineCount != _cachedLineCount;
            if (changed || (++_tick % RefreshEveryTicks) == 0)
            {
                // The status line ("Route created.", errors) is per-action feedback for ONE
                // school. Clear it when the panel moves to a different building so it doesn't
                // linger on every other school's window.
                if (buildingChanged && _statusLabel != null)
                {
                    _statusLabel.text = string.Empty;
                    _regenArmed = false;  // don't carry a primed confirm across schools
                    _deleteArmed = false;
                }
                _cachedBuilding = buildingId;
                _cachedLineCount = lineCount;
                Refresh(buildingId);
            }
        }

        private void TryInit()
        {
            GameObject go = GameObject.Find("(Library) CityServiceWorldInfoPanel");
            if (go == null)
                return;
            _wip = go.GetComponent<WorldInfoPanel>();
            _wipScript = _wip as MonoBehaviour;
            if (_wip == null)
                return;
            Build();
            _initialized = true;
        }

        private void Build()
        {
            _panel = _wip.component.AddUIComponent<UIPanel>();
            _panel.name = "SchoolBuses_BuildingPanel";
            _panel.width = Width;
            _panel.height = 320f;
            _panel.backgroundSprite = "MenuPanel2";
            _panel.canFocus = false;
            _panel.Hide();

            _title = UIHelper.CreateLabel(_panel, 0.95f);
            _title.text = "School Bus Routes";
            _title.relativePosition = new Vector3(Pad, 10f);
            _title.width = Width - 2 * Pad;

            _enrolled = UIHelper.CreateLabel(_panel, 0.8f);
            _enrolled.relativePosition = new Vector3(Pad, 34f);
            _enrolled.width = Width - 2 * Pad;
            _enrolled.height = 16f;

            // Whole-school coverage (union of all routes) — the meaningful figure; a single line's
            // own % is low by design when a school has several routes.
            _coverageLabel = UIHelper.CreateLabel(_panel, 0.8f);
            _coverageLabel.relativePosition = new Vector3(Pad, 52f);
            _coverageLabel.width = Width - 2 * Pad;
            _coverageLabel.height = 34f;
            // No wrapping: keep each line short enough to fit so the label can't overflow DOWN past
            // its height into the route list below it (UILabels don't clip their text).
            _coverageLabel.wordWrap = false;

            // Scrollable list: shows ~MaxVisibleRows rows, scroll for the rest, so a big school's
            // many routes never overflow the game window.
            _lineList = _panel.AddUIComponent<UIScrollablePanel>();
            _lineList.name = "LineList";
            _lineList.width = Width - 2 * Pad;
            _lineList.height = ListHeight;
            _lineList.relativePosition = new Vector3(Pad, ListTop);
            _lineList.autoLayout = true;
            _lineList.autoLayoutDirection = LayoutDirection.Vertical;
            _lineList.autoLayoutPadding = new RectOffset(0, 0, 0, (int)RowGap);
            _lineList.clipChildren = true;
            _lineList.scrollWheelDirection = UIOrientation.Vertical;
            _lineList.scrollWheelAmount = (int)(RowHeight + RowGap);
            BuildScrollbar(_lineList);

            _generateButton = UIHelper.CreateButton(_panel);
            _generateButton.text = "+ Generate Routes";
            _generateButton.tooltip = "Create bus routes from this school's current student roster.";
            _generateButton.size = new Vector2(Width - 2 * Pad, 30f);
            _generateButton.eventClick += (c, p) => StartGenerate();

            _regenButton = UIHelper.CreateButton(_panel);
            _regenButton.text = "⟳ Regenerate All";
            _regenButton.tooltip = "Delete this school's routes and rebuild them from the current roster.";
            _regenButton.size = new Vector2(Width - 2 * Pad, 30f);
            _regenButton.eventClick += (c, p) => StartRegenerateAll();
            _regenButton.Hide();

            _deleteButton = UIHelper.CreateButton(_panel);
            _deleteButton.text = "🗑 Delete All Routes";
            _deleteButton.tooltip = "Delete this school's routes (without rebuilding).";
            _deleteButton.size = new Vector2(Width - 2 * Pad, 30f);
            _deleteButton.eventClick += (c, p) => StartDeleteAll();
            _deleteButton.Hide();

            _statusLabel = UIHelper.CreateLabel(_panel, 0.72f);
            _statusLabel.width = Width - 2 * Pad;
            _statusLabel.height = 30f;
        }

        // Standard CS1 vertical scrollbar wired to the scrollable panel.
        private void BuildScrollbar(UIScrollablePanel target)
        {
            var scrollbar = _panel.AddUIComponent<UIScrollbar>();
            _scrollbar = scrollbar;
            scrollbar.width = 12f;
            scrollbar.height = target.height;
            scrollbar.orientation = UIOrientation.Vertical;
            scrollbar.pivot = UIPivotPoint.TopLeft;
            scrollbar.relativePosition = new Vector3(target.relativePosition.x + target.width + 2f, ListTop);
            scrollbar.minValue = 0f;
            scrollbar.value = 0f;
            scrollbar.incrementAmount = 50f;

            var track = scrollbar.AddUIComponent<UISlicedSprite>();
            track.spriteName = "ScrollbarTrack";
            track.relativePosition = Vector3.zero;
            track.size = scrollbar.size;
            scrollbar.trackObject = track;

            var thumb = track.AddUIComponent<UISlicedSprite>();
            thumb.spriteName = "ScrollbarThumb";
            thumb.width = scrollbar.width;
            scrollbar.thumbObject = thumb;

            target.verticalScrollbar = scrollbar;
        }

        private void Refresh(ushort buildingId)
        {
            int students = EducationBuildingUtil.GetEnrolledStudentCount(buildingId);
            int capacity = EducationBuildingUtil.GetStudentCapacity(buildingId);
            _enrolled.text = capacity > 0
                ? students + " / " + capacity + " students enrolled"
                : students + (students == 1 ? " student enrolled" : " students enrolled");

            ClearRows();
            List<ushort> lines = SchoolLineRegistry.GetLinesForSchool(buildingId);
            float radius = Settings.Instance.ClusterRadius;
            float threshold = Settings.Instance.CoverageThreshold;
            List<ushort> homes = EducationBuildingUtil.GetStudentHomeBuildings(buildingId);

            // Whole-school coverage = union of all routes (each student counted once), measured
            // against students who NEED a bus (roster minus near-school walkers).
            if (lines.Count > 0)
            {
                int coveredUnion, roster, walkers;
                CoverageTracker.SchoolCoverage(buildingId, lines, radius, out coveredUnion, out roster, out walkers);
                int needBus = Mathf.Max(0, roster - walkers);
                float frac = needBus > 0 ? (float)coveredUnion / needBus : 1f;
                _coverageLabel.text = "Covered " + coveredUnion + "/" + needBus + " need bus ("
                    + Mathf.RoundToInt(frac * 100f) + "%)\n" + walkers + " walk · "
                    + lines.Count + " route(s)";
                _coverageLabel.textColor = frac + 1e-4f >= threshold ? UIHelper.Green : UIHelper.Amber;
                _coverageLabel.Show();
            }
            else
            {
                _coverageLabel.Hide();
            }

            // Size the scrollable list BEFORE the rows are added, so each row picks up the right
            // width. The scrollbar only appears when there are more routes than fit, and the list
            // narrows to make room for it ONLY then — otherwise the text would sit under the bar.
            int visibleRows = Mathf.Min(lines.Count, MaxVisibleRows);
            float listH = visibleRows * (RowHeight + RowGap);
            bool overflow = lines.Count > MaxVisibleRows;
            float listW = (Width - 2 * Pad) - (overflow ? 16f : 0f);
            _lineList.width = listW;
            _lineList.height = listH;
            _lineList.isVisible = lines.Count > 0;
            if (_scrollbar != null)
            {
                _scrollbar.height = listH;
                _scrollbar.isVisible = overflow;
                _scrollbar.relativePosition = new Vector3(Pad + listW + 2f, ListTop);
                if (!overflow)
                    _lineList.scrollPosition = Vector2.zero;
            }

            foreach (ushort lineId in lines)
                AddLineRow(buildingId, lineId, homes, radius);

            float y = ListTop + (lines.Count > 0 ? listH + 8f : 0f);
            _generateButton.relativePosition = new Vector3(Pad, y);
            // Generate is additive — block it once routes exist (it would stack more on top).
            // Use Regenerate (rebuild) or Delete instead.
            _generateButton.isEnabled = lines.Count == 0;
            _generateButton.tooltip = lines.Count == 0
                ? "Create bus routes from this school's current student roster."
                : "Routes already exist — use Regenerate All or Delete All Routes.";
            y += 38f;
            if (lines.Count > 0)
            {
                _regenButton.Show();
                _regenButton.relativePosition = new Vector3(Pad, y);
                y += 38f;
                _deleteButton.Show();
                _deleteButton.relativePosition = new Vector3(Pad, y);
                y += 38f;
            }
            else
            {
                _regenButton.Hide();
                _deleteButton.Hide();
            }
            _statusLabel.relativePosition = new Vector3(Pad, y);
            _panel.height = y + 44f;
        }

        // One route row: covered students + the line's own share of the roster. NO per-line problem
        // flag — the meaningful health is whole-school coverage (shown above).
        private void AddLineRow(ushort buildingId, ushort lineId, List<ushort> homes, float radius)
        {
            int stops = Singleton<TransportManager>.instance.m_lines.m_buffer[lineId].CountStops(lineId);
            string name = Singleton<TransportManager>.instance.GetLineName(lineId);
            BoardingStats.Counts counts = BoardingStats.Get(lineId);
            int covered = CoverageTracker.CoveredCount(lineId, buildingId, homes, radius);
            int pct = homes.Count > 0 ? Mathf.RoundToInt(100f * covered / homes.Count) : 0;

            UIButton row = UIHelper.CreateButton(_lineList);
            row.size = new Vector2(_lineList.width, RowHeight);
            row.textHorizontalAlignment = UIHorizontalAlignment.Left;
            row.textVerticalAlignment = UIVerticalAlignment.Top;
            row.textScale = 0.68f;
            row.wordWrap = true;
            row.textPadding = new RectOffset(20, 2, 2, 0); // leave room for the education icon
            row.text = name + "\n"
                       + stops + " stops · " + covered + " students (" + pct + "%)\n"
                       + "served " + counts.Served + " · turned away " + counts.TurnedAway;
            row.textColor = UIHelper.Green;
            row.tooltip = "Click to open this route.";
            // Vanilla education (book) icon marking this as a school route.
            UISprite rowIcon = UIHelper.CreateIcon(row, "InfoIconEducation", 14f);
            rowIcon.relativePosition = new Vector3(3f, 3f);
            ushort captured = lineId;
            row.eventClick += (c, p) => OpenLine(captured);
            // The row button would otherwise eat the wheel event — forward it to the list so the
            // mouse wheel scrolls the routes (clamped so it can't overscroll into blank space).
            row.eventMouseWheel += (c, p) =>
            {
                float maxScroll = Mathf.Max(0f, _rows.Count * (RowHeight + RowGap) - _lineList.height);
                float ny = Mathf.Clamp(_lineList.scrollPosition.y - p.wheelDelta * (RowHeight + RowGap), 0f, maxScroll);
                _lineList.scrollPosition = new Vector2(_lineList.scrollPosition.x, ny);
                p.Use();
            };
            _rows.Add(row);
        }

        private void StartGenerate()
        {
            if (_busy) return;
            ushort buildingId = CurrentBuilding();
            if (buildingId == 0) return;
            // Defensive: Generate is additive, so refuse if the school already has routes.
            if (SchoolLineRegistry.GetLinesForSchool(buildingId).Count > 0)
            {
                _statusLabel.textColor = UIHelper.Amber;
                _statusLabel.text = "Routes already exist — use Regenerate or Delete first.";
                return;
            }
            Util.Log.DebugLog("User clicked Generate Route for school " + buildingId);
            _regenArmed = false;
            _deleteArmed = false;
            _busy = true;
            _generateButton.text = "Generating…";
            _statusLabel.text = string.Empty;
            RouteGenerator.Generate(buildingId, OnGenerated);
        }

        // Two-stage confirm: the first click warns and arms; the second actually deletes the
        // school's routes and rebuilds the whole set (route count can change with the roster).
        private void StartRegenerateAll()
        {
            if (_busy) return;
            ushort buildingId = CurrentBuilding();
            if (buildingId == 0) return;

            int count = SchoolLineRegistry.GetLinesForSchool(buildingId).Count;
            if (!_regenArmed)
            {
                _regenArmed = true;
                _deleteArmed = false;
                _statusLabel.textColor = UIHelper.Amber;
                _statusLabel.text = "Deletes all " + count + " route(s) and rebuilds them. Click Regenerate again to confirm.";
                return;
            }

            _regenArmed = false;
            Util.Log.DebugLog("User confirmed Regenerate All for school " + buildingId + " (" + count + " line(s))");
            _busy = true;
            _statusLabel.textColor = UIHelper.Green;
            _statusLabel.text = "Regenerating…";
            RouteGenerator.RegenerateSchool(buildingId, OnGenerated);
        }

        // Two-stage confirm: delete the school's routes WITHOUT rebuilding.
        private void StartDeleteAll()
        {
            if (_busy) return;
            ushort buildingId = CurrentBuilding();
            if (buildingId == 0) return;

            int count = SchoolLineRegistry.GetLinesForSchool(buildingId).Count;
            if (!_deleteArmed)
            {
                _deleteArmed = true;
                _regenArmed = false;
                _statusLabel.textColor = UIHelper.Red;
                _statusLabel.text = "Delete all " + count + " route(s)? Click Delete again to confirm.";
                return;
            }

            _deleteArmed = false;
            Util.Log.DebugLog("User confirmed Delete All for school " + buildingId + " (" + count + " line(s))");
            _busy = true;
            _statusLabel.textColor = UIHelper.Green;
            _statusLabel.text = "Deleting…";
            RouteGenerator.DeleteSchool(buildingId, OnDeleted);
        }

        private void OnDeleted(int removed)
        {
            _busy = false;
            _deleteArmed = false;
            _statusLabel.textColor = UIHelper.Green;
            _statusLabel.text = removed + " route(s) deleted.";
            _cachedLineCount = -1; // force list rebuild next Update
        }

        private void OnGenerated(RouteBuilder.Result result)
        {
            Util.Log.DebugLog("Generation result: success=" + result.Success + " firstLine=" + result.LineId
                + " routes=" + result.RoutesBuilt + " noDepot=" + result.NoDepot
                + (result.Error != null ? " error=" + result.Error : ""));
            _busy = false;
            _regenArmed = false;
            _deleteArmed = false;
            _generateButton.text = "+ Generate Routes";
            if (result.Success)
            {
                string made = result.RoutesBuilt > 1 ? result.RoutesBuilt + " routes created." : "Route created.";
                _statusLabel.textColor = result.NoDepot ? UIHelper.Amber : UIHelper.Green;
                _statusLabel.text = result.NoDepot
                    ? made + " No bus depot serves them yet — they will stay idle."
                    : made;
            }
            else
            {
                _statusLabel.textColor = UIHelper.Red;
                _statusLabel.text = result.Error ?? "Generation failed.";
            }
            _cachedLineCount = -1; // force list rebuild next Update
        }

        private void OpenLine(ushort lineId)
        {
            List<Vector3> stops = CoverageTracker.GetStopPositions(lineId);
            Vector3 pos = stops.Count > 0 ? stops[0] : Vector3.zero;
            InstanceID id = default(InstanceID);
            id.TransportLine = lineId;
            WorldInfoPanel.Show<PublicTransportWorldInfoPanel>(pos, id);
        }

        private void ClearRows()
        {
            foreach (UIButton row in _rows)
                Destroy(row.gameObject);
            _rows.Clear();
        }

        private ushort CurrentBuilding()
        {
            return PanelUtil.GetInstanceID(_wip.component, _wipScript).Building;
        }

        private void OnDestroy()
        {
            if (_panel != null)
                Destroy(_panel.gameObject);
            _initialized = false;
        }
    }
}
