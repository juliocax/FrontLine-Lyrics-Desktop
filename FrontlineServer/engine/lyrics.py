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
from engine import service_health

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


# Descriptors that can be ignored only as isolated version annotations.
_VERSION_SUFFIX = re.compile(
    r"\s*[-–—]\s*(?:remaster(?:ed)?(?:\s+\d{4})?|\d{4}\s+remaster(?:ed)?|"
    r"live(?:\s+at\s+.+)?|radio\s+edit|acoustic(?:\s+version)?|"
    r"(?:official\s+)?(?:music\s+)?video|lyric\s+video|"
    r"cover(?:\s+version)?|demo|instrumental|single\s+version)\s*$",
    re.I,
)


def _normalized_title(value: str) -> str:
    """Normalize decorative version labels without treating other titles as equivalent."""
    value = _PAREN_OR_BRACKET.sub("", value or "")
    value = _DASH_SUFFIX.sub("", value)
    value = _VERSION_SUFFIX.sub("", value)
    return _normalized_name(value)


def names_are_close(query: str, candidate: str) -> bool:
    """Strict name comparison used by other callers."""
    return bool(_normalized_name(query)) and _normalized_name(query) == _normalized_name(candidate)


_FEAT_IN_TITLE = re.compile(r"\b(?:feat\.?|ft\.?|featuring)\s+([^\r\n)\]]+)", re.I)
_ARTIST_JOIN = re.compile(r"\s*(?:,|;|\s+&\s+|\s+and\s+|\s+feat\.?\s+|\s+ft\.?\s+|\s+featuring\s+)\s*", re.I)


def _artist_parts(value: str) -> List[str]:
    """Split only recognizable performer separators, not arbitrary substrings."""
    return [_normalized_name(p) for p in _ARTIST_JOIN.split(value or "") if _normalized_name(p)]


def _performers(artist: str, song: str) -> Tuple[str, set]:
    names = _artist_parts(artist)
    featured = set(names[1:])
    for match in _FEAT_IN_TITLE.finditer(song or ""):
        featured.update(_artist_parts(match.group(1)))
    return (names[0] if names else ""), featured


def _title_variants(raw_title: str, main_artist: str) -> List[str]:
    variants = [_normalized_title(raw_title)]
    # Some LRCLIB records include "Artist - Song" in trackName.
    parts = re.split(r"\s+[-–—:]\s+", raw_title, maxsplit=1)
    if len(parts) == 2:
        prefix_names = _artist_parts(parts[0])
        if prefix_names and prefix_names[0] == main_artist:
            variants.append(_normalized_title(parts[1]))
    return variants


def _match_score(item: Any, artist: str, song: str) -> int:
    """Match the *original* track identity, regardless of lookup query variants.

    A requested featured performer may be credited either in trackName or
    artistName. Reject solo recordings when a feature was explicitly requested.
    """
    if not isinstance(item, dict) or not item.get("syncedLyrics"):
        return 0
    wanted_main, wanted_feat = _performers(artist, song)
    raw_found_title = str(item.get("trackName") or "")
    found_main, found_feat = _performers(str(item.get("artistName") or ""), raw_found_title)
    if not wanted_main or wanted_main != found_main:
        return 0
    if wanted_feat and not wanted_feat.issubset(found_feat):
        return 0
    wanted_title = _normalized_title(song)
    if not wanted_title or wanted_title not in _title_variants(raw_found_title, wanted_main):
        return 0
    score = 100 if _normalized_title(raw_found_title) == wanted_title else 80
    if wanted_feat:
        score += 20
    elif found_feat:
        score -= 40  # Prefer solo recording when no feature was requested.
    return score


def _valid_track(item: Any, artist: str, song: str) -> bool:
    return _match_score(item, artist, song) > 0


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
    """Prefer exact artist/title; allow artist-prefixed titles as fallback."""
    if not isinstance(results, list):
        return None
    ranked = sorted(
        ((score, item) for item in results if (score := _match_score(item, artist, song)) > 0),
        key=lambda pair: -pair[0],
    )
    return ranked[0][1] if ranked else None


def fetch_lyrics_lrclib(artist: str, song: str) -> Optional[List[Dict[str, Any]]]:
    """Fetch synced lyrics from the LRCLIB API.

    Tries the exact /api/get first (fast when the name already matches
    Spotify/manual-search naming), then /api/search with a Shazam-noise-free
    name. Timeouts are kept short: a long 429 retry used to let the track end
    while we were still stuck in SEARCHING.
    """
    headers = {"User-Agent": "FrontLineLyricsApp/1.3.0"}
    clean_artist, clean_song = clean_lrclib_query(artist, song)
    requested_main, requested_feat = _performers(artist, song)
    attempts: List[Tuple[str, str]] = []
    # Feature-specific search first. The old order found the solo recording and
    # returned immediately, even when the user requested a feat. version.
    for pair in ((clean_artist, song or ""), (artist or "", song or ""),
                 (clean_artist, clean_song)):
        key = (pair[0].lower().strip(), pair[1].lower().strip())
        if not key[1] or key in {(a.lower(), s.lower()) for a, s in attempts}:
            continue
        attempts.append(pair)
    if not attempts:
        return None

    from requests.adapters import HTTPAdapter
    from urllib3.util.retry import Retry

    session = requests.Session()
    observed = {"ok": False, "server_errors": 0, "connection_errors": 0}
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
        service_health.debug("LRCLIB response HTTP %s for %s", r.status_code, r.url if hasattr(r, "url") else "api")
        if r.status_code in (200, 404):
            observed["ok"] = True
        elif r.status_code in (500, 502, 503, 504, 520, 521, 522, 523, 524):
            observed["server_errors"] += 1
        if r.status_code == 200:
            payload = r.json()
            if _valid_track(payload, artist, song):
                lines = parse_synced_lrc(payload["syncedLyrics"])
                if lines:
                    return lines
            elif isinstance(payload, dict) and payload.get("syncedLyrics"):
                logging.warning(
                    "LRCLIB /get returned mismatched track: requested=%r - %r, got=%r - %r",
                    who, track, payload.get("artistName"), payload.get("trackName"),
                )
        return None

    def lookup_search(track: str, who: str, query: Optional[str] = None) -> Optional[List[Dict[str, Any]]]:
        r = session.get(
            "https://lrclib.net/api/search",
            params={"q": query} if query is not None else {"track_name": track, "artist_name": who},
            headers=headers,
            timeout=6,
        )
        service_health.debug("LRCLIB response HTTP %s for %s", r.status_code, r.url if hasattr(r, "url") else "api")
        if r.status_code in (200, 404):
            observed["ok"] = True
        elif r.status_code in (500, 502, 503, 504, 520, 521, 522, 523, 524):
            observed["server_errors"] += 1
        if r.status_code != 200:
            return None
        results = r.json()
        hit = pick_lrclib_search_hit(results, artist, song)
        if not hit:
            # Metadata only: do not log copyrighted lyric text.
            logging.info(
                "LRCLIB search no strict match: query=%r, artist=%r, candidates=%r",
                query if query is not None else track, who,
                [(x.get("trackName"), x.get("artistName")) for x in results[:8] if isinstance(x, dict)]
                if isinstance(results, list) else "invalid response",
            )
        if hit:
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
                if service_health.classify_exception(e): observed["connection_errors"] += 1
                elif isinstance(e, requests.exceptions.RetryError): observed["server_errors"] += 1
                logging.warning(f"Exact match search failed (lrclib): {e}")

        # 2) Structured search: no competing q parameter, and the server can
        # filter artist/title separately. This is especially important for
        # covers, whose results may be buried in the 20-result q limit.
        who, track = attempts[0]
        if who:
            try:
                lines = lookup_search(track, who)
                if lines:
                    return lines
            except Exception as e:
                if service_health.classify_exception(e): observed["connection_errors"] += 1
                elif isinstance(e, requests.exceptions.RetryError): observed["server_errors"] += 1
                logging.warning("Structured search failed (lrclib): %s", e)

        if requested_feat:
            try:
                # Many catalogue entries store the feat only in artistName;
                # structured search with a plain title may find them.
                lines = lookup_search(clean_song, clean_artist)
                if lines:
                    return lines
            except Exception as e:
                if service_health.classify_exception(e): observed["connection_errors"] += 1
                elif isinstance(e, requests.exceptions.RetryError): observed["server_errors"] += 1
                logging.warning("Structured feature fallback failed (lrclib): %s", e)

        if requested_feat:
            # LRCLIB may credit featuring performers in artistName rather than
            # trackName. Try original artist/title and principal+feature title.
            for lookup_artist, lookup_title in ((artist, song), (artist, clean_song)):
                if not lookup_artist or not lookup_title:
                    continue
                try:
                    lines = lookup_search(lookup_title, lookup_artist)
                    if lines:
                        return lines
                except Exception as e:
                    if service_health.classify_exception(e): observed["connection_errors"] += 1
                    elif isinstance(e, requests.exceptions.RetryError): observed["server_errors"] += 1
                    logging.warning("Feature artist search failed (lrclib): %s", e)

        # 3) Broad search, preserving the featured artist in the query.
        try:
            featured_query = f"{clean_song} {clean_artist} {' '.join(sorted(requested_feat))}".strip()
            lines = lookup_search(song, clean_artist, featured_query)
            if lines:
                return lines
        except Exception as e:
            if service_health.classify_exception(e): observed["connection_errors"] += 1
            elif isinstance(e, requests.exceptions.RetryError): observed["server_errors"] += 1
            logging.warning(f"Broad search failed (lrclib): {e}")

        # 4) Title-only search as a last resort.
        if who:
            try:
                lines = lookup_search(track, who, track)
                if lines:
                    return lines
            except Exception as e:
                if service_health.classify_exception(e): observed["connection_errors"] += 1
                elif isinstance(e, requests.exceptions.RetryError): observed["server_errors"] += 1
                logging.warning(f"Title-only search failed (lrclib): {e}")
    finally:
        # At most one health observation per song lookup, regardless of retries.
        if observed["ok"]:
            service_health.record_attempt("ok")
        elif observed["server_errors"]:
            service_health.record_attempt("service_offline", immediate=observed["server_errors"] >= 3)
        elif observed["connection_errors"]:
            service_health.record_attempt("connection_error", immediate=True)
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
