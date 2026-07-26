namespace StellaCareApi.Models;

/// <summary>Body for POST /devices/{id}/config.</summary>
public record ConfigRequest(int TargetBatteryHours);

/// <summary>Body for POST /devices/{id}/activate-search.</summary>
public record ActivateSearchRequest(string? Initiator);

/// <summary>A device that is currently in an abnormal state.</summary>
public record DeviationDto(
    string DeviceId,
    string CurrentMode,
    int BatteryPct,
    string DeviationKind,
    double HoursInCurrentMode,
    ModeRecommendation Recommended);
