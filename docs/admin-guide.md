# Admin guide

This guide is for the person running the Jellyfin server. For what the plugin stores and sends, see [Privacy](privacy.md).

## Contents

1. [Installing and updating](#installing-and-updating)
2. [The setup checklist](#the-setup-checklist)
3. [Choosing the playback address](#choosing-the-playback-address)
4. [Accounts](#accounts)
5. [AudioVault's rules and the daily limit](#audiovaults-rules-and-the-daily-limit)
6. [Settings reference](#settings-reference)
7. [Troubleshooting](#troubleshooting)
8. [Known limitations](#known-limitations)
9. [Uninstalling](#uninstalling)

## Installing and updating

The plugin needs Jellyfin 12.2.

### From the plugin repository (recommended)

1. Add the plugin repository:
   1. In Jellyfin, open the menu and choose Dashboard, under Administration. In the dashboard's menu, choose Plugins.
   2. Press "Manage Repositories", then "New Repository".
   3. Enter `AudioVault` as the Repository Name and `https://jessicategner.github.io/jellyfin-plugin-audiovault/manifest.json` as the Repository URL, then press "Add".
2. Install the plugin:
   1. Go back to Plugins. Search for AudioVault, or find it under "Available", and open it.
   2. Press "Install". Jellyfin warns that third-party plugins carry risks; press "Install" again to confirm.
3. Restart Jellyfin: open the Dashboard, press "Restart", and confirm.

Updates then appear in Jellyfin's plugin list like any other plugin. Install the update and restart Jellyfin.

### Side-loading

1. Unzip the release zip into Jellyfin's plugins folder, in a folder named `AudioVault_<version>`, for example `plugins/AudioVault_0.1.0.0/`.
2. Restart Jellyfin.

This works, but the plugin page shows a repository lookup error and "Unknown" for Developer and Repository. To update a side-loaded plugin, replace the folder with the new version's and restart.

## The setup checklist

Open Dashboard → Plugins → AudioVault using the address you normally use for Jellyfin. The Setup section at the top lists seven steps in order. Each step says whether it is done, in progress, waiting or has a problem, with one line of detail and, where there is one, a button or link for the next action. The checklist reflects the server's actual state and updates while the page is open; a step changing state is announced.

If every step is already done when you open the page, the checklist is collapsed to "Setup complete", and the "Show setup steps" button expands it. It never collapses by itself while the page is open: when the last step becomes done, "Setup complete" is announced and the steps stay where they are until you next open the page. If a step stops being done, the steps are shown again.

Where the plugin can measure it, the catalogue read and the library scan show their progress and a rough time left.

### 1. Playback address works

The library files contain playback links, and both Jellyfin and every player fetch them, so they need an address that works everywhere. The step shows the address in use and warns about problems. See [Choosing the playback address](#choosing-the-playback-address).

### 2. Libraries created

Press "Create AudioVault libraries in Jellyfin". This creates two libraries, AudioVault Movies and AudioVault Shows, with trickplay and chapter images turned off, since there is no picture.

Before pressing it, you can use "Options for the new libraries" to choose:

- OMDb as an extra metadata source for movies;
- which artwork to download: posters, backdrops, extra artwork, season images and episode images.

All are on by default, like a library created in Jellyfin. Turning image types off makes the first scan faster. You can change them later in Dashboard → Libraries → Manage library. Once both libraries exist, the options and the button are hidden behind "Create the libraries again".

### 3. Catalog synced

Press "Sync recent uploads now". The first run reads the whole catalogue, which takes about 10 minutes. The step then shows how many titles were found and when the catalogue was last read.

After that, a full sync runs once a week and recent uploads are picked up every 6 hours. Both are scheduled tasks under the AudioVault category in Dashboard → Scheduled Tasks.

### 4. Jellyfin matching titles

Jellyfin's library scan adds the titles and fetches their details. For the whole catalogue this takes hours, and titles appear in search gradually while it runs. This is normal.

The step shows how many movies and episodes Jellyfin has added and how many are searchable. It counts as done when all but 1% are searchable, because new uploads always wait for the next scan.

### 5. Episode lists

Until its episode list is found, a show season has only episode 1. This is normal. Lists are looked up automatically, mostly from TMDb, once Jellyfin has identified the show. The step shows how many seasons have their list and how many are waiting. It is done once every season has been looked up, even if TMDb had nothing for some of them yet; those are checked again every 12 hours.

If lookups fail, for example because TMDb can't be reached or times out, the step shows a problem. A season whose list may be incomplete because of such an error keeps only episode 1 until a lookup succeeds. Those seasons are tried again every hour, and the Jellyfin log has the errors.

### 6. Your AudioVault login

Your own AudioVault account, needed to play titles nobody has played yet. The step links to the account page and, if the server has several users, says how many Jellyfin users have added one.

### 7. Jellyfin compatibility

At startup the plugin checks that the parts of Jellyfin it relies on, beyond the official plugin interfaces, still work on the running version. It says "works, with limits" and what is limited if one doesn't (for example slower episode lists, or no side-menu link). It's a problem only when new titles can't play at all. Most limits mean a newer plugin version is needed.

## Choosing the playback address

### Why it matters

Every title in the libraries is a small `.strm` file holding a link to this server, such as `https://jellyfin.example.com/AudioVault/Stream/...`. Players open that link directly, and Jellyfin itself fetches it when it reads or transcodes a title. So the address must work from:

- every device anyone plays on, at home and away;
- the Jellyfin server itself.

Each file holds one address for all devices. If your devices reach Jellyfin by different addresses, use the one that works for all of them.

### How the address is chosen

The plugin picks the first of these that applies:

1. the "Public server URL" field on the settings page, if you fill it in;
2. Jellyfin's published server URI (Dashboard → Networking), if one applies to all clients;
3. the address you opened the settings page with, if it isn't localhost and the server can reach it;
4. a published server URI for some subnets only;
5. Jellyfin's local address, with a warning.

When the address changes, the library files are rewritten.

### Examples

Home network only:

- `http://192.168.1.20:8096` works if every device is always on the same network as the server. It fails for anyone playing away from home. With several Jellyfin users, the page notes that a private address may not suit everyone.

A domain behind a reverse proxy:

- `https://jellyfin.example.com` is usually the best choice when Jellyfin is reachable from the internet.
- The server must be able to reach its own domain too. If it can't (some routers don't loop back to a public address), add a local DNS entry for the domain on the server's network.

Tailscale:

- Use the Tailscale IP address, for example `http://100.x.y.z:8096`.
- A MagicDNS name such as `http://jellyfin.tailnet-name.ts.net:8096` works for devices in your own tailnet. Devices on another tailnet that the machine is shared with may not resolve `*.ts.net` names, so they can't play. Use the `100.x` address instead.
- For a plain-http `*.ts.net` address, the page suggests the `100.x` address the name resolves to on the server.
- An https `*.ts.net` address can't simply be swapped for the IP, because its certificate is only valid for the name.

### Checking the address

- "Test the playback address from this device" on the settings page loads a tiny image from the playback address and reports whether it reached this server. It also runs quietly each time the page opens. It only proves that this device can reach the address; other devices may differ. Each browser's result is its own: the checklist shows the result of the browser it is open in. A page opened over https can't load an http address, and the result says so.
- The server checks every 10 minutes that it can reach the address itself.
- When a player tries a direct play of an AudioVault title but never requests the stream within 90 seconds, the checklist reports it, for example: "A player on Living room TV (Jellyfin Android TV, sam) couldn't load the playback address". A player counts as trying when it reports that playback started, reports a failed load, or asks again with direct play turned off (as the web client does when a direct play fails). A play abandoned before that, or one Jellyfin transcodes, isn't reported. A later stream request from that device at that address clears the report; a request for an older link, or at another address, doesn't. Reports are kept for a day. See [Troubleshooting](#a-player-couldnt-load-the-playback-address).

The page also gives advice, not errors, for addresses that only some devices can use: a bare machine name, a `.local` name, a `*.ts.net` name, or a private address when there are several Jellyfin users.

## Accounts

Each Jellyfin user downloads with their own AudioVault account, so each person has their own daily limit. Converted titles are cached for everyone: once anyone has played a title, every user can play it, including users without an account.

- Users add their own login on the AudioVault account page, at `<playback address>/AudioVault/Account`, in a browser where they are signed in to Jellyfin. Jellyfin only shows plugin pages to admins, so this is a separate page.
- The web client's side menu has an "AudioVault account" link to it, unless you turn that off.
- Admins can't see or change other users' logins. The settings page only shows counts: how many Jellyfin users have added a login, and the total downloads in the last 24 hours across all accounts.
- Logins are checked with AudioVault before they are saved. The plugin keeps an encrypted AudioVault sign-in token, not the password.

If someone without an account presses play on a title that isn't cached, a dialog appears after the player closes, explaining how to add an account.

See [Privacy](privacy.md) for how logins are stored and what happens when they are cleared or a user is deleted.

## AudioVault's rules and the daily limit

AudioVault bans accounts that download more than 25 titles a day, use parallel connections, or try to archive the site. The plugin:

- downloads only titles someone pressed play on, with one exception: when a player's queue moves on from an item that was actually listened to (at least 10 minutes long, played to the end and not skipped through), the next item may download, once. A season counts as one download;
- limits each AudioVault account to 20 downloads in any 24 hours by default; you can set 1 to 25;
- counts a whole season as one download;
- counts only downloads made through this server. Downloads in a browser, another app or another server with the same AudioVault account are not seen. If your users also download elsewhere, set the limit well below 25;
- uses one download connection at a time per AudioVault account; different accounts can download at the same time;
- never switches to another account when one has used its limit.

## Settings reference

The settings are on Dashboard → Plugins → AudioVault, below the setup checklist and status.

### Connection

- FlareSolverr URL (optional): only used if Cloudflare blocks the server, for example `http://flaresolverr:8191`. Include `http://` or `https://`. See [Cloudflare blocking](#cloudflare-blocking).

### Playback

- Public server URL (leave empty to detect automatically): the address written into the library files. See [Choosing the playback address](#choosing-the-playback-address).
- Test the playback address from this device: a button that checks whether this device reaches this server at that address.
- Daily download limit per account (1 to 25): downloads per AudioVault account in any 24 hours. Default 20.
- Audio bitrate in kbps: the bitrate of the converted audio, 32 to 256. Default 96.

### Web client

- Show an "AudioVault account" link in the Jellyfin side menu: on by default. Users see the change after reloading the web client.

### Users

- Sign out of AudioVault when a Jellyfin user is deleted: off by default. A deleted user's stored login is always removed; with this on, it is also signed out at AudioVault, which ends "remember me" on every device using that AudioVault account.

### Versions and languages

- Preferred description language: default English. Versions in this language come first.
- Include titles described in other languages: on by default.
- Include text-to-speech descriptions: on by default.

Changing these rewrites the library files; titles they now leave out are removed from the libraries. A renamed file's old copy is removed only once its new one is written. If the files can't all be rewritten, or the library safety rail held back removals, the status section and the "Libraries created" step say so, and the plugin tries again, at first after a minute, then less often. Saving the settings again tries again at once.

### Folders

- Movies library folder (leave empty for default): default `audiovault/library/movies` in Jellyfin's data folder.
- Shows library folder (leave empty for default): default `audiovault/library/shows` in Jellyfin's data folder.
- Converted media cache folder (leave empty for default): default `audiovault` in Jellyfin's cache folder.
- Cache size limit in GB (0 for unlimited): default 0. Titles removed from the cache have to be downloaded again.
- Free space automatically when the disk is nearly full: on by default. Removes the least recently played titles nobody is playing; when off, new plays are refused until you free space.

Each folder must be a full path of its own, not overlapping the others or a system, home or Jellyfin folder. A folder that breaks these rules is not saved, and the page says why.

## Troubleshooting

When reporting a problem on [GitHub](https://github.com/JessicaTegner/jellyfin-plugin-audiovault/issues), include the diagnostic report: on the settings page, under "Diagnostic report", press "Copy diagnostic report" and paste it into the issue. It gives errors as categories, not their text, and removes names, emails, titles, addresses, paths and tokens where it recognises them. Read it before posting; see [Privacy](privacy.md#the-diagnostic-report).

### Nothing plays, or playback stops after a few seconds

When the plugin can't download a title, the player gets a silent placeholder or an error instead, so playback doesn't start or stops after a few seconds. A dialog explaining why appears once the player has closed. Common reasons:

- The user hasn't added an AudioVault login. They add it on the account page.
- The user's daily limit is used up. The dialog says when the next download is possible.
- The player can't reach the playback address. Check the "Playback address works" step.
- Cloudflare is blocking the server. See [Cloudflare blocking](#cloudflare-blocking).
- The player moved on to the next title in a queue by itself. A queue doesn't start downloads; pressing play on the title does, 10 seconds after the player has closed.
- Jellyfin's ffmpeg isn't available. The status section says so.

Press "Check AudioVault connection" in the status section to test whether the server can read AudioVault. The Jellyfin log has details of failed downloads and conversions.

### "A player couldn't load the playback address"

The named device was given a playback link but never fetched it. Usually that device can't resolve or reach the address:

- a `*.ts.net` name on a device in another tailnet: use the `100.x` address;
- a home network address on a device away from home: use a domain;
- an http address in a web client opened over https: browsers block it, so use an https address.

Change the "Public server URL" to an address that device can reach, then play again on that device. The report clears once the device fetches a stream.

### A show only has episode 1

Its season's episode list hasn't been found yet. Lists are looked up automatically once Jellyfin has identified the show, and the "Episode lists" step shows how many are waiting. To look up all waiting seasons now, run the "Resolve episode lists" scheduled task. Seasons TMDb has no list for keep episode 1 until someone plays it; the downloaded season then provides the list.

### Titles missing from search

- During the first library scan, titles appear gradually over several hours. Check the "Jellyfin matching titles" step.
- If a library scan was interrupted, some items can stay unprocessed. The plugin asks Jellyfin to finish them every 30 minutes; the "Repair unprocessed items" task does all of them at once.
- New uploads appear after the next recent-uploads sync (every 6 hours) and library scan. "Sync recent uploads now" fetches them sooner.
- Titles described in other languages or with text-to-speech are left out if those settings are off.

### Cloudflare blocking

The status shows "Cloudflare is blocking this server from AudioVault. Set a FlareSolverr URL in the plugin settings." The plugin then pauses all requests to AudioVault for 15 minutes.

Run [FlareSolverr](https://github.com/FlareSolverr/FlareSolverr) somewhere the server can reach, and enter its address in "FlareSolverr URL (optional)". FlareSolverr is only used when a request is challenged. The address:

- starts with `http://` or `https://`, for example `http://flaresolverr:8191` or `http://192.168.1.5:8191`;
- is how the Jellyfin server reaches FlareSolverr. If Jellyfin runs in Docker, `localhost` means Jellyfin's own container; use FlareSolverr's container name on a shared Docker network, or the host's address;
- doesn't end in `/v1`; the plugin adds that.

Cloudflare's clearance only works from the internet address FlareSolverr got it from, so FlareSolverr has to reach the internet the same way the Jellyfin server does, not through a different VPN or proxy.

A pause caused by the block ends as soon as a different FlareSolverr URL is saved. If you fix FlareSolverr without changing its address, the plugin tries again once the pause shown in the status ends.

#### Common FlareSolverr errors

Each status starts with "Cloudflare is blocking this server from AudioVault". The rest says what went wrong:

- "Set a FlareSolverr URL in the plugin settings." when no URL is set: set one, as above.
- "Set a FlareSolverr URL in the plugin settings." when a URL is already set: FlareSolverr got past the check, but Cloudflare still challenged the plugin's next request. Check that FlareSolverr and the Jellyfin server use the same internet address. If they do and it keeps happening, report it on [GitHub](https://github.com/JessicaTegner/jellyfin-plugin-audiovault/issues) with the diagnostic report.
- "FlareSolverr could not get past it", followed by:
  - "Connection refused", "Name or service not known" or another message about the connection: the server can't reach FlareSolverr at that address. Check FlareSolverr is running and the address follows the rules above.
  - "The 'localhost' scheme is not supported", or another name in place of `localhost`: the address is missing `http://`.
  - "404 (Not Found)" or another status code: something other than FlareSolverr answers at that address, or the address ends in `/v1`.
  - "FlareSolverr failed:" and FlareSolverr's own message: FlareSolverr couldn't solve the check. Update FlareSolverr and look in its log.
  - "HttpClient.Timeout of 90 seconds": FlareSolverr took too long. Restart it and look in its log.

### The account page says the sign-in expired

The account page message "Your Jellyfin sign-in in this browser has expired" is about Jellyfin, not AudioVault. Use the "Open Jellyfin and sign in" link, sign in, then return to the account page.

If a play dialog says "Your AudioVault sign-in has expired", the AudioVault password was probably changed, which cancels the stored token. The user enters their AudioVault email and password again on the account page.

### "AudioVault layout changed"

"Check AudioVault connection" reports "AudioVault layout changed, parser needs an update" when AudioVault's pages can no longer be read. The catalogue sync then fails without removing anything, and users who press play on a new title are told that the plugin needs an update. Titles already cached still play. Check for a plugin update, and report it on GitHub with the diagnostic report if there isn't one.

## Known limitations

- Built for Jellyfin 12.2.
- The "AudioVault account" side-menu link only exists in the Jellyfin web client. Users of other apps open the account page in a browser.
- A show season lists only episode 1 until its episode list is found.
- Each show season has one version, the best-ranked one (your preferred language first). Only movies get a version picker.
- Specials are left out.
- On the first play, the title streams while it is still converting, and seeking isn't possible until conversion has finished. Some players may not play a stream that is still being written; playing again once conversion has finished works.
- The first play of a season waits until the whole season has downloaded.
- No subtitles or chapters.

## Uninstalling

1. Ask users to clear their login on the account page first ("Clear my stored AudioVault details from this server"). That signs the login out at AudioVault. Deleting the files below doesn't.
2. Open Dashboard → Plugins → AudioVault, uninstall it, and restart Jellyfin.

These stay behind and can be removed by hand:

- The libraries: remove AudioVault Movies and AudioVault Shows in Dashboard → Libraries.
- The library folders: by default `audiovault/library` in Jellyfin's data folder, or the folders you set.
- The plugin's data folder, `audiovault` in Jellyfin's data folder: the catalogue, the encrypted logins (`accounts.json`), the download history and the episode lists.
- The key file `audiovault-accounts.key` in Jellyfin's config folder.
- The cache folder: by default `audiovault` in Jellyfin's cache folder, or the folder you set.
- The settings file `Jellyfin.Plugin.AudioVault.xml` in Jellyfin's `plugins/configurations` folder.
