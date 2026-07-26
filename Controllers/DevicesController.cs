using Microsoft.AspNetCore.Mvc;
using StellaCareApi.Interfaces;
using StellaCareApi.Models;

namespace StellaCareApi.Controllers;

[ApiController]
[Route("devices")]
public class DevicesController : ControllerBase
{
    private static readonly int[] AllowedBatteryHours = { 12, 24, 36, 48, 72 };

    private readonly IDeviceStore _store;
    private readonly ITrackingAlgorithm _algorithm;

    public DevicesController(IDeviceStore store, ITrackingAlgorithm algorithm)
    {
        _store = store;
        _algorithm = algorithm;
    }

    /// <summary>Recommended mode + parameters + battery impact + rationale for a device.</summary>
    [HttpGet("{id}/recommended-mode")]
    public ActionResult<ModeRecommendation> GetRecommendedMode(string id)
    {
        var snapshot = _store.Snapshot(id);
        if (snapshot is null) return NotFound($"Unknown device '{id}'.");

        return Ok(Recommend(snapshot));
    }

    /// <summary>Update the user's desired battery life.</summary>
    [HttpPost("{id}/config")]
    public IActionResult UpdateConfig(string id, [FromBody] ConfigRequest request)
    {
        if (!AllowedBatteryHours.Contains(request.TargetBatteryHours))
            return BadRequest($"targetBatteryHours must be one of: {string.Join(", ", AllowedBatteryHours)}.");

        if (!_store.SetTargetBatteryHours(id, request.TargetBatteryHours))
            return NotFound($"Unknown device '{id}'.");

        return NoContent();
    }

    /// <summary>Start a search and switch the device to Active Tracking.</summary>
    [HttpPost("{id}/activate-search")]
    public IActionResult ActivateSearch(string id, [FromBody] ActivateSearchRequest? request)
    {
        var snapshot = _store.ActivateSearch(id, request?.Initiator);
        if (snapshot is null) return NotFound($"Unknown device '{id}'.");

        return Ok(Recommend(snapshot));
    }

    /// <summary>End a search and let the algorithm pick a normal mode.</summary>
    [HttpPost("{id}/deactivate-search")]
    public IActionResult DeactivateSearch(string id)
    {
        var snapshot = _store.DeactivateSearch(id);
        if (snapshot is null) return NotFound($"Unknown device '{id}'.");

        return Ok(Recommend(snapshot));
    }

    /// <summary>Devices currently in an abnormal state — especially stuck in Active Tracking.</summary>
    [HttpGet("deviations")]
    public ActionResult<IEnumerable<DeviationDto>> GetDeviations()
    {
        var deviations = new List<DeviationDto>();
        foreach (var snapshot in _store.AllSnapshots())
        {
            var recommendation = Recommend(snapshot);
            if (!recommendation.IsDeviation) continue;

            var state = snapshot.State;
            deviations.Add(new DeviationDto(
                state.DeviceId,
                state.CurrentMode.ToString(),
                state.BatteryPct,
                recommendation.DeviationKind ?? "unknown",
                Math.Round((snapshot.Now - state.ModeStartedAt).TotalHours, 1),
                recommendation));
        }
        return Ok(deviations);
    }

    private ModeRecommendation Recommend(DeviceSnapshot snapshot) =>
        _algorithm.Recommend(snapshot.State, snapshot.History, snapshot.Now);
}
