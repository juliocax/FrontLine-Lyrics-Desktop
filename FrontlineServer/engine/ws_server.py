"""
WebSocket server that talks to the FrontLine overlay (the C#/WPF frontend).

`manager` is set once by the entrypoint (FrontlineServer.py) via `configure()`
before the server starts; ws_handler/broadcast_ui_state read it as a module
global because `websockets.serve` calls the handler with just the socket, so
there's no per-connection place to thread the manager through otherwise.
"""

import asyncio
import json
import logging
import math
import os
import time

import websockets

from engine.smtc_policy import MAX_MEDIA_POSITION_S
from engine.task_utils import spawn_task
from engine.translation import apply_translation_in_background
from engine.workers import background_verification_worker, live_refingerprint_worker, run_manual_search

manager = None
connected_clients = set()


def configure(app_manager):
    """Wire up the MusicManager instance this module should drive. Call once, before serving."""
    global manager
    manager = app_manager


async def ws_handler(websocket):
    """Handles WebSocket communication with the frontend overlay."""
    connected_clients.add(websocket)
    try:
        async for message in websocket:
            try:
                command = json.loads(message)
                action = command.get("action")

                if action == "LISTEN":
                    manager._release_auto_hold()
                    manager.reset_state()
                    manager.is_listening = True
                    # Balance: Spotify/YT Music -> SMTC (seek/pause at the
                    # right timestamp). YouTube/VLC/live -> Shazam 8s
                    # (video time != song time).
                    if manager._should_use_audio_clock():
                        manager._listen_use_shazam = True
                        logging.info("OUVIR: vídeo/ao vivo — fingerprint Shazam")
                    else:
                        manager._listen_use_shazam = False
                        logging.info(
                            "OUVIR: streaming (%s) — âncora SMTC (letra segue a minutagem)",
                            getattr(manager.watcher.ultima_info, "app", ""),
                        )
                elif action == "AUTO_TOGGLE":
                    manager._release_auto_hold()
                    manager.auto_mode = not manager.auto_mode
                    if manager.auto_mode and not manager.is_listening:
                        manager.reset_state()
                        manager.is_listening = True
                        if manager._should_use_audio_clock():
                            manager._listen_use_shazam = True
                elif action == "AUTO_SET":
                    manager._release_auto_hold()
                    manager.auto_mode = bool(command.get("on", False))
                    if manager.auto_mode and not manager.is_listening:
                        manager.reset_state()
                        manager.is_listening = True
                        if manager._should_use_audio_clock():
                            manager._listen_use_shazam = True
                elif action == "RESET":
                    # hold_auto comes from the C# side; Clear always holds
                    # the current track even if the field is missing
                    # (compatible with older builds).
                    manager._hold_current_track()
                    manager.reset_state()
                elif action == "QUIT":
                    manager.server_running = False
                    os._exit(0)
                elif action == "FONT_UP":
                    manager.overlay_font_size = min(80, manager.overlay_font_size + 2)
                elif action == "FONT_DOWN":
                    manager.overlay_font_size = max(14, manager.overlay_font_size - 2)
                elif action == "TRANSLATE":
                    lang = command.get("lang", "original")
                    if lang == "original":
                        manager.preferred_language = None
                        manager.apply_language("original")
                        manager.last_translation_error = None
                    else:
                        manager.preferred_language = lang
                        spawn_task(apply_translation_in_background(manager, lang))
                elif action == "MANUAL_SEARCH":
                    artist = command.get("artist", "")
                    song = command.get("song", "")
                    if artist and song:
                        manager._release_auto_hold()
                        manager.reset_state()
                        manager.manual_mode = True
                        manager.current_song, manager.current_artist = song, artist
                        manager.is_listening = True
                        spawn_task(run_manual_search(manager, artist, song, manager.session_id))
                elif action == "SET_SYNC_TIME":
                    new_time = command.get("time", 0.0)
                    try:
                        new_time = float(new_time)
                    except (TypeError, ValueError):
                        new_time = 0.0
                    if not math.isfinite(new_time):
                        new_time = 0.0
                    new_time = max(0.0, min(new_time, MAX_MEDIA_POSITION_S))
                    manager.system_reference_time = time.time() - new_time
                    manager.clock_paused = False
                    manager.pause_moment = 0.0
                    manager.media_paused = False
            except Exception as e:
                logging.error(f"WebSocket Message Error: {e}")
    finally:
        connected_clients.remove(websocket)


async def broadcast_ui_state(manager):
    """Continuously broadcasts the current state to all connected clients."""
    while manager.server_running:
        try:
            if connected_clients:
                websockets.broadcast(connected_clients, json.dumps(manager.get_current_state()))
        except Exception:
            logging.exception("broadcast_ui_state")
        await asyncio.sleep(0.1)


def _asyncio_exception_handler(loop, context):
    logging.critical("asyncio: %s", context.get("message"), exc_info=context.get("exception"))


async def main_background(port: int):
    """Main background loop initializer."""
    manager._main_loop = asyncio.get_running_loop()
    manager._main_loop.set_exception_handler(_asyncio_exception_handler)
    manager.watcher.start()
    spawn_task(background_verification_worker(manager))
    spawn_task(live_refingerprint_worker(manager))
    spawn_task(broadcast_ui_state(manager))
    async with websockets.serve(ws_handler, "127.0.0.1", port):
        await asyncio.Future()
