using Jellyfin.Plugin.AudioVault.Services;

namespace Jellyfin.Plugin.AudioVault.Tests;

public sealed class PublicUrlRulesTests
{
    private const string Fallback = "http://172.17.0.2:8096";

    private static PublishedUriSettings Published(params string[] entries) =>
        new(null, entries, string.Empty, false, 8096, 8920);

    [Fact]
    public void PluginFieldWinsOverEverything()
    {
        var choice = PublicUrlRules.Choose(" https://av.example.com/ ", Published("all=https://jf.example.com"), "https://detected.example.com", Fallback);
        Assert.Equal("https://av.example.com", choice.Url);
        Assert.Equal(PublicUrlSource.Plugin, choice.Source);
        Assert.Null(choice.Problem);
    }

    [Theory]
    [InlineData("http://localhost:8096")]
    [InlineData("http://127.0.0.1:8096")]
    [InlineData("http://0.0.0.0:8096")]
    [InlineData("jellyfin.example.com")]
    public void PluginFieldIsUsedButWarnedAboutWhenUnusable(string url)
    {
        var choice = PublicUrlRules.Choose(url, Published(), null, Fallback);
        Assert.Equal(PublicUrlSource.Plugin, choice.Source);
        Assert.NotNull(choice.Problem);
    }

    [Fact]
    public void AllPublishedUriIsUsedAsWritten()
    {
        var choice = PublicUrlRules.Choose("", Published("192.168.1.0/24=http://192.168.1.5:8096", "all=https://jf.example.com/"), "https://detected.example.com", Fallback);
        Assert.Equal("https://jf.example.com", choice.Url);
        Assert.Equal(PublicUrlSource.Published, choice.Source);
        Assert.Null(choice.Problem);
    }

    [Fact]
    public void ExternalPublishedUriBeatsDetected()
    {
        var choice = PublicUrlRules.Choose(null, Published("internal=http://192.168.1.5:8096", "external=https://jf.example.com"), "http://192.168.1.5:8096", Fallback);
        Assert.Equal("https://jf.example.com", choice.Url);
    }

    [Fact]
    public void StartupPublishedUrlReplacesTheList()
    {
        var s = Published("all=https://jf.example.com") with { StartupUrl = "https://startup.example.com/" };
        Assert.Equal("https://startup.example.com", PublicUrlRules.Choose(null, s, null, Fallback).Url);
    }

    [Fact]
    public void SubnetOnlyEntriesLoseToDetectedButBeatFallback()
    {
        var s = Published("192.168.1.0/24=http://192.168.1.5:8096");
        Assert.Equal("https://detected.example.com", PublicUrlRules.Choose(null, s, "https://detected.example.com/", Fallback).Url);

        var choice = PublicUrlRules.Choose(null, s, null, Fallback);
        Assert.Equal("http://192.168.1.5:8096", choice.Url);
        Assert.Equal(PublicUrlSource.Published, choice.Source);
    }

    [Fact]
    public void BareHostGetsSchemePortAndBaseUrl()
    {
        var s = new PublishedUriSettings(null, ["all=jf.example.com"], "/jellyfin", false, 8096, 8920);
        Assert.Equal("http://jf.example.com:8096/jellyfin", PublicUrlRules.Choose(null, s, null, Fallback).Url);

        var https = s with { BySubnet = ["all=jf.example.com:443"], ListenWithHttps = true, BaseUrl = "jellyfin/" };
        // Same as Jellyfin's GetLocalApiUrl, which also uses UriBuilder.
        Assert.Equal("https://jf.example.com:443/jellyfin", PublicUrlRules.Choose(null, https, null, Fallback).Url);
    }

    [Fact]
    public void FullUrlIsNotGivenTheBaseUrlAgain()
    {
        var s = new PublishedUriSettings(null, ["all=https://jf.example.com/jellyfin"], "/jellyfin", false, 8096, 8920);
        Assert.Equal("https://jf.example.com/jellyfin", PublicUrlRules.Choose(null, s, null, Fallback).Url);
    }

    [Fact]
    public void LoopbackPublishedUriIsSkipped()
    {
        var choice = PublicUrlRules.Choose(null, Published("all=http://localhost:8096"), "https://detected.example.com", Fallback);
        Assert.Equal("https://detected.example.com", choice.Url);
        Assert.Equal(PublicUrlSource.Detected, choice.Source);
    }

    [Fact]
    public void UnparsableEntryStopsTheList()
    {
        var (everyone, some) = PublicUrlRules.PublishedUrls(Published("nonsense", "all=https://jf.example.com"));
        Assert.Empty(everyone);
        Assert.Empty(some);
    }

    [Fact]
    public void DetectedIsUsedWithoutPublishedUri()
    {
        var choice = PublicUrlRules.Choose("  ", Published(), "https://jf.example.com/jellyfin/", Fallback);
        Assert.Equal("https://jf.example.com/jellyfin", choice.Url);
        Assert.Equal(PublicUrlSource.Detected, choice.Source);
        Assert.Null(choice.Problem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://localhost:8096")]
    [InlineData("http://127.0.0.1:8096")]
    [InlineData("http://[::1]:8096")]
    [InlineData("ftp://jf.example.com")]
    public void NothingUsableFallsBackWithAProblem(string? detected)
    {
        var choice = PublicUrlRules.Choose(null, Published(), detected, Fallback + "/");
        Assert.Equal(Fallback, choice.Url);
        Assert.Equal(PublicUrlSource.Fallback, choice.Source);
        Assert.Contains("Couldn't detect", choice.Problem);
    }

    [Theory]
    [InlineData("http://localhost:8096")]
    [InlineData("http://0.0.0.0:8096")]
    [InlineData("http://[::]:8096")]
    [InlineData("not a url")]
    [InlineData("/relative")]
    public void ProblemRejectsUnusableAddresses(string url) => Assert.NotNull(PublicUrlRules.Problem(url));

    [Theory]
    [InlineData("http://192.168.1.5:8096")]
    [InlineData("https://jf.example.com/jellyfin")]
    public void ProblemAcceptsReachableLookingAddresses(string url) => Assert.Null(PublicUrlRules.Problem(url));

    [Fact]
    public void NormalizeDropsQueryFragmentAndTrailingSlash() =>
        Assert.Equal("https://jf.example.com/jellyfin", PublicUrlRules.Normalize("https://jf.example.com/jellyfin/?x=1#y"));

    [Theory]
    [InlineData(null, "http://192.168.1.5:8096", true)]
    [InlineData("", "http://192.168.1.5:8096", true)]
    [InlineData("http://localhost:8096", "http://192.168.1.5:8096", true)]
    [InlineData("http://192.168.1.5:8096", "http://192.168.1.5:8096/", false)]
    [InlineData("http://192.168.1.5:8096", "https://jf.example.com", true)]
    [InlineData("https://jf.example.com", "http://192.168.1.5:8096", false)]
    [InlineData("https://jf.example.com", "https://other.example.com", true)]
    [InlineData("http://192.168.1.5:8096", "http://localhost:8096", false)]
    public void ReplacingTheDetectedAddress(string? stored, string candidate, bool expected) =>
        Assert.Equal(expected, PublicUrlRules.ShouldReplaceDetected(stored, candidate));

    [Theory]
    [InlineData("https://jf.example.com/jellyfin?x=1")]
    [InlineData("https://jf.example.com/jellyfin#top")]
    [InlineData("https://jf.example.com/?")]
    [InlineData("https://user:secret@jf.example.com")]
    public void ProblemRejectsQueriesFragmentsAndUserInfo(string url) => Assert.NotNull(PublicUrlRules.Problem(url));

    [Theory]
    [InlineData("https://user:secret@jf.example.com/jellyfin", "https://jf.example.com/jellyfin")]
    [InlineData("https://jf.example.com//jellyfin//", "https://jf.example.com/jellyfin")]
    [InlineData("https://JF.example.com:443/", "https://jf.example.com")]
    [InlineData("http://jf.example.com:8096/a/../jellyfin", "http://jf.example.com:8096/jellyfin")]
    public void NormalizeKeepsOnlySchemeHostPortAndPath(string url, string expected) =>
        Assert.Equal(expected, PublicUrlRules.Normalize(url));

    [Fact]
    public void ExplicitUrlWithQueryIsReportedAndLinksUseTheBase()
    {
        var choice = PublicUrlRules.Choose("https://jf.example.com/jellyfin/?x=1#y", Published(), null, Fallback);

        Assert.Equal("https://jf.example.com/jellyfin", choice.Url);
        Assert.Equal(PublicUrlSource.Plugin, choice.Source);
        Assert.Contains("query", choice.Problem);
    }

    [Theory]
    [InlineData("http://my-mac-mini:8096", "bare machine name")]
    [InlineData("http://jellyfin.local:8096", ".local name")]
    [InlineData("https://jf.tail1234.ts.net", "Tailscale MagicDNS name")]
    [InlineData("http://192.168.1.5:8096", "private network address")]
    [InlineData("http://10.0.0.2:8096", "private network address")]
    [InlineData("http://[fd00::5]:8096", "private network address")]
    public void AddressesOnlySomeDevicesReachGetAdvice(string url, string expected)
    {
        var advice = PublicUrlRules.Advice(url, othersMayPlay: true, []);
        Assert.NotNull(advice);
        Assert.Contains(expected, advice);
        Assert.Contains("may not", advice);
    }

    [Theory]
    [InlineData("https://jf.example.com")]
    [InlineData("http://100.64.0.7:18096")]
    [InlineData("http://203.0.113.5:8096")]
    [InlineData("http://localhost:8096")]
    public void UniversalOrUnusableAddressesGetNoAdvice(string url) =>
        Assert.Null(PublicUrlRules.Advice(url, othersMayPlay: true, []));

    [Fact]
    public void PrivateAddressIsFineWhenOnlyTheAdminPlays() =>
        Assert.Null(PublicUrlRules.Advice("http://192.168.1.5:8096", othersMayPlay: false, []));

    [Fact]
    public void AdviceSuggestsAMoreUniversalKnownAddress()
    {
        var advice = PublicUrlRules.Advice(
            "http://jf.tail1234.ts.net:8096",
            othersMayPlay: false,
            [null, "http://jf.tail1234.ts.net:8096/", "http://jellyfin.local:8096", "http://100.64.0.7:8096"]);
        Assert.NotNull(advice);
        Assert.EndsWith("http://100.64.0.7:8096 may work on more devices.", advice);
    }

    [Fact]
    public void AdviceWithoutAnAlternativeGivesAHint()
    {
        var advice = PublicUrlRules.Advice("https://jf.tail1234.ts.net", othersMayPlay: false, []);
        Assert.Contains("100.x.y.z", advice);
    }

    [Theory]
    [InlineData("192.168.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("169.254.1.1", true)]
    [InlineData("100.100.1.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:10.1.2.3", true)]
    public void PrivateRanges(string ip, bool expected) =>
        Assert.Equal(expected, PublicUrlRules.IsPrivate(System.Net.IPAddress.Parse(ip)));

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("100.63.0.1", false)]
    public void TailscaleRange(string ip, bool expected) =>
        Assert.Equal(expected, PublicUrlRules.IsTailscale(System.Net.IPAddress.Parse(ip)));
}
