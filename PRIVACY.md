# Privacy Policy

_Last updated: November 5, 2026_

FrontLine Lyrics displays song lyrics and translations in a desktop overlay. It can follow information from compatible media players or identify a song from system audio. This policy explains the information used for those features and the optional settings statistics.

## Summary

- No account or login is required. The app does not ask for your name or email to use its features.
- Song recognition uses a short sample of system audio and an external recognition service. This is separate from usage statistics.
- Song information is sent to a lyrics service when looking up lyrics. Lyrics text is sent to a translation provider when you use translation.
- Optional usage statistics describe app settings and basic technical context. They do not include your songs, lyrics, searches, or audio recordings.
- Communication between the desktop interface and its background engine stays on the same computer through localhost.

## Song Recognition, Lyrics, and Translation

### System audio and player information

In Listen mode, the app captures a short sample of the audio playing on your computer to identify the song. On Windows, this uses WASAPI loopback. The Linux version uses its platform-specific audio integration. Recognition uses Shazam through `shazamio`, sending audio-derived recognition data to the service to obtain a match.

The app does not keep audio recordings as a listening history. Capturing audio for recognition is separate from the optional statistics described below; audio recordings are not included in those reports.

When a compatible player provides track information, the app can use that information to follow playback. On Linux, this includes MPRIS. Track information can include the artist, title, playback state, and position needed to show and synchronize lyrics.

### Lyrics lookup and manual search

The artist and song title, whether obtained from recognition, a compatible player, or a manual search, are sent to **LRCLIB** to find lyrics. This information is needed for the lookup; it is not included in usage statistics.

### Translation

If you choose to translate lyrics, the lyrics text is sent to a translation provider. Depending on availability and the build, providers include Google Translate, MyMemory, or Bing, accessed through the app's translation libraries.

Turning off usage statistics does not disable the external requests needed for song recognition, lyrics lookup, or translation when you use those features.

## Optional Usage Statistics

The app can send a configuration report to **Aptabase** to help identify which settings and features are used.

The statistics option is enabled by default in the supplied Windows welcome screen and can be switched off there before continuing. You can also change it later in **Settings → General**. A settings report is not sent before the welcome screen has been completed. Available controls and reported fields depend on the platform and build; the list below describes the expanded Windows report.

When enabled and configured, the report is sent as a `settings_snapshot` event, at most once every 24 hours after a successful send. It is a snapshot of settings, rather than a record of every click or setting change. If a request fails, the app may try again later.

### Settings included in the report

| Category | Information |
| --- | --- |
| Themes and background | Selected theme, active theme, automatic or manual theme mode, background opacity, and whether the theme appears behind lyrics |
| Lyric appearance | Font, font size, color, weight, style, bold and italic settings |
| Lyric layout | Number of lines or full-lyrics mode, and left, center, or right alignment |
| Language and translation | App language, translation language, and whether original lyrics are shown with a translation |
| Controls | Automatic mode, manual synchronization mode, overlay lock state, and the configured lock shortcut keys |
| Window | Width, height, position, and window state |
| Report details | Statistics setting and settings-schema version |

### Technical details included with the event

Each event also includes a timestamp, app version, operating-system name, system language/locale, telemetry implementation version, and whether it is a debug build. Each report uses a newly generated session value; the sender does not create a permanent user identifier for these reports.

### Information excluded from statistics

Settings reports do **not** include:

- Song titles, artists, lyrics, or translations.
- Search terms, playlists, or listening history.
- Audio recordings or song-recognition data.
- API keys or credentials.
- Your name, email address, or account details.

The configured shortcut is the combination of keys you selected in settings. It is not a recording of your keyboard activity.

### Recipient and controls

Reports go to [Aptabase](https://aptabase.com). As with other internet requests, the receiving service can see the connection's IP address. Its handling of requests and server-side data is governed by its own policies. The exclusions above describe the contents of the app's statistics report, not information automatically visible to a network service.

Disabling statistics stops future reports. It does not undo reports already sent. The sender also requires an analytics key and endpoint to be configured; a build without that configuration does not send statistics. Building from source alone does not determine whether statistics are enabled or configured.

The implementation is available in the [project's source code](https://github.com/juliocax/FrontLine-Lyrics-Desktop), including `Telemetry.cs` and `MainWindow.TelemetrySettings.cs`.

## Local Settings and Communication

The app saves preferences on your computer so they can be restored between sessions. These include appearance, lyric layout, languages, controls, window placement, the statistics preference, and the time of the last successful statistics report.

The desktop interface and Python background engine communicate through a local WebSocket connection bound to localhost. That connection does not send their messages to a remote server. The engine makes separate external requests for the features described above.

The app has no advertising-based tracking. Optional settings statistics are separate from its music-recognition and lyrics features.

## Third-Party Services

| Service | Information used or sent | Purpose |
| --- | --- | --- |
| Shazam, through `shazamio` | Recognition data derived from a short system-audio sample | Identify a song |
| [LRCLIB](https://lrclib.net/) | Artist and song title | Retrieve lyrics |
| Google Translate, MyMemory, or Bing | Lyrics text selected for translation | Translate lyrics |
| [Aptabase](https://aptabase.com) | Settings and event details listed above | Optional usage statistics |

These services receive the requests needed for their features and may receive connection metadata such as an IP address. Their own policies govern how they handle information on their servers. This policy describes what FrontLine Lyrics does; it does not set retention periods or other practices for those services.

## Distribution and Platform Diagnostics

FrontLine Lyrics is distributed for Windows through the Microsoft Store and for Linux through the Snap Store and a `.deb` package in the GitHub repository.

Store, operating-system, and package-distribution services may handle download, update, crash, or diagnostic information under their own settings and privacy policies. These processes are separate from the app's optional settings report. Turning off that report does not change your operating system's or store's diagnostics settings.

For Microsoft services, see [Microsoft's Privacy Statement](https://privacy.microsoft.com/). For Linux installations, review the privacy and diagnostics settings of your distribution and the package service you use.

## Changes to This Policy

This document will be updated when the app's data handling changes. The date at the top identifies the latest revision.

## Contact

Questions can be raised through the [GitHub repository](https://github.com/juliocax/FrontLine-Lyrics-Desktop) or sent to **jcaxavier2@gmail.com**.
