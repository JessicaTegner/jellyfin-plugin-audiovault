# Jellyfin AudioVault plugin

This Jellyfin plugin adds the [AudioVault](https://audiovault.net) catalogue of audio-described movies and TV seasons to Jellyfin as two libraries, so every title can be searched and browsed without downloading anything. When someone presses play, the plugin downloads that one title with their own AudioVault account, converts it to an MP4 with a black video track and caches it for everyone on the server.

Not affiliated with or endorsed by AudioVault. You use your own AudioVault account and are responsible for following AudioVault's rules.

Status: beta. Expect rough edges, and please report problems.

## Requirements

- Jellyfin 12.2.
- Disk space for the cache of converted titles. Each title played is kept; you can set a size limit.

## Install

1. In Jellyfin, open Dashboard → Plugins → Repositories and add `https://jessicategner.github.io/jellyfin-plugin-audiovault/manifest.json`.
2. Open the plugin catalogue and install AudioVault.
3. Restart Jellyfin.

Then open Dashboard → Plugins → AudioVault and follow the setup checklist.

## Documentation

- [Admin guide](docs/admin-guide.md): setup, settings, accounts, troubleshooting and uninstalling.
- [Privacy](docs/privacy.md): what is stored, who can see it and what is sent where.
- [Changelog](CHANGELOG.md)
- [Development](docs/development.md): building, testing and the project layout.

## Support

The plugin is free. If you'd like to support its development: [GitHub Sponsors](https://github.com/sponsors/JessicaTegner) or [PayPal](https://paypal.me/JessicaTegner).

AudioVault runs on donations too: [support AudioVault](https://www.paypal.me/audiodescribed).

## Licence

GPL-3.0. See [LICENSE](LICENSE).

The plugin includes [AngleSharp](https://github.com/AngleSharp/AngleSharp), which is under the MIT licence.
