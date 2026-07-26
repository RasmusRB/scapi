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
        var state = _store.GetState(id);
        if (state is null) return NotFound($"Unknown device '{id}'.");

        var recommendation = _algorithm.Recommend(state, _store.History(id), _store.EffectiveNow(id));
        return Ok(recommendation);
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
        if (!_store.ActivateSearch(id, request?.Initiator))
            return NotFound($"Unknown device '{id}'.");

        return Ok(_algorithm.Recommend(_store.GetState(id)!, _store.History(id), _store.EffectiveNow(id)));
    }

    /// <summary>End a search and let the algorithm pick a normal mode.</summary>
    [HttpPost("{id}/deactivate-search")]
    public IActionResult DeactivateSearch(string id)
    {
        if (!_store.DeactivateSearch(id))
            return NotFound($"Unknown device '{id}'.");

        return Ok(_algorithm.Recommend(_store.GetState(id)!, _store.History(id), _store.EffectiveNow(id)));
    }

    /// <summary>Devices currently in an abnormal state — especially stuck in Active Tracking.</summary>
    [HttpGet("deviations")]
    public ActionResult<IEnumerable<DeviationDto>> GetDeviations()
    {
        var deviations = new List<DeviationDto>();
        foreach (var state in _store.AllStates())
        {
            var now = _store.EffectiveNow(state.DeviceId);
            var recommendation = _algorithm.Recommend(state, _store.History(state.DeviceId), now);
            if (!recommendation.IsDeviation) continue;

            deviations.Add(new DeviationDto(
                state.DeviceId,
                state.CurrentMode.ToString(),
                state.BatteryPct,
                recommendation.DeviationKind ?? "unknown",
                Math.Round((now - state.ModeStartedAt).TotalHours, 1),
                recommendation));
        }
        return Ok(deviations);
    }
}
