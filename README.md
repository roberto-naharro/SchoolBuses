# School Buses

A Cities: Skylines 1 mod that makes school bus lines behave like real school buses: only students travelling to or from their assigned K-12 school may board, and a school's routes can be generated, and kept up to date, automatically.

Vanilla CS1 school buses are a cosmetic skin with identical mechanics to a regular bus. This mod adds a school-bus layer **on top of** vanilla: every Harmony patch is observe-only or scoped to lines you mark as school lines, so it composes cleanly with other transit mods (see [Compatibility](#compatibility)).

---

## Features

- **Students-only boarding** on lines marked as school lines, direction-aware (board anywhere outbound; only at the school stop when homebound).
- **One-click route generation** per school: a school's catchment is split into several short **one-bus loops** (like a real district), not one giant loop.
- **City-wide generation**: route every school in one click (with a confirmation warning).
- **Auto-regeneration**: routes follow the students. When a school's coverage drifts as the city changes, its routes regenerate automatically (toggleable).
- **One bus per route** via Improved Public Transport when present (deterministic), with a budget fallback otherwise.
- **Modded-school-safe**: every size-dependent rule reads each school's *actual* capacity, so it works for custom school assets of any size.
- Read-only **route health and ridership** in the school panel.

---

## How it works

### Students-only boarding filter

When a line is marked as a school line, a citizen boards only if:

```
age in {Child, Teen}
and Citizen.Flags.Student
and Citizen.m_workBuilding == lineSchoolBuilding
and (
    CitizenInstance.m_targetBuilding == lineSchoolBuilding         -- outbound: any stop
    or (CitizenInstance.m_targetBuilding == Citizen.m_homeBuilding -- homebound: school stop only
        and currentStop == schoolStopNodeId)
  )
```

The filter is **non-destructive**: a `void` prefix on `BusAI.LoadPassengers` records the school-line context, and the real gate runs inside `HumanAI.TransportArriveAtSource`, the genuine vanilla per-citizen skip-gate. Ineligible riders are skipped exactly as vanilla skips a citizen who isn't at their stop; everyone else loads normally. Non-students who pathfind to a school stop are made to give up and re-route (so they don't pile up forever); this is intrinsic to a school line, so if you want a line anyone can use, just untick **School line**.

### Multi-route generation

Clicking a school opens a side panel; **Generate Routes** builds the school's whole set:

1. **Roster scan.** Read the school's students from `Building.m_citizenUnits` (units flagged `Student`), collect each student's `m_homeBuilding` position.
2. **Cluster** the home positions by their own distribution (greedy radius clustering; the *school position is not used here*, so each stop sits at the true centre of a neighbourhood). Neighbourhoods whose centre is within walking distance of the school are dropped (those students walk).
3. **Zone** the clusters into compact, non-overlapping angular sectors, the classic **Sweep algorithm** (Gillett and Miller). Each sector becomes one route. The per-route budget is the **inter-stop pickup-loop length** (distance driven *between* pickups; the trunk to/from school is excluded), so nearby neighbourhoods chain into one route and the number of routes emerges naturally.
4. **Order** each sector's stops (nearest-neighbour seed plus closed-loop 2-opt) and **close the loop** so the line completes (see *Line completion* below).
5. **Build** each route as its own `TransportLine`, sharing the school stop as a common terminal, pinned to **one bus**.
6. **Name** them `"<school> - <street>"` (or `"... - n"` when there are several), default them to a school-bus-yellow colour and a school-bus vehicle, and tag each with its school.

**Capacity-scaled stop density.** The minimum students a neighbourhood needs to earn a stop scales with the school's *live* capacity (`clamp(round(capacity x factor), 4, 14)`, factor calibrated so a 1000-capacity school uses about 8). Small schools use a lower threshold so their sparse neighbourhoods still get stops; big schools use a higher one to keep the route count sane. Reading the live capacity makes it work for modded schools of any size.

**Line completion.** A CS1 line only marks itself *complete* when a stop is added, in append mode, within 2.5 m of the first stop, i.e. when you "click the first stop to finish the line". Generated lines add their pickups with explicit indices, so the ring stayed open and never completed. The fix (`RouteBuilder.CloseLoop`) replays that closing append at the first stop, and `LineFinalizer` commits the path-finds while the game is paused (players lay routes paused; the sim step that commits paths is frozen then).

### City-wide generation and auto-regeneration

- **Generate routes for all schools** (mod options) routes the whole city in one click, after a confirmation that warns about the ~256 transport-line limit (it stops before the limit and reports skipped schools), depot capacity, and the brief slowdown from creating many lines at once.
- **Auto-regenerate** (on by default, toggleable) periodically checks each school's live coverage; if the student distribution has drifted so a school covers fewer than a target fraction of its bus-needing students, it regenerates that school's routes (current settings) so the stops follow the students. Turn it off to manage regeneration entirely by hand.

### One bus per route (Improved Public Transport bridge)

A real school route is served by **one bus** (a district runs several routes, each with one bus, not several buses per route). With one bus, a long loop starves itself, which is why a big school gets several short routes. To pin exactly one bus deterministically, the mod calls **IPTE's** public `IptVehicleApi` by reflection (no hard dependency). Without IPTE, a minimal per-line budget yields about one bus.

### Route health and ridership

The building panel lists each route with read-only health, plus the school's **whole-school coverage** (the union of students covered by all its routes, measured against students who actually need a bus, i.e. excluding near-school walkers, so it isn't dragged down by them). Debug logging adds per-line and per-school usage reports (`served`, `turnedAway`, a warm-up-cleaned `steady` ridership rate, and `capture`, the ridership per covered student).

---

## Options

Content Manager, mod options:

- **Enable mod.**
- **Route generation:** auto-tune toggle; cluster radius; min students / cluster; scale-min-by-capacity (plus the 1000-capacity reference); auto-regenerate toggle and coverage trigger; max pickup-loop per route; an optional capacity-scaled route trim (trigger plus catchment distance) for players who'd rather have fewer buses than full coverage; coverage warning threshold; **Restore recommended defaults**.
- **City-wide routes:** generate / delete all school routes.
- **Debug:** enable debug logging (traces actions, generation, boarding, and the usage reports).

**Recommended defaults** (from the tuning below): cluster radius 400 m, capacity-scaled min (about 8 at 1000 capacity), pickup-loop 2000 m, route trim off, auto-regenerate at 30% coverage.

---

## Compatibility

Non-destructive by design: it never reimplements the boarding loop or patches vehicle spawning, depots, `CanLeaveStop`, or pathfinding. Its only patches are a `void` prefix plus postfix on `BusAI.LoadPassengers`, a scoped veto in `HumanAI.TransportArriveAtSource`, an observe-only postfix on `HumanAI.SetCurrentVehicle` (ridership), and a cleanup postfix on `TransportManager.ReleaseLine`.

- **Improved Public Transport (IPTE):** provides the one-bus-per-line control via its public `IptVehicleApi` (reflection); its per-line vehicle list overrides our default vehicle. No conflict.
- **Express Bus Services:** calls `TransportArriveAtSource`, so the eligibility filter still applies inside its loop.
- **Better Train Boarding:** boards via `SetCurrentVehicle`, bypassing `TransportArriveAtSource`; on a line it overrides, our filter is simply inert (never a crash or double-boarding).
- **Impatient Commuters:** School Buses registers an exemption (by reflection) so a child waiting for their assigned school bus is exempt from impatience and won't wander off; no-op if it isn't installed.

---

## Development journal: how the defaults were found

The interesting parameters (cluster radius, min-students, route length) were not guessed; they were tuned in-game against measured behaviour. Short version of the process:

1. **Line completion.** The first blocker was that generated loops never completed. Tracing `TransportLine.AddStop` in IL showed completion requires an append-mode stop within 2.5 m of the first stop. `CloseLoop` reproduces that; `LineFinalizer` commits paths while paused.
2. **One bus, short loops.** A single bus on a 20-stop loop fills early and starves far stops (students piled up about 19 deep). So a big school is split into several short one-bus routes: a capacitated vehicle-routing problem solved with the Sweep algorithm, the route length capped by the *inter-stop* loop (the trunk to school shouldn't force splits).
3. **An experiment harness.** Rather than guess, the mod can sweep parameters: it applies one setting to a fixed set of schools, runs, and logs per-school **capture rate** (steady boardings / covered students) and coverage. Running an identical clean save each round made the *same school across runs* a clean comparison.
4. **Findings.** With one bus per route, **ridership tracks coverage**, so the goal is to maximise coverage. Radius 400 m beat 300 and 500 on real ridership; min 8 (per 1000 capacity) is the peak; pickup-loop about 2000 m is the knee between too many buses and oversubscribed loops. A hard route cap *hurt* (it cut coverage, and so ridership), so route count is controlled organically by min and an optional distance-based trim instead.
5. **Per-size and drift handling.** Fixed `min 8` left small schools badly under-covered, so the min became capacity-scaled. Because students move over time, **auto-regeneration** keeps coverage up as the city evolves.

The full method and per-round data are in [docs/route-tuning.md](docs/route-tuning.md).

---

## Building

```bash
# Prerequisites: Mono, xbuild. GameReferences/ and packages/ are committed.
# Copy .env.example to .env and fill in your machine's values.

xbuild SchoolBuses.csproj /p:Configuration=Release /nologo /verbosity:quiet

./deploy.sh           # build + deploy to the mounted game folder (Debug)
./deploy.sh --release # Release
```

## Publishing

```bash
./deploy.sh --release          # build and stage dist/
./publish.sh "Initial release" # uploads, prints the new Workshop item ID
# Add the item ID to .env (WORKSHOP_ITEM_ID=...) and as a GitHub secret
```

GitHub secrets (environment `PRO`): `RELEASE_PLEASE_TOKEN`, `STEAM_CONFIG_VDF`, `STEAM_USERNAME`, `WORKSHOP_ITEM_ID`.

## Source

[github.com/roberto-naharro/SchoolBuses](https://github.com/roberto-naharro/SchoolBuses)
