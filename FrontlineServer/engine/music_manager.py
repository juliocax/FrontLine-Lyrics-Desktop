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
            if cover:
                self.current_cover = cover
        logging.info(
            f"Faixa definida: {title} - {artist} (fonte={source}, {len(lyrics or [])} linhas)"
        )
        if lyrics and self.preferred_language:
            self._schedule_on_main(apply_translation_in_background(self, self.preferred_language))

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

    def _on_media_snapshot(self, info):
        """Callback from MediaSessionWatcher — runs on the watcher's own thread.

        Auto-follow / seek / pause logic originates from Warith Adetayo's PR #2.
        """
        if info is None or self.manual_mode:
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
            if not info.tocando and not self.clock_paused:
                logging.info("Player pausado: congelando letra")
                self._pause_clock()
                self.media_paused = True
            elif info.tocando and self.clock_paused and self.media_paused:
                logging.info("Player retomado: descongelando letra")
                self._resume_clock()
                self.media_paused = False
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
                cover = cover_art.smtc_thumbnail_to_file_uri(info.capa_bytes, info.chave)
                if not cover:
                    cover = cover_art.fetch_cover_art(info.artista, info.titulo)
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
            status = "NOT_FOUND"
            overlay_msg = "Lyrics not found."
            # In Auto mode, don't get stuck here: the track was identified but
            # we found no synced lyrics. After a while, give up and go back
            # to listening for the next one.
            if self.auto_mode and self.not_found_since and (time.time() - self.not_found_since) > tuning.NOT_FOUND_GIVEUP_SECONDS:
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
        }
        # 10Hz * full lyrics recreated the ListBox on the C# side and bloated
        # RAM (OOM 8007000e). Only include the full list when it changed.
        sig = (id(self.synced_lyrics), self.current_language, len(full), status)
        if sig != self._last_full_lyrics_sig:
            self._last_full_lyrics_sig = sig
            payload["full_lyrics"] = full
        return payload
