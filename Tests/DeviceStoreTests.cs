using StellaCareApi.Managers;
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
        Assert.Equal(3, store.AllStates().Count);
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
}
