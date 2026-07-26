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
}
