"""
MusicManager: the state machine at the heart of FrontlineServer.

It owns "what track is playing, what lyrics we found for it, and where the
playback clock currently is", and coordinates between:
- audio_capture.AudioCapture      (recording system audio)
- recognition.ShazamRecognizer    (identifying a track from audio)
- media_session.MediaSessionWatcher (Windows SMTC now-playing info)
- lyrics / cover_art               (fetching synced lyrics + album art)
- translation                      (translating/romanizing lyrics)
- smtc_policy                      (deciding whether to trust the SMTC clock)

The auto-follow / seek / pause re-anchoring logic here was ported from
Warith Adetayo's PR #2 into this headless server.
"""

import asyncio
import logging
import threading
import time
from typing import Any, Dict, List, Optional, Tuple

import engine.cover_art as cover_art
import engine.lyrics as lyrics_mod
import engine.tuning as tuning
from engine.audio_capture import AudioCapture
from engine.recognition import ShazamRecognizer
from engine.media_session import MediaSessionWatcher
from engine.smtc_policy import sane_media_position, should_trust_smtc_clock
from engine.translation import (
    apply_translation_in_background,
    romanize_line,
    translate_line_raced,
    LINE_DISPATCH_EXECUTOR,
)
from engine.task_utils import spawn_task


def _fetch_lyrics_and_cover(artist: str, song: str):
    lines = lyrics_mod.fetch_lyrics_lrclib(artist, song)
    cover = cover_art.fetch_cover_art(artist, song)
    return lines, cover


class AutoHold:
    """After RESET, Auto mode should not immediately re-lock onto the same track.

    RESET goes to IDLE; the SMTC (polled ~1x/s) still sees the same song
    playing and would fire LISTEN again right away. This holds the
    (title, artist) key locked out until it changes, or the user clicks
    LISTEN / re-enables Auto.
    """

    def __init__(self, cooldown_s: float = 2.0):
        self._chave: Optional[Tuple[str, str]] = None
        self._hold_until: float = 0.0
        self._cooldown_s = cooldown_s

    def hold(self, chave: Optional[Tuple[str, str]], agora: Optional[float] = None) -> None:
        agora = time.monotonic() if agora is None else agora
        self._chave = chave
        self._hold_until = agora + self._cooldown_s

    @property
    def ativo(self) -> bool:
        return self._chave is not None or time.monotonic() < self._hold_until

    def release(self) -> None:
        self._chave = None
        self._hold_until = 0.0

    def deve_ignorar(self, chave: Optional[Tuple[str, str]], agora: Optional[float] = None) -> bool:
        agora = time.monotonic() if agora is None else agora
        em_cooldown = agora < self._hold_until
        if self._chave is None:
            return em_cooldown
        if chave is None or chave == self._chave:
            return True
        self.release()
        return False


class MusicManager:
    """Manages audio recording, Shazam recognition, lyrics fetching, and synchronization.

    Auto-follow via Windows Media Session, seek/pause re-anchoring and
    track-change guards were ported from Warith Adetayo's PR #2 into this
    headless server.
    """

    def __init__(self):
        self.audio = AudioCapture()
        self.recognizer = ShazamRecognizer()
        self.server_running = True
        self.overlay_font_size = 26
        self.auto_mode: bool = False
        self.last_translation_error: Optional[str] = None

        self.preferred_language: Optional[str] = None
        self._lock = threading.RLock()
        self._media_busy = False
        self._lrclib_fail_until: Dict[Tuple[str, str], float] = {}
        self._main_loop = None
        self.auto_hold = AutoHold(cooldown_s=2.0)
        self._last_full_lyrics_sig = None
        self._last_festival_sig = None
        # Festival Mode state lives here (not in reset_state()) so RESET/track
        # transitions during a show don't drop the playlist -- only
        # exit_festival_mode() clears it.
        self.festival_mode: bool = False
        self.festival_playlist: Optional[Dict[str, Any]] = None
        # Media Session (SMTC): contributed by Warith Adetayo, ported from PR #2.
        self.watcher = MediaSessionWatcher(self._on_media_snapshot)
        self.reset_state()

    # -- Auto-hold (RESET should not immediately re-lock the same track) --

    def _hold_current_track(self):
        """RESET: Auto must not re-lock the track that is currently playing."""
        chave = self.watcher.ignorar_faixa_atual()
        if not chave or chave == ("", ""):
            chave = self._track_key(self.current_song, self.current_artist)
            if chave == ("", ""):
                chave = None
            else:
                self.watcher.ignorar_chave(chave)
        self.auto_hold.hold(chave)
        logging.info("Limpar: Auto em hold para %s", chave)

    def _release_auto_hold(self):
        self.auto_hold.release()
        self.watcher.limpar_ignorada()

    def _auto_bloqueado(self, chave) -> bool:
        return self.auto_hold.deve_ignorar(chave) or self.watcher.chave_esta_ignorada(chave)

    # -- Session/state reset --

    def reset_state(self):
        """Resets the manager to its initial idle state."""
        self.session_id = time.time()
        self.current_artist: Optional[str] = None
        self.current_song: Optional[str] = None
        self.current_cover: str = ""
        self.system_reference_time: float = 0.0
        self.original_lyrics: List[Dict[str, Any]] = []
        self.synced_lyrics: List[Dict[str, Any]] = []
        self.cached_translations: Dict[str, List[Dict[str, Any]]] = {}
        self.current_language: str = "original"
        self.is_listening: bool = False
        self.search_completed: bool = False
        self.manual_mode: bool = False
        self.is_translating: bool = False
        self.listen_start_time: float = time.time()

        self.pending_candidate: Optional[Tuple[str, str]] = None
        self.pending_candidate_count: int = 0
        self.retry_delay: float = 2.0
        self.not_found_since: Optional[float] = None
        self.track_source: Optional[str] = None
        self.clock_paused: bool = False
        self.pause_moment: float = 0.0
        self.media_paused: bool = False
        self.media_baseline = None
        self.next_reanchor: float = 0.0
        self.calibrating_until: float = 0.0
        self._prev_drift = None
        self.previous_track: Optional[Tuple[str, str]] = None
        self.cooldown_until: float = 0.0
        self._lrclib_fail_until = {}
        # LISTEN always records+fingerprints; Auto on video/live does too.
        # Spotify / YouTube Music: Auto can ride the player's own timeline instead.
        self._listen_use_shazam = False
        self._clock_from_shazam = False
        self._media_busy = False
        self._next_live_fingerprint = 0.0
        self._live_fp_busy = False
        self._live_fp_drift_hits = 0
        self._cover_tried: set = set()

    # -- Track identity / cooldown helpers --

    def _track_key(self, song: Optional[str], artist: Optional[str]) -> Tuple[str, str]:
        return ((song or "").lower().strip(), (artist or "").lower().strip())

    def _same_track_still_playing(self) -> bool:
        """True if SMTC confirms the current track is still playing."""
        info = self.watcher.ultima_info
        if not info or not info.tocando:
            return False
        return info.chave == self._track_key(self.current_song, self.current_artist)

    def _in_previous_track_cooldown(self, song: Optional[str], artist: Optional[str]) -> bool:
        if not self.previous_track or time.time() >= self.cooldown_until:
            return False
        return self._track_key(song, artist) == self._track_key(*self.previous_track)

    # -- SMTC clock trust --

    def _session_trusts_song_clock(self, info=None) -> bool:
        src = info if info is not None else getattr(self.watcher, "ultima_info", None)
        app = getattr(src, "app", "") if src is not None else ""
        dur = getattr(src, "duracao", None) if src is not None else None
        pos = getattr(src, "posicao", None) if src is not None else None
        lyrics_end = None
        if self.synced_lyrics:
            try:
                lyrics_end = float(self.synced_lyrics[-1]["timestamp"])
            except (TypeError, ValueError, KeyError):
                lyrics_end = None
        return should_trust_smtc_clock(app, dur, pos, lyrics_end)

    def _should_use_audio_clock(self, info=None) -> bool:
        """True = Shazam fingerprint clock (live/video). False = SMTC (Spotify etc.)."""
        src = info if info is not None else getattr(self.watcher, "ultima_info", None)
        if src is None or not getattr(src, "titulo", None):
            return True
        return not self._session_trusts_song_clock(src)

    def _lyrics_follow_player(self) -> bool:
        """True when skip/seek/pause on the music app moves the lyric clock.

        Spotify, Apple Music, YouTube Music, etc. Festival Mode and Shazam/
        live/video clocks do not follow the player timeline this way.
        """
        if self.festival_mode:
            return False
        if getattr(self, "_clock_from_shazam", False):
            return False
        if self.track_source not in ("media", "shazam"):
            return False
        return self._session_trusts_song_clock()

    # -- Playback clock --

    def _pause_clock(self):
        if not self.clock_paused:
            self.clock_paused = True
            self.pause_moment = time.time()

    def _resume_clock(self):
        if self.clock_paused:
            self.system_reference_time += time.time() - self.pause_moment
            self.clock_paused = False
            self.pause_moment = 0.0

    def _elapsed_now(self) -> float:
        base = self.pause_moment if self.clock_paused else time.time()
        return base - self.system_reference_time

    def _start_transition(self, reason: str):
        """Track ended / change detected: go back to listening without resetting Auto mode."""
        with self._lock:
            if not self.search_completed:
                return
            self.previous_track = (self.current_song, self.current_artist)
            self.cooldown_until = time.time() + tuning.PREV_TRACK_COOLDOWN
            was_auto = self.auto_mode
            self.session_id = time.time()
            self.current_artist = None
            self.current_song = None
            self.current_cover = ""
            self.system_reference_time = 0.0
            self.original_lyrics = []
            self.synced_lyrics = []
            self.cached_translations = {}
            self.current_language = "original"
            self.search_completed = False
            self.manual_mode = False
            self.is_translating = False
            self.track_source = None
            self.clock_paused = False
            self.pause_moment = 0.0
            self.media_paused = False
            self.media_baseline = None
            self.next_reanchor = 0.0
            self.calibrating_until = 0.0
            self._prev_drift = None
            self.pending_candidate = None
            self.pending_candidate_count = 0
            self.not_found_since = None
            self.listen_start_time = time.time()
            self.is_listening = True if was_auto else False
            self._listen_use_shazam = False
            self._cover_tried = set()
            self._clock_from_shazam = False
            self._next_live_fingerprint = 0.0
            self._live_fp_busy = False
            self._live_fp_drift_hits = 0
        logging.info(f"Transição ({reason})")

    def _fresh_smtc_for(self, chave: Tuple[str, str]):
        """Latest SMTC snapshot for the same track, if it's still that track."""
        info = getattr(self.watcher, "ultima_info", None)
        if info is None:
            return None
        try:
            if info.chave != chave:
                return None
        except Exception:
            return None
        return info

    def _anchor_reference(self, chave: Tuple[str, str], fallback: float) -> float:
        """Anchor the clock on a fresh SMTC position; otherwise use fallback (Shazam/now)."""
        info = self._fresh_smtc_for(chave)
        pos = sane_media_position(getattr(info, "posicao", None) if info is not None else None)
        if pos is None:
            return fallback
        return time.time() - pos

    def _set_track(
        self,
        title: str,
        artist: str,
        reference_time: float,
        lyrics: Optional[List[Dict[str, Any]]],
        cover: str = "",
        source: str = "shazam",
        lock_shazam_clock: bool = False,
    ):
        if not cover:
            cover = cover_art.fetch_cover_art(artist, title)
        with self._lock:
            self.current_song, self.current_artist = title, artist
            self.track_source = source
            self.system_reference_time = reference_time
            self.original_lyrics = self.synced_lyrics = lyrics or []
            self.cached_translations = {}
            self.current_language = "original"
            self.search_completed = True
            self.clock_paused = False
            self.pause_moment = 0.0
            self.media_paused = False
            self._clock_from_shazam = lock_shazam_clock
            self._listen_use_shazam = False
            self._live_fp_drift_hits = 0
            if lock_shazam_clock:
                self.next_reanchor = 0.0
                self.calibrating_until = 0.0
                self._next_live_fingerprint = time.time() + tuning.LIVE_FINGERPRINT_PERIOD
            else:
                self.next_reanchor = time.time()
                self.calibrating_until = time.time() + tuning.CALIBRATION_WINDOW
                self._next_live_fingerprint = 0.0
            self._prev_drift = None
            self.not_found_since = None if lyrics else time.time()
            self.listen_start_time = time.time()
            self.is_listening = True
            self.current_cover = cover or ""
            self._cover_tried = {self._track_key(title, artist)}
        logging.info(
            f"Faixa definida: {title} - {artist} (fonte={source}, {len(lyrics or [])} linhas)"
        )
        if lyrics and self.preferred_language:
            self._schedule_on_main(apply_translation_in_background(self, self.preferred_language))

    # -- Festival Mode --

    def enter_festival_mode(self, name: str, songs: List[Tuple[str, str]]):
        """Starts Festival Mode with an initial (artist, song) list and preloads lyrics.

        `songs` may be empty (no setlist.fm match / no API key) -- the user
        builds the setlist by hand from there via festival_add_song.
        """
        with self._lock:
            self._release_auto_hold()
            self.auto_mode = False
            self.festival_mode = True
            self.festival_playlist = {
                "name": name or "Festival",
                "songs": [
                    {"artist": a, "song": s, "lyrics": None, "has_lyrics": None, "cover": ""} for a, s in songs
                ],
                "current_index": 0,
            }
        if songs:
            self._festival_load_song(0)
        else:
            self.reset_state()
            self.is_listening = True
        self._schedule_on_main(self._festival_preload_all())
        logging.info(f"Festival Mode iniciado: '{name}' com {len(songs)} música(s)")

    def exit_festival_mode(self):
        """Leaves Festival Mode. Persistence of the playlist is the frontend's job
        (it already mirrors festival_playlist via get_current_state)."""
        with self._lock:
            self.festival_mode = False
            self.festival_playlist = None
            self._festival_offer_advance = False
        self.reset_state()
        logging.info("Festival Mode encerrado")

    async def _festival_preload_all(self):
        """Fetches synced lyrics for every song in the current playlist, in parallel."""
        playlist = self.festival_playlist
        if not playlist:
            return
        loop = asyncio.get_event_loop()
        songs = playlist["songs"]

        async def load_one(i: int, entry: Dict[str, Any]):
            if entry.get("lyrics") is not None or entry.get("has_lyrics") is False:
                cover = entry.get("cover") or ""
                if not cover:
                    cover = await loop.run_in_executor(
                        None, cover_art.fetch_cover_art, entry["artist"], entry["song"]
                    )
                    playlist["songs"][i]["cover"] = cover
                    if playlist["current_index"] == i and cover and not self.current_cover:
                        self.current_cover = cover
                return 
            lines, cover = await loop.run_in_executor(
                None, _fetch_lyrics_and_cover, entry["artist"], entry["song"]
            )
            if self.festival_playlist is not playlist or i >= len(playlist["songs"]):
                return  # festival was exited / playlist replaced mid-preload
            playlist["songs"][i]["lyrics"] = lines
            playlist["songs"][i]["has_lyrics"] = bool(lines)
            if cover:
                playlist["songs"][i]["cover"] = cover
            # Refresh on-screen lyrics/cover if this is the song currently displayed
            # and it hadn't resolved yet (e.g. it was the very first song).
            if playlist["current_index"] == i:
                if not self.synced_lyrics and lines:
                    self.original_lyrics = self.synced_lyrics = lines
                if cover and not self.current_cover:
                    self.current_cover = cover

        await asyncio.gather(*(load_one(i, e) for i, e in enumerate(songs)))
        logging.info(f"Festival Mode: preload de {len(songs)} música(s) concluído")

    def _festival_next_entry(self):
        playlist = self.festival_playlist
        if not playlist:
            return None
        songs = playlist.get("songs") or []
        nxt = playlist.get("current_index", 0) + 1
        if nxt >= len(songs):
            return None
        return songs[nxt]

    def _festival_load_song(self, index: int):
        """Makes playlist[index] the current track, parked in manual sync
        (waiting for the user to tap the first line -- see SET_SYNC_TIME)."""
        playlist = self.festival_playlist
        if not playlist:
            return
        songs = playlist["songs"]
        if not (0 <= index < len(songs)):
            return
        entry = songs[index]
        cover = entry.get("cover") or ""
        with self._lock:
            playlist["current_index"] = index
            self.session_id = time.time()
            self.current_song, self.current_artist = entry["song"], entry["artist"]
            self.current_cover = cover or ""
            self.original_lyrics = self.synced_lyrics = entry.get("lyrics") or []
            self.cached_translations = {}
            self.current_language = "original"
            self.search_completed = True
            self.manual_mode = True
            self.is_listening = True
            self.track_source = "festival"
            # Clock parked at "now" until the user taps a line in the full-lyrics
            # view (FullLyricsList_SelectionChanged -> SET_SYNC_TIME) or the
            # quick prev/next controls (festival_jump_line) move it.
            self.clock_paused = True
            self.pause_moment = time.time()
            self.system_reference_time = time.time()
            self.media_paused = False
            self.not_found_since = None if entry.get("has_lyrics") else time.time()
            self._festival_offer_advance = False
        if not cover:
            self._fill_cover_background(entry["artist"], entry["song"], entry)
        logging.info(f"Festival Mode: tocando agora -> {entry['song']} - {entry['artist']}")

    def festival_next_song(self):
        if not self.festival_playlist:
            return
        self._festival_load_song(self.festival_playlist["current_index"] + 1)

    def festival_prev_song(self):
        if not self.festival_playlist:
            return
        self._festival_load_song(self.festival_playlist["current_index"] - 1)

    def festival_jump_line(self, delta: int):
        """Quick sync correction: move the anchor to the next/previous lyric
        line without opening the full manual-sync line list."""
        if not self.synced_lyrics:
            return
        elapsed = self._elapsed_now()
        idx = 0
        for i, item in enumerate(self.synced_lyrics):
            if elapsed >= item["timestamp"]:
                idx = i
            else:
                break
        new_idx = max(0, min(len(self.synced_lyrics) - 1, idx + delta))
        target_ts = self.synced_lyrics[new_idx]["timestamp"]
        with self._lock:
            self.system_reference_time = time.time() - target_ts
            self.clock_paused = False
            self.pause_moment = 0.0

    def festival_add_song(self, artist: str, song: str, make_current: bool = False):
        """Adds a song to the live playlist (or just moves the pointer if it's
        already there). Lyrics get filled in by _festival_preload_all() or, for
        a MANUAL_SEARCH hit, by festival_update_song_lyrics()."""
        playlist = self.festival_playlist
        if not playlist:
            return
        key = self._track_key(song, artist)
        songs = playlist["songs"]
        idx = next((i for i, e in enumerate(songs) if self._track_key(e["song"], e["artist"]) == key), None)
        if idx is None:
            songs.append({"artist": artist, "song": song, "lyrics": None, "has_lyrics": None, "cover": ""})
            idx = len(songs) - 1
        if make_current:
            playlist["current_index"] = idx

    def festival_update_song_lyrics(self, song: str, artist: str, lyrics: Optional[List[Dict[str, Any]]]):
        """Called after a MANUAL_SEARCH resolves while in Festival Mode, so the
        playlist entry reflects what was actually found (item 7)."""
        playlist = self.festival_playlist
        if not playlist:
            return
        key = self._track_key(song, artist)
        for entry in playlist["songs"]:
            if self._track_key(entry["song"], entry["artist"]) == key:
                entry["lyrics"] = lyrics
                entry["has_lyrics"] = bool(lyrics)
                return

    def festival_remove_song(self, index: int):
        playlist = self.festival_playlist
        if not playlist:
            return
        songs = playlist["songs"]
        if 0 <= index < len(songs):
            songs.pop(index)
            if playlist["current_index"] >= len(songs):
                playlist["current_index"] = max(0, len(songs) - 1)

    def festival_reorder_song(self, from_index: int, to_index: int):
        playlist = self.festival_playlist
        if not playlist:
            return
        songs = playlist["songs"]
        if 0 <= from_index < len(songs) and 0 <= to_index < len(songs):
            songs.insert(to_index, songs.pop(from_index))

    def _schedule_on_main(self, coro):
        """Schedule a coroutine on the server's main loop, even if called from the SMTC thread."""
        loop = getattr(self, "_main_loop", None)
        if loop is None or not loop.is_running():
            logging.warning("Loop principal indisponível para agendar task.")
            coro.close()
            return
        try:
            if asyncio.get_running_loop() is loop:
                spawn_task(coro)
                return
        except RuntimeError:
            pass
        asyncio.run_coroutine_threadsafe(coro, loop)

    # -- SMTC snapshot callback (runs on MediaSessionWatcher's own thread) --

    def _sync_media_pause(self, info):
        """Freeze/unfreeze the lyric clock with the player's play/pause."""
        if not (self.is_listening and self.search_completed and self.synced_lyrics):
            return
        if not getattr(info, "tocando", True) and not self.clock_paused:
            logging.info("Player pausado: congelando letra")
            self._pause_clock()
            self.media_paused = True
        elif getattr(info, "tocando", False) and self.clock_paused and self.media_paused:
            logging.info("Player retomado: descongelando letra")
            self._resume_clock()
            self.media_paused = False

    def _on_media_snapshot(self, info):
        """Callback from MediaSessionWatcher — runs on the watcher's own thread.

        Auto-follow / seek / pause logic originates from Warith Adetayo's PR #2.
        """
        if info is None:
            return
        if self.festival_mode:
            # Keep the setlist clock, but still freeze lyrics when the player pauses.
            self._sync_media_pause(info)
            return
        if self.manual_mode:
            return
        chave = info.chave
        self.watcher.preferencia_chave = chave

        # Auto-start only with AUTO on: music playing and the app idle.
        # After RESET, the same track is ignored until title/artist changes
        # (or the user clicks LISTEN / re-enables Auto).
        if (self.auto_mode and not self.is_listening
                and info.tocando and info.titulo):
            if self._auto_bloqueado(info.chave):
                logging.debug("Auto hold: não religar %s", info.chave)
            else:
                logging.info(f"Auto-início SMTC: {info.titulo} - {info.artista}")
                with self._lock:
                    self.reset_state()
                    self.is_listening = True
                    if not self._session_trusts_song_clock(info):
                        self._listen_use_shazam = True
                        logging.info(
                            "Auto em vídeo/ao vivo (%s): sync pelo Shazam, não pelo tempo do player",
                            getattr(info, "app", ""),
                        )

        synced = self.is_listening and self.search_completed and bool(self.synced_lyrics)

        if synced:
            faixa_atual = self._track_key(self.current_song, self.current_artist)
            if self.media_baseline is None:
                self.media_baseline = chave
            elif self.auto_mode and chave != self.media_baseline and chave != faixa_atual:
                logging.info(f"Troca de faixa SMTC: {self.current_song} -> {info.titulo}")
                self.media_baseline = chave
                if self._session_trusts_song_clock(info):
                    self._follow_metadata(info)
                else:
                    logging.info("Vídeo/ao vivo: não ancora no timeline do player; reescuta")
                    self._start_transition("vídeo/ao vivo")
                    self._listen_use_shazam = True
                    self._clock_from_shazam = False
                return
            self._sync_media_pause(info)
            self._maybe_fill_cover(info)
            if (info.tocando and not self.clock_paused
                    and self.track_source in ("media", "shazam")
                    and not self._clock_from_shazam
                    and self._session_trusts_song_clock(info)
                    and chave == faixa_atual):
                # Sync servo (Warith Adetayo): 12s calibration window with
                # single-sample correction, then partial drift correction
                # (~35%) afterwards; seeks >4s only confirmed with 2 samples
                # outside the window.
                pos = sane_media_position(info.posicao)
                if pos is None:
                    logging.debug("SMTC posição inválida ignorada: %r", info.posicao)
                else:
                    agora = time.time()
                    esperado = self._elapsed_now()
                    desvio = esperado - pos
                    em_calibragem = agora < self.calibrating_until
                    limite = tuning.MIN_DRIFT if not em_calibragem else 0.5
                    if abs(desvio) > limite and agora >= self.next_reanchor:
                        if pos < 1.0 and esperado > 15.0:
                            logging.info(
                                f"Posição suspeita ignorada: player={pos:.1f}s esperado={esperado:.1f}s"
                            )
                            self._prev_drift = None
                        elif abs(desvio) > tuning.SEEK_TOLERANCE:
                            confirmado = em_calibragem or (
                                self._prev_drift is not None
                                and agora - self._prev_drift[0] <= 6.0
                                and abs(self._prev_drift[1]) > tuning.SEEK_TOLERANCE
                            )
                            if not confirmado:
                                self._prev_drift = (agora, desvio)
                            else:
                                logging.info(f"Re-ancoragem por seek: {esperado:.1f}s -> {pos:.1f}s")
                                self.system_reference_time = agora - pos
                                self.next_reanchor = agora + (1.5 if em_calibragem else 10.0)
                                self._prev_drift = None
                        else:
                            fator = 1.0 if em_calibragem else tuning.PARTIAL_CORRECTION
                            logging.info(
                                f"Ajuste de sincronia ({'calibragem' if em_calibragem else 'parcial'}): desvio {desvio:+.2f}s"
                            )
                            self.system_reference_time += desvio * fator
                            self.next_reanchor = agora + (1.5 if em_calibragem else 5.0)
                            self._prev_drift = None
                    elif abs(desvio) <= limite:
                        self._prev_drift = None
        elif self.is_listening and not self.search_completed:
            if self._listen_use_shazam or not self._session_trusts_song_clock(info):
                if not self._listen_use_shazam and not self._session_trusts_song_clock(info):
                    self._listen_use_shazam = True
                return
            mesma_em_cooldown = self._in_previous_track_cooldown(info.titulo, info.artista)
            if (info.tocando and info.titulo and not self._media_busy
                    and not mesma_em_cooldown):
                self._follow_metadata(info)

    def _fallback_listen_to_smtc(self, reason: str) -> bool:
        """Audio failed. Only fall back to the player's clock if it's a
        streaming app (Spotify / YT Music). On video/live, SMTC is the
        video's clock, not the song's — keep retrying Shazam instead.
        """
        info = getattr(self.watcher, "ultima_info", None)
        if info is None or not info.tocando or not info.titulo:
            logging.info("Shazam falhou (%s) e não há SMTC; tenta áudio de novo", reason)
            return False
        if not self._session_trusts_song_clock(info):
            logging.info(
                "Shazam falhou (%s) em vídeo/ao vivo (%s); não usa o tempo do player",
                reason,
                getattr(info, "app", ""),
            )
            return False
        self._listen_use_shazam = False
        logging.info("Shazam falhou (%s); âncora SMTC %s - %s", reason, info.titulo, info.artista)
        self._follow_metadata(info)
        return True

    def _follow_metadata(self, info):
        """Instant switch driven by the player's own metadata (no Shazam). Streaming apps only."""
        if self._listen_use_shazam:
            return
        if not self._session_trusts_song_clock(info):
            logging.info("Ignora lock-in SMTC em vídeo/ao vivo (%s)", getattr(info, "app", ""))
            return
        if self._in_previous_track_cooldown(info.titulo, info.artista):
            return
        if time.monotonic() < self._lrclib_fail_until.get(info.chave, 0.0):
            return
        self._media_busy = True
        try:
            letra = lyrics_mod.fetch_lyrics_lrclib(info.artista, info.titulo)
            if letra:
                if self._listen_use_shazam:
                    logging.info("OUVIR em curso: ignora lock-in SMTC tardio")
                    return
                self._lrclib_fail_until.pop(info.chave, None)
                # The lookup takes ~1-2s; the player may have emitted a
                # newer position in that window. Anchor with the fresh read.
                info_fresca = self.watcher.ultima_info
                if info_fresca and info_fresca.chave == info.chave:
                    info = info_fresca
                pos = sane_media_position(info.posicao, fallback=0.0) or 0.0
                cover = cover_art.resolve_cover(
                    info.artista, info.titulo, info.capa_bytes or b"", info.chave
                )
                logging.info(
                    f"Letra via metadados: {info.titulo} - {info.artista} "
                    f"({len(letra)} linhas, pos {pos:.1f}s)"
                )
                self._set_track(
                    info.titulo,
                    info.artista,
                    reference_time=time.time() - pos,
                    lyrics=letra,
                    cover=cover,
                    source="media",
                )
                self.media_baseline = info.chave
            else:
                self._lrclib_fail_until[info.chave] = time.monotonic() + 15.0
                if self.search_completed and self.synced_lyrics:
                    logging.info(f"Sem letra no LRCLib para {info.titulo} - {info.artista}")
                    self._start_transition("metadados mudaram sem letra")
                else:
                    logging.info(
                        f"LRCLib miss via metadados ({info.titulo} - {info.artista}); "
                        "Shazam pode tentar com outro nome"
                    )
        finally:
            self._media_busy = False

    def _maybe_fill_cover(self, info):
        """SMTC thumbnails often arrive a beat after the track locks in. Pick them
        up later, and run one Deezer/iTunes lookup if the player never sends art."""
        if info is None or not self.current_song:
            return
        chave = self._track_key(self.current_song, self.current_artist)
        try:
            if info.chave != chave:
                return
        except Exception:
            return
        bytes_ = getattr(info, "capa_bytes", None) or b""
        if bytes_:
            uri = cover_art.smtc_thumbnail_to_file_uri(bytes_, info.chave)
            if uri and uri != self.current_cover:
                self.current_cover = uri
                return
        if self.current_cover or chave in getattr(self, "_cover_tried", set()):
            return
        self._cover_tried.add(chave)
        artist, title = self.current_artist, self.current_song
        session = self.session_id

        def _job():
            try:
                uri = cover_art.fetch_cover_art(artist, title)
                if uri and self.session_id == session and not self.current_cover:
                    self.current_cover = uri
            except Exception as e:
                logging.warning("Capa em background falhou: %s", e)

        threading.Thread(target=_job, daemon=True, name="cover-fill").start()

    def _fill_cover_background(self, artist: str, song: str, entry: Optional[Dict[str, Any]] = None):
        chave = self._track_key(song, artist)
        session = self.session_id

        def _job():
            try:
                uri = cover_art.fetch_cover_art(artist, song)
                if not uri:
                    return
                if entry is not None:
                    entry["cover"] = uri
                if (self.session_id == session
                        and self._track_key(self.current_song, self.current_artist) == chave):
                    self.current_cover = uri
            except Exception as e:
                logging.warning("Capa em background falhou: %s", e)

        threading.Thread(target=_job, daemon=True, name="cover-fill").start()

    # -- Translation orchestration (uses translation.py's line-racing primitive) --

    def generate_translation(self, target_language: str) -> bool:
        """Translates or romanizes the original lyrics."""
        if not self.original_lyrics:
            logging.warning(f"generate_translation('{target_language}') chamado sem letra original carregada.")
            return False
        if target_language in self.cached_translations:
            return True

        logging.info(f"Traduzindo {len(self.original_lyrics)} linha(s) para '{target_language}'...")
        try:
            if target_language.lower() == "romanized":
                translated_lines = []
                for item in self.original_lyrics:
                    text = str(item['text']) if item['text'] else ""
                    romanized = romanize_line(text)
                    translated_lines.append({"timestamp": item['timestamp'], "text": romanized})

                self.cached_translations[target_language] = translated_lines
                logging.info(f"Romanização concluída ({len(translated_lines)} linha(s)).")
                return True
            else:
                translated_lines = []
                failures = 0
                source_wins: Dict[str, int] = {}

                line_futures: List[Optional[Any]] = []
                for item in self.original_lyrics:
                    text = item['text']
                    if not text or not text.strip():
                        line_futures.append(None)
                        continue
                    line_futures.append(LINE_DISPATCH_EXECUTOR.submit(translate_line_raced, text, target_language))

                for idx, (item, fut) in enumerate(zip(self.original_lyrics, line_futures)):
                    if fut is None:
                        translated_lines.append({"timestamp": item['timestamp'], "text": item['text']})
                        continue
                    text = item['text']
                    try:
                        translated, winning_source = fut.result()
                    except Exception as line_err:
                        logging.warning(f"Falha ao traduzir linha {idx} ('{text[:40]}'): {line_err}")
                        translated, winning_source = None, None
                    if not translated:
                        translated = text
                        failures += 1
                    else:
                        source_wins[winning_source] = source_wins.get(winning_source, 0) + 1
                    translated_lines.append({"timestamp": item['timestamp'], "text": translated})

                if source_wins:
                    logging.info(f"Fontes vencedoras nesta tradução: {source_wins}")

                if failures and failures == len([l for l in self.original_lyrics if l['text'] and l['text'].strip()]):
                    logging.error(f"Todas as {failures} linha(s) falharam ao traduzir para '{target_language}'.")
                    return False

                if failures:
                    logging.warning(f"{failures} linha(s) não foram traduzidas (mantidas no original) para '{target_language}'.")

                self.cached_translations[target_language] = translated_lines
                logging.info(f"Tradução para '{target_language}' concluída ({len(translated_lines)} linha(s), {failures} falha(s)).")
                return True
        except Exception as e:
            logging.error(f"Translation error (target='{target_language}'): {e}", exc_info=True)
            return False

    def apply_language(self, lang: str) -> bool:
        """Sets the current lyrics language, triggering translation if needed.

        Only switches current_language/synced_lyrics if the operation actually
        succeeded -- otherwise a translation error used to leave
        is_translated_active=True while the old (original) lyrics stayed on
        screen, which looked like the language "didn't take".
        """
        if lang == "original":
            self.current_language = lang
            self.synced_lyrics = self.original_lyrics
            return True

        if self.generate_translation(lang):
            self.current_language = lang
            self.synced_lyrics = self.cached_translations[lang]
            return True

        logging.warning(f"Falha ao aplicar idioma '{lang}'; mantendo '{self.current_language}'.")
        return False

    # -- State snapshot sent to the WebSocket clients --

    def get_current_state(self) -> Dict[str, Any]:
        """Calculates the current line based on playback time and returns the full app state."""
        current_line, previous_line, next_line = "", "", ""
        current_line_original = ""
        overlay_msg, status = "", ""

        if not self.is_listening:
            status = "IDLE"
        elif self.is_listening and not self.current_song:
            status = "LISTENING"
        elif self.current_song and not self.search_completed:
            status = "SEARCHING"
        elif self.search_completed and not self.synced_lyrics:
            waiting_lyrics = False
            if self.festival_mode and self.festival_playlist:
                songs = self.festival_playlist.get("songs") or []
                idx = self.festival_playlist.get("current_index", 0)
                if 0 <= idx < len(songs) and songs[idx].get("has_lyrics") is None:
                    waiting_lyrics = True
            if waiting_lyrics:
                status = "SEARCHING"
            else:
                status = "NOT_FOUND"
                overlay_msg = "Lyrics not found."
                # In Auto mode, don't get stuck here: the track was identified but
                # we found no synced lyrics. After a while, give up and go back
                # to listening for the next one.
                if (self.auto_mode and not self.festival_mode and self.not_found_since
                        and (time.time() - self.not_found_since) > tuning.NOT_FOUND_GIVEUP_SECONDS):
                    self.reset_state()
                    self.is_listening = True
                    status = "LISTENING"
                    overlay_msg = ""
        elif self.synced_lyrics:
            status = "SYNCED"
            elapsed_time = self._elapsed_now()

            if elapsed_time < self.synced_lyrics[0]['timestamp']:
                current_line = "♫"
                next_line = self.synced_lyrics[0]['text']
            else:
                for i, item in enumerate(self.synced_lyrics):
                    if elapsed_time >= item['timestamp']:
                        current_line = item['text'] if item['text'].strip() else "♫"
                        previous_line = self.synced_lyrics[i-1]['text'] if i > 0 else ""
                        next_line = self.synced_lyrics[i+1]['text'] if i + 1 < len(self.synced_lyrics) else ""
                        if self.current_language != "original" and i < len(self.original_lyrics):
                            current_line_original = self.original_lyrics[i]['text']
                    else:
                        break

            # Lyrics ending only triggers a track change if the player does
            # NOT confirm the same track still playing (avoids looping on an
            # instrumental break or another song). Without SMTC, falls back
            # to the original timeout. Source: PR #2, Warith Adetayo.
            lyrics_ended = elapsed_time > self.synced_lyrics[-1]['timestamp'] + tuning.END_OF_LYRICS_GRACE
            if self.festival_mode and lyrics_ended and self._festival_next_entry() is not None:
                if not self.clock_paused:
                    self._pause_clock()
                self._festival_offer_advance = True
            if (not self.manual_mode and not self.clock_paused and lyrics_ended
                    and not self._same_track_still_playing()):
                was_auto = self.auto_mode
                self._start_transition("fim_letra")
                if was_auto:
                    status = "LISTENING"
                else:
                    status = "IDLE"
                overlay_msg = ""
            elif lyrics_ended and current_line in ("End", "♫"):
                current_line = "♪"

        if status != "SYNCED":
            current_line = overlay_msg

        full = [{"timestamp": i["timestamp"], "text": i["text"]} for i in self.synced_lyrics] if self.synced_lyrics else []
        payload = {
            "status": status,
            "auto_mode": self.auto_mode,
            "is_translating": self.is_translating,
            "current_lyrics": current_line,
            "previous_lyrics": previous_line,
            "next_lyrics": next_line,
            "current_lyrics_original": current_line_original,
            "is_translated_active": self.current_language != "original",
            "current_language": self.current_language,
            "translation_error": self.last_translation_error,
            "font_size": self.overlay_font_size,
            "song": self.current_song,
            "artist": self.current_artist,
            "cover_art": getattr(self, "current_cover", ""),
            "player_clock": self._lyrics_follow_player(),
        }
        # 10Hz * full lyrics recreated the ListBox on the C# side and bloated
        # RAM (OOM 8007000e). Only include the full list when it changed.
        sig = (id(self.synced_lyrics), self.current_language, len(full), status)
        if sig != self._last_full_lyrics_sig:
            self._last_full_lyrics_sig = sig
            payload["full_lyrics"] = full

        payload["festival_mode"] = self.festival_mode
        if getattr(self, "_festival_offer_advance", False):
            nxt = self._festival_next_entry()
            if nxt:
                payload["festival_advance"] = {
                    "artist": nxt.get("artist") or "",
                    "song": nxt.get("song") or "",
                }
            else:
                self._festival_offer_advance = False
        if self.festival_mode and self.festival_playlist:
            fp = self.festival_playlist
            fp_sig = (id(fp), fp["current_index"], tuple((e["song"], e["artist"], e["has_lyrics"]) for e in fp["songs"]))
            if fp_sig != self._last_festival_sig:
                self._last_festival_sig = fp_sig
                payload["festival_playlist"] = {
                    "name": fp["name"],
                    "current_index": fp["current_index"],
                    "songs": [
                        {"artist": e["artist"], "song": e["song"], "has_lyrics": e["has_lyrics"]}
                        for e in fp["songs"]
                    ],
                }
        return payload