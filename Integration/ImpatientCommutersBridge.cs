using System;
using System.Reflection;
using SchoolBuses.Util;

namespace SchoolBuses.Integration
{
    // Registers our school-rider exemption with Impatient Commuters' generic extension point
    // (ImpatientCommuters.Api.ImpatientCommutersApi). Bound entirely by reflection, so there
    // is no hard dependency and no load-order requirement — if Impatient Commuters is absent,
    // this is a no-op. We are just one consumer of a general API; IC knows nothing about us.
    internal static class ImpatientCommutersBridge
    {
        private const string ApiTypeName = "ImpatientCommuters.Api.ImpatientCommutersApi, ImpatientCommuters";

        private static bool _registered;
        private static Func<ushort, ushort, bool> _predicate;
        private static MethodInfo _remove;

        internal static void Register()
        {
            if (_registered)
                return;
            try
            {
                Type api = Type.GetType(ApiTypeName, false);
                if (api == null)
                {
                    Log.DebugLog("Impatient Commuters not detected — no exemption to register");
                    return;
                }

                Type predType = typeof(Func<ushort, ushort, bool>);
                MethodInfo register = api.GetMethod("RegisterExemption",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { predType }, null);
                _remove = api.GetMethod("RemoveExemption",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { predType }, null);

                if (register == null)
                {
                    Log.Warning("Impatient Commuters found but RegisterExemption is missing — exemption inactive");
                    return;
                }

                _predicate = SchoolBusBridge.IsProtectedRider;
                register.Invoke(null, new object[] { _predicate });
                _registered = true;
                Log.Info("Registered school-rider exemption with Impatient Commuters");
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to register exemption with Impatient Commuters: " + ex.Message);
                _predicate = null;
                _remove = null;
            }
        }

        internal static void Unregister()
        {
            if (!_registered)
                return;
            try
            {
                if (_remove != null && _predicate != null)
                    _remove.Invoke(null, new object[] { _predicate });
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to remove exemption from Impatient Commuters: " + ex.Message);
            }
            finally
            {
                _registered = false;
            }
        }
    }
}
