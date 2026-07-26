namespace StellaCareApi.Models;

/// <summary>What the algorithm recommends for a device.</summary>
public record ModeRecommendation(
    string DeviceId,
    string RecommendedMode,
    int? IntervalSeconds,
    int? StepThreshold,
    int? FallbackIntervalSeconds,
    double ExpectedDrainPctPerHour,
    double ExpectedBatteryHoursRemaining,
    bool IsDeviation,
    string? DeviationKind,
    string Rationale);
