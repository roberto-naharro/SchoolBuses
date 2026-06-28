# Integrating other mods with School Buses

School Buses exposes a small, stable, reflection-friendly API so other mods can cooperate with it
without taking a hard dependency or caring about load order. Other mods drive School Buses by
**calling into this API**; School Buses never reaches into another mod's internals, so partners can
rename or refactor freely without breaking it. (Impatient Commuters already uses this mechanism.)

Type: `SchoolBuses.Integration.SchoolBusBridge` (assembly `SchoolBuses`). All methods are
`public static`, allocation-free, take and return simple types (so they bind cleanly by reflection),
and are safe to call from the simulation thread.

## Calling the API by reflection (no hard dependency)

```csharp
var t = Type.GetType("SchoolBuses.Integration.SchoolBusBridge, SchoolBuses", false);
var isProtected = t?.GetMethod("IsProtectedRider",
    BindingFlags.Public | BindingFlags.Static, null,
    new[] { typeof(ushort), typeof(ushort) }, null);

// later, per waiting citizen:
bool protectedRider = isProtected != null
    && (bool)isProtected.Invoke(null, new object[] { citizenInstanceId, stopNodeId });
```

If School Buses isn't installed, `Type.GetType` returns null and you fall back to your normal
behaviour. Cache the `MethodInfo` once. Call `GetApiVersion()` to confirm the contract version a
build supports before using newer methods.

## API reference

### Query methods

Read-only. Safe to call every frame.

```csharp
// True if the waiting citizen is an eligible student waiting at a stop on a school line that serves
// their school (a child who should be left alone to wait for their school bus).
bool IsProtectedRider(ushort citizenInstanceId, ushort stopNodeId);

// True if the stop node belongs to a registered school line (regardless of who waits there).
bool IsSchoolStop(ushort stopNodeId);

// True if the line is a registered school line (generated OR manually flagged).
bool IsSchoolLine(ushort lineId);

// True if this line's bus is supplied by its school (school-as-depot): feature enabled, school still
// standing, and supply for the line not handed back to depots/TLM. City depots never serve such a
// line, so a line manager can hide its depot selector for it and show no depot cost.
bool IsSchoolOwnedLine(ushort lineId);

// The Education building this line serves, or 0 if it is not a registered school line. The id indexes
// BuildingManager.m_buildings; it is the bound building and is NOT re-validated, so check
// Building.Flags.Created if you need a guaranteed-live building.
ushort GetSchoolBuilding(ushort lineId);

// Integration contract version (currently 5). Also confirms the API is present.
int GetApiVersion();
```

### Control methods

School Buses runs its school lines from the school itself (school-as-depot) and schedules them with
the player's service-hours option. A partner mod can **take that over**. Every setter returns a
`bool` status instead of throwing: `true` = applied, `false` = ignored (wrong mode, or unknown/invalid
input). A partner can never crash School Buses but still learns whether the call took effect.

There are two **mutually-exclusive** styles: the simple service window, or full spawn control.

#### Simple: push a service window

Drive the single on/off window directly. Valid only while advanced control (below) is **not**
engaged.

```csharp
// Buses run only within [startHour, endHour) on the game clock (0-24, wraps past midnight if
// start > end), overriding the player's own service-hours option. Returns false if advanced control
// is engaged or the hours are NaN/Infinity.
bool SetServiceHours(float startHour, float endHour);

// Hand the window back to the player's option. Returns true.
bool ClearServiceHours();

// True while an external service window is in force.
bool HasExternalServiceHours();
```

For a fixed school day this is all a scheduling mod needs: call `SetServiceHours(begin, end)` whenever
the player changes the hours. The comparison is against `SimulationManager.m_currentGameTime`, so it
tracks Real Time's slowed clock automatically.

#### Advanced: full spawn control

For arbitrary schedules (multiple ranges, event-driven) or to hand the whole fleet to a line manager,
**engage external control first**, then drive it. While engaged, the player's service-hours option
and `SetServiceHours` are disabled (the mod's options screen shows a "controlled by another mod"
note); the remaining methods return `false` until control is engaged.

```csharp
// Master flag. Returns the resulting state (true = engaged).
bool SetExternalSpawnControl(bool engaged);
bool IsExternalSpawnControl();

// Pause/resume spawning. Paused = no new buses spawn and running buses finish their route and park at
// the school (soft despawn). Call on your own open/close events for any schedule. The (lineId, …)
// overload sets one line and overrides the global value for it.
bool SetSpawningPaused(bool paused);
bool SetSpawningPaused(ushort lineId, bool paused);

// Disable the SCHOOL supplying buses (school-as-depot). When false, School Buses stops
// spawning/despawning AND stops blocking city depots, so depots / TLM serve the line like a normal
// line. The (lineId, …) overload does this for one line (e.g. only lines a player set to custom
// config). The students-only boarding rule still applies regardless.
bool SetVehicleSupplyEnabled(bool enabled);
bool SetVehicleSupplyEnabled(ushort lineId, bool enabled);

// Introspection: effective values (a per-line override wins, otherwise the global flag).
bool IsSpawningPaused();               // global
bool IsSpawningPaused(ushort lineId);  // for a line
bool IsVehicleSupplyEnabled(ushort lineId);
```

## Mod-specific notes

### Impatient Commuters (and any mod that makes waiting riders give up)

A mod that makes a citizen who has waited too long abandon their journey should **exempt a protected
school rider**, otherwise children wander off before their school bus arrives, exactly the problem
School Buses exists to prevent. Right before your abandon branch, check `IsProtectedRider` with the
stop node the citizen is physically waiting at (School Buses resolves the line from
`NetManager.m_nodes[stopNode].m_transportLine` internally):

```csharp
if (SchoolBusBridgeProxy.IsProtectedRider(instanceId, stopNode))
    return; // skip abandonment for this citizen
```

Impatient Commuters already does this; Real Time should too (see below).

### Real Time

**Abandonment.** Real Time's `ProcessWaitingForTransport` abandons a long-waiting citizen (when *Can
abandon journey* is on). Exempt protected school riders with `IsProtectedRider`, exactly as above, in
`RealTimeResidentAI.ProcessWaitingForTransport` before the `StopMoving` + `DoScheduledHome` branch.
Use a thin reflection wrapper so Real Time keeps no hard dependency.

**Scheduling.** If you want Real Time to own *when* the buses run, push it: call
`SetServiceHours(begin, end)` whenever the player changes the school hours (simple path), or use the
advanced `SetExternalSpawnControl(true)` + `PauseSpawning`/`ResumeSpawning` for anything more
elaborate. School Buses does **not** read Real Time's config, so you can change it freely.

**Pickup vs. drop-off needs nothing.** The "morning is pickup, end of school is drop-off" behaviour
falls out of the eligibility rule, not any time code: a student may board only when `target == school`
(ride to school) or `target == home` (ride home). The resident scheduler (vanilla or Real Time) sets
that target and when; School Buses just reads it. Outside school hours no student has such a target,
so the bus simply carries nobody. Nothing to add on either side.

### Transport Lines Manager

When School Buses detects TLM it **defers line presentation** to it (it does not set the line's colour
or generated name) and leaves the per-line vehicle budget to TLM. It also currently auto-detects TLM
to step out of vehicle management, so the two never both spawn.

The cleaner, explicit path is the control API: call `SetExternalSpawnControl(true)` then
`SetVehicleSupplyEnabled(false)` — globally, or per line for just the lines you manage — and School
Buses hands supply to your depots and stops blocking them. Once TLM does that, the internal TLM
auto-detection becomes redundant and can be removed. School Buses' only remaining behaviour on those
lines is the students-only boarding rule, which TLM does not touch.
