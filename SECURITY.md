# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability, **please do not open a public GitHub issue**. Instead, report it privately so it can be fixed before details are public.

**Contact:** jcaxavier2@gmail.com

Please include as much of the following as you can:
- A description of the vulnerability and its potential impact
- Steps to reproduce it
- Any relevant logs, screenshots, or proof-of-concept code
- Which app version you tested on (Deck / Overlay / Python backend)

You should expect an initial response within a few days. Once the issue is confirmed, a fix will be prioritized and shipped through the Microsoft Store as a new version. Credit will be given in the release notes or kept anonymous, your choice.

## Scope

This project has two components that could be relevant to a report:
- **FrontLineOverlay** (C# / WPF) — the floating overlay window
- **FrontlineServer** (Python) — handles system audio capture (WASAPI loopback), song recognition, and lyrics fetching, and exposes a local WebSocket server used only for communication between the overlay and the backend on the same machine

The app does not record or transmit raw audio, only anonymous fingerprints used for song recognition. See [PRIVACY.md](./PRIVACY.md) for details on what data is and isn't collected.
