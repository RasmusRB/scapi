using StellaCareApi.Models;

namespace StellaCareApi.Interfaces;

/// <summary>The intelligent tracking optimizer: decides which mode a device should run in.</summary>
public interface ITrackingAlgorithm
{
    ModeRecommendation Recommend(DeviceState state, IReadOnlyList<PositionReport> history, DateTimeOffset now);
}
