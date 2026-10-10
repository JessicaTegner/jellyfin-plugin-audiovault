using Jellyfin.Plugin.AudioVault.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioVault.Api;

/// <summary>
/// A 1×1 image the settings page loads from the playback address, to check that the admin's browser reaches this server
/// there. Anonymous and tells nothing: it only marks the page's one-time nonce as seen.
/// </summary>
[ApiController]
[Route("AudioVault")]
public sealed class ReachController(PlaybackReach reach) : ControllerBase
{
    private static readonly byte[] Pixel = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    [HttpGet("Ping")]
    [AllowAnonymous]
    public IActionResult Ping([FromQuery] string? n)
    {
        reach.Probes.Seen(n, DateTimeOffset.UtcNow);
        Response.Headers.CacheControl = "no-store";
        return File(Pixel, "image/gif");
    }
}
