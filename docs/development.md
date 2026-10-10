# Development

## Requirements

- .NET 10 SDK.
- `ffmpeg` and `ffprobe` on `PATH` for the conversion tests.
- A Jellyfin 12.2 server to try the plugin on.

## Building

```sh
dotnet build -c Release
dotnet publish src/Jellyfin.Plugin.AudioVault -c Release -o artifacts/plugin
```

The build should give 0 warnings.

## Tests

```sh
dotnet test
```

- Tests that run ffmpeg are reported as skipped when `ffmpeg` or `ffprobe` isn't on `PATH`.
- Set `AUDIOVAULT_REQUIRE_FFMPEG=1` to make them fail instead. Release builds do this; `scripts/package.sh` sets it.
- The tests never contact AudioVault. Parser tests use invented pages shaped like AudioVault's, in `tests/AudioVault.Sources.AudioVault.Tests/Fixtures`, and sign-in and download tests use a fake site on the local machine. See [Parser fixtures](parser-fixtures.md) for updating them.

## Trying it on a test server

1. Publish the plugin as above.
2. Copy the contents of `artifacts/plugin` into a folder named `AudioVault_<version>` in the test server's plugins folder.
3. Restart Jellyfin, then open Dashboard → Plugins → AudioVault.

Use a test server of your own, not a shared one, and keep its address, accounts and data out of the repository. Every play of an uncached title spends one of your AudioVault account's daily downloads.

## Project layout

| Project | Knows about |
|---|---|
| `src/AudioVault.Core` | The `IContentSource` contract, catalogue, `.strm` layout, download limit, ffmpeg conversion and cache. Nothing about Jellyfin or AudioVault. |
| `src/AudioVault.Sources.AudioVault` | Everything specific to audiovault.net: page parsing, name parsing, sign-in, the Cloudflare fallback and the site's limits. |
| `src/Jellyfin.Plugin.AudioVault` | Jellyfin wiring: the settings and account pages, scheduled tasks, the stream endpoint, library refreshes and user messages. |

Each project has a test project under `tests/`, with `.Tests` added to its name.

### Only Sources.AudioVault knows the site

Everything about audiovault.net (its addresses, page layout, title names, sign-in and limits) belongs in `AudioVault.Sources.AudioVault`. The other projects only use the `IContentSource` contract. If the site changes, that is the only project to touch.

If AudioVault's pages stop parsing, the source throws `SourceFormatChangedException` instead of returning an empty catalogue, so a site change can never look like every title was removed.

## Releasing

See [Releasing](releasing.md).
