using System.Collections.Generic;
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
        private const float Width = 252f;
        private const float TitleBarOffset = 64f; // drop below the line panel's title bar
        private const float SchoolSearchRadius = 140f;

        private bool _initialized;
        private WorldInfoPanel _wip;
        private MonoBehaviour _wipScript;
        private UIPanel _panel;
        private UICheckBox _schoolCheck;
        private UILabel _schoolLabel;
        private UIDropDown _schoolDropdown;
        private UIButton _locateButton;
        private UILabel _statsLabel;
        private UILabel _hintLabel;
        private ushort _cachedLine;
        private int _statsTick;

        // Populated when an ambiguous (multi-school) manual line shows the picker; the dropdown's
        // selected index maps into this list. Null when no picker is shown.
        private List<SchoolCandidate> _candidates;

        // A school reachable from this line, with the nearest stop that reaches it.
        private struct SchoolCandidate
        {
            public ushort SchoolId;
            public ushort StopNode;
            public float SqrDistance;
        }

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
            // School lines apply to BUSES only — the line panel is shared by every transit type, so
            // hide our panel for metro/tram/train/etc. (and never let one be flagged a school line).
            if (lineId == 0 || !IsBusLine(lineId))
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
            else if ((++_statsTick % 120) == 0)
            {
                UpdateStats(lineId); // ridership ticks up while the line stays selected
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
            title.relativePosition = new Vector3(12f, 10f);
            title.width = Width - 24f;

            _schoolCheck = UIHelper.CreateCheckBox(_panel);
            _schoolCheck.text = "School line";
            _schoolCheck.label.text = "School line";
            _schoolCheck.width = Width - 24f;
            _schoolCheck.relativePosition = new Vector3(12f, 38f);
            _schoolCheck.tooltip = "Restrict this line to K–12 students travelling to/from the school it serves.";
            _schoolCheck.eventCheckChanged += OnSchoolCheckChanged;

            _schoolLabel = UIHelper.CreateLabel(_panel, 0.78f);
            _schoolLabel.relativePosition = new Vector3(12f, 66f);
            _schoolLabel.width = Width - 52f;
            _schoolLabel.height = 18f;

            // Picker shown ONLY when a manually-flagged line is near more than one school. It sits
            // in the same row as the school label (which is hidden while the picker is shown).
            _schoolDropdown = UIHelper.CreateDropDown(_panel);
            _schoolDropdown.relativePosition = new Vector3(12f, 62f);
            _schoolDropdown.size = new Vector2(Width - 56f, 24f);
            _schoolDropdown.tooltip = "This line passes more than one school — pick which one it serves.";
            _schoolDropdown.eventSelectedIndexChanged += OnSchoolSelected;
            _schoolDropdown.Hide();

            _locateButton = UIHelper.CreateButton(_panel);
            _locateButton.text = string.Empty;
            _locateButton.tooltip = "Show the school on the map";
            _locateButton.size = new Vector2(28f, 22f);
            _locateButton.relativePosition = new Vector3(Width - 40f, 64f);
            // Vanilla education (book) icon, centred on the button.
            UISprite locateIcon = UIHelper.CreateIcon(_locateButton, "InfoIconEducation", 18f);
            locateIcon.relativePosition = new Vector3((28f - 18f) / 2f, (22f - 18f) / 2f);
            _locateButton.eventClick += OnLocateClick;

            _statsLabel = UIHelper.CreateLabel(_panel, 0.74f);
            _statsLabel.relativePosition = new Vector3(12f, 92f);
            _statsLabel.width = Width - 24f;
            _statsLabel.height = 18f;

            _hintLabel = UIHelper.CreateLabel(_panel, 0.72f);
            _hintLabel.textColor = UIHelper.Amber;
            _hintLabel.relativePosition = new Vector3(12f, 116f);
            _hintLabel.width = Width - 24f;
            _hintLabel.height = 30f;

            _panel.height = 152f;
        }

        private void DockToPanel()
        {
            // Dock to one side of the line info panel, dropped to align with its content (the line
            // panel's component origin sits above its visible title bar).
            //
            // Default side: the LEFT (the panel's right side holds vanilla content). But when
            // Transport Lines Manager is present it widens this panel to ~800 px and fills it with
            // tabs, so the LEFT edge is no longer free — default to the RIGHT then, beyond TLM's full
            // width. A partner mod can override the side / top offset via SchoolBusBridge.SetPanelSide
            // / SetPanelTopOffset. DockBesideManaged flips to the other side, clamped on screen, when
            // the chosen one would clip.
            PanelUtil.DockBesideManaged(_panel, _wip.component, Integration.TlmBridge.IsPresent, TitleBarOffset);
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
                _locateButton.isVisible = true;
                _statsLabel.isVisible = true;
                _hintLabel.text = data.ModGenerated ? string.Empty : "Manually flagged line.";

                // Offer the school picker only for manually-flagged lines that genuinely pass more
                // than one school (a generated line serves exactly one school by construction, so it
                // never gets the picker). With a single school there's nothing to choose — the
                // "go to school" marker already shows which one — so we just show the label.
                List<SchoolCandidate> candidates = data.ModGenerated
                    ? null
                    : DetectSchools(lineId);
                if (candidates != null && candidates.Count > 1)
                {
                    PopulatePicker(candidates, data.SchoolBuildingId);
                    _schoolLabel.isVisible = false;
                }
                else
                {
                    HidePicker();
                    _schoolLabel.isVisible = true;
                    _schoolLabel.text = "School: " + BuildingName(data.SchoolBuildingId);
                }
                UpdateStats(lineId);
            }
            else
            {
                HidePicker();
                _schoolLabel.isVisible = false;
                _locateButton.isVisible = false;
                _statsLabel.isVisible = false;
                _hintLabel.text = string.Empty;
            }
        }

        private void UpdateStats(ushort lineId)
        {
            BoardingStats.Counts c = BoardingStats.Get(lineId);
            _statsLabel.text = "Served " + c.Served + " · turned away " + c.TurnedAway + " (session)";
        }

        private void OnSchoolCheckChanged(UIComponent c, bool isChecked)
        {
            ushort lineId = CurrentLine();
            if (lineId == 0 || !IsBusLine(lineId))
                return; // school lines are bus-only (the panel is hidden for other types anyway)

            if (!isChecked)
            {
                Log.DebugLog("User unflagged line " + lineId + " as a school line");
                SchoolLineRegistry.Unregister(lineId);
                // Back to a normal paid line (field write belongs on the sim thread).
                Singleton<SimulationManager>.instance.AddAction(() => SchoolFares.RestoreDefault(lineId));
                _cachedLine = 0; // force refresh
                return;
            }

            // Auto-detect which school(s) the line serves from its stops. Bind the CLOSEST as the
            // default; if the line passes more than one school, Refresh will show a picker so the
            // player can change it.
            Log.DebugLog("User flagging line " + lineId + " as school line — detecting school…");
            List<SchoolCandidate> candidates = DetectSchools(lineId);
            if (candidates.Count > 0)
            {
                SchoolCandidate best = candidates[0]; // closest (DetectSchools returns sorted)
                Log.DebugLog("Detected " + candidates.Count + " school(s) for line " + lineId
                    + "; binding closest " + best.SchoolId + " at stop " + best.StopNode);
                BindSchool(lineId, best);
                _cachedLine = 0;
            }
            else
            {
                Log.DebugLog("No school found near any stop of line " + lineId + " — reverting flag");
                // Revert the tick; nothing to bind to.
                _schoolCheck.eventCheckChanged -= OnSchoolCheckChanged;
                _schoolCheck.isChecked = false;
                _schoolCheck.eventCheckChanged += OnSchoolCheckChanged;
                _hintLabel.text = "No school found at this line's stops.\nRoute a stop next to a school first.";
            }
        }

        // Binds the line to a chosen school (manual flag) and makes it free for students.
        private void BindSchool(ushort lineId, SchoolCandidate cand)
        {
            SchoolLineRegistry.Register(lineId, new SchoolLineData(cand.SchoolId, cand.StopNode, false));
            // School transport is free for students (field write belongs on the sim thread).
            Singleton<SimulationManager>.instance.AddAction(() => SchoolFares.ApplyFree(lineId));
        }

        // Fills and shows the school picker (one entry per candidate school, nearest first) with
        // `selectedSchool` selected. Caller guarantees candidates.Count > 1.
        private void PopulatePicker(List<SchoolCandidate> candidates, ushort selectedSchool)
        {
            _candidates = candidates;
            var items = new string[candidates.Count];
            int selected = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                items[i] = BuildingName(candidates[i].SchoolId);
                if (candidates[i].SchoolId == selectedSchool)
                    selected = i;
            }

            _schoolDropdown.eventSelectedIndexChanged -= OnSchoolSelected;
            _schoolDropdown.items = items;
            _schoolDropdown.selectedIndex = selected;
            _schoolDropdown.eventSelectedIndexChanged += OnSchoolSelected;
            _schoolDropdown.Show();
        }

        private void HidePicker()
        {
            if (_schoolDropdown != null)
                _schoolDropdown.Hide();
            _candidates = null;
        }

        private void OnSchoolSelected(UIComponent c, int index)
        {
            ushort lineId = CurrentLine();
            if (lineId == 0 || _candidates == null || index < 0 || index >= _candidates.Count)
                return;
            SchoolCandidate cand = _candidates[index];
            Log.DebugLog("User picked school " + cand.SchoolId + " for line " + lineId);
            BindSchool(lineId, cand);
            // Refresh stats/marker against the new school without rebuilding the picker.
            UpdateStats(lineId);
        }

        private void OnLocateClick(UIComponent c, UIMouseEventParameter p)
        {
            ushort lineId = CurrentLine();
            SchoolLineData data;
            if (lineId != 0 && SchoolLineRegistry.TryGet(lineId, out data) && data.SchoolBuildingId != 0)
            {
                Log.DebugLog("User clicked Locate for line " + lineId + " — moving camera to school " + data.SchoolBuildingId);
                PanelUtil.MoveCameraToBuilding(data.SchoolBuildingId, EducationBuildingUtil.GetPosition(data.SchoolBuildingId));
            }
        }

        // Scan ALL the line's stops and collect every distinct K–12 school within range, keeping the
        // nearest stop that reaches each one. Returned sorted nearest-school-first, so element 0 is
        // the default binding and a Count > 1 means the line is ambiguous (offer the picker). Each
        // such stop is a valid school stop (homebound boarding point).
        private List<SchoolCandidate> DetectSchools(ushort lineId)
        {
            var result = new List<SchoolCandidate>();

            TransportManager tm = Singleton<TransportManager>.instance;
            var nodes = Singleton<NetManager>.instance.m_nodes.m_buffer;
            ushort first = tm.m_lines.m_buffer[lineId].m_stops;
            if (first == 0)
                return result;

            ushort stop = first;
            int guard = 0;
            do
            {
                Vector3 stopPos = nodes[stop].m_position;
                ushort school = EducationBuildingUtil.FindSchoolNear(stopPos, SchoolSearchRadius);
                if (school != 0)
                {
                    float sqr = RoadUtil.SqrDistance2D(stopPos, EducationBuildingUtil.GetPosition(school));
                    int existing = result.FindIndex(x => x.SchoolId == school);
                    if (existing < 0)
                        result.Add(new SchoolCandidate { SchoolId = school, StopNode = stop, SqrDistance = sqr });
                    else if (sqr < result[existing].SqrDistance) // a nearer stop for the same school
                        result[existing] = new SchoolCandidate { SchoolId = school, StopNode = stop, SqrDistance = sqr };
                }
                stop = TransportLine.GetNextStop(stop);
                if (++guard > 32768)
                    break;
            }
            while (stop != first && stop != 0);

            result.Sort((a, b) => a.SqrDistance.CompareTo(b.SqrDistance));
            return result;
        }

        private ushort CurrentLine()
        {
            InstanceID id = PanelUtil.GetInstanceID(_wip.component, _wipScript);
            return id.TransportLine;
        }

        // School lines are bus lines only. Metro, tram, train, monorail, ferry, etc. share this
        // same info panel, so the School-line panel must not appear on them.
        private static bool IsBusLine(ushort lineId)
        {
            TransportInfo info = Singleton<TransportManager>.instance.m_lines.m_buffer[lineId].Info;
            return info != null && info.m_transportType == TransportInfo.TransportType.Bus;
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
