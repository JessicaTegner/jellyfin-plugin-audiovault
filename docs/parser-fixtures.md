# Parser fixtures

The parser tests in `tests/AudioVault.Sources.AudioVault.Tests` read pages in `Fixtures/`. These are invented pages with the same structure as AudioVault's and made-up titles. Pages saved from audiovault.net are never committed: they are AudioVault's content.

`scripts/make_fixtures.py` writes the pages, the same way every run. `PackagingTests` fails if the committed pages differ from what it writes.

| Fixture | Shaped like |
|---|---|
| `home.html` | `https://audiovault.net/` (the recent uploads tables) |
| `movies_page1.html` | `https://audiovault.net/movies` |
| `shows_page1.html` | `https://audiovault.net/shows` |
| `login.html` | `https://audiovault.net/login` |
| `names.txt` | Invented titles in every name shape seen on AudioVault, for the name parser |

## When AudioVault changes its pages

The plugin stops syncing and the settings page says the layout changed.

1. Find the page that fails. The settings page's message, the failed task's error in the Jellyfin log, or the *AudioVault changed* issue usually says which one: listing pages for a full sync, the home page for recent uploads, the login page for sign-in.
2. Open that page in a browser and view its source. Don't script downloads from the site, and don't commit the page.
3. Change the templates in `scripts/make_fixtures.py` to match the new markup, then run `python3 -I scripts/make_fixtures.py`.
4. Run the parser tests: `dotnet test tests/AudioVault.Sources.AudioVault.Tests`. They should now fail the way the plugin does.
5. Fix `src/AudioVault.Sources.AudioVault` (usually `ListingParser.cs`) until they pass. The checks that unexpected layouts throw `SourceFormatChangedException` must stay.
6. Add a test for what changed, for example a small HTML snippet in the old and new shape, so the next change is caught too.

When the name parser misreads a title, add an invented title of the same shape to `names.txt`.
