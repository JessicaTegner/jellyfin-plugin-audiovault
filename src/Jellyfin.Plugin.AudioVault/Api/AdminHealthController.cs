using Jellyfin.Plugin.AudioVault.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioVault.Api;

/// <summary>
/// What the settings page shows when the full status can't be built (a part of the plugin failed to start). Depends on
/// nothing that can fail.
/// </summary>
[ApiController]
[Route("AudioVault/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class AdminHealthController(PluginHealth health) : ControllerBase
{
    [HttpGet("Health")]
    public ActionResult<object> Health() => new { PluginProblem = health.Problem };
}
