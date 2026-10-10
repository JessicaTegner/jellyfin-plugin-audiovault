namespace AudioVault.Core.Sources;

public enum MediaKind
{
    Movie,
    Show
}

public enum SourceMediaKind
{
    SingleAudio,
    Archive
}

public sealed record VersionInfo(string Language, bool IsTts, IReadOnlyList<string> Tags)
{
    public static VersionInfo Default(string language) => new(language, false, []);
}

public sealed record SourceEntry(
    string Id,
    MediaKind Kind,
    string Title,
    int? Year,
    int? Season,
    int? Part,
    VersionInfo Version,
    string RawName);

/// <summary>Per-account limits: each account may download this many per day, this many at a time.</summary>
public sealed record SourceLimits(int MaxDownloadsPerDay, int MaxConcurrentDownloads);

/// <summary>
/// A user's account at the source. <see cref="Credential"/> is opaque: whatever the source returned from
/// <see cref="IContentSource.SignInAsync"/>. <see cref="Key"/> identifies the account for quotas and sessions.
/// </summary>
public sealed record SourceAccount(string Login, string Credential)
{
    public string Key => Login.Trim().ToLowerInvariant();

    /// <summary>
    /// Which saving of the account this is. The host bumps it whenever the account is removed or its credentials
    /// replaced, so work bound to an older snapshot can be told apart (see <c>MediaJobManager.AccountIsCurrent</c>).
    /// </summary>
    public long Generation { get; init; }

    /// <summary>
    /// The host's id of the user this snapshot belongs to, so revocation checks look at that user's account only.
    /// Not part of equality: users sharing one login are one account at the source (one session, one quota).
    /// </summary>
    public string? Owner { get; init; }

    public bool Equals(SourceAccount? other) =>
        other is not null
        && string.Equals(Login, other.Login, StringComparison.Ordinal)
        && string.Equals(Credential, other.Credential, StringComparison.Ordinal)
        && Generation == other.Generation;

    public override int GetHashCode() => HashCode.Combine(Login, Credential, Generation);

    public override string ToString() => Key;
}

/// <param name="Failure">Why it isn't OK, when an error says more than the message.</param>
public sealed record SourceHealth(bool Ok, string Message, FailureInfo? Failure = null);

/// <param name="Credential">Store this; pass it back in <see cref="SourceAccount"/>.</param>
/// <param name="ContainsPassword">True when the source could not issue a token and the credential holds the password.</param>
public sealed record SourceSignIn(string Credential, bool ContainsPassword);

public sealed class SourceMedia(Stream content, SourceMediaKind kind, string? fileName, long? length) : IAsyncDisposable
{
    public Stream Content { get; } = content;

    public SourceMediaKind Kind { get; } = kind;

    public string? FileName { get; } = fileName;

    public long? Length { get; } = length;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public class SourceException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Set where the cause is known, so reports needn't quote the message.</summary>
    public FailureKind? Kind { get; init; }

    public int? HttpStatus { get; init; }
}

/// <summary>Thrown when the source's pages no longer look like what the parser expects.</summary>
public sealed class SourceFormatChangedException(string message) : SourceException(message);

public sealed class SourceUnavailableException(string message, Exception? inner = null) : SourceException(message, inner);

/// <summary>Thrown by <see cref="IContentSource.OpenAsync"/> only when no download request reached the source, so it cannot have been counted.</summary>
public sealed class DownloadNotStartedException(string message, Exception? inner = null) : SourceException(message, inner);
