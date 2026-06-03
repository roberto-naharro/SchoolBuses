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

// Integration contract version (currently 1). Also confirms the API is present.
int GetApiVersion();
```

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

### Time of day, pickup vs. drop-off: already handled, by design

School Buses intentionally has **no concept of game time or school hours**: that is Real Time's
domain, and duplicating it here would couple the two mods and be wrong whenever Real Time is absent.
The "morning is pickup, end of school is drop-off" behaviour does **not** need any time code in School
Buses, because it falls out of the eligibility rule combined with Real Time's own scheduling:

A student may board only when `target == school` **or** `target == home`. Real Time decides which one is
set, and when:

- **Morning:** Real Time schedules `GoToSchool`, so `target == school`: students board at neighbourhood
  stops and ride to the school. That is *pickup*.
- **End of school:** Real Time sends them home, so `target == home`: students board at the school and
  ride home. That is *drop-off*.
- **Outside school hours:** no student has a school/home school-trip target, so no one is eligible and
  the bus simply carries nobody.

So directionality is a *consequence* of Real Time setting the target; School Buses just reads the
result. There is nothing to add on either side for this.

The remaining wish, "don't run the buses outside school hours", is fleet scheduling, not a School
Buses concern: a school line runs one bus like any line. Idling it off-hours is done with **Transport
Lines Manager's per-hour budget** (set the line to 0 vehicles outside school hours), which is the
correct, existing place for time-of-day vehicle control.

This mirrors the existing Impatient Commuters integration and needs no changes on the School Buses
side: the contract above is stable and versioned (`GetApiVersion()` returns `1`).

## For Transport Lines Manager

No integration code is needed. When School Buses detects TLM it **defers line presentation to TLM**:
it does not set the line's colour or generated name, and it leaves the per-line vehicle budget to TLM
(TLM seeds that budget from the line's `m_budget`, which School Buses sets to a minimal ~1-bus value,
so a school line still starts at roughly one bus and you manage it from there in TLM). School Buses'
only remaining behaviour on those lines is the students-only boarding rule, which TLM does not touch.
