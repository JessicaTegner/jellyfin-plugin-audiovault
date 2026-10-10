using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// User messages mark links as [text](url). jellyfin-web based clients render dialog text as (sanitized) HTML and get a
/// real link; other clients (Android TV shows a plain toast) get "text (url)".
/// </summary>
public static partial class MessageLinks
{
    public static string Link(string text, string url) => $"[{text}]({url})";

    public static string ToPlainText(string message) => LinkPattern().Replace(message, "$1 ($2)");

    public static string ToHtml(string message)
    {
        var html = new StringBuilder();
        var last = 0;
        foreach (Match m in LinkPattern().Matches(message))
        {
            html.Append(WebUtility.HtmlEncode(message[last..m.Index]))
                .Append("<a href=\"").Append(WebUtility.HtmlEncode(m.Groups[2].Value)).Append("\">")
                .Append(WebUtility.HtmlEncode(m.Groups[1].Value)).Append("</a>");
            last = m.Index + m.Length;
        }

        return html.Append(WebUtility.HtmlEncode(message[last..])).ToString();
    }

    [GeneratedRegex(@"\[([^\[\]]+)\]\((https?://[^\s()]+)\)")]
    private static partial Regex LinkPattern();
}
