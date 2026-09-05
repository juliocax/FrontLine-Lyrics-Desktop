"""
Audio fingerprint recognition (Shazam) and the drift math used to keep the
"live" clock (fingerprint-anchored, no SMTC) honest over time.
"""

import logging
from typing import Optional, Tuple

from shazamio import Shazam


class ShazamRecognizer:
    """Thin async wrapper around shazamio, isolated so it's the only place
    that needs to know about the shazamio response shape."""

    def __init__(self):
        self.shazam = Shazam()

    async def recognize(self, audio_bytes: bytes) -> Tuple[Optional[str], Optional[str], float, str]:
        """Identify a track from a raw audio snippet.

        Returns (title, artist, offset_seconds, cover_art_url); all empty
        on failure or no match.
        """
        try:
            result = await self.shazam.recognize(audio_bytes)
            if result and "track" in result:
                track = result["track"]
                cover_art = track.get("images", {}).get("coverart", "")
                offset = result.get("matches", [{}])[0].get("offset", 0.0)
                return track.get("title"), track.get("subtitle"), offset, cover_art
            return None, None, 0.0, ""
        except Exception as e:
            logging.error(f"Shazam recognition error: {e}")
            return None, None, 0.0, ""


def live_fingerprint_drift(elapsed: float, now: float, record_start: float, offset: float) -> float:
    """Current elapsed time vs. the position implied by a fresh Shazam offset."""
    implied = now - (record_start - offset)
    return elapsed - implied
