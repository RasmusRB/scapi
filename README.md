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

- **Battery model** — a simple `%/hour` drain estimate (`EstimateDrainPctPerHour`) where cost scales with
  fix frequency: Active Tracking pinned high, WiFi Saver near zero, WM8/WM9 driven by interval/fallback.
  Enough to populate "expected battery hours"; not claimed to be physically exact.
- **Car detection** — speed ≥ 15 km/h with a flat step counter over the recent window. Deliberately low so
  we err toward *not losing* someone in a vehicle.
- **Stuck-in-Active-Tracking threshold** — 30 min of Active Tracking with no active search. Below that it's
  treated as a normal post-search wind-down, not a fault.
- **Thrashing vs. transition** — the cooldown/hysteresis split described above.
- **Battery vs. safety** — safety always wins: an active search overrides `targetBatteryHours` outright.
- **System holes** — *Persistence:* in-memory is fine for the case; production would be a DB plus a live
  report stream (the `DeviceStore` seam is already shaped for that). *Real-time vs. batch:* recompute on
  demand per request — the algorithm is cheap and pure, so a nightly batch would only add staleness.
  *Race conditions:* state mutations run under a single lock in `DeviceStore`; a deactivate-search and a
  recommendation can't interleave mid-decision. *Time zones:* devices carry an IANA zone (`Europe/Copenhagen`)
  for future time-of-day patterns — see the limitation below.

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
detection). `DeviceStoreTests` cover parsing the mixed numeric/string encodings in the dataset.

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
