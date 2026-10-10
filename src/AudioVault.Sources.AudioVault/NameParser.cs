using System.Globalization;
using System.Text.RegularExpressions;
using AudioVault.Core.Sources;

namespace AudioVault.Sources.AudioVault;

/// <summary>
/// Parses AudioVault's free-form names, e.g. "Breaking Bad - Season 1 (2008)",
/// "28 Days Later [TTS] (2002)", "Money Heist - Season 5 Part 1 (2021) [Spanish Description]".
/// </summary>
internal static partial class NameParser
{
    public const string DefaultLanguage = "English";

    private static readonly HashSet<string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Afrikaans", "Arabic", "Basque", "Bengali", "Bulgarian", "Catalan", "Chinese", "Croatian", "Czech", "Danish",
        "Dutch", "English", "Estonian", "Filipino", "Finnish", "Flemish", "French", "Galician", "German", "Greek",
        "Gujarati", "Hebrew", "Hindi", "Hungarian", "Icelandic", "Indonesian", "Italian", "Japanese", "Kannada",
        "Korean", "Latvian", "Lithuanian", "Malay", "Malayalam", "Marathi", "Norwegian", "Persian", "Polish",
        "Portuguese", "Punjabi", "Romanian", "Russian", "Serbian", "Slovak", "Slovenian", "Spanish", "Swahili",
        "Swedish", "Tagalog", "Tamil", "Telugu", "Thai", "Turkish", "Ukrainian", "Urdu", "Vietnamese", "Welsh",
    };

    private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Brazilian"] = "Portuguese",
        ["Cantonese"] = "Chinese",
        ["Castilian"] = "Spanish",
        ["Farsi"] = "Persian",
        ["Mandarin"] = "Chinese",
    };

    // What is left of a tag once its language and TTS qualifiers are taken out, and says nothing more.
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        string.Empty, "Description", "Audio Description", "AD", "Version",
    };

    public static SourceEntry Parse(string id, MediaKind kind, string rawName)
    {
        var raw = Normalize(rawName);
        var work = raw;

        var tags = new List<string>();
        work = Bracket().Replace(work, m =>
        {
            tags.Add(m.Groups[1].Value.Trim());
            return " ";
        });

        int? year = null;
        string trailing = string.Empty;
        var ym = ParenYear().Match(work);
        if (ym.Success)
        {
            year = int.Parse(ym.Groups["year"].Value, CultureInfo.InvariantCulture);
            trailing = work[(ym.Index + ym.Length)..].Trim();
            work = work[..ym.Index];
        }
        else if (kind == MediaKind.Movie
                 && BareYear().Match(work) is { Success: true } bm
                 && int.Parse(bm.Groups["year"].Value, CultureInfo.InvariantCulture) <= DateTime.UtcNow.Year + 1)
        {
            year = int.Parse(bm.Groups["year"].Value, CultureInfo.InvariantCulture);
            work = work[..bm.Index];
        }

        int? season = null, part = null;
        if (kind == MediaKind.Show)
        {
            if (SeasonPattern().Match(work) is { Success: true } sm)
            {
                season = int.Parse(sm.Groups["season"].Value, CultureInfo.InvariantCulture);
                if (sm.Groups["part"].Success)
                {
                    part = int.Parse(sm.Groups["part"].Value, CultureInfo.InvariantCulture);
                }

                work = work[..sm.Index];
            }
            else if (PartAsSeason().Match(work) is { Success: true } pm)
            {
                // e.g. "Money Heist - Part 3 (2019)": streaming "parts" map to seasons on TMDB.
                season = int.Parse(pm.Groups["part"].Value, CultureInfo.InvariantCulture);
                work = work[..pm.Index];
            }
        }
        else if (trailing.Length > 0)
        {
            tags.Add(trailing);
        }

        var language = DefaultLanguage;
        var isTts = false;
        var versionTags = new List<string>();
        foreach (var tag in tags.Where(t => t.Length > 0))
        {
            // Qualifiers can be combined in one tag, e.g. "Italian TTS Description" or "TTS Old Description".
            string? tagLanguage = null;
            var tagTts = false;
            var rest = Word().Replace(tag, m =>
            {
                var word = m.Value;
                if (word.Equals("TTS", StringComparison.OrdinalIgnoreCase))
                {
                    tagTts = true;
                    return " ";
                }

                if (LanguageOf(word) is { } lang)
                {
                    tagLanguage ??= lang;
                    return " ";
                }

                return word;
            });

            isTts |= tagTts;
            if (tagLanguage is not null && language == DefaultLanguage)
            {
                language = tagLanguage;
            }

            if (!tagTts && tagLanguage is null)
            {
                versionTags.Add(TitleCase(tag));
                continue;
            }

            rest = Normalize(rest).Trim('-', ',', '/', '&', '+', ':', ' ');
            if (!Filler.Contains(rest))
            {
                versionTags.Add(TitleCase(rest));
            }
        }

        var title = CleanTitle(work);
        return new SourceEntry(
            id,
            kind,
            title.Length > 0 ? title : raw,
            year,
            season,
            part,
            new VersionInfo(language, isTts, versionTags),
            rawName.Trim());
    }

    private static string? LanguageOf(string word) =>
        Languages.TryGetValue(word, out var name) ? name
        : LanguageAliases.TryGetValue(word, out var alias) ? alias
        : null;

    private static string Normalize(string value) => Whitespace().Replace(value, " ").Trim();

    private static string CleanTitle(string value)
    {
        var title = value.Replace("_ ", ": ", StringComparison.Ordinal);
        var aka = title.IndexOf(" AKA ", StringComparison.OrdinalIgnoreCase);
        if (aka > 0)
        {
            title = title[..aka];
        }

        return Normalize(title).TrimEnd('-', ':', ',', ' ').Trim();
    }

    private static string TitleCase(string value) =>
        value.Length > 0 && char.IsLower(value[0])
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant())
            : value;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\[([^\]}]*)[\]}]")]
    private static partial Regex Bracket();

    // "(2008)", "(2008", "12(2021)": last parenthesised 4-digit year, closing paren optional.
    [GeneratedRegex(@"\(\s*(?<year>(?:18|19|20)\d{2})\s*(?:\)|$)", RegexOptions.RightToLeft)]
    private static partial Regex ParenYear();

    [GeneratedRegex(@"(?<=\S)\s+(?<year>(?:19|20)\d{2})\s*$")]
    private static partial Regex BareYear();

    [GeneratedRegex(@"(?:\s*-)?\s*\bseasons?\s*(?<season>\d{1,3})(?:\s*part\s*(?<part>\d{1,2}))?\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonPattern();

    [GeneratedRegex(@"\s+-\s+part\s*(?<part>\d{1,2})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PartAsSeason();

    [GeneratedRegex(@"[A-Za-z]+")]
    private static partial Regex Word();
}
