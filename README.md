# School Buses

A Cities: Skylines 1 mod that makes school bus lines behave like real school buses: only students travelling to or from their assigned K-12 school may board, and a school's routes can be generated, and kept up to date, automatically.

Vanilla CS1 school buses are a cosmetic skin with identical mechanics to a regular bus. This mod adds a school-bus layer **on top of** vanilla: every Harmony patch is observe-only, scoped to school lines, or fails open to vanilla behaviour, so it composes cleanly with other transit mods, TM:PE included (see [Compatibility](#compatibility)).

---

## Features

- **Students-only boarding** on lines marked as school lines: only students enrolled at that school ride it, in either direction.
- **Invisible to everyone else**: non-students never even consider a school line when planning a trip, so they do not crowd school stops. Works on both the vanilla pathfinder and TM:PE's (toggleable).
- **The school is the depot**: generated routes get their bus from the school itself. It spawns there and parks back there; no bus depot required (toggleable).
- **Free school service**: students ride free and school lines cost no transit maintenance (toggleable).
- **One-click route generation** per school: a school's catchment is split into several short **one-bus loops** (like a real district), not one giant loop.
- **City-wide generation**: route every school in one click (with a confirmation warning).
- **Auto-regeneration**: routes follow the students. When a school's coverage drifts as the city changes, its routes regenerate automatically (toggleable).
- **One bus per route**, deterministic (via the school-as-depot spawner, or Improved Public Transport's API when classic depot supply is enabled).
- **Modded-school-safe**: every size-dependent rule reads each school's *actual* capacity, so it works for custom school assets of any size.
- Read-only **route health and ridership** in the school panel.

---

## Quick start

### Make a bus line students-only (the basic behaviour)

1. Open any bus line's info panel.
2. Tick **School line**.

The mod finds the **nearest school automatically** and binds the line to it (that stop becomes the school stop). Only that school's students may board, riding between home and school; everyone else finds another route (and non-students stop planning trips with the line at all). Untick to make it a normal line again. This only changes *who* may board: it does not change the line's vehicles or bus count (the yellow one-bus-per-route styling is applied only to routes the mod generates, below).

### Auto-generate a school's routes

1. Subscribe to (or enable) this mod and **Harmony**, then load your city.
2. **Click the school building.** A panel opens on the side. No bus depot is needed: the school spawns and parks its own buses (toggleable; with the option off, school lines are supplied by your bus depots like any line).
3. Press **Generate Routes.** The mod reads the school's current students, lays out one or more short bus routes covering where they live, marks them as school lines, assigns one bus to each, and styles them as school buses.
4. As your city changes, the routes **regenerate themselves** to keep coverage up (toggle in the options).

To route **every school at once**: mod options, **City-wide routes**, **Generate routes for all schools**.

---

## How it works

### Students-only enforcement (three layers)

When a line is marked as a school line, a citizen is eligible only if:

```text
age in {Child, Teen}
and Citizen.Flags.Student
and Citizen.m_workBuilding == lineSchoolBuilding        -- enrolled at THIS school
and (
    CitizenInstance.m_targetBuilding == lineSchoolBuilding         -- riding to school
    or CitizenInstance.m_targetBuilding == Citizen.m_homeBuilding  -- riding back home
  )
```

Enforcement happens at three points, each scoped so other lines are untouched:

1. **Route planning** (see *School lines do not exist for non-students* below): non-students never put a school line into a trip plan in the first place.
2. **Proactive turn-away**: a stepped scan over waiting citizens makes ineligible riders waiting *for a school line* give up and re-route before a bus even arrives, so they cannot pile up (e.g. riders with pre-existing paths from an old save). The line a citizen intends to ride is read from their own path (the next stop of their current hop), so at a stop shared with a city line the other line's riders are never touched.
3. **Boarding veto**: a `void` prefix on `BusAI.LoadPassengers` records the school-line context, and a postfix on `HumanAI.TransportArriveAtSource` (the genuine vanilla per-citizen arrival gate) refuses, and turns away, an ineligible rider at the exact moment they try to board. Keying off vanilla's own line-and-hop-specific result makes this correct at shared stops too. Everyone else loads normally.

All of this is intrinsic to a school line; if you want a line anyone can use, just untick **School line**.

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

### One bus per route

A real school route is served by **one bus** (a district runs several routes, each with one bus, not several buses per route). With one bus, a long loop starves itself, which is why a big school gets several short routes. With school-as-depot (below, the default) the school supplies exactly one bus per route deterministically. With classic depot supply, the mod pins the count through **IPTE's** public `IptVehicleApi` by reflection (no hard dependency), falling back to a minimal per-line budget without IPTE.

### The school is the depot

Vanilla supplies line vehicles from depots: a line posts a transfer request and `DepotAI.StartTransfer` answers it (resolve the line's vehicle, `CalculateSpawnPosition`, `VehicleManager.CreateVehicle`, `VehicleAI.SetSource(depot)`, `VehicleAI.StartTransfer` with the line in the offer). For mod-generated school lines the mod replays exactly that sequence with the **school** as the source building, while a scoped prefix on `DepotAI.StartTransfer` makes city depots skip school-line requests (no double supply; the block lifts if the school is demolished, so depots take over rather than leaving the line busless). Buses spawn at the school, serve their loop, and return there. The spawner waits until the line's stop-to-stop paths are committed: a freshly generated line's `Complete` flag is set at build time, but its paths land a few hundred frames later, and a bus placed before that cannot path along its own line.

### Free school service

School transport is a school service, not paid transit (toggleable):

- **Fares**: the game's own representation of "free" is a zero per-line ticket price (`BusAI.GetTicketPrice` returns `TransportLine.m_ticketPrice` raw when the vehicle is on a line, and `HumanAI.EnterVehicle` charges income only when it is non-zero). The mod simply writes the field at generation, on manual flagging, and in a one-time sweep for lines from older saves; no patch involved.
- **Maintenance**: weekly upkeep is charged inside `TransportLine.SimulationStep` from two fields on the *shared* bus `TransportInfo`, so a prefix zeroes them only for the duration of a school line's own step and a postfix restores them. Line steps run serially on the simulation thread, so the swap can never leak onto a city bus line's charge.

### School lines do not exist for non-students

Boarding-time rejection alone still lets adults walk to a school stop, wait, get refused and re-path, sometimes oscillating onto the same line until they despawn. The proper fix is at **route planning**: a pedestrian path can only step onto a transit line through one pathfinder method (`PathFind.ProcessItemPublicTransport`, invoked per transit stop node), and a plain Harmony prefix there skips school lines unless the path being computed belongs to a student of that school.

- **Whose path is it?** The pathfinder does not know who it routes for, so a postfix on `CitizenAI.StartPathFind` (the funnel every resident and tourist trip goes through) records path unit to citizen in a flat lock-free array, and `PathManager.ReleasePath` clears it (unit ids are recycled; a stale entry must never classify someone else's path, e.g. a bus's own).
- **Per-school precision**: a student of school A does not see school B's lines either.
- **Fail-open everywhere**: unknown owner, unmarked line, or the feature toggled off all mean vanilla behaviour. The gate runs on the pathfind worker threads and reads only lock-free single-word state (a line-to-school mirror array kept in sync with the registry, the ownership array, and citizen-buffer fields).

**TM:PE support**: TM:PE replaces the game's pathfinder, but its `CustomPathFind` keeps the same method shape, so the mod applies the same prefix to it at level load, resolved entirely by reflection. Nothing of TM:PE is edited and no API is required. Two extra hooks make it complete: TM:PE's Parking AI creates citizen paths through its own manager (`ExtCitizenInstanceManager.StartPathFind`) rather than the vanilla method, so path ownership is recorded there too, and TM:PE's `CustomPathManager` hides `ReleasePath` with a `new` method, so its `CustomReleasePath` is hooked to keep the ownership map clean. Any shape mismatch in a future TM:PE version just logs a warning and falls back to boarding-time rejection.

### Route health and ridership

The building panel lists each route with read-only health, plus the school's **whole-school coverage** (the union of students covered by all its routes, measured against students who actually need a bus, i.e. excluding near-school walkers, so it isn't dragged down by them). Debug logging adds per-line and per-school usage reports (`served`, `turnedAway`, a warm-up-cleaned `steady` ridership rate, and `capture`, the ridership per covered student).

---

## Options

Content Manager, mod options:

- **School Buses:** enable mod; buses spawn from the school (no depot needed); hide school lines from non-students (route planning); school transport is free (no fare, no maintenance).
- **Route generation:** auto-tune toggle; cluster radius; min students / cluster; scale-min-by-capacity (plus the 1000-capacity reference); auto-regenerate toggle and coverage trigger; max pickup-loop per route; an optional capacity-scaled route trim (trigger plus catchment distance) for players who'd rather have fewer buses than full coverage; coverage warning threshold; **Restore recommended defaults**.
- **City-wide routes:** generate / delete all school routes.
- **Debug:** enable debug logging (traces actions, generation, boarding, the usage reports, and the pathfind-gate telemetry).

**Recommended defaults** (from the tuning below): cluster radius 400 m, capacity-scaled min (about 8 at 1000 capacity), pickup-loop 2000 m, route trim off, auto-regenerate at 30% coverage. Settings files from older versions are migrated once on load when a default changed for a measured reason (e.g. auto-tune is switched off in favour of the validated defaults; it produced long, barely used routes).

---

## Compatibility

Designed to compose: it never reimplements another mod's loop, every patch is scoped to school lines, and everything fails open to vanilla. The full patch surface:

| Method | Kind | Scope |
| --- | --- | --- |
| `BusAI.LoadPassengers` | void prefix + postfix | records school-line boarding context |
| `HumanAI.TransportArriveAtSource` | postfix | refuses ineligible boarders on school lines only |
| `HumanAI.SetCurrentVehicle` | observe-only postfix | ridership stats |
| `TransportManager.ReleaseLine` | cleanup postfix | unregister deleted school lines |
| `DepotAI.StartTransfer` | scoped prefix | depots skip school-line supply (school-as-depot) |
| `TransportLine.SimulationStep` | prefix + postfix pair | zero maintenance during a school line's own step |
| `PathFind.ProcessItemPublicTransport` | scoped prefix | the route-planning gate (school lines only) |
| `CitizenAI.StartPathFind` / `PathManager.ReleasePath` | postfixes | path-to-citizen bookkeeping for the gate |
| `CitizenManager.ReleaseCitizenInstance` | debug-only prefix | despawn telemetry when debug logging is on |

- **TM:PE:** fully supported. The route-planning gate is also applied to TM:PE's `CustomPathFind` at level load (runtime Harmony patch, resolved by reflection; TM:PE itself is never edited), including its Parking-AI path creation and custom path release. A future TM:PE with a different shape logs a warning and falls back gracefully.
- **Improved Public Transport (IPTE):** provides the classic one-bus-per-line control via its public `IptVehicleApi` (reflection); its per-line vehicle list overrides our default vehicle. School-as-depot buses correctly attribute **no depot cost** in IPTE's per-line costs (a school is not a `DepotAI`). No conflict.
- **Express Bus Services:** calls `TransportArriveAtSource`, so the eligibility filter still applies inside its loop.
- **Better Train Boarding:** boards via `SetCurrentVehicle`, bypassing `TransportArriveAtSource`; on a line it overrides, our boarding filter is inert (never a crash or double-boarding); the planning gate still keeps non-students off.
- **Impatient Commuters:** School Buses registers an exemption (by reflection) so a child waiting for their assigned school bus is exempt from impatience and won't wander off; no-op if it isn't installed.
- **UI Resolution:** the side panels dock screen-aware (they flip to the other side of the vanilla window and clamp on screen when the preferred side would clip).

For other mods, `SchoolBuses.Integration.SchoolBusBridge` exposes a stable reflection API (`ApiVersion`, `IsProtectedRider`, `IsSchoolStop`, `IsSchoolLine`, `IsSchoolOwnedLine`).

---

## Development journal: how the defaults were found

The interesting parameters (cluster radius, min-students, route length) were not guessed; they were tuned in-game against measured behaviour. Short version of the process:

1. **Line completion.** The first blocker was that generated loops never completed. Tracing `TransportLine.AddStop` in IL showed completion requires an append-mode stop within 2.5 m of the first stop. `CloseLoop` reproduces that; `LineFinalizer` commits paths while paused.
2. **One bus, short loops.** A single bus on a 20-stop loop fills early and starves far stops (students piled up about 19 deep). So a big school is split into several short one-bus routes: a capacitated vehicle-routing problem solved with the Sweep algorithm, the route length capped by the *inter-stop* loop (the trunk to school shouldn't force splits).
3. **An experiment harness.** Rather than guess, the mod can sweep parameters: it applies one setting to a fixed set of schools, runs, and logs per-school **capture rate** (steady boardings / covered students) and coverage. Running an identical clean save each round made the *same school across runs* a clean comparison.
4. **Findings.** With one bus per route, **ridership tracks coverage**, so the goal is to maximise coverage. Radius 400 m beat 300 and 500 on real ridership; min 8 (per 1000 capacity) is the peak; pickup-loop about 2000 m is the knee between too many buses and oversubscribed loops. A hard route cap *hurt* (it cut coverage, and so ridership), so route count is controlled organically by min and an optional distance-based trim instead.
5. **Per-size and drift handling.** Fixed `min 8` left small schools badly under-covered, so the min became capacity-scaled. Because students move over time, **auto-regeneration** keeps coverage up as the city evolves.
6. **The school becomes the depot.** Tracing the depot supply chain in IL (`DepotAI.StartTransfer` answering a line's transfer offer) showed the spawn sequence is reimplementable with the school as the source building, removing the single most common user problem ("why are my routes idle": no depot).
7. **Making the lines invisible.** Even with boarding rejection and proactive turn-away, adults kept planning trips onto school lines and could despawn when no alternative existed. The fix went into the route planner itself, and debugging it needed live telemetry counters inside the gate: they exposed, in order, a TM:PE path-creation funnel that bypassed the vanilla method, a TM:PE release method that hid the vanilla one (stale ownership), and finally the real bug: the transit line id must be read from the stop *node* the pathfinder expands, not from the transit lane's segment. With those fixed, adults are denied at planning time and the stop churn disappears.

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
