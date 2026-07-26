namespace StellaCareApi.Models;

/// <summary>Current, mutable state of a single device.</summary>
public class DeviceState
{
    public string DeviceId { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public int BatteryPct { get; set; }
    public TrackingMode CurrentMode { get; set; }
    public int? CurrentIntervalSeconds { get; set; }
    public int? CurrentStepThreshold { get; set; }
    public int? CurrentFallbackIntervalSeconds { get; set; }
    public DateTimeOffset ModeStartedAt { get; set; }
    public List<string> WifiVisibleNow { get; set; } = new();
    public bool InWifiSaver { get; set; }
    public Position? LastKnownPosition { get; set; }
    public int TargetBatteryHours { get; set; }
    public ActiveSearch? ActiveSearch { get; set; }
    public string Timezone { get; set; } = "UTC";

    /// <summary>Detached copy, for handing out a <see cref="DeviceSnapshot"/> that later
    /// mutations can't reach into. Only the store should call this, and only under its lock.
    /// <see cref="Position"/> and <see cref="ActiveSearch"/> are immutable records, so they
    /// can be shared; the wifi list is copied because it isn't.</summary>
    public DeviceState Clone() => new()
    {
        DeviceId = DeviceId,
        At = At,
        BatteryPct = BatteryPct,
        CurrentMode = CurrentMode,
        CurrentIntervalSeconds = CurrentIntervalSeconds,
        CurrentStepThreshold = CurrentStepThreshold,
        CurrentFallbackIntervalSeconds = CurrentFallbackIntervalSeconds,
        ModeStartedAt = ModeStartedAt,
        WifiVisibleNow = new List<string>(WifiVisibleNow),
        InWifiSaver = InWifiSaver,
        LastKnownPosition = LastKnownPosition,
        TargetBatteryHours = TargetBatteryHours,
        ActiveSearch = ActiveSearch,
        Timezone = Timezone,
    };
}
