using ColossalFramework;
using SchoolBuses.Data;
using SchoolBuses.Util;

namespace SchoolBuses.Integration
{
    // Stable public API for sibling mods to query School Buses state.
    //
    // Intended consumer: Impatient Commuters, which calls IsProtectedRider via reflection
    // so it can exempt school students from its impatience/abandonment logic — a child
    // waiting for their assigned school bus should not give up and wander off.
    //
    // KEEP THIS SIGNATURE STABLE: it is bound by reflection
    // (Type "SchoolBuses.Integration.SchoolBusBridge, SchoolBuses",
    //  method "IsProtectedRider(ushort, ushort) : bool"). Allocation-free; safe per-frame.
    public static class SchoolBusBridge
    {
        // True if the waiting citizen is an eligible school student waiting at a stop that
        // belongs to a school line serving their school. Such riders should be left alone
        // (not made impatient / bored).
        public static bool IsProtectedRider(ushort citizenInstanceId, ushort stopNodeId)
        {
            if (citizenInstanceId == 0 || stopNodeId == 0)
                return false;

            ushort lineId = Singleton<NetManager>.instance.m_nodes.m_buffer[stopNodeId].m_transportLine;
            if (lineId == 0)
                return false;

            SchoolLineData line;
            if (!SchoolLineRegistry.TryGet(lineId, out line))
                return false;

            var inst = Singleton<CitizenManager>.instance.m_instances.m_buffer[citizenInstanceId];
            return CitizenEligibility.IsEligible(inst.m_citizen, inst.m_targetBuilding, stopNodeId, ref line);
        }

        // True if the stop node belongs to a registered school line (regardless of who waits).
        public static bool IsSchoolStop(ushort stopNodeId)
        {
            if (stopNodeId == 0)
                return false;
            ushort lineId = Singleton<NetManager>.instance.m_nodes.m_buffer[stopNodeId].m_transportLine;
            return lineId != 0 && SchoolLineRegistry.IsSchoolLine(lineId);
        }

        // True if the line is a registered school line (generated OR manually flagged). Lets a line
        // manager treat school lines as a free school service (e.g. skip charging maintenance).
        // Reflection contract: "IsSchoolLine(ushort) : bool".
        public static bool IsSchoolLine(ushort lineId)
        {
            return lineId != 0 && SchoolLineRegistry.IsSchoolLine(lineId);
        }

        // True if this line's bus is supplied BY ITS SCHOOL (school-as-depot): any registered school
        // line (generated OR manually flagged), feature enabled, school still standing. City depots
        // never serve such a line, so a line manager can hide/disable its depot selector for it and
        // show no depot cost. Reflection contract: "IsSchoolOwnedLine(ushort) : bool".
        public static bool IsSchoolOwnedLine(ushort lineId)
        {
            // False when TLM is present or a partner mod handed this line's supply back to depots
            // (SetVehicleSupplyEnabled(false)) — then a depot supplies the bus, not the school.
            if (lineId == 0 || !Settings.Instance.Enabled || !Routing.SchoolDepot.SuppliesLine(lineId))
                return false;
            SchoolLineData data;
            if (!SchoolLineRegistry.TryGet(lineId, out data))
                return false;
            if (data.SchoolBuildingId == 0)
                return false;
            var buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            return (buildings[data.SchoolBuildingId].m_flags & Building.Flags.Created) != Building.Flags.None;
        }

        // School (Education building) this line serves, or 0 if it is not a registered school line.
        // The id indexes BuildingManager.m_buildings. NOTE: this is the bound building and is not
        // re-validated — if the school was bulldozed the id may be stale, so a consumer that needs a
        // live building should check its Building.Flags.Created. Allocation-free; safe from any thread.
        // Reflection contract: "GetSchoolBuilding(ushort) : ushort".
        public static ushort GetSchoolBuilding(ushort lineId)
        {
            return lineId == 0 ? (ushort)0 : SchoolLineRegistry.SchoolOfLineFast(lineId);
        }

        // ════════════════════════════════════════════════════════════════════════════════════
        //  EXTERNAL CONTROL  —  partner mods drive School Buses by CALLING US
        // ════════════════════════════════════════════════════════════════════════════════════
        //
        // These let another mod CALL INTO School Buses to drive the school-bus fleet, instead of
        // School Buses reaching into that mod's internals. That INVERTS the dependency: the partner
        // can rename or refactor freely without breaking us, and we keep ZERO mod-specific code — one
        // generic surface serves every mod (Real Time, TLM, a future time mod, anything).
        //
        // Two mutually-exclusive integration styles:
        //   • SIMPLE   — push a single service window: SetServiceHours / ClearServiceHours. Valid
        //                only while external control is NOT engaged.
        //   • ADVANCED — take control with SetExternalSpawnControl(true), then drive the fleet with
        //                SetSpawningPaused / SetVehicleSupplyEnabled (global, or per-line with a line
        //                id). Lets you express any number of time ranges (just pause/resume) and hand
        //                the whole fleet over (supply disabled → city depots / TLM serve the line).
        //
        // Every setter returns a bool STATUS instead of throwing: true = applied, false = ignored
        // (wrong mode, or NaN/unknown input). A partner can never crash us, but still learns whether
        // the call took effect. All allocation-free and safe from the simulation thread.

        // ───────────────────────────── Advanced: master control flag ─────────────────────────────

        /// <summary>
        /// Engage or release EXTERNAL SPAWN CONTROL — the master flag for the advanced integration.
        /// While engaged, the player's service-hours option (and <see cref="SetServiceHours"/>) are
        /// disabled, and the <c>SetSpawningPaused</c> / <c>SetVehicleSupplyEnabled</c> methods become
        /// active. Releasing it hands control back to the player / the simple service-window path.
        /// </summary>
        /// <param name="engaged">true to take control, false to release it.</param>
        /// <returns>The resulting state (true = external control now engaged).</returns>
        /// <remarks>Reflection contract: "SetExternalSpawnControl(bool) : bool".</remarks>
        public static bool SetExternalSpawnControl(bool engaged)
        {
            ExternalControl.SetExternalControl(engaged);
            return ExternalControl.ExternalControlEngaged;
        }

        /// <summary>True while a partner mod holds external spawn control.</summary>
        /// <returns>true if external spawn control is engaged.</returns>
        /// <remarks>Reflection contract: "IsExternalSpawnControl() : bool".</remarks>
        public static bool IsExternalSpawnControl()
        {
            return ExternalControl.ExternalControlEngaged;
        }

        // ───────────────────────────── Advanced: pause / resume ─────────────────────────────

        /// <summary>
        /// Pause or resume school-bus spawning for ALL school lines. While paused, no new buses
        /// spawn and running buses finish their route and park at the school (soft despawn); resuming
        /// lets them spawn again. Use this for arbitrary schedules (call it on your own open/close
        /// events) instead of a fixed window.
        /// </summary>
        /// <param name="paused">true to stop spawning, false to allow it.</param>
        /// <returns>true if applied; false if external spawn control is not engaged.</returns>
        /// <remarks>Reflection contract: "SetSpawningPaused(bool) : bool".</remarks>
        public static bool SetSpawningPaused(bool paused)
        {
            if (!ExternalControl.ExternalControlEngaged)
                return false;
            ExternalControl.SetGlobalPaused(paused);
            return true;
        }

        /// <summary>
        /// Pause or resume school-bus spawning for ONE line, overriding the global pause state for
        /// that line. Same soft-despawn behaviour as the global form.
        /// </summary>
        /// <param name="lineId">The transport line id.</param>
        /// <param name="paused">true to stop spawning on this line, false to allow it.</param>
        /// <returns>true if applied; false if external spawn control is not engaged or lineId is 0.</returns>
        /// <remarks>Reflection contract: "SetSpawningPaused(uint16, bool) : bool".</remarks>
        public static bool SetSpawningPaused(ushort lineId, bool paused)
        {
            if (!ExternalControl.ExternalControlEngaged || lineId == 0)
                return false;
            ExternalControl.SetLinePaused(lineId, paused);
            return true;
        }

        // ───────────────────────────── Advanced: vehicle supply ─────────────────────────────

        /// <summary>
        /// Enable or disable the SCHOOL supplying buses for ALL school lines. Disabling it makes
        /// School Buses stop spawning/despawning AND stop blocking city depots, so depots (or TLM)
        /// supply the lines like a normal line — the way to hand the whole fleet over to a line
        /// manager. The students-only boarding rule still applies regardless.
        /// </summary>
        /// <param name="enabled">true = the school supplies the buses; false = depots/TLM do.</param>
        /// <returns>true if applied; false if external spawn control is not engaged.</returns>
        /// <remarks>Reflection contract: "SetVehicleSupplyEnabled(bool) : bool".</remarks>
        public static bool SetVehicleSupplyEnabled(bool enabled)
        {
            if (!ExternalControl.ExternalControlEngaged)
                return false;
            ExternalControl.SetGlobalSupplyEnabled(enabled);
            return true;
        }

        /// <summary>
        /// Enable or disable the school supplying buses for ONE line, overriding the global supply
        /// state for that line (e.g. TLM taking over only the lines a player set to custom config).
        /// </summary>
        /// <param name="lineId">The transport line id.</param>
        /// <param name="enabled">true = the school supplies this line; false = depots/TLM do.</param>
        /// <returns>true if applied; false if external spawn control is not engaged or lineId is 0.</returns>
        /// <remarks>Reflection contract: "SetVehicleSupplyEnabled(uint16, bool) : bool".</remarks>
        public static bool SetVehicleSupplyEnabled(ushort lineId, bool enabled)
        {
            if (!ExternalControl.ExternalControlEngaged || lineId == 0)
                return false;
            ExternalControl.SetLineSupplyEnabled(lineId, enabled);
            return true;
        }

        // ───────────────────────────── Simple: service window ─────────────────────────────

        /// <summary>
        /// Drive the school-bus SERVICE WINDOW directly (the simple path): buses run only within
        /// [startHour, endHour) on the game clock (0-24, wraps past midnight if start &gt; end),
        /// overriding the player's own service-hours option. Call whenever your hours change.
        /// </summary>
        /// <param name="startHour">Window start, 0-24.</param>
        /// <param name="endHour">Window end, 0-24.</param>
        /// <returns>
        /// true if applied; false if external spawn control is engaged (use pause/resume then) or the
        /// hours are NaN/Infinity.
        /// </returns>
        /// <remarks>Reflection contract: "SetServiceHours(single, single) : bool".</remarks>
        public static bool SetServiceHours(float startHour, float endHour)
        {
            if (ExternalControl.ExternalControlEngaged)
                return false; // advanced mode owns the schedule — the simple window is disabled
            if (float.IsNaN(startHour) || float.IsNaN(endHour)
                || float.IsInfinity(startHour) || float.IsInfinity(endHour))
                return false;
            ExternalControl.SetServiceHours(startHour, endHour);
            return true;
        }

        /// <summary>
        /// Stop driving the service window from outside; the player's own option takes over again.
        /// </summary>
        /// <returns>true (the override is cleared either way).</returns>
        /// <remarks>Reflection contract: "ClearServiceHours() : bool".</remarks>
        public static bool ClearServiceHours()
        {
            ExternalControl.ClearServiceHours();
            return true;
        }

        /// <summary>True if a partner mod is currently driving the service window via SetServiceHours.</summary>
        /// <returns>true if an external service window is in force.</returns>
        /// <remarks>Reflection contract: "HasExternalServiceHours() : bool".</remarks>
        public static bool HasExternalServiceHours()
        {
            return ExternalControl.HasServiceHours;
        }

        // ───────────────────────────── Introspection ─────────────────────────────

        /// <summary>The EFFECTIVE pause state for ALL lines (the global flag).</summary>
        /// <returns>true if spawning is globally paused.</returns>
        /// <remarks>Reflection contract: "IsSpawningPaused() : bool".</remarks>
        public static bool IsSpawningPaused()
        {
            return ExternalControl.GlobalPaused;
        }

        /// <summary>The EFFECTIVE pause state for one line (its per-line override, else the global flag).</summary>
        /// <param name="lineId">The transport line id.</param>
        /// <returns>true if spawning is paused for this line.</returns>
        /// <remarks>Reflection contract: "IsSpawningPaused(uint16) : bool".</remarks>
        public static bool IsSpawningPaused(ushort lineId)
        {
            return ExternalControl.IsSpawningPaused(lineId);
        }

        /// <summary>The EFFECTIVE supply state for one line (its per-line override, else the global flag).</summary>
        /// <param name="lineId">The transport line id.</param>
        /// <returns>true if the school supplies this line's buses.</returns>
        /// <remarks>Reflection contract: "IsVehicleSupplyEnabled(uint16) : bool".</remarks>
        public static bool IsVehicleSupplyEnabled(ushort lineId)
        {
            return ExternalControl.IsVehicleSupplyEnabled(lineId);
        }

        // ───────────────────────────── Version ─────────────────────────────

        /// <summary>The integration contract version; also confirms the API is present.</summary>
        // v5: + external spawn control (SetExternalSpawnControl/IsExternalSpawnControl), pause/resume
        //      (SetSpawningPaused, global + per-line), vehicle supply (SetVehicleSupplyEnabled, global
        //      + per-line), introspection; SetServiceHours/ClearServiceHours now return bool.
        public const int ApiVersion = 5;

        /// <summary>Returns <see cref="ApiVersion"/>.</summary>
        /// <remarks>Reflection contract: "GetApiVersion() : int32".</remarks>
        public static int GetApiVersion() => ApiVersion;
    }
}
