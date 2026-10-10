using AudioVault.Core.Media;
using AudioVault.Core.Sources;
using Jellyfin.Data;
using Jellyfin.Data.Events;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// Keeps stored accounts in step with Jellyfin's users. A deleted user's account is removed and signed out at
/// AudioVault; a disabled user's pending downloads are revoked, and their account (kept for when they are enabled
/// again) authorises nothing meanwhile (see <see cref="AccountStore.OwnerIsActive"/>). Accounts of users deleted
/// while the plugin wasn't running are removed by <see cref="RemoveOrphansAsync"/>.
/// </summary>
public sealed class UserAccountWatcher(
    IUserManager users,
    AccountStore accounts,
    PlaybackIntents intents,
    MediaJobManager jobs,
    IContentSource source,
    ILogger<UserAccountWatcher> logger) : IHostedService
{
    /// <summary>Whether a Jellyfin user exists and isn't disabled; the check behind <see cref="AccountStore.OwnerIsActive"/>.</summary>
    public static bool IsActive(User? user) => user is not null && !user.HasPermission(PermissionKind.IsDisabled);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        users.OnUserUpdated += OnUserUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        users.OnUserUpdated -= OnUserUpdated;
        return Task.CompletedTask;
    }

    /// <summary>The user was deleted: their account goes, with everything still pending for it.</summary>
    public async Task ForgetAsync(Guid userId)
    {
        SourceAccount? account;
        using (await accounts.LockAsync(userId, CancellationToken.None).ConfigureAwait(false))
        {
            account = accounts.Get(userId);
            try
            {
                accounts.Remove(userId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still authorises nothing: the user doesn't exist. Retried by the next orphan cleanup.
                logger.LogError(ex, "Could not delete the stored AudioVault account of deleted user {User}", userId);
            }

            Revoke(userId);
        }

        if (account is not null)
        {
            logger.LogInformation("Removed the AudioVault account of deleted Jellyfin user {User}", userId);
            if (PluginSettings.Config.SignOutDeletedUsers)
            {
                _ = SignOutAsync(account);
            }
        }
    }

    /// <summary>Removes the accounts of users that no longer exist in Jellyfin.</summary>
    public async Task RemoveOrphansAsync()
    {
        foreach (var userId in accounts.Orphans([.. users.GetUsersIds()]))
        {
            await ForgetAsync(userId).ConfigureAwait(false);
        }
    }

    private void Revoke(Guid userId)
    {
        intents.Revoke(userId);
        jobs.RevokeOwner(AccountStore.OwnerOf(userId));
    }

    private void OnUserUpdated(object? sender, GenericEventArgs<User> e)
    {
        try
        {
            if (e.Argument is { } user && !IsActive(user))
            {
                Revoke(user.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not revoke the pending AudioVault downloads of a disabled user");
        }
    }

    /// <summary>Best effort, so the token is void even if a copy survives.</summary>
    private async Task SignOutAsync(SourceAccount account)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await source.SignOutAsync(account, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Signing out of AudioVault failed for {Account}", account.Key);
        }
    }
}

/// <summary>Jellyfin publishes user deletion as an event; consumers are created per event.</summary>
public sealed class UserDeletedConsumer(UserAccountWatcher watcher) : IEventConsumer<UserDeletedEventArgs>
{
    public Task OnEvent(UserDeletedEventArgs eventArgs) => watcher.ForgetAsync(eventArgs.Argument.Id);
}
