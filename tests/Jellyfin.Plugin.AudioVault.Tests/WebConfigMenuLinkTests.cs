using System.Text.Json.Nodes;
using Jellyfin.Plugin.AudioVault.Services;

namespace Jellyfin.Plugin.AudioVault.Tests;

public class WebConfigMenuLinkTests
{
    private static JsonArray Links(string json) => JsonNode.Parse(json)!["menuLinks"]!.AsArray();

    [Fact]
    public void Adds_link_and_creates_menuLinks_when_missing()
    {
        var result = WebConfigMenuLink.AddLink("""{ "multiserver": false }""", "/AudioVault/Account")!;

        var link = Assert.Single(Links(result));
        Assert.Equal("AudioVault account", (string?)link!["name"]);
        Assert.Equal("manage_accounts", (string?)link["icon"]);
        Assert.Equal("/AudioVault/Account", (string?)link["url"]);
        Assert.False((bool)JsonNode.Parse(result)!["multiserver"]!);
    }

    [Fact]
    public void Keeps_existing_links_first()
    {
        const string Config = """{ "menuLinks": [ { "name": "Wiki", "url": "https://wiki.example" } ] }""";

        var links = Links(WebConfigMenuLink.AddLink(Config, "/AudioVault/Account")!);

        Assert.Equal(["https://wiki.example", "/AudioVault/Account"], links.Select(l => (string?)l!["url"]));
    }

    [Fact]
    public void Is_idempotent()
    {
        var once = WebConfigMenuLink.AddLink("{}", "/jellyfin/AudioVault/Account")!;

        Assert.Single(Links(WebConfigMenuLink.AddLink(once, "/jellyfin/AudioVault/Account")!));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "menuLinks": "oops" }""")]
    [InlineData("")]
    public void Returns_null_for_unexpected_config(string config) =>
        Assert.Null(WebConfigMenuLink.AddLink(config, "/AudioVault/Account"));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("jellyfin", "/jellyfin")]
    [InlineData("/jellyfin/", "/jellyfin")]
    public void Normalizes_base_path(string? baseUrl, string expected) =>
        Assert.Equal(expected, WebConfigMenuLink.NormalizeBasePath(baseUrl));

    [Theory]
    [InlineData("/web/config.json", "", true)]
    [InlineData("/jellyfin/web/config.json", "/jellyfin", true)]
    [InlineData("/web/config.json", "/jellyfin", false)]
    [InlineData("/jellyfin/web/config.json", "", false)]
    [InlineData("/web/index.html", "", false)]
    public void Matches_config_json_under_base_path(string path, string basePath, bool expected) =>
        Assert.Equal(expected, WebConfigMenuLink.IsConfigJsonPath(path, basePath));

    [Fact]
    public void Account_url_includes_base_path() =>
        Assert.Equal("/jellyfin/AudioVault/Account", WebConfigMenuLink.AccountUrl("/jellyfin"));
}

public class MessageLinksTests
{
    private const string Message = "Open the [AudioVault account page](http://srv:8096/AudioVault/Account) & sign in <now>.";

    [Fact]
    public void Plain_text_keeps_the_url() =>
        Assert.Equal("Open the AudioVault account page (http://srv:8096/AudioVault/Account) & sign in <now>.", MessageLinks.ToPlainText(Message));

    [Fact]
    public void Html_links_and_escapes_the_rest() =>
        Assert.Equal(
            "Open the <a href=\"http://srv:8096/AudioVault/Account\">AudioVault account page</a> &amp; sign in &lt;now&gt;.",
            MessageLinks.ToHtml(Message));

    [Fact]
    public void Non_http_links_are_left_as_text() =>
        Assert.Equal("[x](javascript:alert(1))", MessageLinks.ToHtml("[x](javascript:alert(1))"));
}
