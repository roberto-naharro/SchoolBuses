# School Buses

A Cities: Skylines mod that makes school bus lines work as real school buses: only students travelling to or from their assigned school may board, with one-click route generation from the school building.

## How it works

Vanilla CS1 school buses are a cosmetic skin with identical mechanics to a regular bus. This mod adds two layers on top:

**Phase 1 — students-only boarding filter.** When a bus line is marked as a school line, `BusAI.LoadPassengers` is patched to enforce a per-citizen eligibility check. A citizen boards only if:

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

**Phase 2 — one-click route generation.** Clicking a school building opens a panel showing clustered home positions of enrolled students. Pressing Generate Route:

1. Reads the school's student roster from `Building.m_citizenUnits` (units with `CitizenUnit.Flags.Student`), collects each student's `m_homeBuilding` position.
2. Clusters those positions with a greedy radius heuristic; caps at ~10 stops.
3. Snaps each cluster centroid to the nearest road node.
4. Orders stops with nearest-neighbour seed + 2-opt to avoid crossing/go-around routes.
5. Calls `TransportManager.CreateLine` + `TransportLine.AddStop` to build the line, with the school as terminal stop.
6. Auto-marks the line as school-only and stores `schoolBuildingID` + `schoolStopNodeId`.
7. Depots are auto-assigned by the game; no manual depot selection needed.

**Phase 2b — route health tracking.** Periodically re-scans the roster and measures what fraction of students live more than R metres from every stop. When coverage drops below a threshold the building panel shows a warning with a one-click Regenerate action.

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
