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
