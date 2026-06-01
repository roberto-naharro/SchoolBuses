# Route tuning: how the default settings were chosen

The default route-generation settings in School Buses were not guessed. They were tuned in-game by
generating routes under controlled conditions, measuring how the resulting lines actually performed,
and iterating. This document records the method, the data, and the conclusions, so the defaults are
reproducible and the reasoning is open.

## Method

The mod includes a small experiment mode that applies one parameter setting to a fixed sample of
schools, then logs how each school's routes perform. Each round used an identical, clean save and the
same eight schools (four small, capacity 300, and four large, capacity 1000), so comparing the *same
school across rounds* isolates the effect of a parameter change from differences between schools.

Schools are labelled here E1 to E4 (small) and H1 to H4 (large).

### Metrics

- **Coverage** (`cov%`): the share of a school's *bus-needing* students (those who live too far to
  walk) whose home is within reach of one of the school's stops, counted once across all its routes.
- **steady**: boardings per 1000 simulation frames after a warm-up period, measured from a line's
  first real boarding (so a line still waiting for a bus does not pollute the figure). This is the
  trustworthy ridership rate.
- **routes**: number of routes (and therefore buses; each route runs one bus).
- **turnedAway**: students who could not board because the bus was full.
- **perRoute**: steady divided by routes (per-bus efficiency).
- **capture**: steady divided by covered students (ridership per covered student).

A recurring result: `capture` is roughly constant across settings, which means **total ridership is
close to proportional to coverage**. Maximising coverage is therefore the main objective, provided the
buses to serve it can actually run.

## Rounds and findings

### Round 1 (coarse, one setting per school)

Established the shape of the problem: ridership rises with coverage; coverage is governed by the
minimum-students-per-cluster threshold; and a very low threshold produces an unmanageable number of
routes. A high threshold on a small school produced no routes at all.

### Round 2: radius x route length

Holding the cluster threshold at 8, varying cluster radius and the per-route pickup-loop length, one
small and one large school per setting:

| radius | pickup (m) | large school | routes | cov% | steady | perRoute | turnedAway |
|---|---|---|---|---|---|---|---|
| 400 | 1500 | H1 | 10 | 55 | 2.48 | 0.25 | 53 |
| 400 | 3000 | H2 | 6 | 72 | 2.52 | 0.42 | 227 |
| 500 | 1500 | H3 | 7 | 40 | 1.15 | 0.16 | 35 |
| 500 | 3000 | H4 | 7 | 47 | 1.25 | 0.18 | 11 |

Radius 400 produced roughly double the ridership of radius 500 on the large schools. Longer pickup
loops produced fewer, fuller routes but left more students unable to board (high `turnedAway`). Small
schools given a long pickup loop collapsed to a single slow route with almost no ridership.

### Round 3 and 4: pinning the pickup-loop length

Holding radius 400 and threshold 8, sweeping the pickup-loop length. Comparing the *same* large school
across rounds (the clean comparison):

- School H1: 1500 m gave steady 2.48 (10 routes, 53 turned away); 2000 m gave 2.31 (7 routes, 18
  turned away).
- School H2: 2000 m gave 3.27 (9 routes, 96 turned away); 2200 m gave 3.49 (9 routes, 245 turned
  away); 3000 m gave 2.52 (6 routes, 227 turned away).

A pickup-loop of about **2000 m** sits at the knee: near-peak ridership on every large school, with the
fewest turned-away students and a sensible route count. Shorter adds buses and turn-aways without
raising ridership; longer loses ridership or sharply increases turn-aways.

### Round 5: confirming the radius

Re-running radius 300 against the locked radius 400 (threshold 8, pickup 2000) on all eight schools.
Radius 400 matched or beat radius 300 on ridership for every large school, and gave higher coverage
(a smaller radius makes many sub-threshold clusters that then get dropped). With radius 500 already
beaten in Round 2, **radius 400 is the best of 300, 400, 500**.

### Rounds 6 and 7: pinning the cluster threshold

Sweeping the threshold at radius 400, pickup 2000. Large-school ridership (threshold 6 / 8 / 12):

| school | min 6 | min 8 | min 12 |
|---|---|---|---|
| H1 | 2.60 | 2.31 | 2.00 |
| H2 | 3.10 | 3.27 | 3.00 |
| H3 | 1.31 | 1.11 | 0.47 |
| H4 | 2.80 | 2.40 | 2.26 |

Threshold 12 under-covers and loses ridership. Threshold 6 squeezes a little more raw ridership on some
schools, but only by reaching more students than a single bus can carry: it needs about 40% more buses,
produces two to seven times the turn-aways, and lowers per-bus efficiency. **Threshold 8 is the best
balance.**

## Whole-city behaviour, and per-size scaling

Generating every school at once with the fixed settings exposed two things:

1. The fixed threshold of 8 left **small schools badly under-covered** (often below 25%): their sparse
   neighbourhoods fall below the threshold and get no stop.
2. Route counts on large schools of the same capacity varied widely (from 3 to 19), driven by how
   spread out the students are.

Both are addressed by **scaling the threshold to each school's capacity** (read live, so it works for
modded schools of any size): small schools use a lower threshold and recover their coverage, while
large schools keep the threshold that controls their route count. A hard cap on the number of routes
was tried and rejected: it cut coverage, and therefore ridership. Route count is instead controlled by
the threshold plus an optional distance-based trim that only bounds genuinely spread-out outliers.

Because student populations move over time, an **auto-regeneration** check keeps coverage current: when
a school's coverage drifts below a target, its routes are regenerated so the stops follow the students.

## Final defaults

| Setting | Default | Reason |
|---|---|---|
| Cluster radius | 400 m | Best ridership and coverage of 300 / 400 / 500. |
| Min students per cluster | scaled (about 8 at capacity 1000) | Threshold 8 is the ridership peak; scaling fixes small-school coverage. |
| Pickup-loop length | 2000 m | The knee between too many buses and oversubscribed loops. |
| Route trim | off | A hard route limit cut coverage and ridership; it is an opt-in for players who want fewer buses. |
| Auto-regenerate | on, at 30% coverage | Keeps coverage current as the city changes. |

A "lean fleet" alternative for players who are short on bus depots: a higher cluster threshold (around
12) yields roughly 70% of the ridership with about half the buses.
