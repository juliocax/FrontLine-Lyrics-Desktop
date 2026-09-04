"""
Cover art helpers.

Two independent sources feed the overlay's cover art:
1. The thumbnail the OS hands us through the SMTC (Windows Media Session) —
   we just need to cache those raw bytes to disk so the C# UI can load them
   via a file:// URI.
2. A Deezer search, used when SMTC has no thumbnail (Shazam-only flow) or
   for manual search.
"""

import hashlib
import logging
import os
import re
from pathlib import Path
from typing import Tuple

import requests


def fetch_cover_art(artist: str, song: str) -> str:
    """Look up the album cover through the Deezer API (avoids picking a random compilation)."""
    try:
        clean_song = re.sub(r'\([^)]*\)', '', song).strip()
        clean_artist = artist.split('feat.')[0].split('&')[0].strip()

        query = f"{clean_artist} {clean_song}"
        r = requests.get(f"https://api.deezer.com/search?q={query}", timeout=5)

        if r.status_code == 200:
            results = r.json().get("data", [])
            if results:
                return results[0].get("album", {}).get("cover_xl", "")
    except Exception as e:
        logging.warning(f"Falha ao pesquisar a capa do álbum no Deezer: {e}")
    return ""


def smtc_thumbnail_to_file_uri(cover_bytes: bytes, track_key: Tuple[str, str]) -> str:
    """Write the SMTC thumbnail to disk and return a file:// URI the C# UI can load."""
    if not cover_bytes:
        return ""
    covers_dir = os.path.join(
        os.environ.get("LOCALAPPDATA", os.path.expanduser("~")),
        "FrontLineLyrics",
        "covers",
    )
    try:
        os.makedirs(covers_dir, exist_ok=True)
    except Exception:
        return ""
    digest = hashlib.sha1(
        f"{track_key[0]}|{track_key[1]}".encode("utf-8", errors="ignore")
    ).hexdigest()
    path = os.path.join(covers_dir, f"{digest}.jpg")
    try:
        if not os.path.exists(path) or os.path.getsize(path) == 0:
            with open(path, "wb") as fh:
                fh.write(cover_bytes)
        return Path(path).resolve().as_uri()
    except Exception as e:
        logging.warning(f"Falha ao gravar capa SMTC: {e}")
        return ""
