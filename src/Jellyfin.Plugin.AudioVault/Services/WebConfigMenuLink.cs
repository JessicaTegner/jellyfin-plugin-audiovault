using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>Adds the account page to the custom side-menu links jellyfin-web reads from its config.json.</summary>
public static class WebConfigMenuLink
{
    public const string Name = "AudioVault account";
    public const string Icon = "manage_accounts";
    public const string AccountPath = "/AudioVault/Account";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Jellyfin's base URL ("", "/jellyfin") in the form it prefixes request paths with.</summary>
    public static string NormalizeBasePath(string? baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().Trim('/');
        return trimmed.Length == 0 ? string.Empty : "/" + trimmed;
    }

    public static bool IsConfigJsonPath(string requestPath, string basePath) =>
        string.Equals(requestPath, basePath + "/web/config.json", StringComparison.OrdinalIgnoreCase);

    public static string AccountUrl(string basePath) => basePath + AccountPath;

    /// <summary>The config with the link appended to menuLinks, or null if the config isn't the expected shape.</summary>
    public static string? AddLink(string configJson, string url)
    {
        try
        {
            if (JsonNode.Parse(configJson) is not JsonObject root)
            {
                return null;
            }

            JsonArray links;
            switch (root["menuLinks"])
            {
                case null:
                    links = [];
                    root["menuLinks"] = links;
                    break;
                case JsonArray existing:
                    links = existing;
                    break;
                default:
                    return null;
            }

            var present = links.Any(l => l is JsonObject o
                && o["url"] is JsonValue v
                && v.TryGetValue<string>(out var u)
                && string.Equals(u, url, StringComparison.OrdinalIgnoreCase));
            if (!present)
            {
                links.Add(new JsonObject { ["name"] = Name, ["icon"] = Icon, ["url"] = url });
            }

            return root.ToJsonString(Indented);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
