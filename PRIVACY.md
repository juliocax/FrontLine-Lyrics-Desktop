# Privacy Policy — FrontLine Lyrics

**Version:** 0.0.2  
**Effective Date:** March 27, 2026  

## 1. Overview
FrontLine Lyrics ("the Application") is an open-source tool developed with privacy as a core principle. This Privacy Policy explains how the Application handles data to provide its synchronized lyrics service. The Application does not collect, store, sell, or intentionally transmit any Personally Identifiable Information (PII).

## 2. Audio Processing and System Audio Access
To identify the song being played, the Application captures the system's internal audio output (loopback).
* Audio is processed in real-time in temporary memory (RAM) and is never recorded or stored on disk.
* The Application does not request access to, nor does it use, the physical system microphone.

## 3. Third-Party Services and Secure Transmission
To function properly, the Application communicates with external services. All network communications are transmitted securely using modern encryption protocols (HTTPS/TLS).

### ShazamIO
An unofficial open-source client used to communicate with Shazam's backend. A short, ephemeral, and strictly anonymous mathematical audio fingerprint is generated locally and transmitted to identify the track. This fingerprint cannot be reverse-engineered into playable audio or linked to any user identity. No raw audio is uploaded or stored.

### LRCLib
Receives the artist name and track title via secure HTTPS requests in order to return synchronized lyrics.

### Translation Services
Lyrics text may be processed using `deep-translator` (Google Translate endpoints) or the `anyascii` library for translation and romanization features via secure network requests.

Third-party services may process network-related data (such as IP addresses) as part of standard internet communication. The Application itself does not collect, store, or control this data. The Application is not responsible for the privacy practices of third-party services, nor for temporary service unavailabilities on their end.

## 4. Telemetry and Analytics
The Application does not include tracking software, analytics, or telemetry mechanisms. It does not track:
* Application usage
* Songs listened to
* User behavior
* IP addresses (by the Application itself)

## 5. Data Retention
The Application does not collect or store personal data. As no personal data is retained, no data retention period applies.

## 6. User Rights
Because the Application does not collect or process personal data, there are no user data records to access, modify, or delete. For data handled by third-party services, users should refer to their respective privacy policies.

## 7. Open Source Transparency
FrontLine Lyrics is licensed under the GNU General Public License v3 (GPLv3). The full source code is publicly available for inspection and verification of its behavior at our [official GitHub repository](https://github.com/juliocax/FrontLine-Lyrics-Desktop).

## 8. Legal and Jurisdiction
This Privacy Policy is governed by applicable data protection laws, including the Brazilian General Data Protection Law (LGPD) and other relevant regulations where applicable.

## 9. Contact
For questions or concerns regarding this Privacy Policy, please [open an issue on the official FrontLine Lyrics GitHub repository](https://github.com/juliocax/FrontLine-Lyrics-Desktop/issues/new).
