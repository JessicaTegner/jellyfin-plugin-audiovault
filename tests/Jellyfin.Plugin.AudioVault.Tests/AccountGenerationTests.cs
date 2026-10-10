using System.Text.Json;
using AudioVault.Core.Media;
using AudioVault.Core.Sources;
using Jellyfin.Plugin.AudioVault.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AudioVault.Tests;

public sealed class AccountGenerationTests : IDisposable
{
    private const string Secret = "0123456789ABCDEF";
    private static readonly Guid Alice = Guid.Parse("8d3c1f8a-7a43-4b55-9f0e-2c7d1f0b9a11");
    private static readonly Guid Bob = Guid.Parse("1b2c3d4e-0000-4000-8000-000000000002");
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("av-accounts-");

    public void Dispose() => _dir.Delete(recursive: true);

    private AccountStore Store() =>
        new(Path.Combine(_dir.FullName, "data"), Path.Combine(_dir.FullName, "config"), NullLogger<AccountStore>.Instance);

    private static PlaybackTicket TicketFor(Guid user, SourceAccount? account) =>
        new PlaybackTicket(user, account?.Key ?? string.Empty, "12345", null, "session-1", DateTimeOffset.UtcNow + PlaybackTicket.Lifetime)
        {
            Generation = account?.Generation ?? 0,
        };

    [Fact]
    public void TicketForTheCurrentAccountAuthorizes()
    {
        var store = Store();
        var saved = store.Set(Alice, "alice@example.com", new SourceSignIn("token-1", false));

        Assert.Equal(saved, store.Get(Alice));
        Assert.True(TicketFor(Alice, store.Get(Alice)).Authorizes(store.Get(Alice)));
        Assert.True(store.IsCurrent(saved));
    }

    [Fact]
    public void ReplacingWithTheSameEmailVoidsOldTickets()
    {
        var store = Store();
        var old = store.Set(Alice, "alice@example.com", new SourceSignIn("token-1", false));
        var ticket = TicketFor(Alice, old);

        var replaced = store.Set(Alice, "Alice@Example.com", new SourceSignIn("token-2", false));

        Assert.Equal(old.Key, replaced.Key);
        Assert.NotEqual(old.Generation, replaced.Generation);
        Assert.False(ticket.Authorizes(store.Get(Alice)));
        Assert.Null(PlayAttempt.AccountFor(ticket, store.Get(Alice), out var stale));
        Assert.True(stale);
        Assert.False(store.IsCurrent(old));
        Assert.True(store.IsCurrent(replaced));
    }

    [Fact]
    public void RemovingAndAddingTheSameEmailAgainVoidsOldTickets()
    {
        var store = Store();
        var old = store.Set(Alice, "alice@example.com", new SourceSignIn("token-1", false));
        var ticket = TicketFor(Alice, old);

        store.Remove(Alice);
        Assert.Null(PlayAttempt.AccountFor(ticket, store.Get(Alice), out var staleAfterRemoval));
        Assert.False(staleAfterRemoval);
        Assert.False(store.IsCurrent(old));

        store.Set(Alice, "alice@example.com", new SourceSignIn("token-1", false));
        Assert.False(ticket.Authorizes(store.Get(Alice)));
        Assert.False(store.IsCurrent(old));
    }

    [Fact]
    public void GenerationsSurviveARestartAndRemoval()
    {
        var first = Store();
        var old = first.Set(Alice, "alice@example.com", new SourceSignIn("token-1", false));
        first.Remove(Alice);

        var restarted = Store();
        var again = restarted.Set(Alice, "alice@example.com", new SourceSignIn("token-1", false));

        Assert.True(again.Generation > old.Generation);
        Assert.False(TicketFor(Alice, old).Authorizes(restarted.Get(Alice)));
        Assert.Equal(again, Store().Get(Alice));
    }

    [Fact]
    public void ChangingOneUserLeavesAnotherUsersTicketsAlone()
    {
        var store = Store();
        var bob = store.Set(Bob, "bob@example.com", new SourceSignIn("b", false));
        var ticket = TicketFor(Bob, bob);

        store.Set(Alice, "alice@example.com", new SourceSignIn("a", false));
        store.Remove(Alice);

        Assert.True(ticket.Authorizes(store.Get(Bob)));
        Assert.True(store.IsCurrent(bob));
    }

    // Writes accounts.json the way versions before generations did: just the map, both users sharing one login.
    private (AccountStore Store, SourceAccount Alice, SourceAccount Bob) LegacyUsersSharingALogin()
    {
        var store = Store();
        store.Set(Alice, "shared@example.com", new SourceSignIn("a", false));
        store.Set(Bob, "shared@example.com", new SourceSignIn("b", false));
        var path = Path.Combine(_dir.FullName, "data", "accounts.json");
        var (accounts, _) = AccountStore.Parse(File.ReadAllText(path));
        File.WriteAllText(path, JsonSerializer.Serialize(accounts));

        var restarted = Store();
        return (restarted, restarted.Get(Alice)!, restarted.Get(Bob)!);
    }

    [Fact]
    public void LegacyAccountsGetUniqueGenerationsOnce()
    {
        var (store, alice, bob) = LegacyUsersSharingALogin();

        Assert.NotEqual(0, alice.Generation);
        Assert.NotEqual(0, bob.Generation);
        Assert.NotEqual(alice.Generation, bob.Generation);
        Assert.True(store.IsCurrent(alice));
        Assert.True(store.IsCurrent(bob));

        // Saved: another start keeps them, so tickets issued since still count.
        var again = Store();
        Assert.Equal(alice.Generation, again.Get(Alice)!.Generation);
        Assert.Equal(bob.Generation, again.Get(Bob)!.Generation);
    }

    [Fact]
    public void RemovingOneUserOfASharedLoginRevokesOnlyTheirSnapshots()
    {
        var (store, alice, bob) = LegacyUsersSharingALogin();
        var ticket = TicketFor(Alice, alice);

        store.Remove(Alice);

        Assert.False(store.IsCurrent(alice));
        Assert.True(store.IsCurrent(bob));
        Assert.Null(PlayAttempt.AccountFor(ticket, store.Get(Alice), out _));
        Assert.False(ticket.Authorizes(store.Get(Alice)));
        Assert.True(TicketFor(Bob, bob).Authorizes(store.Get(Bob)));
    }

    [Fact]
    public void SnapshotIsCheckedAgainstItsOwnerOnly()
    {
        var store = Store();
        var alice = store.Set(Alice, "shared@example.com", new SourceSignIn("a", false));
        var bob = store.Set(Bob, "shared@example.com", new SourceSignIn("b", false));

        // Same login and generation as Bob's, but someone else's (or nobody's): not current.
        Assert.False(store.IsCurrent(bob with { Owner = Alice.ToString("N") }));
        Assert.False(store.IsCurrent(bob with { Owner = null }));
        Assert.Equal(Alice.ToString("N"), alice.Owner);
        Assert.Equal(alice, store.Get(Alice));
    }

    [Fact]
    public void DeletedOrDisabledUsersIssuedTicketAuthorizesNothing()
    {
        var store = Store();
        var active = new HashSet<Guid> { Alice, Bob };
        store.OwnerIsActive = active.Contains;
        var alice = store.Set(Alice, "shared@example.com", new SourceSignIn("a", false));
        var bob = store.Set(Bob, "shared@example.com", new SourceSignIn("b", false));
        var ticket = TicketFor(Alice, alice);
        Assert.Equal(alice, PlayAttempt.AccountFor(ticket, store.GetForPlayback(Alice), out _));

        // Disabled (or deleted): the ticket finds no account, and the final check before opening refuses the snapshot.
        active.Remove(Alice);

        Assert.Null(PlayAttempt.AccountFor(ticket, store.GetForPlayback(Alice), out _));
        Assert.False(store.IsCurrent(alice));
        Assert.True(store.IsCurrent(bob));

        // A disabled user's account is kept and counts again once they are enabled.
        Assert.Equal(alice, store.Get(Alice));
        active.Add(Alice);
        Assert.True(store.IsCurrent(alice));
    }

    [Fact]
    public void FailingUserCheckAuthorizesNothing()
    {
        var store = Store();
        var alice = store.Set(Alice, "alice@example.com", new SourceSignIn("a", false));
        store.OwnerIsActive = _ => throw new InvalidOperationException("users not loaded");

        Assert.False(store.IsCurrent(alice));
        Assert.Null(store.GetForPlayback(Alice));
    }

    [Fact]
    public void OrphanedAccountsAreThoseOfUsersThatNoLongerExist()
    {
        var store = Store();
        store.Set(Alice, "alice@example.com", new SourceSignIn("a", false));
        store.Set(Bob, "bob@example.com", new SourceSignIn("b", false));

        Assert.Equal([Alice], store.Orphans([Bob, Guid.NewGuid()]));

        // Jellyfin reporting no users at all means they aren't loaded, not that everyone was deleted.
        Assert.Empty(store.Orphans([]));
    }

    [Fact]
    public void OwnerIsNotPartOfAccountEquality()
    {
        // Users sharing a login share the source session, which is replaced whenever the account record differs.
        var a = new SourceAccount("shared@example.com", "token") { Generation = 5, Owner = Alice.ToString("N") };
        var b = a with { Owner = Bob.ToString("N") };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a with { Generation = 6 });
        Assert.NotEqual(a, a with { Credential = "other" });
    }

    [Fact]
    public void TicketWithoutAnAccountNeverAuthorizes()
    {
        var store = Store();
        var ticket = TicketFor(Alice, null);
        store.Set(Alice, "alice@example.com", new SourceSignIn("a", false));

        Assert.False(ticket.Authorizes(store.Get(Alice)));
    }

    [Fact]
    public void ReadsTheOlderFileFormatWithGenerationZero()
    {
        var stored = new StoredAccount("alice@example.com", "cipher", false, DateTimeOffset.UnixEpoch);
        var json = JsonSerializer.Serialize(new Dictionary<Guid, StoredAccount> { [Alice] = stored });

        var (accounts, generations) = AccountStore.Parse(json);

        Assert.Equal(stored, accounts[Alice]);
        Assert.Empty(generations);
    }

    [Fact]
    public void DropsEntriesThatCantBeAccounts()
    {
        var json = "{\"Accounts\":{\"" + Alice + "\":null,\"" + Bob + "\":{\"Login\":\"bob@example.com\",\"Protected\":\"x\",\"ContainsPassword\":false,\"UpdatedAt\":\"2026-01-01T00:00:00Z\"}},\"Generations\":{\"" + Bob + "\":7}}";

        var (accounts, generations) = AccountStore.Parse(json);

        Assert.Equal([Bob], accounts.Keys);
        Assert.Equal(7, generations[Bob]);
    }

    [Fact]
    public void UnreadableCredentialsAreStoredButNotUsable()
    {
        var data = Path.Combine(_dir.FullName, "data");
        Store().Set(Alice, "alice@example.com", new SourceSignIn("a", false));

        // Another key: the stored entry can no longer be decrypted.
        File.WriteAllText(Path.Combine(_dir.FullName, "config", "audiovault-accounts.key"), Convert.ToBase64String(new byte[32]));
        var store = Store();

        Assert.NotNull(store.Describe(Alice));
        Assert.Null(store.Get(Alice));
        Assert.True(Directory.Exists(data));
    }

    [Fact]
    public void TicketCarriesGenerationAndVersion()
    {
        var item = Guid.NewGuid();
        var version = Guid.NewGuid();
        var ticket = TicketFor(Alice, new SourceAccount("alice@example.com", "a") { Generation = 1_700_000_000_123 }) with
        {
            ItemId = item,
            VersionId = version,
            Expires = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600),
        };

        var read = PlaybackTicket.Read(Secret, ticket.Create(Secret), "12345", null, DateTimeOffset.UtcNow);

        Assert.Equal(ticket, read);
        Assert.Equal(1_700_000_000_123, read!.Generation);
        Assert.Equal(version, read.VersionId);
    }
}

public class PlayAttemptTests
{
    private static readonly SourceEntry Entry = new("1", MediaKind.Movie, "Film", 2020, null, null, VersionInfo.Default("English"), "Film");

    [Theory]
    [InlineData(true, false, HeadAnswer.Ready)]
    [InlineData(true, true, HeadAnswer.Ready)]
    [InlineData(false, true, HeadAnswer.Live)]
    [InlineData(false, false, HeadAnswer.Placeholder)]
    public void HeadNeverPrepares(bool cached, bool running, HeadAnswer expected) =>
        Assert.Equal(expected, PlayAttempt.Head(cached, running));

    [Fact]
    public void AllowedPlayHasNoMessage() =>
        Assert.Null(PlayAttempt.BlockedMessage(new PreflightResult(PreflightStatus.Ok, null, true, false, Entry), false, 0, 25, DateTimeOffset.UtcNow, "u"));

    [Fact]
    public void MissingFfmpegIsExplainedAsAServerProblem()
    {
        var blocked = new PreflightResult(PreflightStatus.ConversionUnavailable, "ffmpeg not found", true, false, Entry);

        Assert.Equal(UserMessages.ConversionUnavailable, PlayAttempt.BlockedMessage(blocked, false, 0, 25, DateTimeOffset.UtcNow, "u"));
        Assert.Equal(UserMessages.ConversionUnavailable, UserMessages.Describe(new ConversionUnavailableException("x")));
        Assert.Contains("ffmpeg", UserMessages.ConversionUnavailable);
    }

    [Fact]
    public void StaleTicketSaysTheAccountChangedInsteadOfMissing()
    {
        var noAccount = new PreflightResult(PreflightStatus.NoAccount, null, true, false, Entry);

        Assert.Equal(UserMessages.AccountChanged, PlayAttempt.BlockedMessage(noAccount, true, 0, 25, DateTimeOffset.UtcNow, "u"));
        Assert.Contains("haven't added your AudioVault account", PlayAttempt.BlockedMessage(noAccount, false, 0, 25, DateTimeOffset.UtcNow, "u"));
    }
}
