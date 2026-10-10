using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioVault.Sources.AudioVault;

/// <summary>
/// What gets stored for an AudioVault account: preferably Laravel's "remember me" cookie, so the password itself
/// is never kept. Only if AudioVault issues no such cookie does it fall back to the password.
/// </summary>
internal sealed record AudioVaultCredential(
    [property: JsonPropertyName("t")] AudioVaultCredential.RememberToken? Token,
    [property: JsonPropertyName("pw")] string? Password)
{
    public const string RememberCookiePrefix = "remember_web_";

    public static AudioVaultCredential? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AudioVaultCredential>(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static AudioVaultCredential? FromCookies(CookieContainer cookies) =>
        cookies.GetAllCookies()
            .Where(c => c.Name.StartsWith(RememberCookiePrefix, StringComparison.Ordinal) && !c.Expired)
            .Select(c => new AudioVaultCredential(new RememberToken(c.Name, c.Value, c.Domain, c.Path, c.Expires == DateTime.MinValue ? null : c.Expires, c.Secure), null))
            .FirstOrDefault();

    public bool IsUsable => Token is not null || !string.IsNullOrEmpty(Password);

    public string Serialize() => JsonSerializer.Serialize(this);

    public sealed record RememberToken(
        [property: JsonPropertyName("n")] string Name,
        [property: JsonPropertyName("v")] string Value,
        [property: JsonPropertyName("d")] string Domain,
        [property: JsonPropertyName("p")] string Path,
        [property: JsonPropertyName("e")] DateTime? Expires,
        [property: JsonPropertyName("s")] bool? Secure = null)
    {
        /// <param name="https">An https site never gets the token over plain http, whatever the site set.</param>
        public Cookie ToCookie(bool https) =>
            new(Name, Value, Path, Domain) { Expires = Expires ?? DateTime.MinValue, Secure = https || Secure == true };
    }
}
