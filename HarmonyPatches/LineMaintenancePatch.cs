using ColossalFramework;
using HarmonyLib;
using SchoolBuses.Data;

namespace SchoolBuses.HarmonyPatches
{
    // Free school transport, cost side: TransportLine.SimulationStep charges weekly maintenance as
    // vehicleCount × TransportInfo.m_maintenanceCostPerVehicle + capacity × m_maintenanceCostPerPassenger
    // (IL-verified). Those fields live on the SHARED bus TransportInfo, so we zero them only for the
    // duration of a school line's own step and restore right after — line steps run serially on the
    // simulation thread, so the swap can never leak into another line's charge.
    //
    // Coexists with IPTE: its transpiler stubs the vanilla FetchResource and re-charges in its own
    // postfix reading the same fields; depending on postfix order IPTE may still charge school
    // lines, so IPTE is asked (bridge handoff) to skip lines where SchoolBusBridge.IsSchoolLine.
    [HarmonyPatch(typeof(TransportLine), "SimulationStep")]
    internal static class LineMaintenancePatch
    {
        internal struct Swap
        {
            public TransportInfo Info;
            public int PerVehicle;
            public float PerPassenger;
        }

        private static void Prefix(ushort lineID, out Swap __state)
        {
            __state = default(Swap);
            if (!Settings.Instance.Enabled || !Settings.Instance.FreeSchoolTransport)
                return;
            if (!SchoolLineRegistry.IsSchoolLine(lineID))
                return;

            TransportInfo info = Singleton<TransportManager>.instance.m_lines.m_buffer[lineID].Info;
            if (info == null)
                return;

            __state.Info = info;
            __state.PerVehicle = info.m_maintenanceCostPerVehicle;
            __state.PerPassenger = info.m_maintenanceCostPerPassenger;
            info.m_maintenanceCostPerVehicle = 0;
            info.m_maintenanceCostPerPassenger = 0f;
        }

        private static void Postfix(Swap __state)
        {
            if (__state.Info == null)
                return;
            __state.Info.m_maintenanceCostPerVehicle = __state.PerVehicle;
            __state.Info.m_maintenanceCostPerPassenger = __state.PerPassenger;
        }
    }
}
