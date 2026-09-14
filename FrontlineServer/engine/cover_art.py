"""
Cover art helpers.

Two independent sources feed the overlay's cover art:
1. The thumbnail the OS hands us through the SMTC (Windows Media Session) —
   we cache those raw bytes to disk so the C# UI can load them via a file:// URI.
2. Deezer (then iTunes) search, used when SMTC has no thumbnail (Shazam,
   Festival, manual search) or the thumbnail arrives empty / in a format WPF
   cannot decode (WebP).

Images are always cached locally. WPF BitmapImage is unreliable with remote
HTTPS URLs and with WebP, which is why covers used to appear only sometimes.
"""

from __future__ import annotations

import hashlib
import logging
import os
import re
from pathlib import Path
from typing import Optional, Tuple

import requests

_HEADERS = {
    "User-Agent": "FrontlineLyrics/1.3",
    "Accept": "application/json",
}
_IMG_HEADERS = {
    "User-Agent": "FrontlineLyrics/1.3",
    "Accept": "image/jpeg,image/png,image/*;q=0.8,*/*;q=0.5",
}
_MAX_COVER_FILES = 80
_MAX_COVER_BYTES = 32 * 1024 * 1024
_JUNK_COVER = (
    "karaoke", "karaokê", "karaokee",
    "instrumental", "backing track", "playback",
    "tribute", "made famous", "originally performed",
    "in the style of", "singalong", "sing-along", "sing along",
    "minus one", "sem voz", "no vocal", "without vocal",
    "piano tribute", "8-bit", "8 bit",
    "versão karaokê", "version karaoke", "karaoke version",
    "karaoke band", "the hit crew",
)


def _covers_dir() -> str:
    path = os.path.join(
        os.environ.get("LOCALAPPDATA", os.path.expanduser("~")),
        "FrontLineLyrics",
        "covers",
    )
    os.makedirs(path, exist_ok=True)
    return path


def _fold(value: str) -> str:
    cleaned = re.sub(r"\([^)]*\)", " ", value or "")
    cleaned = re.sub(r"\[[^\]]*\]", " ", cleaned)
    cleaned = re.sub(r"\s+", " ", cleaned).strip().lower()
    return cleaned


def _track_digest(artist: str, song: str) -> str:
    key = f"v2|{_fold(artist)}|{_fold(song)}"
    return hashlib.sha1(key.encode("utf-8", errors="ignore")).hexdigest()


def _ext_for(data: bytes) -> Optional[str]:
    if len(data) < 12:
        return None
    if data[:2] == b"\xff\xd8":
        return ".jpg"
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return ".png"
    if data[:2] == b"BM":
        return ".bmp"
    if data[:6] in (b"GIF87a", b"GIF89a"):
        return ".gif"
    # WPF BitmapImage does not decode WebP — skip so the Deezer JPEG can win.
    if data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return None
    return ".jpg"


def _write_cover(data: bytes, stem: str) -> str:
    if not data:
        return ""
    ext = _ext_for(data)
    if ext is None:
        return ""
    try:
        folder = _covers_dir()
    except Exception:
        return ""
    path = os.path.join(folder, stem + ext)
    try:
        existing = os.path.getsize(path) if os.path.exists(path) else 0
        if existing != len(data):
            tmp = path + ".tmp"
            with open(tmp, "wb") as fh:
                fh.write(data)
            os.replace(tmp, path)
            prune_cover_cache()
        else:
            _touch(path)
        return Path(path).resolve().as_uri()
    except Exception as e:
        logging.warning("Falha ao gravar capa: %s", e)
        return ""


def _cached_uri(artist: str, song: str) -> str:
    stem = _track_digest(artist, song)
    try:
        folder = _covers_dir()
    except Exception:
        return ""
    for ext in (".jpg", ".png", ".bmp", ".gif"):
        path = os.path.join(folder, stem + ext)
        if os.path.exists(path) and os.path.getsize(path) > 0:
            _touch(path)
            return Path(path).resolve().as_uri()
    return ""


def _touch(path: str) -> None:
    try:
        os.utime(path, None)
    except OSError:
        pass


def prune_cover_cache() -> None:
    """Drop oldest cached covers when the folder grows too large."""
    try:
        folder = _covers_dir()
        entries = []
        for name in os.listdir(folder):
            path = os.path.join(folder, name)
            if not os.path.isfile(path) or name.endswith(".tmp"):
                continue
            try:
                st = os.stat(path)
            except OSError:
                continue
            entries.append((st.st_mtime, st.st_size, path))
        if not entries:
            return
        entries.sort()  # oldest first
        total = sum(size for _, size, _ in entries)
        while entries and (len(entries) > _MAX_COVER_FILES or total > _MAX_COVER_BYTES):
            _, size, path = entries.pop(0)
            try:
                os.remove(path)
                total -= size
            except OSError:
                pass
    except Exception as e:
        logging.debug("Limpeza de capas ignorada: %s", e)


def smtc_thumbnail_to_file_uri(cover_bytes: bytes, track_key: Tuple[str, str]) -> str:
    """Write the SMTC thumbnail to disk and return a file:// URI the C# UI can load."""
    if not cover_bytes:
        return ""
    digest = hashlib.sha1(
        f"{track_key[0]}|{track_key[1]}".encode("utf-8", errors="ignore")
    ).hexdigest()
    return _write_cover(cover_bytes, digest)


def fetch_cover_art(artist: str, song: str) -> str:
    """Look up the album cover and cache it locally. Empty string on miss."""
    artist = (artist or "").strip()
    song = (song or "").strip()
    if not artist and not song:
        return ""
    cached = _cached_uri(artist, song)
    if cached:
        return cached
    url = _search_deezer(artist, song) or _search_itunes(artist, song)
    if not url:
        return ""
    downloaded = _download_to_cache(url, artist, song)
    return downloaded or url


def resolve_cover(artist: str, song: str, smtc_bytes: bytes = b"", track_key: Optional[Tuple[str, str]] = None) -> str:
    """Prefer a usable SMTC thumbnail; otherwise search Deezer/iTunes."""
    if smtc_bytes and track_key:
        uri = smtc_thumbnail_to_file_uri(smtc_bytes, track_key)
        if uri:
            return uri
    return fetch_cover_art(artist, song)


def _download_to_cache(url: str, artist: str, song: str) -> str:
    try:
        r = requests.get(url, headers=_IMG_HEADERS, timeout=8)
        if r.status_code != 200 or not r.content:
            return ""
        return _write_cover(r.content, _track_digest(artist, song))
    except Exception as e:
        logging.warning("Falha ao baixar capa: %s", e)
        return ""


def _cover_from_deezer_album(album: dict) -> str:
    if not isinstance(album, dict):
        return ""
    for key in ("cover_xl", "cover_big", "cover_medium", "cover"):
        value = album.get(key)
        if isinstance(value, str) and value.startswith("http"):
            return value
    return ""


def _looks_like_karaoke(text: str) -> bool:
    blob = (text or "").lower()
    return any(token in blob for token in _JUNK_COVER)


def _query_wants_karaoke(artist: str, song: str) -> bool:
    return _looks_like_karaoke(artist) or _looks_like_karaoke(song)


def _score_hit(wanted_artist: str, wanted_song: str, artist: str, title: str, album: str, popularity: float) -> Optional[float]:
    """Higher is better. None = discard (karaoke / tribute / etc.)."""
    if not _query_wants_karaoke(wanted_artist, wanted_song):
        if _looks_like_karaoke(title) or _looks_like_karaoke(album) or _looks_like_karaoke(artist):
            return None
    wa, ws = _fold(wanted_artist.split("feat.")[0].split("&")[0]), _fold(wanted_song)
    a, t = _fold(artist), _fold(title)
    if not t:
        return None
    score = 0.0
    if t == ws:
        score += 8
    elif ws and (t.startswith(ws) or ws.startswith(t)):
        score += 5
    elif ws and (ws in t or t in ws):
        # Substring match without being the same song — often "Song Karaoke Mix".
        extra = abs(len(t.split()) - len(ws.split()))
        score += max(0, 3 - extra)
    else:
        return None
    if wa:
        if a == wa:
            score += 6
        elif wa in a or a in wa:
            score += 3
        else:
            return None
    extra_words = max(0, len(t.split()) - len(ws.split()))
    score -= extra_words * 0.75
    score += min(popularity, 1.0)
    return score


def _search_deezer(artist: str, song: str) -> str:
    try:
        clean_artist = artist.split("feat.")[0].split("&")[0].strip()
        quoted = f'artist:"{clean_artist}" track:"{song}"' if clean_artist and song else f"{clean_artist} {song}".strip()
        loose = f"{clean_artist} {song}".strip()
        best_url, best_score = "", -1.0
        for query in (quoted, loose):
            if not query:
                continue
            r = requests.get(
                "https://api.deezer.com/search",
                params={"q": query, "limit": 15},
                headers=_HEADERS,
                timeout=5,
            )
            if r.status_code != 200:
                continue
            results = r.json().get("data") or []
            for item in results:
                if not isinstance(item, dict):
                    continue
                art = ((item.get("artist") or {}) or {}).get("name") or ""
                title = item.get("title") or item.get("title_short") or ""
                album = ((item.get("album") or {}) or {}).get("title") or ""
                rank = item.get("rank")
                popularity = (float(rank) / 1_000_000.0) if isinstance(rank, (int, float)) else 0.0
                scored = _score_hit(artist, song, str(art), str(title), str(album), popularity)
                if scored is None:
                    continue
                url = _cover_from_deezer_album(item.get("album") or {})
                if url and scored > best_score:
                    best_url, best_score = url, scored
            if best_url:
                break
        return best_url
    except Exception as e:
        logging.warning("Falha ao pesquisar a capa do álbum no Deezer: %s", e)
        return ""


def _search_itunes(artist: str, song: str) -> str:
    try:
        term = f"{artist} {song}".strip()
        r = requests.get(
            "https://itunes.apple.com/search",
            params={"term": term, "entity": "song", "limit": 10},
            headers=_HEADERS,
            timeout=5,
        )
        if r.status_code != 200:
            return ""
        results = r.json().get("results") or []
        best_url, best_score = "", -1.0
        for item in results:
            if not isinstance(item, dict):
                continue
            art = item.get("artistName") or ""
            title = item.get("trackName") or ""
            album = item.get("collectionName") or ""
            scored = _score_hit(artist, song, str(art), str(title), str(album), 0.0)
            if scored is None:
                continue
            art_url = item.get("artworkUrl100") or item.get("artworkUrl60") or ""
            if not isinstance(art_url, str) or not art_url.startswith("http"):
                continue
            art_url = art_url.replace("100x100bb", "600x600bb").replace("60x60bb", "600x600bb")
            if scored > best_score:
                best_url, best_score = art_url, scored
        return best_url
    except Exception as e:
        logging.warning("Falha ao pesquisar a capa do álbum no iTunes: %s", e)
        return ""
