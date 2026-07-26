namespace StellaCareApi.Models;

/// <summary>
/// An immutable, point-in-time view of one device: its state, its history, and the "now"
/// that belongs with them — all captured inside a single critical section in the store.
///
/// This is what answers the brief's race-condition question. The algorithm reads a device's
/// state field by field, so handing it the live <see cref="DeviceState"/> would let a
/// concurrent activate/deactivate-search rewrite those fields mid-decision: the algorithm
/// could see <see cref="DeviceState.ActiveSearch"/> already set but <see cref="DeviceState.CurrentMode"/>
/// not yet updated, and recommend against a state that never actually existed. Taking a copy
/// under the lock gives every decision a definite serialization point — it is made against
/// the device as it was either strictly before or strictly after the competing event.
/// </summary>
public record DeviceSnapshot(
    DeviceState State,
    IReadOnlyList<PositionReport> History,
    DateTimeOffset Now);
