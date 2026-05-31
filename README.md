# School Buses

A Cities: Skylines mod that makes school bus lines work as real school buses: only students travelling to or from their assigned school may board, with one-click route generation from the school building.

## How it works

Vanilla CS1 school buses are a cosmetic skin with identical mechanics to a regular bus. This mod adds a school-bus layer **on top of** vanilla — every patch is observe-only or scoped to lines you mark as school lines, so it composes cleanly with other transit mods (see [Compatibility](#compatibility)).

**Students-only boarding filter.** When a bus line is marked as a school line, a citizen boards only if:

```
age ∈ {Child, Teen}
∧ Citizen.Flags.Student
∧ Citizen.m_workBuilding == lineSchoolBuilding
∧ (
    CitizenInstance.m_targetBuilding == lineSchoolBuilding   -- outbound: any stop
    ∨
    (CitizenInstance.m_targetBuilding == Citizen.m_homeBuilding  -- homebound: school stop only
     ∧ currentStop == schoolStopNodeId)
  )
```

The stop-aware gate on the homebound leg ensures students can only board at the school itself when going home, not at intermediate neighbourhood stops.

The filter is **non-destructive**: instead of reimplementing the boarding loop, a `void` prefix on `BusAI.LoadPassengers` records the school-line context, and the real gate runs inside `HumanAI.TransportArriveAtSource` — the genuine vanilla per-citizen skip-gate. Ineligible riders are skipped exactly as vanilla skips a citizen who isn't at their stop; everyone else loads normally.

**Marking a line as a school line.** Two ways:

- *Manual* — open any bus line and tick **School line**; the mod scans the line's stops and binds it to the K–12 school next to one of them (that stop becomes the school stop).
- *Automatic* — Generate Route (below) marks the line it creates.

**One-click route generation.** Clicking a school building opens a side panel. Pressing **Generate Route**:

1. Reads the school's student roster from `Building.m_citizenUnits` (units with `CitizenUnit.Flags.Student`), collects each student's `m_homeBuilding` position.
2. Clusters those positions with a greedy radius heuristic; caps at a configurable number of stops.
3. Snaps each cluster centroid to the nearest road.
4. Orders stops with nearest-neighbour seed + 2-opt to avoid crossing/go-around routes.
5. Calls `TransportManager.CreateLine` + `TransportLine.AddStop` to build the line, with the school as terminal stop.
6. Auto-marks the line as school-only and stores `schoolBuildingID` + `schoolStopNodeId`.
7. **Assigns a school-bus vehicle** if one is available (see below). Depots are auto-assigned by the game; if none serves the area the line is created but idle and the panel says so.

**Default school-bus vehicle.** Generated lines are set to a "school bus"–style vehicle (detected by name among loaded bus assets — base, DLC or Workshop) via the **public vanilla** `TransportManager.AssignSelectedLineVehicle`. This is the same per-line selection the vanilla vehicle-selector button writes, so you can change it any time with vanilla tools or with **Improved Public Transport**. If no school-bus asset is present, the line keeps the depot's default bus.

**Route health & ridership.** The building panel lists each of the school's lines with live, read-only health — coverage drift, "no buses running" (often: no depot), or "buses stuck in traffic" (`Vehicle.m_blockCounter`) — and offers one-click **Regenerate** when coverage has drifted. Each line also shows a session tally of students *served* vs *turned away*.

## Compatibility

The mod is designed to be **non-destructive** and to layer on top of base behaviour:

- It never reimplements the boarding loop or patches vehicle spawning, depots, `CanLeaveStop`, or pathfinding. Its only patches are a `void` prefix + postfix on `BusAI.LoadPassengers`, a scoped veto in `HumanAI.TransportArriveAtSource`, an observe-only postfix on `HumanAI.SetCurrentVehicle` (ridership stat), and a cleanup postfix on `TransportManager.ReleaseLine`.
- **Improved Public Transport (IPT/IPTE)** — its boarding stat (a passenger-count diff) stays correct, and its per-line vehicle list overrides our default vehicle. No conflict.
- **Express Bus Services** — calls `TransportArriveAtSource`, so our eligibility filter still applies inside its loop.
- **Better Train Boarding** — boards via `SetCurrentVehicle` and bypasses `TransportArriveAtSource`; on a line it overrides, our filter is simply inert (never a crash or double-boarding).
- **Vehicle Selector** — we never patch spawning, so it wins if used on the serving depot.

### Impatient Commuters integration

The companion **Impatient Commuters** mod exposes a generic exemption API (`ImpatientCommuters.Api.ImpatientCommutersApi.RegisterExemption(Func<ushort,ushort,bool>)`). School Buses registers a predicate into it (by reflection, no hard dependency) so a child waiting for their assigned school bus is **exempt from impatience** and won't give up and wander off. If Impatient Commuters isn't installed, this is a no-op. School Buses also exposes `SchoolBuses.Integration.SchoolBusBridge.IsProtectedRider/IsSchoolStop` for any other mod that wants to query school-line state.

## Options

In the Content Manager mod options: master enable, route-generation tunables (cluster/coverage radius, max stops, coverage warning threshold), and **Enable debug logging** (persisted). Turn debug logging on to trace user actions, route generation, and per-stop boarding in the game log.

## Building

```bash
# Prerequisites: Mono, xbuild
# GameReferences/ and packages/ are committed.
# Copy .env.example to .env and fill in your machine's values.

xbuild SchoolBuses.csproj /p:Configuration=Release /nologo /verbosity:quiet

# Build + deploy to mounted game folder:
./deploy.sh           # Debug
./deploy.sh --release # Release
```

## GitHub Secrets required (environment: PRO)

| Secret | Description |
|---|---|
| `RELEASE_PLEASE_TOKEN` | PAT with repo scope — lets the Release Please tag trigger the deploy workflow |
| `STEAM_CONFIG_VDF` | base64-encoded `~/.steam/steamcmd/config/config.vdf` |
| `STEAM_USERNAME` | Steam account username |
| `WORKSHOP_ITEM_ID` | Workshop item ID — set after first publish via `./publish.sh` |

## First publish

```bash
./deploy.sh --release          # build and stage dist/
./publish.sh "Initial release" # uploads, prints the new item ID
# Add the item ID to .env (WORKSHOP_ITEM_ID=...) and as a GitHub secret
```
