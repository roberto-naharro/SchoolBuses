using ColossalFramework;
using ColossalFramework.UI;
using SchoolBuses.Data;
using SchoolBuses.Routing;
using SchoolBuses.Util;
using UnityEngine;

namespace SchoolBuses.UI
{
    // Surface B — a compact floating group docked to the left of the transport line
    // info panel. Lets the player flag any line as a school line (manual flagging) and
    // shows which school it serves. Auto-flagging happens via Generate Route (Surface A).
    //
    // Docked to the left so it never fights IPT's / the vanilla panel's internal layout.
    public class SchoolLinePanelExtender : MonoBehaviour
    {
        private const float Width = 230f;
        private const float SchoolSearchRadius = 140f;

        private bool _initialized;
        private WorldInfoPanel _wip;
        private MonoBehaviour _wipScript;
        private UIPanel _panel;
        private UICheckBox _schoolCheck;
        private UILabel _schoolLabel;
        private UIButton _locateButton;
        private UILabel _hintLabel;
        private ushort _cachedLine;

        private void Update()
        {
            if (!_initialized)
            {
                TryInit();
                return;
            }
            if (_wip == null || !_wip.component.isVisible)
            {
                if (_panel != null && _panel.isVisible)
                    _panel.Hide();
                return;
            }

            ushort lineId = CurrentLine();
            if (lineId == 0)
            {
                if (_panel.isVisible) _panel.Hide();
                return;
            }

            if (!_panel.isVisible)
                _panel.Show();
            DockToPanel();

            if (lineId != _cachedLine)
            {
                _cachedLine = lineId;
                Refresh(lineId);
            }
        }

        private void TryInit()
        {
            GameObject go = GameObject.Find("(Library) PublicTransportWorldInfoPanel");
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
            _panel.name = "SchoolBuses_LineGroup";
            _panel.width = Width;
            _panel.height = 110f;
            _panel.backgroundSprite = "MenuPanel2";
            _panel.canFocus = false;
            _panel.isInteractive = true;
            _panel.Hide();

            UILabel title = UIHelper.CreateLabel(_panel, 0.9f);
            title.text = "School Bus Line";
            title.relativePosition = new Vector3(10f, 8f);
            title.width = Width - 20f;

            _schoolCheck = UIHelper.CreateCheckBox(_panel);
            _schoolCheck.text = "School line";
            _schoolCheck.label.text = "School line";
            _schoolCheck.width = Width - 20f;
            _schoolCheck.relativePosition = new Vector3(10f, 32f);
            _schoolCheck.tooltip = "Restrict this line to K–12 students travelling to/from the school it serves.";
            _schoolCheck.eventCheckChanged += OnSchoolCheckChanged;

            _schoolLabel = UIHelper.CreateLabel(_panel, 0.75f);
            _schoolLabel.relativePosition = new Vector3(10f, 56f);
            _schoolLabel.width = Width - 60f;
            _schoolLabel.height = 16f;

            _locateButton = UIHelper.CreateButton(_panel);
            _locateButton.text = "⌖"; // crosshair-ish
            _locateButton.tooltip = "Show the school on the map";
            _locateButton.size = new Vector2(28f, 22f);
            _locateButton.relativePosition = new Vector3(Width - 38f, 54f);
            _locateButton.eventClick += OnLocateClick;

            _hintLabel = UIHelper.CreateLabel(_panel, 0.7f);
            _hintLabel.textColor = UIHelper.Amber;
            _hintLabel.relativePosition = new Vector3(10f, 78f);
            _hintLabel.width = Width - 20f;
            _hintLabel.height = 28f;
        }

        private void DockToPanel()
        {
            // Left of the info panel, top-aligned.
            _panel.relativePosition = new Vector3(-Width - 1f, 0f);
        }

        private void Refresh(ushort lineId)
        {
            SchoolLineData data;
            bool isSchool = SchoolLineRegistry.TryGet(lineId, out data);

            _schoolCheck.eventCheckChanged -= OnSchoolCheckChanged;
            _schoolCheck.isChecked = isSchool;
            _schoolCheck.eventCheckChanged += OnSchoolCheckChanged;

            if (isSchool && data.SchoolBuildingId != 0)
            {
                _schoolLabel.isVisible = true;
                _locateButton.isVisible = true;
                _schoolLabel.text = "School: " + BuildingName(data.SchoolBuildingId);
                _hintLabel.text = data.ModGenerated ? string.Empty : "Manually flagged line.";
            }
            else
            {
                _schoolLabel.isVisible = false;
                _locateButton.isVisible = false;
                _hintLabel.text = string.Empty;
            }
        }

        private void OnSchoolCheckChanged(UIComponent c, bool isChecked)
        {
            ushort lineId = CurrentLine();
            if (lineId == 0)
                return;

            if (!isChecked)
            {
                SchoolLineRegistry.Unregister(lineId);
                _cachedLine = 0; // force refresh
                return;
            }

            // Auto-detect which school the line serves from its stops.
            ushort schoolId, schoolStop;
            if (DetectSchool(lineId, out schoolId, out schoolStop))
            {
                SchoolLineRegistry.Register(lineId, new SchoolLineData(schoolId, schoolStop, false));
                _cachedLine = 0;
            }
            else
            {
                // Revert the tick; nothing to bind to.
                _schoolCheck.eventCheckChanged -= OnSchoolCheckChanged;
                _schoolCheck.isChecked = false;
                _schoolCheck.eventCheckChanged += OnSchoolCheckChanged;
                _hintLabel.text = "No school found at this line's stops.\nRoute a stop next to a school first.";
            }
        }

        private void OnLocateClick(UIComponent c, UIMouseEventParameter p)
        {
            ushort lineId = CurrentLine();
            SchoolLineData data;
            if (lineId != 0 && SchoolLineRegistry.TryGet(lineId, out data) && data.SchoolBuildingId != 0)
                PanelUtil.MoveCameraToBuilding(data.SchoolBuildingId, EducationBuildingUtil.GetPosition(data.SchoolBuildingId));
        }

        // Scan the line's stop nodes; bind to the first stop that sits next to a K–12
        // school. That stop becomes the school stop (homebound boarding point).
        private bool DetectSchool(ushort lineId, out ushort schoolId, out ushort schoolStop)
        {
            schoolId = 0;
            schoolStop = 0;

            TransportManager tm = Singleton<TransportManager>.instance;
            var nodes = Singleton<NetManager>.instance.m_nodes.m_buffer;
            ushort first = tm.m_lines.m_buffer[lineId].m_stops;
            if (first == 0)
                return false;

            ushort stop = first;
            int guard = 0;
            do
            {
                ushort found = EducationBuildingUtil.FindSchoolNear(nodes[stop].m_position, SchoolSearchRadius);
                if (found != 0)
                {
                    schoolId = found;
                    schoolStop = stop;
                    return true;
                }
                stop = TransportLine.GetNextStop(stop);
                if (++guard > 32768)
                    break;
            }
            while (stop != first && stop != 0);

            return false;
        }

        private ushort CurrentLine()
        {
            InstanceID id = PanelUtil.GetInstanceID(_wip.component, _wipScript);
            return id.TransportLine;
        }

        private static string BuildingName(ushort buildingId)
        {
            string name = Singleton<BuildingManager>.instance.GetBuildingName(buildingId, default(InstanceID));
            return string.IsNullOrEmpty(name) ? ("Building #" + buildingId) : name;
        }

        private void OnDestroy()
        {
            if (_panel != null)
                Destroy(_panel.gameObject);
            _initialized = false;
        }
    }
}
