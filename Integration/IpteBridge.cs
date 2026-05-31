using System;
using System.Reflection;
using SchoolBuses.Util;

namespace SchoolBuses.Integration
{
    // Pins a generated school line to exactly one bus through Improved Public Transport's generic
    // vehicle-count API (ImprovedPublicTransport.Api.IptVehicleApi in assembly
    // ImprovedPublicTransport2). Bound entirely by reflection — no hard dependency, no load-order
    // requirement. If IPT isn't installed this is a no-op and we keep the per-line m_budget
    // fallback (which yields ~1 bus). We are just one consumer of IPT's general API; it knows
    // nothing about us.
    internal static class IpteBridge
    {
        private const string ApiTypeName =
            "ImprovedPublicTransport.Api.IptVehicleApi, ImprovedPublicTransport2";

        private static bool _resolved;
        private static bool _available;
        private static MethodInfo _setCount;   // void SetVehicleCount(ushort, int)
        private static MethodInfo _resetCount;  // void ResetVehicleCount(ushort)

        internal static bool Available
        {
            get { EnsureResolved(); return _available; }
        }

        private static void EnsureResolved()
        {
            if (_resolved)
                return;
            _resolved = true;
            try
            {
                Type api = Type.GetType(ApiTypeName, false);
                if (api == null)
                {
                    Log.DebugLog("IPT not detected — vehicle count left to the budget setting");
                    return;
                }

                _setCount = api.GetMethod("SetVehicleCount",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(ushort), typeof(int) }, null);
                _resetCount = api.GetMethod("ResetVehicleCount",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(ushort) }, null);

                MethodInfo version = api.GetMethod("GetApiVersion",
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                int v = version != null ? (int)version.Invoke(null, null) : 0;

                _available = _setCount != null;
                if (_available)
                    Log.Info("Improved Public Transport detected (IptVehicleApi v" + v
                        + ") — pinning school lines to 1 bus");
                else
                    Log.Warning("IPT found but IptVehicleApi.SetVehicleCount is missing — using budget fallback");
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to bind IPT vehicle API: " + ex.Message);
                _available = false;
            }
        }

        // Pin the line to a fixed number of vehicles (1 for a school route). Returns true if IPT
        // applied it; false means IPT is absent and the caller keeps the budget fallback.
        internal static bool TrySetVehicleCount(ushort lineId, int count)
        {
            EnsureResolved();
            if (!_available)
                return false;
            try
            {
                _setCount.Invoke(null, new object[] { lineId, count });
                Log.DebugLog("IPT: pinned line " + lineId + " to " + count + " vehicle(s)");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("IPT SetVehicleCount failed for line " + lineId + ": " + ex.Message);
                return false;
            }
        }

        // Hand the line back to IPT's default budget control (undo the pin).
        internal static void Reset(ushort lineId)
        {
            EnsureResolved();
            if (!_available || _resetCount == null)
                return;
            try
            {
                _resetCount.Invoke(null, new object[] { lineId });
            }
            catch (Exception ex)
            {
                Log.Warning("IPT ResetVehicleCount failed for line " + lineId + ": " + ex.Message);
            }
        }
    }
}
