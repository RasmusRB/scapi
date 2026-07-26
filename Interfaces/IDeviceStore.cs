using StellaCareApi.Models;

namespace StellaCareApi.Interfaces;

/// <summary>Holds device state + history. Backed by in-memory data for the case.</summary>
public interface IDeviceStore
{
    IReadOnlyCollection<DeviceState> AllStates();
    DeviceState? GetState(string id);
    IReadOnlyList<PositionReport> History(string id);

    /// <summary>Latest timestamp known for a device; used as "now".</summary>
    DateTimeOffset EffectiveNow(string id);

    bool SetTargetBatteryHours(string id, int hours);
    bool ActivateSearch(string id, string? initiator);
    bool DeactivateSearch(string id);
}
