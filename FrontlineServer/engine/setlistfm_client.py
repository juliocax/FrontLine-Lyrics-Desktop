"""
Client for the setlist.fm REST API (https://api.setlist.fm/docs/1.0/index.html).

Used only as a starting-point guess for Festival Mode: the show's real
setlist is usually published after the performance, so the artist's most
recent public setlist is the best estimate available beforehand. The API key
belongs to the user (see https://www.setlist.fm/settings/api) and is passed
in per-request by the frontend -- nothing here persists it.
"""

import logging
from typing import List, Optional, Tuple

import requests

_BASE_URL = "https://api.setlist.fm/rest/1.0"


def fetch_latest_setlist(api_key: str, artist_name: str) -> Optional[List[Tuple[str, str]]]:
    """Fetch the artist's most recent public setlist as (artist, song) pairs.

    Returns None (caller should fall back to an empty playlist) when there's
    no API key, no artist match, or no setlist with actual songs in it.
    """
    if not api_key or not (artist_name or "").strip():
        return None

    headers = {"x-api-key": api_key, "Accept": "application/json", "Accept-Language": "en"}
    try:
        r = requests.get(
            f"{_BASE_URL}/search/setlists",
            params={"artistName": artist_name, "p": 1},
            headers=headers,
            timeout=8,
        )
        if r.status_code == 404:
            logging.info(f"setlist.fm: nenhum setlist encontrado para '{artist_name}'")
            return None
        if r.status_code == 401:
            logging.warning("setlist.fm: API key inválida/recusada")
            return None
        if r.status_code != 200:
            logging.warning(f"setlist.fm: busca falhou ({r.status_code}) para '{artist_name}'")
            return None
        data = r.json()
    except Exception as e:
        logging.warning(f"setlist.fm: erro de request: {e}")
        return None

    setlists = data.get("setlist")
    if not isinstance(setlists, list) or not setlists:
        return None

    # The API doesn't guarantee ordering; sort by eventDate ("dd-MM-yyyy") to
    # get the most recent show first.
    def event_key(item):
        try:
            d, m, y = item.get("eventDate", "").split("-")
            return (y, m, d)
        except Exception:
            return ("0000", "00", "00")

    for item in sorted(setlists, key=event_key, reverse=True):
        songs = _extract_song_names(item)
        if songs:
            artist = (item.get("artist") or {}).get("name") or artist_name
            return [(artist, song) for song in songs]

    return None


def _extract_song_names(setlist_item: dict) -> List[str]:
    """Flattens every set/encore of a setlist.fm setlist into an ordered song list."""
    names: List[str] = []
    for block in ((setlist_item.get("sets") or {}).get("set")) or []:
        for song in block.get("song") or []:
            name = (song.get("name") or "").strip()
            # "tape" entries are intro/outro/interlude tracks, not performed songs.
            if name and not song.get("tape"):
                names.append(name)
    return names
