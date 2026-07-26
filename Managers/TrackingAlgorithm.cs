using StellaCareApi.Interfaces;
using StellaCareApi.Models;

namespace StellaCareApi.Managers;

/// <summary>
/// Decides which tracking mode a device should run in.
///
/// Priority order (safety first, battery second):
///   1. Active, valid search           -> Active Tracking (overrides the battery target)
///   2. Search past auto-timeout        -> deviation, drop back to a normal mode
///   3. Stuck in Active Tracking        -> deviation past a grace period, drop back to a normal mode
///   4. Home wifi still visible         -> WiFi Saver (device-managed, GPS off)
///   5. Otherwise                       -> WM8/WM9 based on movement + battery target
///
/// Safety-critical transitions (into/out of Active Tracking, car detection, a WiFi-Saver
/// wake-up) are always immediate. Only the battery-comfort WM8&lt;-&gt;WM9 flip is rate-limited by
/// a cooldown, which is how we reconcile the brief's two opposing demands: avoid thrashing,
/// yet transition instantly when it matters.
///
/// The numeric thresholds below are the deliberate "holes" from the brief — our own,
/// documented assumptions (see README), not values handed to us.
/// </summary>
public class TrackingAlgorithm : ITrackingAlgorithm
{
    private const double CarSpeedKmh = 15.0;          // >= this with a flat step count => likely a car
    private const int SearchAutoTimeoutMinutes = 120; // a search older than this should have closed
    private const int RecentWindowMinutes = 30;       // history window for car / movement detection
    private const int StuckActiveTrackingMinutes = 30; // grace period before "in Active Tracking, no search" counts as stuck
    private const int ModeChangeCooldownMinutes = 15;  // min dwell before a battery-only WM8<->WM9 flip (anti-thrashing)

    public ModeRecommendation Recommend(DeviceState s, IReadOnlyList<PositionReport> history, DateTimeOffset now)
    {
        var recent = history.Where(r => r.Timestamp >= now.AddMinutes(-RecentWindowMinutes)).ToList();

        // 1 & 2. Active search.
        if (s.ActiveSearch is { Status: "activated" } search)
        {
            var age = now - search.StartedAt;
            if (age <= TimeSpan.FromMinutes(SearchAutoTimeoutMinutes))
                return Build(s, TrackingMode.ActiveTracking, 15, null, null, false, null,
                    $"Active search in progress ({age.TotalMinutes:F0} min). Active Tracking overrides the {s.TargetBatteryHours}h battery target.");

            var afterTimeout = ChooseNormalMode(s, recent, now);
            return afterTimeout with
            {
                IsDeviation = true,
                DeviationKind = "search_timeout",
                Rationale = $"Search active {age.TotalHours:F1}h (> {SearchAutoTimeoutMinutes} min auto-timeout). " +
                            $"Close it and drop to {afterTimeout.RecommendedMode}. {afterTimeout.Rationale}"
            };
        }

        // 3. In Active Tracking with no active search. A short spell is just a normal
        //    transition (e.g. a search was just deactivated and the device is waiting for
        //    its next mode command). Past the grace period it's a genuine failure — the
        //    device is stuck burning battery for no reason, the most expensive one there is.
        if (s.CurrentMode == TrackingMode.ActiveTracking)
        {
            var stuckFor = now - s.ModeStartedAt;
            var normal = ChooseNormalMode(s, recent, now);

            if (stuckFor <= TimeSpan.FromMinutes(StuckActiveTrackingMinutes))
                return normal with
                {
                    Rationale = $"Active Tracking with no active search ({stuckFor.TotalMinutes:F0} min) — " +
                                $"normal wind-down after a search. Switch to {normal.RecommendedMode}. {normal.Rationale}"
                };

            return normal with
            {
                IsDeviation = true,
                DeviationKind = "stuck_active_tracking",
                Rationale = $"Stuck in Active Tracking {stuckFor.TotalHours:F1}h with no active search — battery-critical. " +
                            $"Corrective: switch to {normal.RecommendedMode}. {normal.Rationale}"
            };
        }

        // 4. WiFi Saver still valid (device-managed).
        if (s.InWifiSaver && s.WifiVisibleNow.Count > 0)
            return Build(s, TrackingMode.WifiSaver, null, null, null, false, null,
                "Home wifi still visible; device-managed WiFi Saver stays active (GPS off). " +
                "Algorithm picks WM8/WM9 immediately once wifi drops.");

        // 5. Normal selection (also covers wifi-saver-just-dropped and the car case).
        return ChooseNormalMode(s, recent, now);
    }

    /// <summary>Pick WM8 vs WM9 and parameters from movement context + battery target.</summary>
    private ModeRecommendation ChooseNormalMode(DeviceState s, IReadOnlyList<PositionReport> recent, DateTimeOffset now)
    {
        var profile = TargetProfile(s.TargetBatteryHours);

        // Car detection: recent high speed with a flat step counter. WM9 can't track a vehicle,
        // so this is safety-critical and bypasses the anti-thrashing cooldown below.
        var highSpeed = recent.Any(r => r.SpeedKmh >= CarSpeedKmh);
        if (highSpeed && StepsFlat(recent))
        {
            // Safety overrides the battery target here: a resident in a car is the case where
            // losing them is most likely, so we tighten the interval past what the target buys.
            var carInterval = Math.Min(profile.Wm8IntervalSeconds, CarIntervalSeconds);
            return BuildNormal(s, TrackingMode.WorkingMode8, carInterval, profile,
                $"Likely car trip (speed ≥ {CarSpeedKmh:F0} km/h, step counter flat). " +
                $"WM9 would lose them — switch to WM8 {carInterval / 60}-min fixed interval.");
        }

        // The battery-comfort choice: WM9 for on-foot movers, WM8 when mostly still.
        var desired = IsMover(recent) ? TrackingMode.WorkingMode9 : TrackingMode.WorkingMode8;

        // Anti-thrashing (hysteresis): if the device is already sitting in a steady working
        // mode and the *only* reason to switch is this comfort signal, hold the current mode
        // until the cooldown elapses. Transitions into a normal mode from Active Tracking or
        // WiFi Saver land here with CurrentMode != WM8/WM9, so wake-ups are never suppressed —
        // that keeps the fast-transition guarantee for the cases that actually matter.
        var inSteadyWorkingMode = s.CurrentMode is TrackingMode.WorkingMode8 or TrackingMode.WorkingMode9;
        var timeInMode = now - s.ModeStartedAt;
        if (inSteadyWorkingMode && desired != s.CurrentMode &&
            timeInMode < TimeSpan.FromMinutes(ModeChangeCooldownMinutes))
        {
            return BuildNormal(s, s.CurrentMode, null, profile,
                $"Movement suggests {desired}, but only {timeInMode.TotalMinutes:F0} min in {s.CurrentMode} " +
                $"(< {ModeChangeCooldownMinutes}-min cooldown) — holding to avoid thrashing.");
        }

        return desired == TrackingMode.WorkingMode9
            ? BuildNormal(s, TrackingMode.WorkingMode9, null, profile,
                $"Moving on foot; WM9 (every {profile.StepThreshold} steps, {profile.FallbackSeconds / 60}-min fallback) " +
                TargetClause(s.TargetBatteryHours, profile.Wm9MeetsTarget, profile.Wm9Hours))
            : BuildNormal(s, TrackingMode.WorkingMode8, profile.Wm8IntervalSeconds, profile,
                $"Mostly stationary; WM8 {profile.Wm8IntervalSeconds / 60}-min fixed interval " +
                TargetClause(s.TargetBatteryHours, profile.Wm8MeetsTarget, profile.Wm8Hours));
    }

    /// <summary>Say honestly whether the chosen parameters actually reach the user's target.
    /// The old code asserted "meets the Nh target" unconditionally; with the table derived from
    /// the battery model, some targets provably can't be met with GPS on and we say so.</summary>
    private static string TargetClause(int targetHours, bool meetsTarget, double hours) =>
        meetsTarget
            ? $"meets the {targetHours}h battery target (≈{hours:F0}h from a full charge)."
            : $"does NOT reach the {targetHours}h target — ≈{hours:F0}h from a full charge is the longest " +
              "any GPS-on setting lasts; only WiFi Saver time at home closes the rest of the gap.";

    /// <summary>Build a WM8 or WM9 recommendation with parameters from the target profile.
    /// Switching to WM8 clears WM9's step/fallback (and vice-versa) so a transition never
    /// carries the previous mode's stale parameters — the mode-transition-lag pitfall.</summary>
    private static ModeRecommendation BuildNormal(
        DeviceState s, TrackingMode mode, int? intervalOverride, ModeProfile profile, string rationale) =>
        mode == TrackingMode.WorkingMode8
            ? Build(s, TrackingMode.WorkingMode8, intervalOverride ?? profile.Wm8IntervalSeconds, null, null, false, null, rationale)
            : Build(s, TrackingMode.WorkingMode9, null, profile.StepThreshold, profile.FallbackSeconds, false, null, rationale);

    // --- target profile: derived, not hard-coded --------------------------

    // The parameter values the devices actually accept, per the brief.
    private static readonly int[] Wm8IntervalOptions = { 120, 180, 240, 300, 600, 900, 1200, 1800 };
    private static readonly int[] Wm9StepOptions = { 100, 200, 300, 400, 500, 1000 };
    private static readonly int[] Wm9FallbackOptions = { 600, 1200, 1800, 2400 };

    private const int CarIntervalSeconds = 180; // tightest interval we'll force during a car trip

    /// <summary>Concrete parameters for one battery target, plus whether they actually reach it.</summary>
    private readonly record struct ModeProfile(
        int Wm8IntervalSeconds, bool Wm8MeetsTarget, double Wm8Hours,
        int StepThreshold, int FallbackSeconds, bool Wm9MeetsTarget, double Wm9Hours);

    /// <summary>
    /// Turn the desired battery life into concrete mode parameters by *searching* the allowed
    /// values with the battery model, instead of hard-coding a lookup table.
    ///
    /// This exists because the hand-written table and the drain model disagreed: the table
    /// claimed a 5-minute WM8 interval "meets the 24h target" while the model priced the same
    /// interval at 8.2 %/h — i.e. 12 hours. Deriving the table makes targetBatteryHours a real
    /// constraint: we take the *most frequent* reporting (best tracking) that still survives the
    /// target from a full charge, and if the constants ever change the table moves with them.
    /// </summary>
    private static ModeProfile TargetProfile(int targetHours)
    {
        // Shortest interval first = best tracking; take the first that survives the target.
        var wm8 = Wm8IntervalOptions
            .Select(i => (Interval: i, Hours: HoursFromFull(Wm8Drain(i))))
            .ToList();
        var wm8Pick = wm8.FirstOrDefault(c => c.Hours >= targetHours, wm8[^1]);

        // Same idea for WM9, ranked by how often it reports (fallback fixes + step-triggered
        // fixes). Note the model says step-triggered fixes are cheap and the *fallback* is what
        // costs you, so this systematically prefers a low step threshold with a long fallback —
        // the opposite of what the hand-written table assumed.
        var wm9 = (from st in Wm9StepOptions
                   from fb in Wm9FallbackOptions
                   orderby Wm9FixesPerHour(st, fb) descending
                   select (Steps: st, Fallback: fb, Hours: HoursFromFull(Wm9Drain(st, fb)))).ToList();
        var wm9Pick = wm9.FirstOrDefault(c => c.Hours >= targetHours, wm9[^1]);

        return new ModeProfile(
            wm8Pick.Interval, wm8Pick.Hours >= targetHours, wm8Pick.Hours,
            wm9Pick.Steps, wm9Pick.Fallback, wm9Pick.Hours >= targetHours, wm9Pick.Hours);
    }

    // --- movement helpers -------------------------------------------------

    private static bool IsMover(IReadOnlyList<PositionReport> recent)
    {
        if (recent.Count == 0) return false;
        var steps = recent[^1].StepsToday - recent[0].StepsToday;
        var walking = recent.Any(r => r.SpeedKmh > 1 && r.SpeedKmh < CarSpeedKmh);
        return steps > 20 || walking;
    }

    private static bool StepsFlat(IReadOnlyList<PositionReport> recent)
    {
        if (recent.Count < 2) return true;
        return recent[^1].StepsToday - recent[0].StepsToday <= 5;
    }

    // --- battery model (our own assumption, calibrated against the dataset) ---

    // Calibration: the dataset's 14 days give observed drain per mode, which is what these
    // constants are fitted to (fractional battery in the history makes this measurable):
    //   active_tracking ~13.9 %/h · WM9 ~1.6-5.0 %/h · wifi_saver ~1.2 %/h
    //
    // Shape of the model: drain = idle + (cost of a GPS fix) x (fixes per hour), with a ceiling
    // on the GPS term. The ceiling is the interesting part — a naive linear model priced Active
    // Tracking at 240 cold fixes an hour and predicted ~60 %/h, four times what the data shows.
    // Below roughly a 70-second interval the receiver never powers down between fixes: it tracks
    // continuously, so there is no cold-acquisition cost to pay per fix and the drain saturates.

    private const double IdleDrainPctPerHour = 1.0;      // radio + housekeeping, GPS off
    private const double CostPerFixPct = 0.25;           // one cold GPS acquisition
    private const double ContinuousGpsPctPerHour = 12.9; // ceiling: receiver stays hot (=> AT ~13.9 %/h)
    private const double WifiSaverDrainPctPerHour = 1.2; // GPS off entirely, passive scanning only

    /// <summary>Steps per hour assumed when pricing WM9's step-triggered fixes. ~2 500 steps
    /// over a 10-hour waking day, which is what the dataset's walker device actually does.
    /// Per-resident history would beat this constant — see the README's known limitation.</summary>
    private const double AssumedStepsPerActiveHour = 250.0;

    private static double DrainForFixRate(double fixesPerHour) =>
        IdleDrainPctPerHour + Math.Min(CostPerFixPct * fixesPerHour, ContinuousGpsPctPerHour);

    private static double HoursFromFull(double drainPctPerHour) => 100.0 / drainPctPerHour;

    private static double Wm8Drain(int intervalSeconds) =>
        DrainForFixRate(3600.0 / Math.Max(intervalSeconds, 1));

    /// <summary>WM9 reports on the fallback timer *and* every N steps, so both count.</summary>
    private static double Wm9FixesPerHour(int stepThreshold, int fallbackSeconds) =>
        3600.0 / Math.Max(fallbackSeconds, 1) + AssumedStepsPerActiveHour / Math.Max(stepThreshold, 1);

    private static double Wm9Drain(int stepThreshold, int fallbackSeconds) =>
        DrainForFixRate(Wm9FixesPerHour(stepThreshold, fallbackSeconds));

    private static double EstimateDrainPctPerHour(TrackingMode mode, int? intervalSeconds, int? stepThreshold, int? fallbackSeconds) => mode switch
    {
        TrackingMode.ActiveTracking => Wm8Drain(15),
        TrackingMode.WifiSaver => WifiSaverDrainPctPerHour,
        TrackingMode.WorkingMode8 => Wm8Drain(intervalSeconds ?? 300),
        TrackingMode.WorkingMode9 => Wm9Drain(stepThreshold ?? 300, fallbackSeconds ?? 1800),
        _ => DrainForFixRate(1),
    };

    private static ModeRecommendation Build(
        DeviceState s, TrackingMode mode, int? interval, int? steps, int? fallback,
        bool isDeviation, string? deviationKind, string rationale)
    {
        var drain = EstimateDrainPctPerHour(mode, interval, steps, fallback);
        var hoursLeft = drain > 0 ? s.BatteryPct / drain : double.PositiveInfinity;
        return new ModeRecommendation(
            s.DeviceId,
            mode.ToString(),
            interval,
            steps,
            fallback,
            Math.Round(drain, 2),
            Math.Round(hoursLeft, 1),
            isDeviation,
            deviationKind,
            rationale);
    }
}
