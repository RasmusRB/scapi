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

### A note on the numbers, and on "now"

The brief left the concrete thresholds unspecified — car speed, search timeout, the target-battery
lookup table, the drain model. Those are **our documented assumptions, not values we were handed.**
They're isolated as named constants with the reasoning in comments, so they're easy to challenge and tune.

One quirk worth knowing: the dataset's `referenceNow` actually predates the search events in it, so
wall-clock time is misleading here. Instead, "now" for a device is derived as the latest timestamp we
know about it (via `DeviceStore.EffectiveNow`). The algorithm is always given that value rather than
`DateTimeOffset.UtcNow`.

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
