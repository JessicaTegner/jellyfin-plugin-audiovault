namespace AudioVault.Core.Library;

public sealed record LibraryOptions
{
    public required string MoviesPath { get; init; }

    public required string ShowsPath { get; init; }

    public string PreferredLanguage { get; init; } = "English";

    public bool IncludeOtherLanguages { get; init; } = true;

    public bool IncludeTts { get; init; } = true;
}
