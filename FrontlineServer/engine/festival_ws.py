"""WebSocket actions for Festival Mode.

Wire this into engine/ws_server.py, before the existing action switch:

    from engine.festival_ws import try_handle_festival
    if try_handle_festival(manager, data):
        return  # or continue, depending on the handler loop
"""

from __future__ import annotations

import logging
from typing import Any, Dict, List, Tuple


def try_handle_festival(manager, data: Dict[str, Any]) -> bool:
    action = data.get("action")
    if action == "FESTIVAL_ENTER":
        manager.enter_festival_mode(data.get("name") or "Festival", _parse_songs(data.get("songs")))
        return True
    if action == "FESTIVAL_EXIT":
        manager.exit_festival_mode()
        return True
    if action == "FESTIVAL_JUMP_LINE":
        try:
            delta = int(data.get("delta") or 0)
        except (TypeError, ValueError):
            delta = 0
        manager.festival_jump_line(delta)
        return True
    if action == "FESTIVAL_ADD_SONG":
        artist = data.get("artist") or ""
        song = data.get("song") or ""
        make_current = bool(data.get("make_current"))
        manager.festival_add_song(artist, song, make_current)
        if getattr(manager, "festival_playlist", None):
            manager._schedule_on_main(manager._festival_preload_all())
        return True
    if action == "FESTIVAL_REMOVE_SONG":
        manager.festival_remove_song(_as_int(data.get("index")))
        return True
    if action == "FESTIVAL_REORDER":
        manager.festival_reorder_song(_as_int(data.get("from_index")), _as_int(data.get("to_index")))
        return True
    if action == "FESTIVAL_LOAD_SONG":
        manager._festival_load_song(_as_int(data.get("index")))
        return True
    if action == "FESTIVAL_NEXT_SONG":
        manager.festival_next_song()
        return True
    if action == "FESTIVAL_PREV_SONG":
        manager.festival_prev_song()
        return True
    return False


def _as_int(value, default: int = 0) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def _parse_songs(raw) -> List[Tuple[str, str]]:
    songs: List[Tuple[str, str]] = []
    if not raw:
        return songs
    for item in raw:
        if isinstance(item, dict):
            songs.append((item.get("artist") or "", item.get("song") or ""))
        elif isinstance(item, (list, tuple)) and len(item) >= 2:
            songs.append((str(item[0] or ""), str(item[1] or "")))
        else:
            logging.debug("Festival Mode: entrada de música ignorada: %r", item)
    return songs
