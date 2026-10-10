namespace AudioVault.Core.Sources;

public interface IContentSource
{
    string Id { get; }

    SourceLimits Limits { get; }

    IAsyncEnumerable<SourceEntry> ListAllAsync(IProgress<double>? progress, CancellationToken ct);

    IAsyncEnumerable<SourceEntry> ListRecentAsync(CancellationToken ct);

    /// <summary>Signs in once with a password and returns what to store instead of it. Throws <see cref="SourceException"/> if rejected.</summary>
    Task<SourceSignIn> SignInAsync(string login, string password, CancellationToken ct);

    /// <summary>
    /// Best effort: ends the account's session at the source so a stored token stops working, and forgets it locally.
    /// Returns true only when the source confirmed the sign-out; false when it could not be confirmed. Does not throw for that.
    /// </summary>
    Task<bool> SignOutAsync(SourceAccount account, CancellationToken ct);

    /// <summary>Checks the catalog can be read and, with an account, that it is still signed in. No downloads.</summary>
    Task<SourceHealth> CheckHealthAsync(SourceAccount? account, CancellationToken ct);

    /// <summary>Instant, no network: is this account usable and not known to be failing?</summary>
    SourceHealth GetReadiness(SourceAccount account);

    Task<SourceMedia> OpenAsync(string entryId, SourceAccount account, CancellationToken ct);
}
