# Changelog

## 0.1.0 (beta)

Initial release.

- Indexes the AudioVault catalogue of audio-described movies and TV seasons as two Jellyfin libraries of `.strm` files, so every title can be searched and browsed.
- Pressing play downloads that one title with the user's own AudioVault account, converts it to an MP4 with a black video track, and caches it for every user on the server. A movie streams while it converts.
- Each Jellyfin user adds their own AudioVault login on a separate account page. Logins are stored as encrypted sign-in tokens, and admins can't see them.
- Keeps to AudioVault's rules: a daily download limit per account (default 20, at most 25), one download connection per account, and no downloads nobody asked for. A player's queue downloads nothing by itself, except one next item after an item that was actually listened to.
- Versions of a movie (languages, text-to-speech, regional cuts) are grouped under one movie with a version picker.
- Show seasons get their episode lists from TMDb or from the downloaded season.
- Recently Added follows AudioVault's upload order.
- A setup checklist on the settings page walks through the playback address, libraries, catalogue sync, Jellyfin matching, episode lists and your login.
- Explains blocked plays (no login, daily limit used up, download failures) in a dialog after the player closes.
- Optional FlareSolverr support for when Cloudflare blocks the server.
