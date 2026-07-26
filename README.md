# StellaCareApi — Intelligent Tracking Optimizer

A small .NET 10 ASP.NET Core Web API built for the *Intelligent Tracking* case. The domain is
GPS trackers worn by people living with dementia: the device has to stay findable if the person
wanders, but its battery also has to last. Those two goals pull in opposite directions — GPS every
15 seconds keeps you safe and drains the battery in hours; a slow, step-triggered mode lasts for days
but can lose someone mid-search. The job of this service is to look at what a device is currently
doing and recommend the *tracking mode* that strikes the right balance.

This is a case/assignment project, so there's no database and no real devices. A simulated dataset
(`Docs/case_backend_dataset_v3.json` — 14 days of history plus the current state for 3 devices) is
loaded into memory at startup and everything runs against that.

## Running it

```bash
dotnet build
dotnet run                                       # http://localhost:5153 (Development by default)
dotnet run --DatasetPath=/path/to/dataset.json   # point at a different dataset
dotnet test Tests                                # run the unit tests
```

`StellaCareApi.http` has ready-made sample requests. In Development the OpenAPI document is served at
`/openapi/v1.json` and an interactive Swagger UI at `/swagger`.

Running the app needs the ASP.NET Core 10 runtime (not just the base .NET runtime) — on Arch that's the
`aspnet-runtime` package.

## Endpoints

| Method & path | What it does |
|---|---|
| `GET  /devices/{id}/recommended-mode` | The core call: recommended mode, its parameters, the expected battery impact, and a plain-language rationale. |
| `POST /devices/{id}/config` | Set the desired battery life (`targetBatteryHours`, one of 12/24/36/48/72). |
| `POST /devices/{id}/activate-search` | Start a search — forces the device into Active Tracking. |
| `POST /devices/{id}/deactivate-search` | End a search and let the algorithm fall back to a normal mode. |
| `GET  /devices/deviations` | Devices currently in an abnormal state (e.g. stuck in Active Tracking, burning battery). |

## How it's put together

The HTTP layer is deliberately thin. `DevicesController` does little more than translate requests into
calls against two singletons and shape the response DTOs. Those two singletons are the whole system:

- **`DeviceStore` (state)** — parses the JSON dataset once, keeps each device's current `DeviceState`
  and its sorted position-report history, and applies mutations (config changes, search on/off) under
  a lock. In a real deployment this would be a database plus a live stream of device reports; here it's
  just in-memory. I kept the two responsibilities apart on purpose so the interesting part stays pure.
  `DeviceState` is mutable and never leaves the store: readers get a detached `DeviceSnapshot` built
  inside the lock (see *Race conditions* below).

- **`TrackingAlgorithm` (decisions)** — takes `(state, history, now)` and returns a recommendation.
  No I/O, no shared state, so it's easy to reason about and would be easy to test. **This is where the
  case actually lives.**

### The decision logic

The algorithm follows one rule above all others: **safety first, battery second.** In priority order:

1. **Active search** → Active Tracking (15s GPS), even if it blows the battery target. Losing a person
   is never worth saving battery.
2. **Search ran past the auto-timeout** → flagged as a deviation and dropped back to a normal mode.
3. **Stuck in Active Tracking with no search** → flagged as a deviation. This is the worst failure mode
   because it silently drains the battery; the dataset's `sc-dev-stuck-active` device is exactly this.
4. **Home wifi visible** → WiFi Saver (GPS off, the person is safe at home).
5. **Otherwise** → pick a normal mode: WM8 (fixed interval) vs WM9 (step-based with a fallback), with a
   bit of car detection (high speed + a flat step counter means they're in a vehicle, so WM8, because
   WM9's step logic can't follow a car).

**Thrashing vs. fast transition.** The brief asks for two things that pull against each other: don't flip
modes too often, but when you *do* switch, make it instant. We split the difference by *what* is driving
the change. Safety-critical transitions — into/out of Active Tracking, car detection, and the WiFi-Saver
wake-up when the person leaves home — are always immediate. Only the pure battery-comfort WM8↔WM9 flip is
rate-limited, by a 15-minute cooldown (hysteresis): if the sole reason to switch is that the person went
still or started walking, we hold the current mode until the cooldown passes. Transitions *out of* Active
Tracking or WiFi Saver never hit the cooldown (the device isn't in a steady working mode there), so the
critical wake-ups stay instant. Switching also always writes the new mode's parameters and clears the old
mode's (e.g. WM9's fallback), so a transition never lingers on stale parameters.

### A note on the numbers, and on "now"

The brief left the concrete thresholds unspecified — car speed, search timeout, the target-battery
lookup table, the drain model. Those are **our documented assumptions, not values we were handed.**
They're isolated as named constants with the reasoning in comments, so they're easy to challenge and tune.

One quirk worth knowing: the dataset's `referenceNow` actually predates the search events in it, so
wall-clock time is misleading here. Instead, "now" for a device is derived as the latest timestamp we
know about it (via `DeviceStore.EffectiveNow`). The algorithm is always given that value rather than
`DateTimeOffset.UtcNow`.

## Handling the "holes"

The brief deliberately leaves the hard numbers and some system questions open. Our stances:

- **Battery model** — `drain = idle + (cost of a GPS fix) × (fixes per hour)`, with a *ceiling* on the GPS
  term. The constants are calibrated against the dataset, whose fractional battery values make drain
  measurable: `active_tracking ≈ 13.9 %/h`, `WM9 ≈ 1.6–5.0 %/h`, `wifi_saver ≈ 1.2 %/h`. The ceiling is
  the part worth arguing about: a naive linear per-fix model prices Active Tracking at 240 cold fixes an
  hour and predicts ~60 %/h — four times reality. Below about a 70-second interval the receiver never
  powers down between fixes, so there's no cold-acquisition cost to pay and the drain saturates.
- **Target battery hours → parameters** — *derived from the battery model, not hard-coded.* For a given
  target we search the device's allowed parameter values and take the most frequent reporting that still
  survives the target from a full charge. This started as a hand-written lookup table, and deriving it is
  what exposed that the table was wrong: it claimed a 5-minute WM8 interval "meets the 24h target" while
  the model priced that same interval at 12 hours. Two consequences worth noting:
  - **72h is unreachable with GPS on** — even a 30-min WM8 interval only gets to ~67h. The service now
    says so in the rationale rather than silently claiming the target is met; closing the gap requires
    WiFi Saver time at home.
  - The model prefers a **low** WM9 step threshold with a **long** fallback — the opposite of the table's
    original guess. Step-triggered fixes only fire when the resident actually moves, which is exactly when
    you want them; the fallback timer is what costs battery while they sit still.
- **Car detection** — speed ≥ 15 km/h with a flat step counter over the recent window. Deliberately low so
  we err toward *not losing* someone in a vehicle.
- **Stuck-in-Active-Tracking threshold** — 30 min of Active Tracking with no active search. Below that it's
  treated as a normal post-search wind-down, not a fault.
- **Thrashing vs. transition** — the cooldown/hysteresis split described above.
- **Battery vs. safety** — safety always wins: an active search overrides `targetBatteryHours` outright.
- **System holes** — *Persistence:* in-memory is fine for the case; production would be a DB plus a live
  report stream (the `DeviceStore` seam is already shaped for that). *Real-time vs. batch:* recompute on
  demand per request — the algorithm is cheap and pure, so a nightly batch would only add staleness.
  *Time zones:* devices carry an IANA zone (`Europe/Copenhagen`) for future time-of-day patterns — see the
  limitation below.
- **Race conditions** — the brief's example is a deactivate-search landing while the algorithm has decided
  to stay in Active Tracking. Taking the lock around *mutations* alone isn't enough, because the algorithm
  reads state field by field: handed the live `DeviceState`, it can observe `ActiveSearch` already set but
  `CurrentMode` not yet updated, and decide against a state that never existed. So reads are locked too,
  and what leaves the store is an immutable `DeviceSnapshot` (state copy + history + the matching "now")
  built in one critical section. The mutating endpoints return the snapshot from *their own* critical
  section, so the recommendation a caller gets back is the one for the state its call actually produced.
  Every decision therefore has a definite serialization point: strictly before, or strictly after, the
  competing event — never halfway through it.

**Known limitation:** the algorithm currently reasons over a ~30-minute recent window, not the full 14-day
history. Per-resident time-of-day / weekday baselines (interpreted in the device's local time zone) are the
natural next step and the reason the `Timezone` field is already plumbed through.

## Test strategy

Tests live in `Tests/` (xUnit) and target the two things worth protecting: the algorithm's decision branches
and the dataset parsing. The algorithm is a pure function, so each test builds just enough `DeviceState` +
history to isolate one branch — no HTTP, no fixtures. The branches the brief calls out are covered explicitly:
stuck-in-Active-Tracking detection (and the wind-down grace period that must *not* trip it), search timeout,
car detection, the WiFi-Saver → normal-mode wake-up, mode-transition timing (no stale parameters carried
across a switch), and the anti-thrashing cooldown (held within the window, flips after it, bypassed by car
detection). Two more guard the fixes above: the derived parameter table must either meet each battery
target or say out loud that it can't, and the drain constants must stay within range of what the dataset
actually shows. `DeviceStoreTests` cover parsing the mixed numeric/string encodings, plus snapshot
isolation — including a concurrent activate/deactivate loop asserting a reader never sees an activated
search without Active Tracking already applied.

## AI usage note

- **Tools:** Claude Code (Anthropic) as the primary pair-programmer for scaffolding, the algorithm, and tests.
- **How much:** heavy on boilerplate (DTOs, parsing, DI wiring, test skeletons) and first-draft algorithm
  structure; every threshold and the safety-first priority order were decided and reviewed by hand.
- **Where it got it wrong (one example):** an early pass built out the priority order and battery model but
  silently skipped two things the brief actually *requires* — the anti-thrashing/hysteresis logic and two of
  the four named tests (WiFi-Saver→normal transition, mode-transition timing). It only surfaced on a
  deliberate re-read of the brief against the code; the AI had treated "looks complete" as "is complete."

## Layout

```
Program.cs            DI wiring, dataset path, OpenAPI
Controllers/          DevicesController — HTTP → store + algorithm
Managers/             DeviceStore (state), TrackingAlgorithm (decisions)
Interfaces/           IDeviceStore, ITrackingAlgorithm
Models/               DeviceState, PositionReport, ModeRecommendation, requests, ...
Tests/                xUnit tests — algorithm branches + dataset parsing
Docs/                 case brief (Danish) + the simulated dataset
```

Models are immutable `record`s, except `DeviceState`, which is a mutable class the store updates in
place under its lock. Nullable reference types are on. Code and comments are in English; the case brief
and dataset are in Danish.
