using StellaCareApi.Managers;
using StellaCareApi.Models;
using Xunit;

namespace StellaCareApi.Tests;

/// <summary>
/// Exercises the algorithm's priority order (safety first, battery second). Each test
/// isolates one branch by constructing just enough device state + history.
/// </summary>
public class TrackingAlgorithmTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly TrackingAlgorithm _algo = new();

    // --- 1. Active, valid search wins over everything ---------------------

    [Fact]
    public void ActiveSearch_WithinTimeout_ForcesActiveTracking()
    {
        var state = Device(mode: TrackingMode.WorkingMode8);
        state.ActiveSearch = new ActiveSearch("activated", Now.AddMinutes(-10), "carer", null);

        var r = _algo.Recommend(state, NoHistory, Now);

        Assert.Equal(nameof(TrackingMode.ActiveTracking), r.RecommendedMode);
        Assert.Equal(15, r.IntervalSeconds);
        Assert.False(r.IsDeviation);
    }

    // --- 2. Search past the auto-timeout is a deviation -------------------

    [Fact]
    public void ActiveSearch_PastTimeout_FlagsSearchTimeout()
    {
        var state = Device(mode: TrackingMode.ActiveTracking);
        state.ActiveSearch = new ActiveSearch("activated", Now.AddHours(-3), "carer", null);

        var r = _algo.Recommend(state, NoHistory, Now);

        Assert.True(r.IsDeviation);
        Assert.Equal("search_timeout", r.DeviationKind);
        Assert.NotEqual(nameof(TrackingMode.ActiveTracking), r.RecommendedMode);
    }

    // --- 3. Stuck in Active Tracking with no search ----------------------

    [Fact]
    public void ActiveTracking_NoSearch_PastGrace_FlagsStuck()
    {
        var state = Device(mode: TrackingMode.ActiveTracking, modeStartedAt: Now.AddHours(-2));

        var r = _algo.Recommend(state, NoHistory, Now);

        Assert.True(r.IsDeviation);
        Assert.Equal("stuck_active_tracking", r.DeviationKind);
    }

    [Fact]
    public void ActiveTracking_NoSearch_WithinGrace_IsNormalWindDown()
    {
        var state = Device(mode: TrackingMode.ActiveTracking, modeStartedAt: Now.AddMinutes(-5));

        var r = _algo.Recommend(state, NoHistory, Now);

        Assert.False(r.IsDeviation);
        Assert.Null(r.DeviationKind);
        Assert.NotEqual(nameof(TrackingMode.ActiveTracking), r.RecommendedMode);
    }

    // --- 4. Home wifi -> WiFi Saver --------------------------------------

    [Fact]
    public void HomeWifiVisible_ReturnsWifiSaver()
    {
        var state = Device(mode: TrackingMode.WorkingMode8);
        state.InWifiSaver = true;
        state.WifiVisibleNow = new List<string> { "home-net" };

        var r = _algo.Recommend(state, NoHistory, Now);

        Assert.Equal(nameof(TrackingMode.WifiSaver), r.RecommendedMode);
        Assert.False(r.IsDeviation);
    }

    // --- 5. Normal mode selection ----------------------------------------

    [Fact]
    public void FastMovingWithFlatSteps_DetectedAsCar_UsesWorkingMode8()
    {
        var state = Device(mode: TrackingMode.WorkingMode9);
        var history = new[]
        {
            Report(Now.AddMinutes(-10), speedKmh: 30, stepsToday: 1000),
            Report(Now.AddMinutes(-3), speedKmh: 35, stepsToday: 1000), // steps flat -> not walking
        };

        var r = _algo.Recommend(state, history, Now);

        Assert.Equal(nameof(TrackingMode.WorkingMode8), r.RecommendedMode);
        Assert.False(r.IsDeviation);
    }

    [Fact]
    public void WalkingOnFoot_UsesWorkingMode9()
    {
        var state = Device(mode: TrackingMode.WorkingMode8);
        var history = new[]
        {
            Report(Now.AddMinutes(-10), speedKmh: 4, stepsToday: 100),
            Report(Now.AddMinutes(-3), speedKmh: 4, stepsToday: 180),
        };

        var r = _algo.Recommend(state, history, Now);

        Assert.Equal(nameof(TrackingMode.WorkingMode9), r.RecommendedMode);
        Assert.Equal(300, r.StepThreshold); // 24h profile
    }

    [Fact]
    public void MostlyStationary_UsesWorkingMode8FixedInterval()
    {
        var state = Device(mode: TrackingMode.WorkingMode9);
        var history = new[]
        {
            Report(Now.AddMinutes(-10), speedKmh: 0, stepsToday: 500),
            Report(Now.AddMinutes(-3), speedKmh: 0, stepsToday: 500),
        };

        var r = _algo.Recommend(state, history, Now);

        Assert.Equal(nameof(TrackingMode.WorkingMode8), r.RecommendedMode);
        Assert.Equal(300, r.IntervalSeconds); // 24h profile fixed interval
    }

    // --- 6. WiFi Saver -> normal mode transition (must be immediate) ------

    [Fact]
    public void WifiSaverButWifiDropped_ImmediatelyPicksNormalMode()
    {
        // Person just left home: still flagged InWifiSaver, but no wifi is visible anymore.
        var state = Device(mode: TrackingMode.WifiSaver);
        state.InWifiSaver = true;
        state.WifiVisibleNow = new List<string>(); // wifi gone
        var history = new[]
        {
            Report(Now.AddMinutes(-10), speedKmh: 4, stepsToday: 100),
            Report(Now.AddMinutes(-3), speedKmh: 4, stepsToday: 190),
        };

        var r = _algo.Recommend(state, history, Now);

        // No lingering in WiFi Saver, and a concrete normal mode with real parameters.
        Assert.NotEqual(nameof(TrackingMode.WifiSaver), r.RecommendedMode);
        Assert.Equal(nameof(TrackingMode.WorkingMode9), r.RecommendedMode);
        Assert.NotNull(r.StepThreshold);
        Assert.False(r.IsDeviation);
    }

    // --- 7. Mode-transition timing: no stale parameters carried over ------

    [Fact]
    public void SwitchingWm9ToWm8_DropsWm9FallbackImmediately()
    {
        // Car case forces WM9 -> WM8. The result must be WM8's fixed interval with NO
        // WM9 fallback lingering (the mode-transition-lag pitfall from the brief).
        var state = Device(mode: TrackingMode.WorkingMode9);
        var history = new[]
        {
            Report(Now.AddMinutes(-10), speedKmh: 40, stepsToday: 500),
            Report(Now.AddMinutes(-3), speedKmh: 45, stepsToday: 500),
        };

        var r = _algo.Recommend(state, history, Now);

        Assert.Equal(nameof(TrackingMode.WorkingMode8), r.RecommendedMode);
        Assert.NotNull(r.IntervalSeconds);
        Assert.Null(r.StepThreshold);
        Assert.Null(r.FallbackIntervalSeconds);
    }

    // --- 8. Anti-thrashing: cooldown holds a battery-only flip -----------

    [Fact]
    public void ComfortFlip_WithinCooldown_HoldsCurrentMode()
    {
        // In WM9 for only 5 min; the person is now sitting still (would suggest WM8).
        // The cooldown should keep it in WM9 to avoid flapping.
        var state = Device(mode: TrackingMode.WorkingMode9, modeStartedAt: Now.AddMinutes(-5));
        var history = new[]
        {
            Report(Now.AddMinutes(-10), speedKmh: 0, stepsToday: 500),
            Report(Now.AddMinutes(-3), speedKmh: 0, stepsToday: 500),
        };

        var r = _algo.Recommend(state, history, Now);

        Assert.Equal(nameof(TrackingMode.WorkingMode9), r.RecommendedMode);
        Assert.False(r.IsDeviation);
    }

    [Fact]
    public void ComfortFlip_PastCooldown_SwitchesMode()
    {
        // Same signal, but the device has been in WM9 for 30 min — cooldown elapsed, flip allowed.
        var state = Device(mode: TrackingMode.WorkingMode9, modeStartedAt: Now.AddMinutes(-30));
        var history = new[]
        {
            Report(Now.AddMinutes(-10), speedKmh: 0, stepsToday: 500),
            Report(Now.AddMinutes(-3), speedKmh: 0, stepsToday: 500),
        };

        var r = _algo.Recommend(state, history, Now);

        Assert.Equal(nameof(TrackingMode.WorkingMode8), r.RecommendedMode);
    }

    [Fact]
    public void CarDetection_BypassesCooldown_SwitchesImmediately()
    {
        // Only 2 min in WM9, but a car is detected — safety wins over the cooldown.
        var state = Device(mode: TrackingMode.WorkingMode9, modeStartedAt: Now.AddMinutes(-2));
        var history = new[]
        {
            Report(Now.AddMinutes(-8), speedKmh: 50, stepsToday: 800),
            Report(Now.AddMinutes(-2), speedKmh: 55, stepsToday: 800),
        };

        var r = _algo.Recommend(state, history, Now);

        Assert.Equal(nameof(TrackingMode.WorkingMode8), r.RecommendedMode);
    }

    // --- helpers ----------------------------------------------------------

    private static readonly IReadOnlyList<PositionReport> NoHistory = Array.Empty<PositionReport>();

    private static DeviceState Device(
        TrackingMode mode,
        DateTimeOffset? modeStartedAt = null,
        int targetBatteryHours = 24) => new()
    {
        DeviceId = "test-dev",
        At = Now,
        BatteryPct = 80,
        CurrentMode = mode,
        // Default to "well past the cooldown" so mode-selection tests exercise the choice,
        // not the anti-thrashing hold. Cooldown tests set a recent ModeStartedAt explicitly.
        ModeStartedAt = modeStartedAt ?? Now.AddHours(-1),
        TargetBatteryHours = targetBatteryHours,
    };

    private static PositionReport Report(DateTimeOffset ts, double speedKmh, int stepsToday) =>
        new(ts, 55.0, 12.0, 90, TrackingMode.WorkingMode9, stepsToday, speedKmh, Array.Empty<string>(), false);
}
