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
/// The numeric thresholds below are the deliberate "holes" from the brief — our own,
/// documented assumptions (see README), not values handed to us.
/// </summary>
public class TrackingAlgorithm : ITrackingAlgorithm
{
    private const double CarSpeedKmh = 15.0;          // >= this with a flat step count => likely a car
    private const int SearchAutoTimeoutMinutes = 120; // a search older than this should have closed
    private const int RecentWindowMinutes = 30;       // history window for car / movement detection
    private const int StuckActiveTrackingMinutes = 30; // grace period before "in Active Tracking, no search" counts as stuck

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

            var afterTimeout = ChooseNormalMode(s, recent);
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
            var normal = ChooseNormalMode(s, recent);

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
        return ChooseNormalMode(s, recent);
    }

    /// <summary>Pick WM8 vs WM9 and parameters from movement context + battery target.</summary>
    private ModeRecommendation ChooseNormalMode(DeviceState s, IReadOnlyList<PositionReport> recent)
    {
        var profile = TargetProfile(s.TargetBatteryHours);

        // Car detection: recent high speed with a flat step counter. WM9 can't track a vehicle.
        var highSpeed = recent.Any(r => r.SpeedKmh >= CarSpeedKmh);
        if (highSpeed && StepsFlat(recent))
        {
            var carInterval = Math.Min(profile.Interval, 180); // cap at 3 min while moving fast
            return Build(s, TrackingMode.WorkingMode8, carInterval, null, null, false, null,
                $"Likely car trip (speed ≥ {CarSpeedKmh:F0} km/h, step counter flat). " +
                $"WM9 would lose them — switch to WM8 {carInterval / 60}-min fixed interval.");
        }

        // On-foot mover => WM9 (position when they actually walk, efficient).
        if (IsMover(recent))
            return Build(s, TrackingMode.WorkingMode9, null, profile.StepThreshold, profile.Fallback, false, null,
                $"Moving on foot; WM9 (every {profile.StepThreshold} steps, {profile.Fallback / 60}-min fallback) " +
                $"fits the {s.TargetBatteryHours}h battery target.");

        // Mostly still => WM8 fixed interval.
        return Build(s, TrackingMode.WorkingMode8, profile.Interval, null, null, false, null,
            $"Mostly stationary; WM8 {profile.Interval / 60}-min fixed interval meets the {s.TargetBatteryHours}h battery target.");
    }

    /// <summary>Map the desired battery life to concrete mode parameters (all values valid per the brief).</summary>
    private static (int Interval, int StepThreshold, int Fallback) TargetProfile(int targetHours) => targetHours switch
    {
        <= 12 => (180, 200, 600),   // 3-min WM8 / 200 steps / 10-min fallback  (aggressive)
        <= 24 => (300, 300, 1200),  // 5-min       / 300       / 20-min          (standard)
        <= 36 => (900, 400, 1800),  // 15-min      / 400       / 30-min          (conservative)
        <= 48 => (1200, 500, 2400), // 20-min      / 500       / 40-min          (very conservative)
        _ => (1800, 1000, 2400),    // 30-min      / 1000      / 40-min          (max battery)
    };

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

    // --- battery model (our own, documented assumption) -------------------

    /// <summary>Estimated battery drain in %/hour. GPS is the main cost, so drain scales
    /// with how often we take a fix. Active Tracking is pinned high; WiFi Saver near zero.</summary>
    private static double EstimateDrainPctPerHour(TrackingMode mode, int? intervalSeconds, int? fallbackSeconds) => mode switch
    {
        TrackingMode.ActiveTracking => 25.0,
        TrackingMode.WifiSaver => 0.4,
        TrackingMode.WorkingMode8 => 1.0 + 3600.0 / Math.Max(intervalSeconds ?? 300, 1) * 0.6,
        TrackingMode.WorkingMode9 => 1.0 + 3600.0 / Math.Max(fallbackSeconds ?? 1800, 1) * 0.5,
        _ => 2.0
    };

    private static ModeRecommendation Build(
        DeviceState s, TrackingMode mode, int? interval, int? steps, int? fallback,
        bool isDeviation, string? deviationKind, string rationale)
    {
        var drain = EstimateDrainPctPerHour(mode, interval, fallback);
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
