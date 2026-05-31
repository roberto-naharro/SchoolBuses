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
        private const float Width = 272f;
        private const float Pad = 10f;
        private const float TitleBarOffset = 60f; // align dock with the building panel title bar
        private const int RefreshEveryTicks = 120; // ~2 s at 60 fps

        private bool _initialized;
        private WorldInfoPanel _wip;
        private MonoBehaviour _wipScript;

        private UIPanel _panel;
        private UILabel _title;
        private UILabel _enrolled;
        private UIPanel _lineList;
        private UIButton _generateButton;
        private UILabel _statusLabel;

        private readonly List<UIButton> _rows = new List<UIButton>();
        private ushort _cachedBuilding;
        private int _cachedLineCount = -1;
        private int _tick;
        private bool _busy;

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
            _panel.relativePosition = new Vector3(_wip.component.width + 1f, TitleBarOffset);

            int lineCount = SchoolLineRegistry.GetLinesForSchool(buildingId).Count;
            bool buildingChanged = buildingId != _cachedBuilding;
            bool changed = buildingChanged || lineCount != _cachedLineCount;
            if (changed || (++_tick % RefreshEveryTicks) == 0)
            {
                // The status line ("Route created.", errors) is per-action feedback for ONE
                // school. Clear it when the panel moves to a different building so it doesn't
                // linger on every other school's window.
                if (buildingChanged && _statusLabel != null)
                    _statusLabel.text = string.Empty;
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

            _lineList = _panel.AddUIComponent<UIPanel>();
            _lineList.name = "LineList";
            _lineList.width = Width - 2 * Pad;
            _lineList.relativePosition = new Vector3(Pad, 56f);
            _lineList.autoLayout = true;
            _lineList.autoLayoutDirection = LayoutDirection.Vertical;
            _lineList.autoLayoutPadding = new RectOffset(0, 0, 0, 4);
            _lineList.height = 180f;

            _generateButton = UIHelper.CreateButton(_panel);
            _generateButton.text = "+ Generate Route";
            _generateButton.tooltip = "Create a bus line from this school's current student roster.";
            _generateButton.size = new Vector2(Width - 2 * Pad, 30f);
            _generateButton.eventClick += (c, p) => StartGenerate();

            _statusLabel = UIHelper.CreateLabel(_panel, 0.72f);
            _statusLabel.width = Width - 2 * Pad;
            _statusLabel.height = 30f;
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
            float threshold = Settings.Instance.CoverageThreshold;
            float radius = Settings.Instance.ClusterRadius;

            foreach (ushort lineId in lines)
                AddLineRow(buildingId, lineId, radius, threshold);

            // Layout the action button + status below the (variable-height) list.
            float listBottom = _lineList.relativePosition.y + Mathf.Max(28f, _rows.Count * 50f);
            _generateButton.relativePosition = new Vector3(Pad, listBottom + 8f);
            _statusLabel.relativePosition = new Vector3(Pad, listBottom + 44f);
            _panel.height = listBottom + 80f;
        }

        private void AddLineRow(ushort buildingId, ushort lineId, float radius, float threshold)
        {
            LineHealthResult health = LineHealth.Evaluate(lineId, buildingId, radius, threshold);
            int stops = Singleton<TransportManager>.instance.m_lines.m_buffer[lineId].CountStops(lineId);
            string name = Singleton<TransportManager>.instance.GetLineName(lineId);
            BoardingStats.Counts counts = BoardingStats.Get(lineId);
            bool problem = health.IsProblem;

            UIButton row = UIHelper.CreateButton(_lineList);
            row.size = new Vector2(_lineList.width, 46f);
            row.textHorizontalAlignment = UIHorizontalAlignment.Left;
            row.textVerticalAlignment = UIVerticalAlignment.Top;
            row.textScale = 0.68f;
            row.wordWrap = true;
            row.text = (problem ? "⚠ " : "● ") + name + "\n"
                       + stops + " stops · " + Mathf.RoundToInt(health.Coverage * 100f) + "% covered\n"
                       + "served " + counts.Served + " · turned away " + counts.TurnedAway;
            row.textColor = problem ? UIHelper.Amber : UIHelper.Green;
            row.tooltip = health.IsProblem ? health.Message : "Coverage and ridership look healthy.";
            ushort captured = lineId;
            row.eventClick += (c, p) => OpenLine(captured);
            _rows.Add(row);

            // Offer Regenerate when coverage has drifted (not for transient traffic issues).
            if (health.Status == HealthStatus.StaleCoverage)
            {
                UIButton regen = UIHelper.CreateButton(row);
                regen.text = "⟳";
                regen.tooltip = "Regenerate this line from the current roster.";
                regen.size = new Vector2(24f, 22f);
                regen.relativePosition = new Vector3(row.width - 26f, 2f);
                ushort capLine = lineId;
                ushort capBuilding = buildingId;
                regen.eventClick += (c, p) =>
                {
                    p.Use();
                    StartRegenerate(capLine, capBuilding);
                };
            }
        }

        private void StartGenerate()
        {
            if (_busy) return;
            ushort buildingId = CurrentBuilding();
            if (buildingId == 0) return;
            Util.Log.DebugLog("User clicked Generate Route for school " + buildingId);
            _busy = true;
            _generateButton.text = "Generating…";
            _statusLabel.text = string.Empty;
            RouteGenerator.Generate(buildingId, OnGenerated);
        }

        private void StartRegenerate(ushort lineId, ushort buildingId)
        {
            if (_busy) return;
            Util.Log.DebugLog("User clicked Regenerate for line " + lineId + " (school " + buildingId + ")");
            _busy = true;
            _statusLabel.text = "Regenerating…";
            RouteGenerator.Regenerate(lineId, buildingId, OnGenerated);
        }

        private void OnGenerated(RouteBuilder.Result result)
        {
            Util.Log.DebugLog("Generation result: success=" + result.Success + " line=" + result.LineId
                + " noDepot=" + result.NoDepot + (result.Error != null ? " error=" + result.Error : ""));
            _busy = false;
            _generateButton.text = "+ Generate Route";
            if (result.Success)
            {
                _statusLabel.textColor = result.NoDepot ? UIHelper.Amber : UIHelper.Green;
                _statusLabel.text = result.NoDepot
                    ? "Line created, but no bus depot serves it — it will stay idle."
                    : "Route created.";
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
