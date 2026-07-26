using StellaCareApi.Models;

namespace StellaCareApi.Interfaces;

/// <summary>
/// Holds device state + history. Backed by in-memory data for the case.
///
/// Everything the algorithm consumes leaves this interface as a <see cref="DeviceSnapshot"/>,
/// never as live mutable state — see that type for why. The mutating calls return the snapshot
/// produced by the same critical section, so a caller that acts on an event always gets back
/// the state that event actually produced.
/// </summary>
public interface IDeviceStore
{
    IReadOnlyCollection<DeviceSnapshot> AllSnapshots();

    /// <summary>Consistent view of one device, or null if it's unknown.</summary>
    DeviceSnapshot? Snapshot(string id);

    IReadOnlyList<PositionReport> History(string id);

    /// <summary>Latest timestamp known for a device; used as "now".</summary>
    DateTimeOffset EffectiveNow(string id);

    bool SetTargetBatteryHours(string id, int hours);
    DeviceSnapshot? ActivateSearch(string id, string? initiator);
    DeviceSnapshot? DeactivateSearch(string id);
}
