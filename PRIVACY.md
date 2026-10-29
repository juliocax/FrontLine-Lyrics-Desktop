# Privacy Policy

_Last updated: October 28, 2026_

FrontLine Lyrics is designed to work with minimal data collection. This document explains what data the app handles, what it doesn't, and which third-party services it talks to in order to work.

## Summary

- The app does **not** record, save, or upload your system audio.
- The app does **not** collect personal information (name, email, account data, there is no account).
- The app only sends short audio snippets to a song-recognition service to identify what's playing, and text (artist/song name) to lyrics and translation services.
- The app can send **optional, anonymous usage statistics** (which theme, lyric font and language you use, for example). It is explained on a welcome screen at first launch and can be turned off at any time. It never includes songs, lyrics or anything that identifies you. See [Anonymous Usage Statistics](#anonymous-usage-statistics-optional).
- All communication between the two parts of the app (the overlay and the background engine) stays on your own machine.

## Data We Collect

### Audio fingerprint for song recognition
To identify what's playing, the app captures a short snippet of your system's audio output (via Windows WASAPI loopback — the same audio you're already hearing through your speakers/headphones) and sends it to Shazam's recognition service to get a match. This snippet is used only for identification and is not stored by the app afterward.

### Artist and song title
Once a song is identified (or entered manually via Manual Search), the artist and song title are sent to:
- **LRCLib**: to fetch synchronized lyrics
- A translation provider (Google Translate, MyMemory, or Bing, depending on availability): only if you choose to translate the lyrics into another language

## Data We Do NOT Collect

- We do not record or store your system audio.
- We do not track what songs you listen to over time — nothing is logged to a server or account.
- We do not collect your name, email, or any account information (the app has no login/account system).
- We do not use advertising SDKs or third-party tracking SDKs. The only analytics is the optional, anonymous report described [below](#anonymous-usage-statistics-optional), which you can turn off.

## Anonymous Usage Statistics (optional)

To understand how the app is used and decide what to improve (for example, which themes are popular), FrontLine Lyrics can send a small, anonymous report about how the app is set up.

- **On by default, but never hidden.** A welcome screen on first launch explains it, and you can opt out right there.
- **You can turn it off at any time** in **Settings (App tab)**. When it is off, nothing is sent.
- **At most once every 24 hours**, as a single event named `settings_snapshot`.

**What is sent:**

| Field | Example |
|---|---|
| `theme`: the theme you selected | `default` |
| `lyric_font`: the lyric font you selected | `segoe` |
| `ui_language`: the app language | `pt` |
| `lock_shortcut_set` / `lock_shortcut`: whether an overlay lock shortcut is set, and which keys | `yes` / `Ctrl+Alt+L` |
| App version, OS name and your system language | `1.4.2`, `Windows`, `pt-BR` |

**What is never sent:** songs, artists, lyrics, searches, your name, e-mail, or any ID that identifies you. No persistent identifier is created or stored: each report uses a new random session value.

The data is sent to [Aptabase](https://aptabase.com), a privacy-focused analytics service. As with any internet request, the service receives your connection's IP address; how Aptabase handles data on its servers is described in its own privacy policy. The full implementation is open source in [`Telemetry.cs`](Frontline/Telemetry.cs), so you can check exactly what leaves your PC.

Builds compiled from source do not include the analytics key, so they never send anything.

## Third-Party Services

Because the app relies on external services to identify songs and fetch lyrics, some data leaves your machine as described above. Each of these services has its own privacy policy, which governs how they handle the data sent to them:

| Service | What's sent | Purpose |
|---|---|---|
| Shazam (via `shazamio`) | Short audio snippet | Song recognition |
| [LRCLib](https://lrclib.net/) | Artist + song title | Fetching synchronized lyrics |
| Google Translate / MyMemory / Bing (via `deep-translator` / `translators`) | Lyrics text | On-the-fly translation (only if you use this feature) |
| [Aptabase](https://aptabase.com) | Theme, lyric font, app language, lock shortcut, app version, Windows language (see above) | Optional anonymous usage statistics (can be turned off) |

## Local-Only Communication

The C# overlay (`FrontLineOverlay.exe`) and the Python background engine (`FrontlineServer.exe`) communicate over a WebSocket that only listens on localhost. This connection never leaves your machine and is not accessible from your network or the internet.

## Microsoft Store Diagnostic Data

Because the app is distributed through the Microsoft Store, Microsoft automatically collects basic crash and performance diagnostics (e.g. crash reports, hang reports) as part of the standard Store platform, independent of anything the app itself does. This is governed by [Microsoft's Privacy Statement](https://privacy.microsoft.com/), not by this document. You can review or adjust your Windows diagnostic data settings in **Settings > Privacy & security > Diagnostics & feedback**.

## Changes to This Policy

This policy may be updated as the app evolves. Changes will be reflected in this file, with the "Last updated" date above.

## Contact

Questions about this policy can be opened as a GitHub Discussion, or sent to jcaxavier2@gmail.com
