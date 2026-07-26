using System.Text.Json;
using StellaCareApi.Interfaces;
using StellaCareApi.Models;

namespace StellaCareApi.Managers;

/// <summary>
/// In-memory store. Loads the simulated dataset once at startup and keeps device
/// state + 14-day history in memory. Simple for the case; in production this would
/// be a database + a stream of incoming reports (see README).
///
/// Concurrency rule: <see cref="DeviceState"/> is mutable, so it never leaves this class.
/// Reads and writes both happen under <see cref="_lock"/>, and readers get a detached
/// <see cref="DeviceSnapshot"/> built inside that same critical section. Methods suffixed
/// <c>Locked</c> assume the caller already holds the lock.
/// </summary>
public class DeviceStore : IDeviceStore
{
    private static readonly List<PositionReport> EmptyHistory = new();

    private readonly object _lock = new();
    private readonly Dictionary<string, DeviceState> _states = new();
    private readonly Dictionary<string, List<PositionReport>> _history = new();

    public DeviceStore(string datasetPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(datasetPath));
        var root = doc.RootElement;

        foreach (var cs in root.GetProperty("currentState").EnumerateArray())
        {
            var state = ParseState(cs);
            _states[state.DeviceId] = state;
            _history[state.DeviceId] = new List<PositionReport>();
        }

        foreach (var ev in root.GetProperty("events").EnumerateArray())
        {
            if (ev.GetProperty("type").GetString() != "PositionReport") continue;
            var id = ev.GetProperty("deviceId").GetString()!;
            if (!_history.TryGetValue(id, out var list)) continue;
            list.Add(ParseReport(ev));
        }

        foreach (var list in _history.Values)
            list.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
    }

    public IReadOnlyCollection<DeviceSnapshot> AllSnapshots()
    {
        // One lock for the whole sweep, so /devices/deviations reports a set of devices that
        // were all in these states at the same instant rather than a smear across the loop.
        lock (_lock) return _states.Keys.Select(id => SnapshotLocked(id)!).ToList();
    }

    public DeviceSnapshot? Snapshot(string id)
    {
        lock (_lock) return SnapshotLocked(id);
    }

    public IReadOnlyList<PositionReport> History(string id)
    {
        lock (_lock) return HistoryCopyLocked(id);
    }

    /// <summary>Latest timestamp we know about for a device. Used as "now" because the
    /// dataset's referenceNow predates the active-search events (documented in README).</summary>
    public DateTimeOffset EffectiveNow(string id)
    {
        lock (_lock)
        {
            return _states.TryGetValue(id, out var s)
                ? EffectiveNowLocked(s, HistoryLocked(id))
                : DateTimeOffset.UtcNow;
        }
    }

    public bool SetTargetBatteryHours(string id, int hours)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(id, out var s)) return false;
            s.TargetBatteryHours = hours;
            return true;
        }
    }

    public DeviceSnapshot? ActivateSearch(string id, string? initiator)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(id, out var s)) return null;
            var now = EffectiveNowLocked(s, HistoryLocked(id));
            s.ActiveSearch = new ActiveSearch("activated", now, initiator, null);
            s.CurrentMode = TrackingMode.ActiveTracking;
            s.CurrentIntervalSeconds = 15;
            s.ModeStartedAt = now;
            s.InWifiSaver = false;
            // Snapshot inside the same critical section: the recommendation the caller gets
            // back is the one for the state this very call produced.
            return SnapshotLocked(id);
        }
    }

    public DeviceSnapshot? DeactivateSearch(string id)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(id, out var s)) return null;
            s.ActiveSearch = null;
            // The device is still physically in Active Tracking until it gets a new mode
            // command. Restart the mode clock from now so the algorithm reads this as a
            // fresh transition, not a device that's been stuck for the whole search. Mode
            // selection itself is still left to the algorithm.
            s.ModeStartedAt = EffectiveNowLocked(s, HistoryLocked(id));
            return SnapshotLocked(id);
        }
    }

    // --- snapshotting (all require _lock) ---------------------------------

    private DeviceSnapshot? SnapshotLocked(string id)
    {
        if (!_states.TryGetValue(id, out var s)) return null;
        return new DeviceSnapshot(s.Clone(), HistoryCopyLocked(id), EffectiveNowLocked(s, HistoryLocked(id)));
    }

    private List<PositionReport> HistoryLocked(string id) =>
        _history.TryGetValue(id, out var h) ? h : EmptyHistory;

    /// <summary>History is append-only today, but a live report stream would write to it while
    /// a decision is in flight, so snapshots get their own array rather than the live list.
    /// A few hundred records per device makes the copy free; if it ever isn't, the fix is an
    /// immutable list swapped in on append.</summary>
    private PositionReport[] HistoryCopyLocked(string id) =>
        _history.TryGetValue(id, out var h) ? h.ToArray() : Array.Empty<PositionReport>();

    private static DateTimeOffset EffectiveNowLocked(DeviceState s, IReadOnlyList<PositionReport> history)
    {
        var now = s.At;
        if (s.ModeStartedAt > now) now = s.ModeStartedAt;
        if (s.ActiveSearch is { } search && search.StartedAt > now) now = search.StartedAt;
        if (history.Count > 0 && history[^1].Timestamp > now) now = history[^1].Timestamp;
        return now;
    }

    private static DeviceState ParseState(JsonElement e)
    {
        var state = new DeviceState
        {
            DeviceId = e.GetProperty("deviceId").GetString()!,
            At = e.GetProperty("at").GetDateTimeOffset(),
            BatteryPct = e.GetProperty("batteryPct").GetInt32(),
            CurrentMode = ParseMode(e.GetProperty("currentMode")),
            ModeStartedAt = e.GetProperty("modeStartedAt").GetDateTimeOffset(),
            TargetBatteryHours = e.GetProperty("targetBatteryHours").GetInt32(),
            Timezone = GetStringOrDefault(e, "timezone", "UTC")!,
        };

        if (e.TryGetProperty("currentIntervalSeconds", out var ci) && ci.ValueKind == JsonValueKind.Number)
            state.CurrentIntervalSeconds = ci.GetInt32();
        if (e.TryGetProperty("currentStepThreshold", out var st) && st.ValueKind == JsonValueKind.Number)
            state.CurrentStepThreshold = st.GetInt32();
        if (e.TryGetProperty("currentFallbackIntervalSeconds", out var fb) && fb.ValueKind == JsonValueKind.Number)
            state.CurrentFallbackIntervalSeconds = fb.GetInt32();
        if (e.TryGetProperty("inWifiSaver", out var ws) && ws.ValueKind == JsonValueKind.True)
            state.InWifiSaver = true;

        if (e.TryGetProperty("wifiVisibleNow", out var wifi) && wifi.ValueKind == JsonValueKind.Array)
            state.WifiVisibleNow = wifi.EnumerateArray().Select(x => x.GetString()!).ToList();

        if (e.TryGetProperty("lastKnownPosition", out var pos) && pos.ValueKind == JsonValueKind.Object)
            state.LastKnownPosition = new Position(pos.GetProperty("lat").GetDouble(), pos.GetProperty("lng").GetDouble());

        if (e.TryGetProperty("activeSearch", out var srch) && srch.ValueKind == JsonValueKind.Object)
        {
            state.ActiveSearch = new ActiveSearch(
                srch.GetProperty("status").GetString() ?? "activated",
                srch.GetProperty("startedAt").GetDateTimeOffset(),
                GetStringOrDefault(srch, "startedBy", null),
                GetStringOrDefault(srch, "note", null));
        }

        return state;
    }

    private static PositionReport ParseReport(JsonElement e) => new(
        e.GetProperty("timestamp").GetDateTimeOffset(),
        e.GetProperty("lat").GetDouble(),
        e.GetProperty("lng").GetDouble(),
        // History samples carry fractional battery (e.g. 84.4); read as double and round.
        (int)Math.Round(e.GetProperty("batteryPct").GetDouble()),
        ParseMode(e.GetProperty("workingMode")),
        e.TryGetProperty("stepsToday", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0,
        e.TryGetProperty("speedKmh", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetDouble() : 0,
        e.TryGetProperty("wifiSSIDsVisible", out var w) && w.ValueKind == JsonValueKind.Array
            ? w.EnumerateArray().Select(x => x.GetString()!).ToList()
            : Array.Empty<string>(),
        e.TryGetProperty("inWifiSaver", out var iw) && iw.ValueKind == JsonValueKind.True);

    private static TrackingMode ParseMode(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetInt32() == 8 ? TrackingMode.WorkingMode8 : TrackingMode.WorkingMode9,
        JsonValueKind.String when e.GetString() == "active_tracking" => TrackingMode.ActiveTracking,
        JsonValueKind.String when e.GetString() == "wifi_saver" => TrackingMode.WifiSaver,
        _ => TrackingMode.WorkingMode9
    };

    private static string? GetStringOrDefault(JsonElement e, string prop, string? fallback) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : fallback;
}
