"""
Synced-lyrics lookup against the LRCLIB API (https://lrclib.net).

Nothing here depends on MusicManager state: every function takes plain
strings/lists in and returns plain data out, which makes this module easy to
test and easy to swap for another lyrics provider later.
"""

import logging
import re
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


def names_are_close(query: str, candidate: str) -> bool:
    """Loose title/artist match: exact, substring, or a shared long first word."""
    q = (query or "").lower().strip()
    c = (candidate or "").lower().strip()
    if not q or not c:
        return False
    if q == c or q in c or c in q:
        return True
    q0 = q.split()[0]
    c0 = c.split()[0]
    return len(q0) >= 4 and (q0 in c or c0 in q)


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
        # Sentinel line so the UI knows where the last line ends.
        lines.append({"timestamp": lines[-1]["timestamp"] + 5.0, "text": "End"})
    return lines


def pick_lrclib_search_hit(results: Any, artist: str, song: str) -> Optional[Dict[str, Any]]:
    """Pick a search result with synced lyrics; does not require an exact artist match."""
    if not isinstance(results, list):
        return None
    scored: List[Tuple[int, Dict[str, Any]]] = []
    for item in results:
        if not isinstance(item, dict) or not item.get("syncedLyrics"):
            continue
        score = 0
        if names_are_close(artist, item.get("artistName") or ""):
            score += 2
        if names_are_close(song, item.get("trackName") or ""):
            score += 2
        if score > 0:
            scored.append((score, item))
    if not scored:
        for item in results:
            if not isinstance(item, dict) or not item.get("syncedLyrics"):
                continue
            if names_are_close(song, item.get("trackName") or ""):
                return item
        return None
    scored.sort(key=lambda pair: -pair[0])
    return scored[0][1]


def fetch_lyrics_lrclib(artist: str, song: str) -> Optional[List[Dict[str, Any]]]:
    """Fetch synced lyrics from the LRCLIB API.

    Tries the exact /api/get first (fast when the name already matches
    Spotify/manual-search naming), then /api/search with a Shazam-noise-free
    name. Timeouts are kept short: a long 429 retry used to let the track end
    while we were still stuck in SEARCHING.
    """
    headers = {"User-Agent": "FrontLineLyricsApp/1.2.0"}
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
            if payload.get("syncedLyrics"):
                lines = parse_synced_lrc(payload["syncedLyrics"])
                if lines:
                    return lines
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
        if hit and hit.get("syncedLyrics"):
            lines = parse_synced_lrc(hit["syncedLyrics"])
            if lines:
                return lines
        return None

    try:
        # 1) Exact get for every (artist, title) candidate we have.
        for who, track in attempts:
            try:
                lines = lookup_get(track, who)
                if lines:
                    return lines
            except Exception as e:
                logging.warning(f"Exact match search failed (lrclib): {e}")

        # 2) Broad search with "title artist".
        who, track = attempts[0]
        try:
            lines = lookup_search(track, who, f"{track} {who}".strip())
            if lines:
                return lines
        except Exception as e:
            logging.warning(f"Broad search failed (lrclib): {e}")

        # 3) Title-only search as a last resort.
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
