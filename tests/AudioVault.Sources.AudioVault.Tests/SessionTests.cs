using AudioVault.Core.Sources;

namespace AudioVault.Sources.AudioVault.Tests;

/// <summary>An account's shared session changing credentials while requests and downloads are still using it.</summary>
public class SessionTests
{
    private static async Task<SourceAccount> SignInAsync(AudioVaultSource source) =>
        new(FakeLaravel.Email, (await source.SignInAsync(FakeLaravel.Email, FakeLaravel.Password, default)).Credential);

    /// <summary>The same AudioVault account with a different stored credential, as after signing in again.</summary>
    private static SourceAccount WithPassword() =>
        new(FakeLaravel.Email, new AudioVaultCredential(null, FakeLaravel.Password).Serialize());

    private static int Count(List<FakeLaravel> sites)
    {
        lock (sites)
        {
            return sites.Count;
        }
    }

    [Fact]
    public async Task ReplacingTheCredentialWaitsForARequestStillWaitingForHeaders()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var (source, sites) = SignInTests.Create(intercept: async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath != "/download/1")
            {
                return null;
            }

            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return FakeLaravel.Audio();
        });
        var account = await SignInAsync(source);
        var pending = source.OpenAsync("1", account, default);
        await entered.Task;
        var original = sites[^1];

        // The new credential's first use replaces the session while the old one's request is still out.
        await using (await source.OpenAsync("2", WithPassword(), default))
        {
            Assert.False(original.Disposed);
            release.SetResult();
            var media = await pending;
            Assert.False(original.Disposed);
            await media.DisposeAsync();
        }

        Assert.True(original.Disposed);
    }

    [Fact]
    public async Task SignInDoesNotReplaceTheSharedSession()
    {
        var (source, sites) = SignInTests.Create();
        var account = await SignInAsync(source);
        await using var media = await source.OpenAsync("1", account, default);
        var shared = sites[^1];

        await SignInAsync(source);
        var created = Count(sites);
        await using var again = await source.OpenAsync("2", account, default);

        Assert.False(shared.Disposed);
        Assert.Equal(created, Count(sites));
        Assert.Equal(2, shared.Requests.Count(r => r.StartsWith("GET /download/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ReadinessOnlyLooks()
    {
        var (source, sites) = SignInTests.Create();
        var account = await SignInAsync(source);
        await using var media = await source.OpenAsync("1", account, default);
        var shared = sites[^1];
        var created = Count(sites);

        Assert.True(source.GetReadiness(WithPassword()).Ok);
        Assert.True(source.GetReadiness(new SourceAccount("other@example.com", account.Credential)).Ok);
        Assert.False(source.GetReadiness(new SourceAccount(FakeLaravel.Email, "not a credential")).Ok);

        Assert.Equal(created, Count(sites));
        Assert.False(shared.Disposed);
    }

    [Fact]
    public async Task SignOutLeavesAnOpenDownloadReadable()
    {
        var (source, sites) = SignInTests.Create();
        var account = await SignInAsync(source);
        var media = await source.OpenAsync("1", account, default);
        var shared = sites[^1];

        await source.SignOutAsync(account, default);

        Assert.False(shared.Disposed);
        Assert.Equal(3, (await new StreamReader(media.Content).ReadToEndAsync()).Length);
        await media.DisposeAsync();
        Assert.True(shared.Disposed);
    }

    [Fact]
    public async Task AlternatingCredentialsUnderLoadNeverDisposeABusySession()
    {
        var (source, sites) = SignInTests.Create(intercept: async (_, _) =>
        {
            await Task.Yield();
            return null;
        });
        var token = await SignInAsync(source);
        var password = WithPassword();

        await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
        {
            await using var media = await source.OpenAsync(i.ToString(System.Globalization.CultureInfo.InvariantCulture), i % 2 == 0 ? token : password, default);
            await Task.Yield();
            Assert.Equal(3, (await new StreamReader(media.Content).ReadToEndAsync()).Length);
        }));

        // Only the session in use now is left; every replaced one was disposed once its downloads finished.
        lock (sites)
        {
            Assert.Single(sites, s => !s.Disposed);
        }
    }

    [Fact]
    public async Task UsersWithTheSameCredentialsShareOneSession()
    {
        var (source, sites) = SignInTests.Create();
        var alice = await SignInAsync(source) with { Generation = 1, Owner = "alice" };
        await using var media = await source.OpenAsync("1", alice, default);
        var shared = sites[^1];
        var created = Count(sites);

        // Bob saved the same credential: another generation and owner of the host, but the same AudioVault session.
        await using var next = await source.OpenAsync("2", alice with { Generation = 2, Owner = "bob" }, default);

        Assert.Equal(created, Count(sites));
        Assert.False(shared.Disposed);
        Assert.Equal(2, shared.Requests.Count(r => r.StartsWith("GET /download/", StringComparison.Ordinal)));
    }

    private static SourceAccount WrongPassword(long generation, string owner) =>
        new(FakeLaravel.Email, new AudioVaultCredential(null, "wrong").Serialize()) { Generation = generation, Owner = owner };

    private static int Logins(List<FakeLaravel> sites)
    {
        lock (sites)
        {
            return sites.Sum(s => s.Requests.Count(r => r == "POST /login"));
        }
    }

    [Fact]
    public async Task ARejectedLoginPausesEveryoneUsingThoseCredentials()
    {
        var (source, sites) = SignInTests.Create();
        var alice = WrongPassword(1, "alice");
        var bob = WrongPassword(2, "bob");

        await Assert.ThrowsAsync<DownloadNotStartedException>(() => source.OpenAsync("1", alice, default));
        Assert.Equal(1, Logins(sites));

        // Alternating users must not take turns retrying the rejected password.
        Assert.False(source.GetReadiness(bob).Ok);
        Assert.Contains("rejected", source.GetReadiness(bob).Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<DownloadNotStartedException>(() => source.OpenAsync("2", bob, default));
        Assert.False(source.GetReadiness(alice).Ok);
        await Assert.ThrowsAsync<DownloadNotStartedException>(() => source.OpenAsync("3", alice, default));
        Assert.Equal(1, Logins(sites));
    }

    [Fact]
    public async Task ARejectedLoginOutlivesItsSession()
    {
        var (source, sites) = SignInTests.Create();
        var alice = WrongPassword(1, "alice");
        await Assert.ThrowsAsync<DownloadNotStartedException>(() => source.OpenAsync("1", alice, default));

        // Signing out drops the session; a new one for the same credential still waits out the pause.
        await source.SignOutAsync(alice, default);
        await Assert.ThrowsAsync<DownloadNotStartedException>(() => source.OpenAsync("2", alice with { Generation = 2 }, default));
        Assert.Equal(1, Logins(sites));
    }

    [Fact]
    public async Task ANewCredentialMayTryAfterARejectedOne()
    {
        var (source, sites) = SignInTests.Create();
        await Assert.ThrowsAsync<DownloadNotStartedException>(() => source.OpenAsync("1", WrongPassword(1, "alice"), default));

        var corrected = WithPassword() with { Generation = 2, Owner = "alice" };
        Assert.True(source.GetReadiness(corrected).Ok);
        await using var media = await source.OpenAsync("2", corrected, default);
        Assert.Equal(2, Logins(sites));
    }
}
