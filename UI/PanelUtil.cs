using System;
using System.Reflection;
using ColossalFramework.UI;
using UnityEngine;

namespace SchoolBuses.UI
{
    // Shared helpers for reading the currently-selected entity out of a vanilla
    // WorldInfoPanel and for moving the camera.
    internal static class PanelUtil
    {
        // Dock `panel` (a child of `host`) beside its host, keeping it ON SCREEN. The preferred
        // host-relative x is used first; if that would clip past either screen edge, the fallback
        // side is used instead, and the vertical offset is clamped into the screen. Bounds come
        // from GetUIView().GetScreenResolution(), which is in UI units and therefore correct under
        // UI scaling mods (UI Resolution) — the reported bug was our panel landing off-screen when
        // the rescaled vanilla panel sits near a screen edge.
        internal static void DockBeside(UIComponent panel, UIComponent host,
            float preferredX, float fallbackX, float y)
        {
            Vector2 screen = panel.GetUIView().GetScreenResolution();
            Vector3 hostAbs = host.absolutePosition;

            float x = preferredX;
            if (hostAbs.x + x < 0f || hostAbs.x + x + panel.width > screen.x)
                x = fallbackX;

            if (hostAbs.y + y + panel.height > screen.y)
                y = screen.y - panel.height - hostAbs.y;
            if (hostAbs.y + y < 0f)
                y = -hostAbs.y;

            panel.relativePosition = new Vector3(x, y);
        }

        // Reads the protected `m_InstanceID` field declared on WorldInfoPanel (walks
        // the base-type chain). Returns default(InstanceID) on failure.
        internal static InstanceID GetInstanceID(UIComponent panelComponent, object panelScript)
        {
            object target = panelScript ?? panelComponent;
            if (target == null)
                return default(InstanceID);

            FieldInfo field = FindField(target.GetType(), "m_InstanceID");
            if (field == null)
                return default(InstanceID);
            try
            {
                return (InstanceID)field.GetValue(target);
            }
            catch
            {
                return default(InstanceID);
            }
        }

        private static FieldInfo FindField(Type type, string name)
        {
            while (type != null)
            {
                FieldInfo f = type.GetField(name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null)
                    return f;
                type = type.BaseType;
            }
            return null;
        }

        internal static void MoveCameraToBuilding(ushort buildingId, Vector3 position)
        {
            try
            {
                var cam = ToolsModifierControl.cameraController;
                if (cam != null)
                {
                    InstanceID id = default(InstanceID);
                    id.Building = buildingId;
                    cam.SetTarget(id, position, false);
                }
            }
            catch (Exception ex)
            {
                Util.Log.Warning("MoveCamera failed: " + ex.Message);
            }
        }
    }
}
