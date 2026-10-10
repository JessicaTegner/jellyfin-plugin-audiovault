namespace AudioVault.Sources.AudioVault;

public sealed record AudioVaultOptions
{
    public string BaseUrl { get; init; } = "https://audiovault.net";

    /// <summary>Optional, e.g. http://flaresolverr:8191. Only used when Cloudflare challenges a request.</summary>
    public string? FlareSolverrUrl { get; init; }

    /// <summary>Gap between any two requests to the site, picked at random in this range so the pace isn't mechanical.</summary>
    public TimeSpan MinRequestGap { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRequestGap { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Longest wait for a whole page, so a stalled connection can't hold a session forever.</summary>
    public TimeSpan PageTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Longest wait for a download's response headers; the body has its own inactivity limit downstream.</summary>
    public TimeSpan DownloadHeadersTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Where provider pauses are kept so they survive a restart; null keeps them in memory only.</summary>
    public string? StateDirectory { get; init; }

    public string UserAgent { get; init; } =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
}
