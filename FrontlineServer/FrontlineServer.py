import sys
import os
from crash_guard import instalar as instalar_crash_guard
instalar_crash_guard()
import time
import asyncio
import json
import wave
import io
import traceback
import subprocess
import logging
import threading
import hashlib
from pathlib import Path
from typing import Optional, Tuple, List, Dict, Any
import requests
import re
import array
import math
import websockets
import pyaudiowpatch as pyaudio
from shazamio import Shazam
from deep_translator import GoogleTranslator, MyMemoryTranslator
from anyascii import anyascii
from logging.handlers import RotatingFileHandler
from concurrent.futures import ThreadPoolExecutor, FIRST_COMPLETED, wait as futures_wait
from media_session import MediaSessionWatcher, MEDIA_SESSION_DISPONIVEL

if sys.platform == "win32":
    _original_popen_init = subprocess.Popen.__init__

    def _hidden_window_popen_init(self, *args, **kwargs):
        startupinfo = kwargs.get("startupinfo")
        if startupinfo is None:
            startupinfo = subprocess.STARTUPINFO()
        startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        kwargs["startupinfo"] = startupinfo
        kwargs["creationflags"] = kwargs.get("creationflags", 0) | subprocess.CREATE_NO_WINDOW
        _original_popen_init(self, *args, **kwargs)

    subprocess.Popen.__init__ = _hidden_window_popen_init

os.environ.setdefault("translators_default_region", "EN")
import translators as ts

LOG_DIR = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "FrontLineLyrics", "logs")
try:
    os.makedirs(LOG_DIR, exist_ok=True)
except Exception:
    LOG_DIR = os.path.dirname(os.path.abspath(__file__))
LOG_FILE = os.path.join(LOG_DIR, "frontline_server.log")

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s',
    handlers=[
        RotatingFileHandler(LOG_FILE, maxBytes=2_000_000, backupCount=3, encoding="utf-8"),
    ]
)
logging.info(f"Log iniciado em: {LOG_FILE}")
logging.info("Build do servidor: tradução com 3 fontes em paralelo (google/mymemory/translators-bing) v1")
if MEDIA_SESSION_DISPONIVEL:
    logging.info("Media Session (SMTC) disponível — auto-follow de metadados ligado.")
else:
    logging.info("winrt não instalado: auto-follow via Media Session desligado (Shazam permanece).")

def global_exception_handler(exctype, value, tb):
    """Custom exception handler to log critical errors."""
    logging.critical("=== CRITICAL ERROR ENCOUNTERED ===")
    logging.critical("".join(traceback.format_exception(exctype, value, tb)))
sys.excepthook = global_exception_handler
# Envolve o hook acima para também gravar python_crash.log.
instalar_crash_guard()

_background_tasks: set = set()

def spawn_task(coro):
    """Cria uma task em background sem risco dela ser coletada pelo GC antes de terminar."""
    task = asyncio.create_task(coro)
    _background_tasks.add(task)
    task.add_done_callback(_background_tasks.discard)
    return task


# Posição SMTC absurda (NaN, negativa, >12h) é lixo de alguns players.
MAX_MEDIA_POSITION_S = 12 * 3600


def _sane_media_position(value, fallback: Optional[float] = None) -> Optional[float]:
    """Aceita só posições finitas em [0, 12h]. Usado no servo de sync (Warith Adetayo)."""
    try:
        pos = float(value)
    except (TypeError, ValueError):
        return fallback
    if not math.isfinite(pos) or pos < 0.0 or pos > MAX_MEDIA_POSITION_S:
        return fallback
    return pos

class AutoHold:
    """Depois do Limpar, o Auto não religa a faixa atual até ela mudar.

    RESET vai para IDLE; o SMTC (1x/s) via a mesma música tocando e disparava
    LISTEN de novo. Aqui a chave (título, artista) fica bloqueada até mudar,
    ou o usuário clicar OUVIR / religar o Auto.
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

    Auto-follow via Windows Media Session, seek/pause re-anchoring and track-change
    guards were ported from Warith Adetayo's PR #2 into this headless server.
    """
    
    def __init__(self):
        self.shazam = Shazam()
        self.server_running = True   
        self.pyaudio_instance = pyaudio.PyAudio()
        self.device_info = self._configure_loopback()
        self.overlay_font_size = 26
        self.auto_mode: bool = False
        self.last_translation_error: Optional[str] = None

        self.preferred_language: Optional[str] = None
        self._lock = threading.RLock()
        self._media_busy = False
        self._main_loop = None
        self.auto_hold = AutoHold(cooldown_s=2.0)
        self._last_full_lyrics_sig = None
        # Media Session (SMTC): contribuição de Warith Adetayo, portada do PR #2.
        self.watcher = MediaSessionWatcher(self._on_media_snapshot)
        self.reset_state()

    def _hold_current_track(self):
        """Limpar: Auto não religa a faixa que está tocando agora."""
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

    def _configure_loopback(self) -> Optional[Dict[str, Any]]:
        """Configures WASAPI loopback to record system audio."""
        try:
            wasapi_info = self.pyaudio_instance.get_host_api_info_by_type(pyaudio.paWASAPI)
            default_speakers = self.pyaudio_instance.get_device_info_by_index(wasapi_info["defaultOutputDevice"])
            
            if not default_speakers["isLoopbackDevice"]:
                for loopback in self.pyaudio_instance.get_loopback_device_info_generator():
                    if default_speakers["name"] in loopback["name"]:
                        return loopback
            return default_speakers
        except Exception as e: 
            logging.error(f"Error configuring loopback: {e}")
            return None

    def record_audio_to_memory(self, duration: float) -> bytes:
        """Records system audio for a given duration and returns it as WAV bytes."""
        if not self.device_info: 
            raise Exception("Audio device error.")
        
        chunk = 512
        channels = self.device_info["maxInputChannels"]
        rate = int(self.device_info["defaultSampleRate"])
        
        stream = self.pyaudio_instance.open(
            format=pyaudio.paInt16, 
            channels=channels, 
            rate=rate,
            frames_per_buffer=chunk, 
            input=True, 
            input_device_index=self.device_info["index"]
        )
        
        frames = [stream.read(chunk) for _ in range(0, int(rate / chunk * duration))]
        stream.stop_stream()
        stream.close()
        
        audio_buffer = io.BytesIO()
        with wave.open(audio_buffer, 'wb') as wf:
            wf.setnchannels(channels)
            wf.setsampwidth(self.pyaudio_instance.get_sample_size(pyaudio.paInt16))
            wf.setframerate(rate)
            wf.writeframes(b''.join(frames))
            
        return audio_buffer.getvalue() 

    @staticmethod
    def _pcm_rms(audio_bytes: bytes) -> float:
        """Calcula o RMS (energia) de um WAV in-memory a partir dos bytes crus do buffer.
        Usada como 'gate' de silêncio: evita gastar reconhecimento Shazam em trechos
        mudos/quase mudos (tela de seleção de música, transição, propaganda sem áudio, etc).
        Python 3.13 removeu o módulo 'audioop', então calculamos na mão com 'array'."""
        try:
            with io.BytesIO(audio_bytes) as buf, wave.open(buf, 'rb') as wf:
                raw = wf.readframes(wf.getnframes())
            if not raw:
                return 0.0
            samples = array.array('h')  # int16
            samples.frombytes(raw[: len(raw) - (len(raw) % 2)])
            if not samples:
                return 0.0
            sum_sq = sum(s * s for s in samples)
            return math.sqrt(sum_sq / len(samples))
        except Exception:
            return 0.0

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

    def _track_key(self, song: Optional[str], artist: Optional[str]) -> Tuple[str, str]:
        return ((song or "").lower().strip(), (artist or "").lower().strip())

    def _same_track_still_playing(self) -> bool:
        """True se o SMTC confirma que a faixa atual ainda está tocando."""
        info = self.watcher.ultima_info
        if not info or not info.tocando:
            return False
        return info.chave == self._track_key(self.current_song, self.current_artist)

    def _in_previous_track_cooldown(self, song: Optional[str], artist: Optional[str]) -> bool:
        if not self.previous_track or time.time() >= self.cooldown_until:
            return False
        return self._track_key(song, artist) == self._track_key(*self.previous_track)

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

    def _cover_bytes_to_url(self, capa_bytes: bytes, chave: Tuple[str, str]) -> str:
        """Grava o thumbnail do SMTC em disco para o C# carregar via file://."""
        if not capa_bytes:
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
        digest = hashlib.sha1(f"{chave[0]}|{chave[1]}".encode("utf-8", errors="ignore")).hexdigest()
        path = os.path.join(covers_dir, f"{digest}.jpg")
        try:
            if not os.path.exists(path) or os.path.getsize(path) == 0:
                with open(path, "wb") as fh:
                    fh.write(capa_bytes)
            return Path(path).resolve().as_uri()
        except Exception as e:
            logging.warning(f"Falha ao gravar capa SMTC: {e}")
            return ""

    def _start_transition(self, reason: str):
        """Fim de faixa / troca detectada: volta a escutar sem zerar o modo Auto."""
        with self._lock:
            if not self.search_completed:
                return
            self.previous_track = (self.current_song, self.current_artist)
            self.cooldown_until = time.time() + PREV_TRACK_COOLDOWN
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
        logging.info(f"Transição ({reason})")

    def _fresh_smtc_for(self, chave: Tuple[str, str]):
        """Último snapshot SMTC da mesma faixa, se ainda for ela."""
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
        """Âncora o relógio na posição SMTC fresca; senão usa fallback (Shazam/now)."""
        info = self._fresh_smtc_for(chave)
        pos = _sane_media_position(getattr(info, "posicao", None) if info is not None else None)
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
            # A janela de 12s precisa começar já no lock-in. O +5s antigo
            # pulava exatamente o trecho em que a âncora SMTC vem velha
            # Calibragem + servo: Warith Adetayo.
            self.next_reanchor = time.time()
            self.calibrating_until = time.time() + CALIBRATION_WINDOW
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
        """Agenda coroutine no loop do servidor, mesmo se chamado da thread do SMTC."""
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

    def _on_media_snapshot(self, info):
        """Callback do MediaSessionWatcher — roda na thread do watcher.

        Lógica de auto-follow / seek / pause originada no PR #2 de Warith Adetayo.
        """
        if info is None or self.manual_mode:
            return
        chave = info.chave
        self.watcher.preferencia_chave = chave

        # Auto-início só com AUTO ligado: música tocando e app parado.
        # Depois de Limpar, a mesma faixa é ignorada até mudar título/artista
        # (ou o usuário clicar OUVIR / religar o Auto).
        if (self.auto_mode and not self.is_listening
                and info.tocando and info.titulo):
            if self._auto_bloqueado(info.chave):
                logging.debug("Auto hold: não religar %s", info.chave)
            else:
                logging.info(f"Auto-início SMTC: {info.titulo} - {info.artista}")
                with self._lock:
                    self.reset_state()
                    self.is_listening = True

        synced = self.is_listening and self.search_completed and bool(self.synced_lyrics)

        if synced:
            faixa_atual = self._track_key(self.current_song, self.current_artist)
            if self.media_baseline is None:
                self.media_baseline = chave
            elif self.auto_mode and chave != self.media_baseline and chave != faixa_atual:
                logging.info(f"Troca de faixa SMTC: {self.current_song} -> {info.titulo}")
                self.media_baseline = chave
                self._follow_metadata(info)
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
                    and chave == faixa_atual):
                # Servo de sync (Warith Adetayo): 12s de calibragem com
                # correção de uma amostra + deriva parcial (~35%) depois,
                # seeks >4s só confirmados com 2 amostras fora da janela.
                pos = _sane_media_position(info.posicao)
                if pos is None:
                    logging.debug("SMTC posição inválida ignorada: %r", info.posicao)
                else:
                    agora = time.time()
                    esperado = self._elapsed_now()
                    desvio = esperado - pos
                    em_calibragem = agora < self.calibrating_until
                    limite = MIN_DRIFT if not em_calibragem else 0.5
                    if abs(desvio) > limite and agora >= self.next_reanchor:
                        if pos < 1.0 and esperado > 15.0:
                            logging.info(
                                f"Posição suspeita ignorada: player={pos:.1f}s esperado={esperado:.1f}s"
                            )
                            self._prev_drift = None
                        elif abs(desvio) > SEEK_TOLERANCE:
                            confirmado = em_calibragem or (
                                self._prev_drift is not None
                                and agora - self._prev_drift[0] <= 6.0
                                and abs(self._prev_drift[1]) > SEEK_TOLERANCE
                            )
                            if not confirmado:
                                self._prev_drift = (agora, desvio)
                            else:
                                logging.info(f"Re-ancoragem por seek: {esperado:.1f}s -> {pos:.1f}s")
                                self.system_reference_time = agora - pos
                                self.next_reanchor = agora + (1.5 if em_calibragem else 10.0)
                                self._prev_drift = None
                        else:
                            fator = 1.0 if em_calibragem else PARTIAL_CORRECTION
                            logging.info(
                                f"Ajuste de sincronia ({'calibragem' if em_calibragem else 'parcial'}): desvio {desvio:+.2f}s"
                            )
                            self.system_reference_time += desvio * fator
                            self.next_reanchor = agora + (1.5 if em_calibragem else 5.0)
                            self._prev_drift = None
                    elif abs(desvio) <= limite:
                        self._prev_drift = None
        elif self.is_listening and not self.search_completed:
            mesma_em_cooldown = self._in_previous_track_cooldown(info.titulo, info.artista)
            if (info.tocando and info.titulo and not self._media_busy
                    and not mesma_em_cooldown):
                self._follow_metadata(info)

    def _follow_metadata(self, info):
        """Troca instantânea via metadados do player (sem Shazam)."""
        if self._in_previous_track_cooldown(info.titulo, info.artista):
            return
        self._media_busy = True
        try:
            letra = self.fetch_lyrics_lrclib(info.artista, info.titulo)
            if letra:
                # A busca demora ~1–2s; nesse intervalo o player pode ter
                # emitido uma posição mais recente. Ancorar com a leitura nova.
                info_fresca = self.watcher.ultima_info
                if info_fresca and info_fresca.chave == info.chave:
                    info = info_fresca
                pos = _sane_media_position(info.posicao, fallback=0.0) or 0.0
                cover = self._cover_bytes_to_url(info.capa_bytes, info.chave)
                if not cover:
                    cover = self.fetch_cover_art(info.artista, info.titulo)
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
            elif self.search_completed and self.synced_lyrics:
                logging.info(f"Sem letra no LRCLib para {info.titulo} - {info.artista}")
                self._start_transition("metadados mudaram sem letra")
        finally:
            self._media_busy = False

    async def recognize_audio_snippet(self, audio_bytes: bytes) -> Tuple[Optional[str], Optional[str], float, str]:
        """Sends audio to Shazam to identify the track."""
        try:
            result = await self.shazam.recognize(audio_bytes)
            if result and 'track' in result:
                track = result['track']
                cover_art = track.get('images', {}).get('coverart', '')
                offset = result.get('matches', [{}])[0].get('offset', 0.0)
                return track.get('title'), track.get('subtitle'), offset, cover_art
            return None, None, 0.0, ""
        except Exception as e: 
            logging.error(f"Shazam recognition error: {e}")
            return None, None, 0.0, ""

    def fetch_lyrics_lrclib(self, artist: str, song: str) -> Optional[List[Dict[str, Any]]]:
        """Fetches synchronized lyrics from the LRCLIB API."""
        headers = {"User-Agent": "FrontLineLyricsApp/1.0.0"}
        
        def extract_lines(synced_lyrics: str) -> List[Dict[str, Any]]:
            lines = []
            pattern = re.compile(r'\[(\d{2,}):(\d{2}(?:\.\d{1,3})?)\](.*)')
            for line in synced_lyrics.split('\n'):
                match = pattern.match(line)
                if match:
                    timestamp = (int(match.group(1)) * 60) + float(match.group(2))
                    text = match.group(3).strip()
                    if text: lines.append({"timestamp": timestamp, "text": text})
            return lines

        clean_song = re.sub(r'\([^)]*\)', '', song).strip()
        clean_artist = artist.split('feat.')[0].split('&')[0].strip()

        from requests.adapters import HTTPAdapter
        from urllib3.util.retry import Retry
        
        session = requests.Session()
        retries = Retry(total=2, backoff_factor=1, status_forcelist=[ 429, 500, 502, 503, 504 ])
        session.mount('https://', HTTPAdapter(max_retries=retries))

        try:
            r = session.get(
                "https://lrclib.net/api/get", 
                params={"track_name": clean_song, "artist_name": clean_artist}, 
                headers=headers, 
                timeout=10
            )
            if r.status_code == 200 and r.json().get("syncedLyrics"):
                lines = extract_lines(r.json()["syncedLyrics"])
                if lines: return lines + [{"timestamp": lines[-1]["timestamp"] + 5.0, "text": "End"}]
        except Exception as e:
            logging.warning(f"Exact match search failed (lrclib): {e}")

        try:
            r = session.get(
                "https://lrclib.net/api/search", 
                params={"q": f"{clean_song} {clean_artist}"}, 
                headers=headers, 
                timeout=10 
            )
            if r.status_code == 200:
                results = r.json()
                for item in results:
                    if isinstance(item, dict) and item.get("syncedLyrics"):
                        result_artist = item.get("artistName", "").lower()
                        if clean_artist.lower() in result_artist or result_artist in clean_artist.lower():
                            lines = extract_lines(item["syncedLyrics"])
                            if lines: return lines + [{"timestamp": lines[-1]["timestamp"] + 5.0, "text": "End"}]
        except Exception as e:
            logging.warning(f"Broad search failed (lrclib): {e}")

        return None

    def _translate_line_raced(self, text: str, target_language: str) -> Tuple[Optional[str], Optional[str]]:
        """Traduz uma linha correndo 3 fontes gratuitas em paralelo (não uma depois da
        outra) e fica com a PRIMEIRA resposta válida. As fontes perdedoras não são
        canceladas (thread já em execução), só são ignoradas -- então rodar em paralelo
        nunca deixa a linha mais lenta que a fonte mais rápida, e se uma fonte cair ou
        travar (como o scraping do Google quebrou), as outras cobrem sem atraso extra.
        Retorna (texto_traduzido, nome_da_fonte_vencedora) ou (None, None) se as 3 falharem
        dentro do timeout."""

        def _google():
            return GoogleTranslator(source='auto', target=target_language).translate(text)

        def _mymemory():
            return MyMemoryTranslator(source='auto', target=target_language).translate(text)

        def _translators_pkg():

            return ts.translate_text(
                text, translator='bing', from_language='auto', to_language=target_language,
                timeout=6.0, if_print_warning=False,
            )

        futures = {
            TRANSLATION_EXECUTOR.submit(_google): "google",
            TRANSLATION_EXECUTOR.submit(_mymemory): "mymemory",
            TRANSLATION_EXECUTOR.submit(_translators_pkg): "translators/bing",
        }

        deadline = time.monotonic() + LINE_TRANSLATE_TIMEOUT
        pending = set(futures.keys())

        while pending:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            done, pending = futures_wait(pending, timeout=remaining, return_when=FIRST_COMPLETED)
            for fut in done:
                source_name = futures[fut]
                try:
                    result = fut.result()
                except Exception as e:
                    logging.warning(f"Fonte de tradução '{source_name}' falhou: {e}")
                    continue
                if result and str(result).strip():
                    candidate = str(result).strip()
                    if _looks_like_bogus_translation(candidate, text):
                        logging.warning(f"Fonte '{source_name}' devolveu algo que parece erro/boilerplate em vez de tradução ('{candidate[:60]}...'); ignorando.")
                        continue
                    return candidate, source_name
                logging.warning(f"Fonte de tradução '{source_name}' retornou vazio.")

        return None, None

    def generate_translation(self, target_language: str) -> bool:
        """Translates or romanizes the original lyrics."""
        if not self.original_lyrics: 
            logging.warning(f"generate_translation('{target_language}') chamado sem letra original carregada.")
            return False
        if target_language in self.cached_translations: return True 
        
        logging.info(f"Traduzindo {len(self.original_lyrics)} linha(s) para '{target_language}'...")
        try:
            if target_language.lower() == "romanized":
                translated_lines = []
                for item in self.original_lyrics:
                    text = str(item['text']) if item['text'] else ""
                    try: romanized = anyascii(text).capitalize()
                    except Exception: romanized = text
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
                    line_futures.append(LINE_DISPATCH_EXECUTOR.submit(self._translate_line_raced, text, target_language))

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
        Só troca current_language/synced_lyrics se a operação realmente deu certo --
        antes disso, um erro de tradução deixava is_translated_active=True com a
        letra antiga (original) ainda na tela, parecendo que "não tinha pego"."""
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
    

    def fetch_cover_art(self, artist: str, song: str) -> str:
        """Pesquisa a capa do álbum através da API do Deezer para evitar coletâneas."""
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
            # No modo Auto, não trava aqui: a música foi identificada mas não achamos
            # letra sincronizada. Depois de um tempo, desiste e volta a escutar a próxima.
            if self.auto_mode and self.not_found_since and (time.time() - self.not_found_since) > NOT_FOUND_GIVEUP_SECONDS:
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

            # Fim da letra só dispara troca se o player NÃO confirmar a mesma
            # faixa ainda tocando (evita loop em instrumental/outro). Sem SMTC,
            # cai no timeout original. Origem: PR #2, Warith Adetayo.
            lyrics_ended = elapsed_time > self.synced_lyrics[-1]['timestamp'] + END_OF_LYRICS_GRACE
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
        # 10 Hz * letra inteira recriava o ListBox no C# e inflava RAM (OOM 8007000e).
        sig = (id(self.synced_lyrics), self.current_language, len(full), status)
        if sig != self._last_full_lyrics_sig:
            self._last_full_lyrics_sig = sig
            payload["full_lyrics"] = full
        return payload

manager = MusicManager()
connected_clients = set()

SILENCE_RMS_THRESHOLD = 180.0
AUTO_MIN_RETRY_DELAY = 2.0
AUTO_MAX_RETRY_DELAY = 20.0
AUTO_BACKOFF_MULTIPLIER = 1.7
NOT_FOUND_GIVEUP_SECONDS = 10.0
# Guards de faixa / seek — originados no PR #2 de Warith Adetayo.
# Calibragem (~12s, correção de uma amostra, tolerância 0.5s) + servo de
# deriva (~35% por ajuste, seeks >4s exigem duas amostras fora da janela).
END_OF_LYRICS_GRACE = 2.0
PREV_TRACK_COOLDOWN = 25.0
SEEK_TOLERANCE = 4.0
MIN_DRIFT = 0.8
CALIBRATION_WINDOW = 12.0
PARTIAL_CORRECTION = 0.35

LINE_DISPATCH_EXECUTOR = ThreadPoolExecutor(max_workers=8, thread_name_prefix="translate-line")
TRANSLATION_EXECUTOR = ThreadPoolExecutor(max_workers=24, thread_name_prefix="translate-src")
LINE_TRANSLATE_TIMEOUT = 8.0

_HTTP_ERROR_CODE_STRINGS = {"400", "401", "403", "404", "408", "409", "429", "500", "502", "503", "504"}
_ERROR_PAGE_MARKERS = (
    "server error", "that's all we know", "error 500", "error 404", "error 429",
    "bad gateway", "service unavailable", "too many requests",
)

def _looks_like_bogus_translation(candidate: str, original_text: str) -> bool:
    """True se 'candidate' parece ser lixo de erro (código HTTP cru, ou texto de página
    de erro genérica) em vez de uma tradução de verdade da linha original."""
    stripped = candidate.strip()
    original_stripped = original_text.strip()

    if stripped in _HTTP_ERROR_CODE_STRINGS and original_stripped not in _HTTP_ERROR_CODE_STRINGS:
        return True

    low = stripped.lower()
    if any(marker in low for marker in _ERROR_PAGE_MARKERS):
        return True
    if len(stripped) > 120 and len(stripped) > 4 * max(len(original_stripped), 1):
        return True

    return False

async def apply_translation_in_background(manager: MusicManager, target_lang: str):
    """Traduz em background sem travar o loop de eventos nem o listener de WebSocket.
    Usada tanto quando o usuário aperta um botão de idioma quanto quando uma letra nova
    é carregada (Auto ou manual) e precisa reaplicar o idioma que ficou 'grudado'."""
    manager.is_translating = True
    ok = await asyncio.to_thread(manager.apply_language, target_lang)
    manager.last_translation_error = None if ok else target_lang
    manager.is_translating = False

async def background_verification_worker(manager: MusicManager):
    """Background task to continuously capture audio and detect music."""
    loop = asyncio.get_event_loop()
    while manager.server_running:
        if (not manager.is_listening or manager.search_completed
                or manager.manual_mode or manager._media_busy):
            await asyncio.sleep(1)
            continue
            
        current_session = manager.session_id
        record_start_time = time.time()
        
        try: 
            audio_bytes = await loop.run_in_executor(None, manager.record_audio_to_memory, 4)
        except Exception as e:
            logging.warning(f"Audio capture failed, resetting device: {e}")
            manager.device_info = manager._configure_loopback()
            await asyncio.sleep(2)
            continue
            
        if (manager.session_id != current_session or not manager.is_listening
                or manager.manual_mode or manager.search_completed or manager._media_busy):
            continue

        rms = await loop.run_in_executor(None, manager._pcm_rms, audio_bytes)
        if rms < SILENCE_RMS_THRESHOLD:
            manager.pending_candidate = None
            manager.pending_candidate_count = 0
            if manager.auto_mode:
                manager.retry_delay = min(AUTO_MAX_RETRY_DELAY, manager.retry_delay * AUTO_BACKOFF_MULTIPLIER)
                await asyncio.sleep(manager.retry_delay)
            else:
                await asyncio.sleep(2)
            continue

        new_song, new_artist, shazam_offset, new_cover = await manager.recognize_audio_snippet(audio_bytes)

        if (manager.session_id != current_session or not manager.is_listening
                or manager.manual_mode or manager.search_completed or manager._media_busy):
            continue

        if not new_song:
            manager.pending_candidate = None
            manager.pending_candidate_count = 0
            if manager.auto_mode:
                manager.retry_delay = min(AUTO_MAX_RETRY_DELAY, manager.retry_delay * AUTO_BACKOFF_MULTIPLIER)
                await asyncio.sleep(manager.retry_delay)
            else:
                await asyncio.sleep(2)
            continue

        candidate = (new_song, new_artist)

        if manager.auto_mode:
            if manager.pending_candidate == candidate:
                manager.pending_candidate_count += 1
            else:
                manager.pending_candidate = candidate
                manager.pending_candidate_count = 1

            if manager.pending_candidate_count < 2:
                manager.retry_delay = AUTO_MIN_RETRY_DELAY
                await asyncio.sleep(manager.retry_delay)
                continue

        if manager._in_previous_track_cooldown(new_song, new_artist):
            await asyncio.sleep(2)
            continue

        manager.current_song, manager.current_artist = new_song, new_artist
        manager.current_cover = new_cover
        manager.pending_candidate = None
        manager.pending_candidate_count = 0
        manager.retry_delay = AUTO_MIN_RETRY_DELAY

        lyrics = await loop.run_in_executor(None, manager.fetch_lyrics_lrclib, new_artist, new_song)

        if (manager.session_id == current_session and not manager.search_completed
                and not manager._media_busy):
            if lyrics:
                offset = _sane_media_position(shazam_offset, fallback=0.0) or 0.0
                chave = manager._track_key(new_song, new_artist)
                fallback = record_start_time - offset
                manager._set_track(
                    new_song,
                    new_artist,
                    reference_time=manager._anchor_reference(chave, fallback),
                    lyrics=lyrics,
                    cover=new_cover,
                    source="shazam",
                )
            else:
                manager.search_completed = True
                manager.not_found_since = time.time()
        await asyncio.sleep(2)

async def run_manual_search(manager: MusicManager, artist: str, song: str, current_session: float):
    """Executa a pesquisa manual de texto para a letra e a capa do álbum."""
    loop = asyncio.get_event_loop()
    
    lyrics = await loop.run_in_executor(None, manager.fetch_lyrics_lrclib, artist, song)
    cover_art = await loop.run_in_executor(None, manager.fetch_cover_art, artist, song)
    
    if manager.session_id == current_session: 
        manager.search_completed = True
        
        if cover_art:
            manager.current_cover = cover_art
            
        if lyrics:
            chave = manager._track_key(song, artist)
            manager._set_track(
                song,
                artist,
                reference_time=manager._anchor_reference(chave, time.time()),
                lyrics=lyrics,
                cover=cover_art or manager.current_cover,
                source="manual",
            )

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
                elif action == "AUTO_TOGGLE":
                    manager._release_auto_hold()
                    manager.auto_mode = not manager.auto_mode
                    if manager.auto_mode and not manager.is_listening:
                        manager.reset_state()
                        manager.is_listening = True
                elif action == "RESET":
                    # hold_auto vem do C#; Limpar sempre segura a faixa atual
                    # mesmo se o campo faltar (compatível com builds antigas).
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

async def broadcast_ui_state(manager: MusicManager):
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

async def main_background(manager: MusicManager, port: int):
    """Main background loop initializer."""
    manager._main_loop = asyncio.get_running_loop()
    manager._main_loop.set_exception_handler(_asyncio_exception_handler)
    manager.watcher.start()
    spawn_task(background_verification_worker(manager))
    spawn_task(broadcast_ui_state(manager))
    async with websockets.serve(ws_handler, "127.0.0.1", port): 
        await asyncio.Future()

if __name__ == "__main__":
    
    # pyinstaller --noconfirm --onedir --windowed --collect-all anyascii --collect-all winrt --hidden-import winrt.windows.media.control --hidden-import winrt.windows.storage.streams --hidden-import crash_guard --hidden-import media_session --name "FrontlineServer" "FrontlineServer.py"
    server_port = 8765 
    
    if len(sys.argv) > 1:
        try:
            server_port = int(sys.argv[1])
        except ValueError:
            pass

    logging.info(f"Iniciando servidor FrontLine em background na porta {server_port}...")

    try:
        asyncio.run(main_background(manager, server_port))
    except KeyboardInterrupt:
        logging.info("Servidor encerrado manualmente.")
