namespace StellaCareApi.Models;

/// <summary>The tracking modes a device can run in. WifiSaver is device-managed (GPS off).</summary>
public enum TrackingMode
{
    ActiveTracking, // 15s GPS - most precise, drains battery fast
    WorkingMode8,   // fixed interval
    WorkingMode9,   // step-based with fallback interval
    WifiSaver       // GPS off, home wifi seen consistently (device decides, not us)
}
