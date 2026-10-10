using System.ComponentModel.DataAnnotations;
using AudioVault.Core.Media;
using AudioVault.Core.Sources;
using Jellyfin.Plugin.AudioVault.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Api;

public sealed record AccountRequest([Required, EmailAddress] string Email, [Required] string Password);

/// <summary>Per-user AudioVault logins: users manage their own, admins can manage everyone's. Passwords are never returned.</summary>
[ApiController]
[Route("AudioVault")]
public sealed class AccountController(
    AccountStore accounts,
    IContentSource source,
    MediaJobManager jobs,
    PlaybackIntents intents,
    IAuthorizationContext auth,
    IUserManager users,
    ILogger<AccountController> logger) : ControllerBase
{
    /// <summary>Self-service page. It reads the browser's Jellyfin sign-in to call the endpoints below.</summary>
    [HttpGet("Account")]
    [AllowAnonymous]
    public ActionResult AccountPage()
    {
        var stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.AudioVault.Configuration.accountPage.html")!;
        return File(stream, "text/html; charset=utf-8");
    }

    [HttpGet("Me/Account")]
    [Authorize]
    public async Task<ActionResult<object>> GetMine() => Describe(await CurrentUserAsync().ConfigureAwait(false));

    [HttpPut("Me/Account")]
    [Authorize]
    public async Task<ActionResult<object>> SetMine([FromBody] AccountRequest request, CancellationToken ct) =>
        await SaveAsync(await CurrentUserAsync().ConfigureAwait(false), request, ct).ConfigureAwait(false);

    [HttpDelete("Me/Account")]
    [Authorize]
    public async Task<ActionResult<object>> RemoveMine(CancellationToken ct) =>
        await ClearAsync(await CurrentUserAsync().ConfigureAwait(false), ct).ConfigureAwait(false);

    /// <summary>
    /// Deletes what's stored and revokes pending playback for it, then signs out at AudioVault so the token is void
    /// even if a copy survives. Whether AudioVault confirmed that is reported, since it can't always be.
    /// </summary>
    private async Task<ActionResult<object>> ClearAsync(Guid userId, CancellationToken ct)
    {
        using var held = await accounts.LockAsync(userId, ct).ConfigureAwait(false);
        var account = accounts.Get(userId);
        var stored = accounts.Describe(userId) is not null;
        try
        {
            accounts.Remove(userId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not delete the stored AudioVault account of user {User}", userId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "The details could not be deleted because the server could not write its accounts file. Nothing was changed." });
        }

        // Only this user's pending work: others who saved the same AudioVault login keep theirs.
        intents.Revoke(userId);
        jobs.RevokeOwner(AccountStore.OwnerOf(userId));

        // Stored but unreadable (key changed, damaged entry): nothing could be sent to AudioVault, so its sign-in may live on.
        var unreadable = stored && account is null;
        var confirmed = !unreadable && (account is null || await SignOutAsync(account).ConfigureAwait(false));
        return new { Removed = stored, SignOutConfirmed = confirmed, CredentialsUnreadable = unreadable };
    }

    /// <summary>Best effort and never cancelled by the browser going away: the local change has already happened.</summary>
    private async Task<bool> SignOutAsync(SourceAccount account)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await source.SignOutAsync(account, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Signing out of AudioVault failed for {Account}", account.Key);
            return false;
        }
    }

    private async Task<Guid> CurrentUserAsync() =>
        (await auth.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).UserId;

    /// <summary>Signs in once and stores what the source returns (a token where possible), never downloading anything.</summary>
    private async Task<ActionResult<object>> SaveAsync(Guid userId, AccountRequest request, CancellationToken ct)
    {
        using var held = await accounts.LockAsync(userId, ct).ConfigureAwait(false);
        SourceSignIn signIn;
        try
        {
            signIn = await source.SignInAsync(request.Email.Trim(), request.Password, ct).ConfigureAwait(false);
        }
        catch (SourceException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
        catch (HttpRequestException)
        {
            return BadRequest(new { Message = "AudioVault could not be reached. Try again later." });
        }

        var previous = accounts.Get(userId);
        SourceAccount saved;
        try
        {
            saved = accounts.Set(userId, request.Email, signIn);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not save the AudioVault account of user {User}", userId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "The login works, but the server could not write its accounts file." });
        }

        // Only once the new account is saved: a failed save leaves the old one working. Saving moved the generation
        // on, so tickets, intents and queued downloads holding the previous saving no longer count, even for the same
        // email; queued downloads of it fail now rather than when they reach the source.
        // Only this user's earlier savings: other users sharing the login keep their queued downloads.
        intents.Revoke(userId);
        jobs.RevokeOwner(AccountStore.OwnerOf(userId), keepGeneration: saved.Generation);
        if (previous is not null)
        {
            if (previous.Key != new SourceAccount(request.Email, string.Empty).Key)
            {
                await SignOutAsync(previous).ConfigureAwait(false);
            }
        }

        return Describe(userId);
    }

    private object Describe(Guid userId)
    {
        var account = accounts.Get(userId);
        var stored = accounts.Describe(userId);
        return new
        {
            UserId = userId,
            UserName = users.GetUserById(userId)?.Username,
            // HasAccount: usable. HasStoredAccount: something is stored, perhaps unreadable (key changed, damaged
            // entry), which can still be cleared.
            HasAccount = account is not null,
            HasStoredAccount = stored is not null,
            Email = account?.Login,
            PasswordStored = stored?.ContainsPassword ?? false,
            DownloadsLast24h = account is null ? 0 : jobs.DownloadsUsed(account),
            DownloadLimit = jobs.DownloadLimit,
        };
    }
}
