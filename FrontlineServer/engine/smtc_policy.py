"""
SMTC (System Media Transport Controls) trust policy.

Windows exposes one "now playing" timeline per app (Spotify, a browser tab,
VLC, etc.) through GlobalSystemMediaTransportControlsSessionManager. Some of
those timelines are the position *inside a song* (safe to sync lyrics to);
others are the position *inside a video* (a YouTube video, a movie, a live
stream) and must not be used as the song clock.

Every function in this module is pure (no I/O, no shared state) so it can be
unit tested and reasoned about on its own, separately from MusicManager.
"""

import math
from typing import Optional

# A bogus SMTC position (NaN, negative, or absurdly large) shows up on some
# players; we clamp/reject anything outside this range.
MAX_MEDIA_POSITION_S = 12 * 3600

_STREAMING_MUSIC_APP_MARKERS = (
    "spotify",
    "applemusic",
    "apple music",
    "itunes",
    "youtubemusic",
    "youtube.music",
    "amazonmusic",
    "amazon.music",
    "deezer",
    "tidal",
    "pandora",
    "soundcloud",
    "groove",
    "zunemusic",
)

_VIDEO_SURFACE_APP_MARKERS = (
    "chrome",
    "msedge",
    "microsoftedge",
    "firefox",
    "brave",
    "opera",
    "chromium",
    "vlc",
    "zunevideo",
    "moviesandtv",
    "primevideo",
    "netflix",
)

VIDEO_DURATION_S = 15 * 60
VIDEO_POSITION_S = 12 * 60
TRACK_DURATION_MIN_S = 20.0
LYRIC_POSITION_SLACK_S = 45.0


def sane_media_position(value, fallback: Optional[float] = None) -> Optional[float]:
    """Accept only finite positions in [0, MAX_MEDIA_POSITION_S], else fallback."""
    try:
        pos = float(value)
    except (TypeError, ValueError):
        return fallback
    if not math.isfinite(pos) or pos < 0.0 or pos > MAX_MEDIA_POSITION_S:
        return fallback
    return pos


def smtc_trusts_song_clock(app_id: str) -> bool:
    """True if the AUMID belongs to a track-streaming app."""
    blob = (app_id or "").lower().replace("\\", "/")
    return any(marker in blob for marker in _STREAMING_MUSIC_APP_MARKERS)


def smtc_app_is_video_surface(app_id: str) -> bool:
    """Chrome/Edge/VLC/YouTube video — a video surface, not a track streamer."""
    if smtc_trusts_song_clock(app_id):
        return False
    blob = (app_id or "").lower().replace("\\", "/")
    if "youtube" in blob:
        return True
    return any(marker in blob for marker in _VIDEO_SURFACE_APP_MARKERS)


def _smtc_finite_nonneg(value):
    try:
        x = float(value)
    except (TypeError, ValueError):
        return None
    if x != x or x < 0.0 or x > 12 * 3600:
        return None
    return x


def smtc_timeline_is_video_shaped(duration, position, lyrics_end=None) -> bool:
    """Long show/clip: position or duration don't fit a 3-8 minute track."""
    pos = _smtc_finite_nonneg(position) or 0.0
    dur = _smtc_finite_nonneg(duration)
    if pos >= VIDEO_POSITION_S:
        return True
    if dur is not None and dur >= VIDEO_DURATION_S:
        return True
    if lyrics_end is not None:
        try:
            end = float(lyrics_end)
        except (TypeError, ValueError):
            end = None
        if end is not None and end > 0 and pos > end + LYRIC_POSITION_SLACK_S:
            return True
    return False


def smtc_timeline_is_track_shaped(duration, position) -> bool:
    if smtc_timeline_is_video_shaped(duration, position):
        return False
    pos = _smtc_finite_nonneg(position) or 0.0
    dur = _smtc_finite_nonneg(duration)
    if dur is None:
        return pos < VIDEO_POSITION_S
    if dur < TRACK_DURATION_MIN_S:
        return False
    if pos > dur + 8.0:
        return False
    return True


def should_trust_smtc_clock(app_id, duration, position, lyrics_end=None) -> bool:
    """Hybrid check: video shape wins over the allowlist; browsers/VLC stay on Shazam.

    An unknown streaming app with a track-length duration (~20s-15min) still
    gets to use SMTC for quick seeking, without waiting for a new allowlist entry.
    """
    if smtc_timeline_is_video_shaped(duration, position, lyrics_end):
        return False
    if smtc_trusts_song_clock(app_id):
        return True
    if smtc_app_is_video_surface(app_id):
        return False
    return smtc_timeline_is_track_shaped(duration, position)
