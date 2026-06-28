# Integrating other mods with School Buses

School Buses exposes a small, stable, reflection-friendly API so other mods can cooperate with it
without taking a hard dependency or caring about load order. This is the same mechanism Impatient
Commuters already uses.

## The contract

Type: `SchoolBuses.Integration.SchoolBusBridge` (assembly `SchoolBuses`). All methods are `public static`,
allocation-free, and safe to call every frame from the simulation thread.

```csharp
// True if the waiting citizen is an eligible student waiting at a stop on a school line that
// serves their school (i.e. a child who should be left alone to wait for their school bus).
bool IsProtectedRider(ushort citizenInstanceId, ushort stopNodeId);

// True if the stop node belongs to a registered school line (regardless of who waits there).
bool IsSchoolStop(ushort stopNodeId);

// True if the line is a registered school line (generated OR manually flagged).
bool IsSchoolLine(ushort lineId);

// True if this line's bus is supplied by its school (school-as-depot): feature enabled, school
// still standing, and supply for the line has not been handed back to depots/TLM. City depots
// never serve such a line.
bool IsSchoolOwnedLine(ushort lineId);

// School (Education building) this line serves, or 0 if it is not a registered school line.
// The id indexes BuildingManager.m_buildings. NOTE: it is the bound building and is not
// re-validated, so check Building.Flags.Created if you need a guaranteed-live building.
ushort GetSchoolBuilding(ushort lineId);

// Integration contract version (currently 5). Also confirms the API is present.
int GetApiVersion();
```

There is also a small **control** surface (added in v5) that lets a partner mod *drive* the
school-bus fleet by calling School Buses, instead of School Buses reaching into the partner. See
[Driving the fleet externally](#driving-the-fleet-externally) below.

### Calling it by reflection (no hard dependency)

```csharp
var t = Type.GetType("SchoolBuses.Integration.SchoolBusBridge, SchoolBuses", false);
var isProtected = t?.GetMethod("IsProtectedRider",
    BindingFlags.Public | BindingFlags.Static, null,
    new[] { typeof(ushort), typeof(ushort) }, null);

// later, per waiting citizen:
bool protectedRider = isProtected != null
    && (bool)isProtected.Invoke(null, new object[] { citizenInstanceId, stopNodeId });
```

If School Buses isn't installed, `Type.GetType` returns null and you simply fall back to your normal
behaviour. Cache the `MethodInfo` once.

## For Real Time (and any mod that makes waiting citizens give up)

Real Time's `ProcessWaitingForTransport` makes a citizen who has waited too long **abandon the journey
and go home** (when *Can abandon journey* is enabled). A child waiting for their assigned school bus
should be exempt from that, otherwise they wander off before the bus arrives, exactly the problem
School Buses exists to prevent. (Impatient Commuters already exempts these riders via this same API.)

Suggested integration, in `RealTimeResidentAI.ProcessWaitingForTransport`, right before the abandon
branch (`StopMoving` + `DoScheduledHome`):

```csharp
// Don't abandon a protected school rider: let them wait for their school bus.
ushort stopNode = /* the stop node the citizen is waiting at, from their path */;
if (SchoolBusBridgeProxy.IsProtectedRider(instanceId, stopNode))
{
    return; // skip abandonment for this citizen
}
```

Where `SchoolBusBridgeProxy` is a thin reflection wrapper as shown above (so Real Time keeps no hard
dependency on School Buses). The stop node is the same one Real Time already resolves for the waiting
citizen; School Buses uses `NetManager.m_nodes[stopNode].m_transportLine` internally to find the line,
so pass the node the citizen is physically waiting at.

### Pickup vs. drop-off directionality: handled by eligibility, by design

The "morning is pickup, end of school is drop-off" behaviour needs **no time code** in School Buses,
because it falls out of the eligibility rule combined with the resident scheduler (vanilla, or Real
Time when present):

A student may board only when `target == school` **or** `target == home`. The scheduler decides which
one is set, and when:

- **Morning:** the scheduler sends them to school, so `target == school`: students board at
  neighbourhood stops and ride to the school. That is *pickup*.
- **End of school:** the scheduler sends them home, so `target == home`: students board at the school
  and ride home. That is *drop-off*.
- **Outside school hours:** no student has a school/home school-trip target, so no one is eligible and
  the bus simply carries nobody.

So directionality is a *consequence* of the scheduler setting the target; School Buses just reads the
result. There is nothing to add on either side for this.

### Running on a schedule

School Buses has its own service-hours option (start/end hour, compared against
`SimulationManager.m_currentGameTime`, so it tracks Real Time's slowed clock), which the **player**
sets. School Buses deliberately does **not** read Real Time's config — instead, if you want to own
the timing, you *push* it to School Buses through the control API (`SetServiceHours`, or the advanced
`PauseSpawning`/`ResumeSpawning`). That way Real Time can rename or refactor its internals freely
without ever breaking School Buses. See the next section.

This mirrors the existing Impatient Commuters integration: the query contract above is stable and
versioned (`GetApiVersion()` returns `5`).

## Driving the fleet externally

School Buses runs its generated/flagged school lines from the school itself (school-as-depot) and
schedules them with the player's service-hours option. A partner mod can **take that over** by calling
School Buses — School Buses never reaches into the partner, so the partner owns its own internals.

All control methods are `public static` on `SchoolBusBridge`, take/return simple types (so they bind
cleanly by reflection), and **return a `bool` status instead of throwing**: `true` = applied, `false`
= ignored (wrong mode, or unknown/invalid input). A partner can never crash School Buses but still
learns whether the call took effect.

There are two mutually-exclusive styles.

### Simple: push a service window

Drive the single on/off window directly. Valid only while advanced control (below) is **not** engaged.

```csharp
// Buses run only within [startHour, endHour) on the game clock (0-24, wraps past midnight).
// Overrides the player's own service-hours option. Returns false if advanced control is engaged
// or the hours are NaN/Infinity.
bool SetServiceHours(float startHour, float endHour);

// Hand the window back to the player's option.
bool ClearServiceHours();

// True while an external window is in force.
bool HasExternalServiceHours();
```

For a fixed school day this is all Real Time needs: call `SetServiceHours(begin, end)` whenever the
player changes the hours.

### Advanced: full spawn control

For arbitrary schedules (multiple ranges, event-driven) or to hand the whole fleet to a line manager,
**engage external control first**, then drive it. While engaged, the player's service-hours option
(and `SetServiceHours`) are disabled — the mod's options screen shows a "controlled by another mod"
note — and the methods below become active (they return `false` until control is engaged).

```csharp
// Master flag. Returns the resulting state (true = engaged).
bool SetExternalSpawnControl(bool engaged);
bool IsExternalSpawnControl();

// Pause/resume spawning. Paused = no new buses spawn and running buses finish their route and park
// at the school (soft despawn). Call on your own open/close events for any schedule. The (lineId,…)
// overload sets one line and overrides the global value for it.
bool SetSpawningPaused(bool paused);
bool SetSpawningPaused(ushort lineId, bool paused);

// Disable the SCHOOL supplying buses (the school-as-depot). When false, School Buses stops
// spawning/despawning AND stops blocking city depots, so depots / TLM serve the line like a normal
// line. The (lineId,…) overload does this for one line (e.g. only lines a player set to custom
// config). The students-only boarding rule still applies regardless.
bool SetVehicleSupplyEnabled(bool enabled);
bool SetVehicleSupplyEnabled(ushort lineId, bool enabled);

// Introspection (effective values).
bool IsSpawningPaused();              // global
bool IsSpawningPaused(ushort lineId); // per-line override, else global
bool IsVehicleSupplyEnabled(ushort lineId);
```

## For Transport Lines Manager

When School Buses detects TLM it **defers line presentation to TLM** (it does not set the line's
colour or generated name) and leaves the per-line vehicle budget to TLM. Today it also auto-detects
TLM to step out of the way of vehicle management (so the two don't both spawn). The cleaner, explicit
path is the control API above: call `SetExternalSpawnControl(true)` then
`SetVehicleSupplyEnabled(false)` (globally, or per line for just the lines you manage), and School
Buses hands supply to your depots. Once TLM does that, the internal TLM auto-detection becomes
redundant and can be removed. School Buses' only remaining behaviour on those lines is the
students-only boarding rule, which TLM does not touch.
