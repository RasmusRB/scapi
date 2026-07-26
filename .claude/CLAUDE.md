# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

StellaCareApi is a .NET 10 ASP.NET Core Web API implementing the "Intelligent Tracking Optimizer" case: given GPS trackers for dementia care, recommend which tracking *mode* each device should run in, balancing safety (never lose a person mid-search) against battery life. It is a case/assignment project — there is no database or live device feed; a simulated dataset (`Docs/case_backend_dataset_v3.json`, 14 days of history + current state for 3 devices) is loaded into memory at startup.

The case brief (`Docs/Case_IntelligentTracking_backend_kandidat.docx`) and the dataset are in Danish; code and comments are in English.

## Commands

```bash
dotnet build                 # build
dotnet run                   # run API on http://localhost:5153 (Development env by default)
dotnet run --DatasetPath=/path/to/dataset.json   # override which dataset is loaded
dotnet test Tests            # run the xUnit tests (Tests/StellaCareApi.Tests.csproj)
```

Running (not just building) needs the ASP.NET Core 10 shared runtime — `Microsoft.AspNetCore.App` (Arch: the `aspnet-runtime` package), separate from the base .NET runtime.

The `Tests/` project (xUnit) lives inside the web app's folder, so the app `.csproj` excludes `Tests/**` from its compile glob. `StellaCareApi.http` contains sample requests. OpenAPI is mapped at `/openapi/v1.json` in Development only, with a Swagger UI at `/swagger`.

## Architecture

The request path is thin — controllers delegate to two singletons registered in `Program.cs`:

- **`IDeviceStore` / `DeviceStore`** — the state layer. Parses the JSON dataset once (raw `System.Text.Json`, see `ParseState`/`ParseReport`), holds each device's `DeviceState` plus its sorted `PositionReport` history, and applies mutations (config changes, activate/deactivate search) under a lock. In production this would be a DB + a live report stream; here it's in-memory.
- **`ITrackingAlgorithm` / `TrackingAlgorithm`** — the decision layer. Pure: takes `(DeviceState, history, now)` and returns a `ModeRecommendation`. No I/O, no state. This is where the actual case logic lives.

`DevicesController` maps HTTP to store + algorithm and shapes DTOs (`ModeRecommendation`, `DeviationDto`). Endpoints: `GET /devices/{id}/recommended-mode`, `POST /devices/{id}/config`, `POST /devices/{id}/activate-search`, `POST /devices/{id}/deactivate-search`, `GET /devices/deviations`.

### The algorithm (the core of the case)

`TrackingAlgorithm.Recommend` applies a strict **safety-first, battery-second** priority order — read the class doc-comment before changing it:

1. Active, valid search → **Active Tracking** (15s GPS), overriding the battery target.
2. Search past the auto-timeout → flagged as a **deviation** (`search_timeout`), drop to a normal mode.
3. Stuck in Active Tracking with no search → **deviation** (`stuck_active_tracking`) — the most battery-expensive failure; the dataset's `sc-dev-stuck-active` device is exactly this case.
4. Home wifi still visible → **WiFi Saver** (device-managed, GPS off).
5. Otherwise → `ChooseNormalMode`: **WM8** (fixed interval) vs **WM9** (step-based + fallback), plus car detection (high speed + flat step counter → WM8, since WM9 can't track a vehicle).

`TargetProfile` maps the user's desired battery hours (allowed values: 12/24/36/48/72, enforced in the controller) to concrete mode parameters. `EstimateDrainPctPerHour` is a battery model used to fill in `ExpectedDrainPctPerHour` / `ExpectedBatteryHoursRemaining`.

**Important:** the numeric thresholds (`CarSpeedKmh`, `SearchAutoTimeoutMinutes`, `RecentWindowMinutes`, the `TargetProfile` table, the drain constants) are deliberate gaps in the brief — they are our own documented assumptions, not values handed to us. Treat them as tunable and keep the reasoning in comments when changing them.

### "Now" handling

The dataset's `referenceNow` predates the active-search events, so wall-clock time is not used. `DeviceStore.EffectiveNow(id)` derives "now" as the latest timestamp known for a device (state timestamps, mode start, search start, last history sample). Always pass this into the algorithm rather than `DateTimeOffset.UtcNow`.

## Conventions

- Models are immutable `record`s except `DeviceState`, which is a mutable class (the store mutates it in place under `_lock`).
- `TrackingMode` enum values 8/9 correspond to dataset modes `"8"`/`"9"`; parsing tolerates both numeric and string encodings (`ParseMode`).
- Nullable reference types and implicit usings are enabled.
