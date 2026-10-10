using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AudioVault.Core.Sources;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <param name="Protected">AES-GCM encrypted credential: nonce | ciphertext | tag, base64.</param>
/// <param name="ContainsPassword">The source issued no sign-in token, so the encrypted credential holds the password.</param>
public sealed record StoredAccount(string Login, string Protected, bool ContainsPassword, DateTimeOffset UpdatedAt);

/// <summary>accounts.json. Older versions stored just the accounts map; that still reads, with every generation 0.</summary>
/// <param name="Generations">Per user, the current saving of their account. Kept after removal so a re-added account never
/// reuses an old generation.</param>
public sealed record StoredAccounts(Dictionary<Guid, StoredAccount>? Accounts, Dictionary<Guid, long>? Generations);

/// <summary>
/// Source accounts per Jellyfin user, encrypted at rest.
/// Kept out of the plugin configuration because Jellyfin hands that to any admin through its API.
/// The key lives in Jellyfin's config folder, apart from the data file, so a copied data file or backup
/// reveals nothing on its own. Someone with full access to the server can still decrypt it.
/// </summary>
public sealed class AccountStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _path;
    private readonly byte[] _key;
    private readonly ILogger<AccountStore> _logger;
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _userLocks = new();
    private Dictionary<Guid, StoredAccount> _accounts = [];
    private Dictionary<Guid, long> _generations = [];

    public AccountStore(PluginSettings settings, IApplicationPaths paths, ILogger<AccountStore> logger)
        : this(settings.DataDirectory, paths.ConfigurationDirectoryPath, logger)
    {
    }

    public AccountStore(string dataDirectory, string configDirectory, ILogger<AccountStore> logger)
    {
        _logger = logger;
        _path = Path.Combine(dataDirectory, "accounts.json");
        var keyPath = Path.Combine(configDirectory, "audiovault-accounts.key");

        // Nothing here may throw: this store is a dependency of a filter that runs on every Jellyfin API request.
        byte[]? key = null;
        try
        {
            key = ReadKey(keyPath);
        }
        catch (Exception ex)
        {
            // Accounts encrypted with the unreadable key can't be used with a new one, so both are set aside, not replaced.
            Problem = "The key of the stored AudioVault accounts could not be read, so every user has to add their account again. The old files were kept with a .bad ending.";
            _logger.LogError(ex, "The AudioVault account key {Path} can't be read; setting it and the stored accounts aside", keyPath);
            Quarantine(keyPath);
            Quarantine(_path);
        }

        if (key is null && Problem is null && File.Exists(_path))
        {
            Problem = "The key of the stored AudioVault accounts is missing, so every user has to add their account again. The old accounts file was kept with a .bad ending.";
            _logger.LogError("The AudioVault account key {Path} is missing; setting the stored accounts aside", keyPath);
            Quarantine(_path);
        }

        try
        {
            _key = key ?? CreateKey(keyPath);
        }
        catch (Exception ex)
        {
            // Without a key file accounts can't survive a restart; keep working with a key for this run only.
            Problem = "The AudioVault account key could not be saved, so stored accounts will be lost when Jellyfin restarts. Check that Jellyfin can write to its config folder.";
            _logger.LogError(ex, "Could not create the AudioVault account key {Path}", keyPath);
            _key = RandomNumberGenerator.GetBytes(32);
        }

        try
        {
            if (File.Exists(_path))
            {
                (_accounts, _generations) = Parse(File.ReadAllText(_path));
                GiveLegacyAccountsGenerations();
            }
        }
        catch (Exception ex)
        {
            Problem = "The stored AudioVault accounts file was damaged, so every user has to add their account again. The old file was kept with a .bad ending.";
            _logger.LogError(ex, "The stored AudioVault accounts {Path} can't be read; setting the file aside and starting without accounts", _path);
            Quarantine(_path);
            _accounts = [];
            _generations = [];
        }
    }

    /// <summary>Why stored accounts were lost or may be lost, for the settings page; null when all is well.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// Set by the host: whether the Jellyfin user exists and is enabled. A deleted or disabled user's account authorises
    /// no downloads, whatever tickets or intents still name it; a disabled user's account is kept for when they return.
    /// </summary>
    public Func<Guid, bool>? OwnerIsActive { get; set; }

    /// <summary>Whether this user may download now (exists and isn't disabled). False if that can't be checked.</summary>
    public bool IsActive(Guid userId)
    {
        try
        {
            return OwnerIsActive?.Invoke(userId) ?? true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check whether Jellyfin user {User} is still active", userId);
            return false;
        }
    }

    /// <summary>The account this user may play with now: none while the user is disabled or deleted.</summary>
    public SourceAccount? GetForPlayback(Guid userId) => IsActive(userId) ? Get(userId) : null;

    /// <summary>Serialises changes to one user's account (sign-in, replacement, removal) across requests.</summary>
    public async Task<IDisposable> LockAsync(Guid userId, CancellationToken ct)
    {
        var gate = _userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(gate);
    }

    public SourceAccount? Get(Guid userId)
    {
        lock (_lock)
        {
            if (!_accounts.TryGetValue(userId, out var stored))
            {
                return null;
            }

            try
            {
                return new SourceAccount(stored.Login, Decrypt(stored.Protected, userId)) { Generation = _generations.GetValueOrDefault(userId), Owner = OwnerOf(userId) };
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                _logger.LogWarning("The stored AudioVault account of user {User} can't be read (key changed or old format); it has to be added again", userId);
                return null;
            }
        }
    }

    public StoredAccount? Describe(Guid userId)
    {
        lock (_lock)
        {
            return _accounts.GetValueOrDefault(userId);
        }
    }

    /// <summary>
    /// Whether this snapshot is still its owner's saved account: same login and the same generation. Anything older (the
    /// account was removed, or its credentials replaced, even with the same email) is not, even while another user has
    /// the same login saved. A snapshot without an owner is never current, nor one whose owner was deleted or disabled.
    /// </summary>
    public bool IsCurrent(SourceAccount account)
    {
        if (!Guid.TryParseExact(account.Owner, "N", out var owner) || !IsActive(owner))
        {
            return false;
        }

        lock (_lock)
        {
            return _accounts.TryGetValue(owner, out var stored)
                && _generations.GetValueOrDefault(owner) == account.Generation
                && string.Equals(new SourceAccount(stored.Login, string.Empty).Key, account.Key, StringComparison.Ordinal);
        }
    }

    /// <summary>Saves the account under a new generation; it is in use only once it is on disk.</summary>
    public SourceAccount Set(Guid userId, string login, SourceSignIn signIn)
    {
        lock (_lock)
        {
            var candidate = new Dictionary<Guid, StoredAccount>(_accounts)
            {
                [userId] = new StoredAccount(login.Trim(), Encrypt(signIn.Credential, userId), signIn.ContainsPassword, DateTimeOffset.UtcNow),
            };
            var generations = NextGeneration(userId);
            Save(candidate, generations);
            _accounts = candidate;
            _generations = generations;
            return new SourceAccount(login.Trim(), signIn.Credential) { Generation = generations[userId], Owner = OwnerOf(userId) };
        }
    }

    /// <summary>Deletes the account; its generation moves on, so nothing bound to it (tickets, intents, queued downloads) counts any more.</summary>
    public void Remove(Guid userId)
    {
        lock (_lock)
        {
            if (!_accounts.ContainsKey(userId))
            {
                return;
            }

            var candidate = new Dictionary<Guid, StoredAccount>(_accounts);
            candidate.Remove(userId);
            var generations = NextGeneration(userId);
            Save(candidate, generations);
            _accounts = candidate;
            _generations = generations;
        }
    }

    /// <summary>
    /// Users with a stored account who no longer exist in Jellyfin. Nothing while Jellyfin reports no users at all
    /// (not loaded yet), so a startup hiccup can't remove everyone's account.
    /// </summary>
    public IReadOnlyList<Guid> Orphans(IReadOnlyCollection<Guid> existingUsers)
    {
        if (existingUsers.Count == 0)
        {
            return [];
        }

        lock (_lock)
        {
            return [.. _accounts.Keys.Where(u => !existingUsers.Contains(u))];
        }
    }

    /// <summary>
    /// Parses accounts.json in either format. Entries without the fields every account has are dropped (they can't be
    /// used anyway) rather than failing the whole file.
    /// </summary>
    public static (Dictionary<Guid, StoredAccount> Accounts, Dictionary<Guid, long> Generations) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Dictionary<Guid, StoredAccount>? accounts;
        Dictionary<Guid, long>? generations = null;
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(nameof(StoredAccounts.Accounts), out _))
        {
            var file = doc.RootElement.Deserialize<StoredAccounts>();
            accounts = file?.Accounts;
            generations = file?.Generations;
        }
        else
        {
            accounts = doc.RootElement.Deserialize<Dictionary<Guid, StoredAccount>>();
        }

        return (
            (accounts ?? []).Where(a => a.Value is { Login: not null, Protected: not null }).ToDictionary(a => a.Key, a => a.Value),
            generations ?? []);
    }

    /// <summary>The owner a user's account snapshots carry (<see cref="SourceAccount.Owner"/>).</summary>
    public static string OwnerOf(Guid userId) => userId.ToString("N");

    // Accounts saved before generations existed all have generation 0, so snapshots of different users (or of a removed
    // and re-added account) can't be told apart. Each gets a unique one, saved once.
    private void GiveLegacyAccountsGenerations()
    {
        var legacy = _accounts.Keys.Where(u => _generations.GetValueOrDefault(u) == 0).OrderBy(u => u).ToList();
        if (legacy.Count == 0)
        {
            return;
        }

        var next = Math.Max(_generations.Values.DefaultIfEmpty(0).Max() + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var generations = new Dictionary<Guid, long>(_generations);
        foreach (var userId in legacy)
        {
            generations[userId] = next++;
        }

        _generations = generations;
        try
        {
            Save(_accounts, _generations);
        }
        catch (Exception ex)
        {
            // Still unique for this run; it is tried again on the next start.
            _logger.LogError(ex, "Could not save the new generations of the stored AudioVault accounts to {Path}", _path);
        }
    }

    // Above every generation handed out so far, to any user, so no two savings share one. Never below the current time
    // in milliseconds either, so a generation stays new even if the file with the old ones was lost (set aside as
    // damaged) and a ticket from before still exists.
    private Dictionary<Guid, long> NextGeneration(Guid userId) => new(_generations)
    {
        [userId] = Math.Max(_generations.Values.DefaultIfEmpty(0).Max() + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
    };

    // The user id is bound as associated data, so an entry can't be moved to another user.
    private string Encrypt(string plaintext, Guid userId)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + plain.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(NonceSize, plain.Length), output.AsSpan(NonceSize + plain.Length), userId.ToByteArray());
        return Convert.ToBase64String(output);
    }

    private string Decrypt(string protectedValue, Guid userId)
    {
        var input = Convert.FromBase64String(protectedValue);
        var cipherLength = input.Length - NonceSize - TagSize;
        if (cipherLength < 0)
        {
            throw new CryptographicException("Stored credential is truncated");
        }

        var plain = new byte[cipherLength];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize, cipherLength), input.AsSpan(NonceSize + cipherLength), plain, userId.ToByteArray());
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[]? ReadKey(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var key = Convert.FromBase64String(File.ReadAllText(path).Trim());
        return key.Length == 32 ? key : throw new CryptographicException($"The key has {key.Length} bytes instead of 32");
    }

    private static byte[] CreateKey(string path)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        PrivateFile.Write(path, Convert.ToBase64String(key));
        return key;
    }

    private void Quarantine(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, $"{path}.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bad", overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not set {Path} aside", path);
        }
    }

    private void Save(Dictionary<Guid, StoredAccount> accounts, Dictionary<Guid, long> generations) =>
        PrivateFile.Write(_path, JsonSerializer.Serialize(new StoredAccounts(accounts, generations)));

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
