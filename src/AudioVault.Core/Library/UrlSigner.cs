using System.Security.Cryptography;
using System.Text;

namespace AudioVault.Core.Library;

public sealed class UrlSigner(string secret)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(secret);

    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public string Sign(string entryId, int? episode)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{entryId}|{episode}"));
        return Base64Url(mac.AsSpan(0, 16));
    }

    public bool Verify(string entryId, int? episode, string? signature)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Sign(entryId, episode)),
            Encoding.ASCII.GetBytes(signature));
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
