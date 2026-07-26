using StellaCareApi.Managers;
using StellaCareApi.Models;
using Xunit;

namespace StellaCareApi.Tests;

/// <summary>
/// Loads the real simulated dataset through DeviceStore. This is the regression guard for
/// the startup crash: history samples carry fractional battery (e.g. 84.4), which used to
/// blow up the parser and take every endpoint down with it.
/// </summary>
public class DeviceStoreTests
{
    private static string DatasetPath =>
        Path.Combine(AppContext.BaseDirectory, "case_backend_dataset_v3.json");

    private static DeviceStore Load() => new(DatasetPath);

    [Fact]
    public void Loads_TheThreeDevices_WithoutThrowing()
    {
        var store = Load();
        Assert.Equal(3, store.AllSnapshots().Count);
    }

    [Fact]
    public void ParsesHistory_IncludingFractionalBatterySamples()
    {
        var store = Load();
        // sc-dev-stuck-active is the device whose history contains fractional battery values.
        var history = store.History("sc-dev-stuck-active");
        Assert.NotEmpty(history);
    }

    [Fact]
    public void EffectiveNow_IsLatestKnownTimestamp_NotWallClock()
    {
        var store = Load();
        // "Now" is derived from the data (its latest known timestamp), never the wall clock.
        // For this device the last history sample is the most recent timestamp on record.
        var latestSample = store.History("sc-dev-stuck-active").Max(r => r.Timestamp);
        Assert.Equal(latestSample, store.EffectiveNow("sc-dev-stuck-active"));
    }

    // --- snapshot isolation (the brief's race-condition question) ----------

    [Fact]
    public void Snapshot_IsDetached_FromLaterMutations()
    {
        var store = Load();
        var before = store.Snapshot("sc-dev-walker-car")!;

        store.ActivateSearch("sc-dev-walker-car", "test");

        // The decision in flight must not see the state change underneath it.
        Assert.Null(before.State.ActiveSearch);
        Assert.NotEqual(TrackingMode.ActiveTracking, before.State.CurrentMode);
    }

    [Fact]
    public async Task ConcurrentSearchToggling_NeverExposesAHalfAppliedState()
    {
        // ActivateSearch writes ActiveSearch, CurrentMode, CurrentIntervalSeconds, ModeStartedAt
        // and InWifiSaver as five separate field writes. Handing the live DeviceState to a reader
        // let it observe that sequence part-way through and decide against a state that never
        // existed. The invariant: an activated search always comes with Active Tracking applied.
        var store = Load();
        const string id = "sc-dev-mostly-home";
        var stop = false;
        var failures = 0;

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                store.ActivateSearch(id, "test");
                store.DeactivateSearch(id);
            }
            stop = true;
        });

        var reader = Task.Run(() =>
        {
            while (!stop)
            {
                var s = store.Snapshot(id)!.State;
                if (s.ActiveSearch is { Status: "activated" } &&
                    (s.CurrentMode != TrackingMode.ActiveTracking || s.CurrentIntervalSeconds != 15 || s.InWifiSaver))
                    Interlocked.Increment(ref failures);
            }
        });

        await Task.WhenAll(writer, reader);
        Assert.Equal(0, failures);
    }
}
