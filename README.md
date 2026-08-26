<div align="center">


<img src="Frontline/assets/banner.jpg" alt="FrontLine Lyrics" width="700"/>

**Real-time, synced lyrics for whatever is playing on your PC.**

[![Microsoft Store](https://img.shields.io/badge/Get%20it%20on-Microsoft%20Store-0078D4?logo=microsoft&logoColor=white)](https://get.microsoft.com/installer/download/9P6LNJCL8ZCC?referrer=appbadge&cid=readme)
![C#](https://img.shields.io/badge/C%23-WPF-239120?logo=csharp&logoColor=white)
![Python](https://img.shields.io/badge/Python-3.13-3776AB?logo=python&logoColor=white)
![Windows](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D6?logo=windows&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-lightgrey)
[![Contributors](https://img.shields.io/badge/Contributors-2-orange)](CONTRIBUTORS.md)

<a href="https://get.microsoft.com/installer/download/9P6LNJCL8ZCC?referrer=appbadge&cid=readme" target="_self">
  <img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200"/>
</a>

</div>

---

## Table of Contents

- [Introduction](#introduction)
- [Features](#features)
- [How It Works](#how-it-works)
- [Technologies Used](#technologies-used)
- [Installation](#installation)
- [Usage Guide](#usage-guide)
- [Screenshots](#screenshots)
- [Building From Source](#building-from-source)
- [Contributors](#contributors)
- [Support the Project](#support-the-project)
- [License](#license)

## Introduction

FrontLine Lyrics is a always-on-top overlay for Windows that listens to whatever is playing on your system and shows synced lyrics in real time, Spotify, YouTube, Apple Music, a local media player, or anything else that comes out of your speakers.

It works two ways, it can automatically follow the track info exposed by Windows' native media session (title, artist, playback position), and it can also *listen* to your system audio directly and recognize the song with Shazam, so it still works with sources that don't expose media metadata at all.

## Features

- **Automatic track detection**: via Windows Media Session (SMTC) reads title, artist and playback position straight from any compatible player, with no manual input.
- **Audio-fingerprint recognition**: as a fallback/primary source, records a snippet of system audio (WASAPI loopback) and identifies the song with Shazam.
- **Auto mode**: continuously re-listens and re-syncs as tracks change, with adaptive retry backoff and a cooldown guard against false "previous track" triggers.
- **Synced lyrics display**: an always-on-top, transparent, draggable overlay window that scrolls lyrics in time with the music.
- **Pause-aware sync**: pausing the track pauses the lyrics scroll too, so everything stays perfectly aligned when playback resumes.
- **Live translation**: translate the displayed lyrics into English, Spanish, French, Portuguese, or a romanized transliteration, resolved in parallel across multiple translation backends for reliability.
- **Manual search**: look up lyrics and cover art by artist/song name when you'd rather not rely on auto-detection.
- **Playback shortcuts**: previous/next track controls built right into the lyrics view, plus manual sync-time adjustment, all without leaving the overlay.
- **Customizable overlay**: adjustable font size and a compact/expanded layout.
- **Multi-language UI**: interface available in English, Portuguese, and Spanish.

## How It Works

FrontLine Lyrics is split into two cooperating processes:

1. **`FrontlineServer` (Python, headless)** — the audio/recognition engine. It captures system audio via WASAPI loopback, talks to the Windows Media Session API for auto-follow, fingerprints audio snippets with Shazam, fetches synced lyrics from LRCLIB, and runs translations. It's packaged as a standalone `.exe` with PyInstaller and exposes a local WebSocket server.
2. **`FrontLineOverlay` (C# / WPF)** — the visible overlay window. It launches the Python server as a child process on startup and communicates with it exclusively over the WebSocket, sending commands (`LISTEN`, `AUTO_TOGGLE`, `TRANSLATE`, `MANUAL_SEARCH`, etc.) and receiving live state broadcasts to render.

## Technologies Used

| Layer | Stack |
|---|---|
| Desktop overlay | C#, WPF (.NET) |
| Backend / audio engine | Python 3.13, `asyncio`, `websockets` |
| Audio capture | `pyaudiowpatch` (WASAPI loopback) |
| Song recognition | `shazamio` |
| Lyrics source | [LRCLIB](https://lrclib.net/) |
| Translation | `deep-translator`, `translators` (parallel multi-backend resolution) |
| Media metadata (auto-follow) | Windows Runtime — `GlobalSystemMediaTransportControlsSessionManager` via `winrt` |
| Packaging | PyInstaller (server), MSIX (Microsoft Store) |


## Installation

The easiest way to install FrontLine Lyrics is through the Microsoft Store:

<a href="https://get.microsoft.com/installer/download/9P6LNJCL8ZCC?referrer=appbadge&cid=readme" target="_self">
  <img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200"/>
</a>


## Usage Guide

1. Launch FrontLine Lyrics, the overlay appears on top of your other windows.
2. Play music in any app (Spotify, browser, local player, etc.).
3. Click **LISTEN** to start automatic recognition/follow, or toggle **AUTO** to keep it continuously syncing as tracks change.
4. Use **SEARCH** to look up lyrics by artist and song name directly.
5. Use the translation toggles (Orig / Rom / EN / ES / FR / PT) to switch how the lyrics are displayed.
6. Adjust font size, drag the window anywhere, and use the previous/next track buttons to control playback without leaving the overlay.

## Screenshots

<p align="center">
  <img src="Frontline/assets/help1.png" alt="Home screen" width="260"/>
  <img src="Frontline/assets/help3.png" alt="Listening to a song" width="260"/>
  <img src="Frontline/assets/help7.png" alt="Synced lyrics" width="260"/>
</p>

| Home screen | Listening | Synced lyrics |
|---|---|---|
| The initial screen when you open the overlay | Recognizing what's currently playing | Lyrics scrolling in sync with the music |

## Building From Source

Want to tinker with the code or build your own copy? Here's how:

1. Clone the repository:
   ```
   git clone https://github.com/juliocax/FrontLine-Lyrics-Desktop.git
   ```
2. Open Visual Studio and make sure the **.NET desktop development** workload (which includes WPF tooling) is installed.
3. Open [`Frontline.sln`](https://github.com/juliocax/FrontLine-Lyrics-Desktop/blob/main/Frontline.sln) in Visual Studio.
4. Set [`Frontline`](https://github.com/juliocax/FrontLine-Lyrics-Desktop/tree/main/Frontline) as the startup project and run it.

If you make changes to the Python backend (`FrontlineServer`), you'll also need to rebuild the standalone executable and swap it into the C# project so `Frontline` picks up your changes:

1. From the `FrontlineServer` folder, rebuild the `.exe` with PyInstaller:
   ```
   pyinstaller --noconfirm --onedir --windowed --collect-all anyascii --collect-all winrt --hidden-import winrt.windows.media.control --hidden-import winrt.windows.storage.streams --name "FrontlineServer" "FrontlineServer.py"
   ```
2. From the `dist` folder created by PyInstaller, copy the `_internal` folder and `FrontlineServer.exe`, and use them to replace the existing ones in `Frontline/FrontlineServer`.
3. Run the `Frontline` project again — it will launch your updated server automatically.

## Contributors

See [CONTRIBUTORS.md](CONTRIBUTORS.md) for the full list of people who have contributed code to this project.

## Support the Project

If FrontLine Lyrics is useful to you, consider [buying me a coffee](https://www.buymeacoffee.com/juliocax).

## License

Distributed under the MIT License. See `LICENSE` for more information.