# Privacy

What the AudioVault plugin stores, who can see it, and what it sends where.

## What is stored, and where

### AudioVault logins

- When a user saves their login on the account page, the plugin signs in to AudioVault once with the email and password. It then stores AudioVault's "remember me" sign-in token, not the password.
- If AudioVault ever stops issuing that token, the plugin stores the password instead. The account page then tells the user so.
- The token (or password) is encrypted with AES-GCM and stored with the email in `accounts.json`, in the `audiovault` folder of Jellyfin's data folder. Each entry is tied to its Jellyfin user, so it can't be moved to another user.
- The encryption key is a separate file, `audiovault-accounts.key`, in Jellyfin's config folder. A copy of the data file or a backup without the key reveals nothing on its own. Anyone with full access to the server can still decrypt the logins.
- On Linux and macOS, both files are readable only by the account Jellyfin runs as.

### Other data

- Download history (`downloads.json` in the same folder): the AudioVault email, title ID and time of each download, used for the daily limit. Entries older than 24 hours are dropped.
- The catalogue, episode lists and library files hold only AudioVault's public title information.
- The cache holds converted titles, shared by all users. It doesn't record who played what.
- The Jellyfin log includes AudioVault email addresses in some lines, for example when the plugin signs in, signs out or starts a download. Passwords and tokens are never logged.

## Who can see what

- Each user can see and change only their own login on the account page: the email, how many downloads they have used in the last 24 hours, and whether a password is stored instead of a token.
- Admins can't see or change other users' logins. The settings page only shows counts: how many Jellyfin users have added a login and the total downloads across all accounts. An admin's own email appears in their own setup checklist.
- Logins are not kept in the plugin's settings, which Jellyfin hands to any admin through its API. Nothing in Jellyfin's API returns another user's login, and no endpoint returns a password or token.
- Admins with access to the Jellyfin log can see the email addresses mentioned above.

## What is sent to AudioVault, and when

All requests come from the Jellyfin server's IP address. Requests to AudioVault are spaced 1 to 2 seconds apart across all users. If AudioVault asks the server to slow down, every request pauses for as long as it asks, or 15 minutes.

- Sign-in checks: when a user saves their login (a sign-in, no download), and when a download needs a fresh sign-in.
- Catalogue sync: AudioVault's public listing pages, without signing in. A full sync reads every movie and show listing page once a week. Recent uploads are read from the home page every 6 hours.
- "Check AudioVault connection" on the settings page reads the first movie listing page.
- Downloads: only when someone presses play on a title that isn't cached, with that user's own account. One exception: when a player's queue moves on from an item that was actually listened to (at least 10 minutes long, played to the end and not skipped through), the next item may download once, with that user's account. A season counts as one download.
- Sign-out: when a user clears their login, when they replace it with a different email, and when a Jellyfin user is deleted while "Sign out of AudioVault when a Jellyfin user is deleted" is on.

## What is sent to TMDb

The plugin looks up runtimes and season episode lists through Jellyfin's own TMDb client, with Jellyfin's API key and cache. These requests contain TMDb IDs, season and episode numbers and the library language. If that client isn't available, the plugin asks Jellyfin's metadata providers instead.

Artwork and descriptions come from Jellyfin's own metadata providers, as for any library: TMDb, and OMDb if you chose it when creating the libraries.

## FlareSolverr

FlareSolverr is used only if you set its URL and only when Cloudflare challenges a request. The plugin then sends it AudioVault's home page address. No login, email or token is sent to FlareSolverr.

## Clearing a login, deleting a user, and signing out

- Clearing on the account page ("Clear my stored AudioVault details from this server") deletes the stored login, cancels that user's waiting downloads, and asks AudioVault to sign it out. Signing out ends "remember me" on every device using that AudioVault account. The page says whether AudioVault confirmed it.
- Deleting a Jellyfin user deletes their stored login and cancels their waiting downloads. It is signed out at AudioVault only if "Sign out of AudioVault when a Jellyfin user is deleted" is on (off by default). Logins of users deleted while the plugin wasn't running are removed shortly after it next starts.
- A disabled user's login is kept, but downloads nothing until the user is enabled again.
- Changing the AudioVault password cancels the stored token. The user then sees "Your AudioVault sign-in has expired" and enters their login again.
- If the key file is lost or replaced, stored logins can't be read. The old files are set aside with a `.bad` ending, and the pages still offer to clear them. AudioVault can't be asked to sign those out, so change the AudioVault password to be sure.

## The diagnostic report

The "Copy diagnostic report" button on the settings page produces a plain-text report for GitHub issues.

It contains:

- plugin, Jellyfin, .NET and operating system versions;
- the setup checklist, compatibility results, health checks and warnings;
- counts: titles, library files, items in Jellyfin, downloads in the last 24 hours, cache size;
- the shape of the settings: what kind of playback address (for example "https, public name"), not the address itself; yes/no options, language and bitrate;
- task states and times;
- recent download and conversion failures, as a category (for example "disk full" or "archive entry outside its folder"), the error's type name and any HTTP status. The error's own text is left out.

The plugin's own messages (warnings, setup details) are included as text. Before they go in, the report removes:

- known titles from the catalog, user names, device names, AudioVault logins and the server's addresses;
- full and relative paths, from the start of the path to the end of the clause;
- URLs (only the scheme and kind of address remain), host names, IP addresses and emails;
- tokens, passwords and signatures;
- anything else in quotes.

This works by recognising patterns, so it can miss something or remove too much. Read the report before posting it.

## No telemetry

The plugin doesn't collect usage data or contact its developers. The only outside services it contacts are AudioVault, FlareSolverr if you set it up, and TMDb through Jellyfin's own client. Jellyfin's metadata providers work as they do for any library.

The plugin also contacts this Jellyfin server at its own playback address, to check that the address works. The settings page does the same from the admin's browser.
