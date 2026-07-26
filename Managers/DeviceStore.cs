using System.Text.Json;
using StellaCareApi.Interfaces;
using StellaCareApi.Models;

namespace StellaCareApi.Managers;

/// <summary>
/// In-memory store. Loads the simulated dataset once at startup and keeps device
/// state + 14-day history in memory. Simple for the case; in production this would
/// be a database + a stream of incoming reports (see README).
/// </summary>
public class DeviceStore : IDeviceStore
{
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

    public IReadOnlyCollection<DeviceState> AllStates()
    {
        lock (_lock) return _states.Values.ToList();
    }

    public DeviceState? GetState(string id)
    {
        lock (_lock) return _states.TryGetValue(id, out var s) ? s : null;
    }

    public IReadOnlyList<PositionReport> History(string id) =>
        _history.TryGetValue(id, out var h) ? h : Array.Empty<PositionReport>();

    /// <summary>Latest timestamp we know about for a device. Used as "now" because the
    /// dataset's referenceNow predates the active-search events (documented in README).</summary>
    public DateTimeOffset EffectiveNow(string id)
    {
        var s = GetState(id);
        var candidates = new List<DateTimeOffset>();
        if (s != null)
        {
            candidates.Add(s.At);
            candidates.Add(s.ModeStartedAt);
            if (s.ActiveSearch != null) candidates.Add(s.ActiveSearch.StartedAt);
        }
        var hist = History(id);
        if (hist.Count > 0) candidates.Add(hist[^1].Timestamp);
        return candidates.Count > 0 ? candidates.Max() : DateTimeOffset.UtcNow;
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

    public bool ActivateSearch(string id, string? initiator)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(id, out var s)) return false;
            var now = EffectiveNow(id);
            s.ActiveSearch = new ActiveSearch("activated", now, initiator, null);
            s.CurrentMode = TrackingMode.ActiveTracking;
            s.CurrentIntervalSeconds = 15;
            s.ModeStartedAt = now;
            s.InWifiSaver = false;
            return true;
        }
    }

    public bool DeactivateSearch(string id)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(id, out var s)) return false;
            s.ActiveSearch = null;
            // The device is still physically in Active Tracking until it gets a new mode
            // command. Restart the mode clock from now so the algorithm reads this as a
            // fresh transition, not a device that's been stuck for the whole search. Mode
            // selection itself is still left to the algorithm.
            s.ModeStartedAt = EffectiveNow(id);
            return true;
        }
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
