# Privacy Policy

_Last updated: August 29, 2026_

FrontLine Lyrics is designed to work with minimal data collection. This document explains what data the app handles, what it doesn't, and which third-party services it talks to in order to work.

## Summary

- The app does **not** record, save, or upload your system audio.
- The app does **not** collect personal information (name, email, account data, there is no account).
- The app only sends short audio snippets to a song-recognition service to identify what's playing, and text (artist/song name) to lyrics and translation services.
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
- We do not use analytics or advertising SDKs.

## Third-Party Services

Because the app relies on external services to identify songs and fetch lyrics, some data leaves your machine as described above. Each of these services has its own privacy policy, which governs how they handle the data sent to them:

| Service | What's sent | Purpose |
|---|---|---|
| Shazam (via `shazamio`) | Short audio snippet | Song recognition |
| [LRCLib](https://lrclib.net/) | Artist + song title | Fetching synchronized lyrics |
| Google Translate / MyMemory / Bing (via `deep-translator` / `translators`) | Lyrics text | On-the-fly translation (only if you use this feature) |

## Local-Only Communication

The C# overlay (`FrontLineOverlay.exe`) and the Python background engine (`FrontlineServer.exe`) communicate over a WebSocket that only listens on localhost. This connection never leaves your machine and is not accessible from your network or the internet.

## Microsoft Store Diagnostic Data

Because the app is distributed through the Microsoft Store, Microsoft automatically collects basic crash and performance diagnostics (e.g. crash reports, hang reports) as part of the standard Store platform, independent of anything the app itself does. This is governed by [Microsoft's Privacy Statement](https://privacy.microsoft.com/), not by this document. You can review or adjust your Windows diagnostic data settings in **Settings > Privacy & security > Diagnostics & feedback**.

## Changes to This Policy

This policy may be updated as the app evolves. Changes will be reflected in this file, with the "Last updated" date above.

## Contact

Questions about this policy can be opened as a GitHub Discussion, or sent to jcaxavier2@gmail.com
