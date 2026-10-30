"""
Synced-lyrics lookup against the LRCLIB API (https://lrclib.net).

Nothing here depends on MusicManager state: every function takes plain
strings/lists in and returns plain data out, which makes this module easy to
test and easy to swap for another lyrics provider later.
"""

import logging
import re
import unicodedata
from typing import Any, Dict, List, Optional, Tuple

import requests

_LRC_LINE = re.compile(r"\[(\d{2,}):(\d{2}(?:\.\d{1,3})?)\](.*)")
_PAREN_OR_BRACKET = re.compile(r"\([^)]*\)|\[[^\]]*\]")
_FEAT_SPLIT = re.compile(r"\s+(?:feat\.?|ft\.?|featuring)\s+", re.I)
_DASH_SUFFIX = re.compile(
    r"\s+[\-–—]\s+(official.*|audio|video|lyric.*|from\s+.*|remaster.*|"
    r"live.*|radio\s+edit.*|slowed.*|sped\s+up.*)$",
    re.I,
)


def clean_lrclib_query(artist: str, song: str) -> Tuple[str, str]:
    """Strip Shazam-style noise from the title/artist so it looks like what LRCLIB indexes."""
    raw_song = song or ""
    raw_artist = artist or ""
    cleaned_song = _PAREN_OR_BRACKET.sub("", raw_song)
    cleaned_song = _DASH_SUFFIX.sub("", cleaned_song)
    cleaned_song = re.sub(r"\s+", " ", cleaned_song).strip(" -")
    if not cleaned_song:
        cleaned_song = re.sub(r"\s+", " ", raw_song).strip()

    cleaned_artist = _FEAT_SPLIT.split(raw_artist)[0]
    cleaned_artist = cleaned_artist.split(",")[0].split("&")[0].split("/")[0].split(";")[0]
    cleaned_artist = _PAREN_OR_BRACKET.sub("", cleaned_artist)
    cleaned_artist = re.sub(r"\s+", " ", cleaned_artist).strip()
    if not cleaned_artist:
        cleaned_artist = re.sub(r"\s+", " ", raw_artist).strip()
    return cleaned_artist, cleaned_song


def _normalized_name(value: str) -> str:
    """Compare names without case, accents or punctuation differences."""
    value = unicodedata.normalize("NFKD", value or "")
    value = "".join(c for c in value if not unicodedata.combining(c))
    return " ".join(re.findall(r"[a-z0-9]+", value.casefold()))


def _normalized_title(value: str) -> str:
    """Ignore *descriptive* suffixes, but not different song titles."""
    value = _PAREN_OR_BRACKET.sub("", value or "")
    value = _DASH_SUFFIX.sub("", value)
    return _normalized_name(value)


def names_are_close(query: str, candidate: str) -> bool:
    """Require a genuine name match; substring/first-word matches are unsafe."""
    return bool(_normalized_name(query)) and _normalized_name(query) == _normalized_name(candidate)


def _valid_track(item: Any, artist: str, song: str) -> bool:
    """Require both the requested song and performer to match the LRCLIB record.

    An unrelated song must never be accepted merely because a broad search
    returned it. Covers under a *different* artist are deliberately not treated
    as time-synced matches for a live performance.
    """
    if not isinstance(item, dict) or not item.get("syncedLyrics"):
        return False
    wanted_title = _normalized_title(song)
    found_title = _normalized_title(str(item.get("trackName") or ""))
    if not wanted_title or wanted_title != found_title:
        return False
    wanted_artist = _normalized_name(artist)
    found_artist = _normalized_name(str(item.get("artistName") or ""))
    return bool(wanted_artist) and wanted_artist == found_artist


def parse_synced_lrc(synced_lyrics: str) -> List[Dict[str, Any]]:
    """Turn an LRC-format blob into a list of {timestamp, text} lines."""
    lines: List[Dict[str, Any]] = []
    for line in (synced_lyrics or "").split("\n"):
        match = _LRC_LINE.match(line)
        if not match:
            continue
        timestamp = (int(match.group(1)) * 60) + float(match.group(2))
        text = match.group(3).strip()
        if text:
            lines.append({"timestamp": timestamp, "text": text})
    if lines:
        lines.append({"timestamp": lines[-1]["timestamp"] + 5.0, "text": "End"})
    return lines


def pick_lrclib_search_hit(results: Any, artist: str, song: str) -> Optional[Dict[str, Any]]:
    """Accept only synced lyrics for the requested song *and* artist."""
    if not isinstance(results, list):
        return None
    for item in results:
        if _valid_track(item, artist, song):
            return item
    return None


def fetch_lyrics_lrclib(artist: str, song: str) -> Optional[List[Dict[str, Any]]]:
    """Fetch synced lyrics from the LRCLIB API.

    Tries the exact /api/get first (fast when the name already matches
    Spotify/manual-search naming), then /api/search with a Shazam-noise-free
    name. Timeouts are kept short: a long 429 retry used to let the track end
    while we were still stuck in SEARCHING.
    """
    headers = {"User-Agent": "FrontLineLyricsApp/1.3.0"}
    clean_artist, clean_song = clean_lrclib_query(artist, song)
    attempts: List[Tuple[str, str]] = []
    for pair in ((clean_artist, clean_song), (artist or "", song or "")):
        key = (pair[0].lower().strip(), pair[1].lower().strip())
        if not key[1] or key in {(a.lower(), s.lower()) for a, s in attempts}:
            continue
        attempts.append(pair)
    if not attempts:
        return None

    from requests.adapters import HTTPAdapter
    from urllib3.util.retry import Retry

    session = requests.Session()
    retries = Retry(
        total=1,
        backoff_factor=0.2,
        status_forcelist=[502, 503, 504],
    )
    session.mount("https://", HTTPAdapter(max_retries=retries))

    def lookup_get(track: str, who: str) -> Optional[List[Dict[str, Any]]]:
        r = session.get(
            "https://lrclib.net/api/get",
            params={"track_name": track, "artist_name": who},
            headers=headers,
            timeout=5,
        )
        if r.status_code == 200:
            payload = r.json()
            if _valid_track(payload, who, track):
                lines = parse_synced_lrc(payload["syncedLyrics"])
                if lines:
                    return lines
            elif isinstance(payload, dict) and payload.get("syncedLyrics"):
                logging.warning("LRCLIB /get returned mismatched track: requested=%r - %r, got=%r - %r",
                                who, track, payload.get("artistName"), payload.get("trackName"))
        return None

    def lookup_search(track: str, who: str, query: str) -> Optional[List[Dict[str, Any]]]:
        r = session.get(
            "https://lrclib.net/api/search",
            params={"q": query},
            headers=headers,
            timeout=6,
        )
        if r.status_code != 200:
            return None
        hit = pick_lrclib_search_hit(r.json(), who, track)
        if hit:
            lines = parse_synced_lrc(hit["syncedLyrics"])
            if lines:
                return lines
        return None

    try:
        for who, track in attempts:
            try:
                lines = lookup_get(track, who)
                if lines:
                    return lines
            except Exception as e:
                logging.warning(f"Exact match search failed (lrclib): {e}")

        who, track = attempts[0]
        try:
            lines = lookup_search(track, who, f"{track} {who}".strip())
            if lines:
                return lines
        except Exception as e:
            logging.warning(f"Broad search failed (lrclib): {e}")

        if who:
            try:
                lines = lookup_search(track, who, track)
                if lines:
                    return lines
            except Exception as e:
                logging.warning(f"Title-only search failed (lrclib): {e}")
    finally:
        try:
            session.close()
        except Exception:
            pass

    return None


def fetch_lyrics_from_candidates(pairs: List[Tuple[str, str]]) -> Optional[List[Dict[str, Any]]]:
    """Try several (artist, title) pairs until LRCLIB returns synced lyrics."""
    seen = set()
    for artist, song in pairs:
        key = ((artist or "").lower().strip(), (song or "").lower().strip())
        if not key[1] or key in seen:
            continue
        seen.add(key)
        lines = fetch_lyrics_lrclib(artist, song)
        if lines:
            return lines
    return None
