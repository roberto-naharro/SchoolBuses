using System;
using SchoolBuses.Util;

namespace SchoolBuses.Integration
{
    // Detects Transport Lines Manager (TLM). When TLM is installed it is the line-presentation
    // manager: it owns line colour and naming (auto-colour / auto-name, and a GetColor override),
    // and it governs each line's vehicle budget (it seeds that budget from our m_budget on first
    // access, so a school line still starts at ~1 bus). To avoid fighting it, School Buses suppresses
    // its own cosmetic styling (colour + generated name) when TLM is present and lets TLM manage them.
    //
    // Detection only — no hard dependency, no load-order requirement. Bound by assembly name so it
    // survives namespace/type renames across TLM versions.
    internal static class TlmBridge
    {
        private const string AssemblyName = "TransportLinesManager";

        private static bool _checked;
        private static bool _present;

        internal static bool IsPresent
        {
            get
            {
                if (_checked)
                    return _present;
                _checked = true;
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == AssemblyName)
                        {
                            _present = true;
                            break;
                        }
                    }
                    Log.Info(_present
                        ? "Transport Lines Manager detected — deferring line colour/name to TLM"
                        : "Transport Lines Manager not detected");
                }
                catch (Exception ex)
                {
                    Log.Warning("TLM detection failed: " + ex.Message);
                    _present = false;
                }
                return _present;
            }
        }
    }
}
