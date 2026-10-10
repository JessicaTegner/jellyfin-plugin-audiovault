using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AudioVault.Core.Sources;

namespace AudioVault.Sources.AudioVault;

internal sealed record Clearance(string UserAgent, IReadOnlyList<Cookie> Cookies);

internal sealed class FlareSolverrClient(HttpClient http)
{
    public async Task<Clearance> SolveAsync(string flareSolverrUrl, string url, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
                flareSolverrUrl.TrimEnd('/') + "/v1",
                new { cmd = "request.get", url, maxTimeout = 60000 },
                ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<Response>(ct).ConfigureAwait(false);
        if (body is not { Status: "ok", Solution: { } solution })
        {
            throw new SourceUnavailableException($"FlareSolverr failed: {body?.Message}");
        }

        return new Clearance(
            solution.UserAgent,
            solution.Cookies.Select(c => new Cookie(c.Name, c.Value, c.Path ?? "/", c.Domain)).ToList());
    }

    private sealed record Response(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("solution")] Solution? Solution);

    private sealed record Solution(
        [property: JsonPropertyName("userAgent")] string UserAgent,
        [property: JsonPropertyName("cookies")] List<SolutionCookie> Cookies);

    private sealed record SolutionCookie(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("domain")] string Domain,
        [property: JsonPropertyName("path")] string? Path);
}
