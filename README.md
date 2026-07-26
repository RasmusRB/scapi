# StellaCareApi — Intelligent Tracking Optimizer

.NET 10 Web API for the *Intelligent Tracking* case: given a GPS tracker worn by someone living with
dementia, decide which **tracking mode** it should run in. Safety and battery pull in opposite directions —
15-second GPS keeps the person findable and drains the battery in hours; a step-triggered mode lasts days but
can lose someone mid-search. The simulated dataset (`Docs/case_backend_dataset_v3.json`, 14 days of history +
current state for 3 devices) loads into memory at startup; there is no database.

```bash
dotnet build && dotnet run   # http://localhost:5153, Swagger UI at /swagger
dotnet test Tests            # --DatasetPath=/path/to.json points at another dataset
```

Running needs the ASP.NET Core 10 *shared runtime*, not just the base .NET runtime (Arch: `aspnet-runtime`).
`StellaCareApi.http` has ready-made requests for all five endpoints — `recommended-mode`, `config`,
`activate-search`, `deactivate-search`, `deviations`.

## Architecture

The HTTP layer is thin — `DevicesController` translates requests into calls on two singletons.

- **`DeviceStore` (state)** — parses the dataset once, holds each device's `DeviceState` plus its sorted
  history, and applies mutations under a lock. `DeviceState` is mutable and never leaves the class: callers get
  an immutable `DeviceSnapshot` built inside the lock (see *Race conditions*). Production would swap this seam
  for a database plus a live report stream.
- **`TrackingAlgorithm` (decisions)** — `(state, history, now) → ModeRecommendation`. Pure: no I/O, no shared
  state. **This is where the case lives**, and keeping it pure is why every branch is testable without HTTP.

"Now" is `DeviceStore.EffectiveNow` — the latest timestamp known for a device, never `UtcNow`. The dataset's
`referenceNow` predates its own search events, so wall-clock time is actively misleading here.

## The algorithm

**Safety first, battery second**, in priority order:

1. **Active search** → Active Tracking (15s), overriding `targetBatteryHours` outright.
2. **Search past the 120-min auto-timeout** → deviation `search_timeout`, drop to a normal mode.
3. **Active Tracking, no search, > 30 min** → deviation `stuck_active_tracking`, the most expensive failure
   there is. Under 30 min it's a normal post-search wind-down, not a fault.
4. **Home wifi visible** → WiFi Saver (device-managed, GPS off).
5. **Otherwise** → WM8 (fixed interval) vs WM9 (step-based + fallback), plus car detection: ≥ 15 km/h with a
   flat step counter means a vehicle, so WM8 — WM9's step logic can't follow a car.

The dataset's `sc-dev-stuck-active` hits **2, not 3** — its search was never closed, so the root cause is a
search that outlived its timeout rather than a device that never got its mode command back. Both drop it out of
Active Tracking; the deviation kind records which. Branch 3 is covered by tests.

**Thrashing vs. instant transition** — the brief asks for both, so we split on *what drives the change*.
Safety-critical transitions (into/out of Active Tracking, car detection, the WiFi-Saver wake-up) are always
immediate; only the battery-comfort WM8↔WM9 flip is rate-limited, by a 15-minute cooldown. That falls out
structurally: the cooldown only applies when the device is already in a steady working mode, so wake-ups *out
of* Active Tracking or WiFi Saver can't be suppressed. Every switch also writes the new mode's parameters and
clears the old mode's, so a transition never lingers on stale ones.

## The holes

The thresholds are **our assumptions, not values we were handed** — named constants with the reasoning in
comments, so they're easy to challenge and tune.

- **Battery model** — `drain = idle + cost-per-fix × fixes-per-hour`, with a *ceiling* on the GPS term,
  calibrated against the dataset (its fractional battery values make drain measurable: Active Tracking ≈ 13.9 %/h,
  WM9 ≈ 1.6–5.0, WiFi Saver ≈ 1.2). The ceiling is the part worth arguing about: a naive linear model charges
  Active Tracking for 240 cold fixes an hour and predicts ~60 %/h — four times reality. Below roughly 70 s between
  fixes the receiver never powers down, so there is no acquisition cost per fix and drain saturates.
- **Target hours → parameters** — *derived from that model, not hard-coded*: for each target we search the allowed
  values and take the most frequent reporting that still survives it from full. Deriving it is what exposed the
  original hand-written table as wrong — it claimed a 5-min WM8 interval "meets the 24h target" while the model
  priced that same interval at 12h. Two things fell out: **72h is unreachable with GPS on** (30-min WM8 tops out
  near 67h), which the rationale now states instead of pretending otherwise; and the model prefers a **low** step
  threshold with a **long** fallback, the opposite of the first guess — step fixes fire only when the resident
  actually moves, while the fallback timer burns battery while they sit still.
- **Car detection** — ≥ 15 km/h with a flat step counter. Deliberately low: err toward *not* losing someone in a car.
- **Battery vs. safety** — safety wins; an active search overrides the battery target outright.
- **Race conditions** — the brief's example is a deactivate-search landing mid-decision. Locking mutations alone
  isn't enough: the algorithm reads state field by field, so handed the live object it can see `ActiveSearch` set
  but `CurrentMode` not yet updated, and decide against a state that never existed. So reads are locked too, and
  what leaves the store is an immutable snapshot (state copy + history + the matching "now") built in one critical
  section; the mutating endpoints return the snapshot from their own. Every decision therefore has a definite
  serialization point — strictly before, or strictly after, the competing event.
- **Persistence, real-time vs. batch** — in-memory is fine here; production is a DB plus a report stream. Recompute
  per request: the algorithm is pure and cheap, so batching would only buy staleness.
- **Time zones** — devices carry an IANA zone, plumbed through for the time-of-day work below.

**Known limitation:** the algorithm reasons over a ~30-minute recent window, not the full 14 days. Per-resident,
per-hour-of-day baselines in local time are the natural next step — they would replace the assumed step rate in
the battery model and catch the brief's "WM8 at a 2-min interval while the resident sleeps".

## Tests

xUnit in `Tests/`, aimed at the algorithm's decision branches and the dataset parsing. The algorithm is pure, so
each test builds just enough state + history to isolate one branch — no HTTP, no fixtures. The four the brief
names are covered explicitly: stuck-in-Active-Tracking detection (plus the wind-down grace period that must *not*
trip it), car detection, the WiFi-Saver → normal-mode wake-up, and mode-transition timing (no stale parameters
across a switch). Also: the anti-thrashing cooldown, that every derived profile either meets its battery target or
says out loud that it can't, that the drain constants stay near observed values, and snapshot isolation under a
concurrent activate/deactivate loop.

The AI-usage note the brief asks for is a separate deliverable — see [`AI-USAGE.md`](AI-USAGE.md).
