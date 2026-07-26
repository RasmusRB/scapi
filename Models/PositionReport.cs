namespace StellaCareApi.Models;

/// <summary>One historical GPS/sensor sample.</summary>
public record PositionReport(
    DateTimeOffset Timestamp,
    double Lat,
    double Lng,
    int BatteryPct,
    TrackingMode WorkingMode,
    int StepsToday,
    double SpeedKmh,
    IReadOnlyList<string> WifiVisible,
    bool InWifiSaver);
