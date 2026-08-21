import sys
import os
import time
import asyncio
import threading
import requests
import re
import wave
import io
import traceback
import json
import websockets
import subprocess
import socket
import numpy as np
import pyaudiowpatch as pyaudio
from shazamio import Shazam
from deep_translator import GoogleTranslator
from anyascii import anyascii
from media_session import MediaSessionWatcher
from PyQt6.QtWidgets import (QApplication, QWidget, QVBoxLayout, QHBoxLayout,
                             QLabel, QPushButton, QLineEdit, QComboBox,
                             QListWidget, QListWidgetItem, QFrame, QSlider,
                             QSizePolicy, QCheckBox, QSystemTrayIcon, QMenu)
from PyQt6.QtCore import Qt, QTimer, pyqtSignal, QObject, QEvent
from PyQt6.QtGui import QFont, QIcon, QPixmap, QImage, QAction

def controle_de_erros(exctype, value, tb):
    print("=== ERRO CRÍTICO ENCONTRADO ===")
    traceback.print_exception(exctype, value, tb)
sys.excepthook = controle_de_erros

def log(mensagem, categoria="SYSTEM"):
    timestamp = time.strftime("%H:%M:%S")
    print(f"[{timestamp}] [{categoria}] {mensagem}")

class Signaler(QObject):
    update_cover = pyqtSignal(bytes)
    song_finished = pyqtSignal()
    search_error = pyqtSignal(str)

ui_signals = Signaler()

class MusicManager:
    ESTADO_PARADO = "parado"
    ESTADO_RECONHECENDO = "reconhecendo"
    ESTADO_SINCRONIZADO = "sincronizado"

    GRACA_FIM_LETRA = 2.0          # pausa pós-fim da letra antes de re-ouvir (crossfade)
    COOLDOWN_FAIXA_ANTERIOR = 25.0  # ignora re-match da faixa anterior logo após transição
    LIMIAR_SILENCIO = 150.0        # RMS (int16) abaixo disso considera-se silêncio
    SILENCIO_PARA_PAUSA = 4.0      # s de silêncio até congelar o relógio da letra
    SILENCIO_PARA_TRANSICAO = 45.0 # s de silêncio até assumir que a reprodução parou
    TOLERANCIA_SEEK = 4.0          # s de desvio até tratar como seek (re-ancoragem dura)
    TOLERANCIA_MINIMA = 0.8        # desvios abaixo disso são ruído do player: ignorar
    JANELA_CALIBRAGEM = 12.0       # s após definir faixa com correção agressiva
    CORRECAO_PARCIAL = 0.35        # fração do desvio aplicada por ajuste (regime estável)

    def __init__(self):
        self.shazam = Shazam()
        self.session_id = time.time()
        self.servidor_rodando = True
        self.pyaudio_instance = pyaudio.PyAudio()
        self.device_info = self._configurar_loopback()
        self.overlay_font_size = 26
        self.modo_fantasma = False
        self.auto_sync_ativado = True
        self.inicio_escuta = 0.0
        self._lock = threading.RLock()
        self._media_busy = False
        self.suprimir_auto_inicio = False
        self.watcher = MediaSessionWatcher(self.ao_snapshot_media)
        self.reset_state()

    def _configurar_loopback(self):
        try:
            wasapi_info = self.pyaudio_instance.get_host_api_info_by_type(pyaudio.paWASAPI)
            default_speakers = self.pyaudio_instance.get_device_info_by_index(wasapi_info["defaultOutputDevice"])
            if not default_speakers["isLoopbackDevice"]:
                for loopback in self.pyaudio_instance.get_loopback_device_info_generator():
                    if default_speakers["name"] in loopback["name"]:
                        return loopback
            return default_speakers
        except Exception: return None

    def gravar_audio_com_nivel(self, duracao):
        """Grava áudio do loopback e retorna (wav_bytes, rms)."""
        if not self.device_info: raise Exception("Audio device error.")
        CHUNK, canais, taxa = 512, self.device_info["maxInputChannels"], int(self.device_info["defaultSampleRate"])
        stream = self.pyaudio_instance.open(format=pyaudio.paInt16, channels=canais, rate=taxa,
                                            frames_per_buffer=CHUNK, input=True, input_device_index=self.device_info["index"])
        frames = [stream.read(CHUNK) for _ in range(0, int(taxa / CHUNK * duracao))]
        stream.stop_stream()
        stream.close()
        dados = b''.join(frames)
        amostras = np.frombuffer(dados, dtype=np.int16).astype(np.float32)
        rms = float(np.sqrt(np.mean(amostras * amostras))) if amostras.size else 0.0
        audio_buffer = io.BytesIO()
        wf = wave.open(audio_buffer, 'wb')
        wf.setnchannels(canais)
        wf.setsampwidth(self.pyaudio_instance.get_sample_size(pyaudio.paInt16))
        wf.setframerate(taxa)
        wf.writeframes(dados)
        wf.close()
        return audio_buffer.getvalue(), rms

    def gravar_audio_memoria(self, duracao):
        return self.gravar_audio_com_nivel(duracao)[0]

    def reset_state(self):
        with self._lock:
            self.session_id = time.time()
            self.estado = self.ESTADO_PARADO
            self.artista_atual, self.musica_atual = None, None
            self.fonte_atual = None
            self.tempo_referencia_sistema = 0.0
            self.letra_original, self.letra_sincronizada = [], []
            self.traducoes_cacheadas = {}
            self.idioma_atual = "original"
            self.delay_manual = 0.0
            self.letra_pausada, self.momento_pausa = False, 0.0
            self.media_baseline = None
            self.media_pausou = False
            self.faixa_anterior, self.cooldown_ate = None, 0.0
            self.silencio_desde, self.silencio_congelado = 0.0, False
            self.proximo_listen_permitido = 0.0
            self.proximo_reancoragem = 0.0
            self.calibrando_ate = 0.0
            self._desvio_previo = None
            self.inicio_escuta = time.time()
        ui_signals.update_cover.emit(b'')

    # ==========================================
    # MÁQUINA DE ESTADOS / TRANSIÇÕES
    # ==========================================
    def iniciar_escuta(self):
        self.reset_state()
        with self._lock:
            self.estado = self.ESTADO_RECONHECENDO
            self.suprimir_auto_inicio = False

    def iniciar_transicao(self, motivo=""):
        """Fim da faixa ou troca detectada: limpa estado e volta a reconhecer."""
        with self._lock:
            if self.estado != self.ESTADO_SINCRONIZADO:
                return
            self.faixa_anterior = (self.musica_atual, self.artista_atual)
            self.cooldown_ate = time.time() + self.COOLDOWN_FAIXA_ANTERIOR
            self.estado = self.ESTADO_RECONHECENDO
            self.artista_atual, self.musica_atual = None, None
            self.fonte_atual = None
            self.letra_original, self.letra_sincronizada = [], []
            self.traducoes_cacheadas = {}
            self.idioma_atual = "original"
            self.delay_manual = 0.0
            self.letra_pausada, self.momento_pausa = False, 0.0
            self.media_baseline = None
            self.media_pausou = False
            self.silencio_desde, self.silencio_congelado = 0.0, False
            self.proximo_listen_permitido = time.time() + self.GRACA_FIM_LETRA if motivo == "fim_letra" else 0.0
            self.inicio_escuta = time.time()
        log(f"Transição ({motivo})", "SYNC")
        ui_signals.update_cover.emit(b'')
        ui_signals.song_finished.emit()

    def definir_faixa(self, titulo, artista, tempo_referencia, letra=None, capa_bytes=b'', fonte="shazam"):
        with self._lock:
            self.musica_atual, self.artista_atual = titulo, artista
            self.fonte_atual = fonte
            self.tempo_referencia_sistema = tempo_referencia
            self.letra_original = self.letra_sincronizada = letra or []
            self.traducoes_cacheadas = {}
            self.idioma_atual = "original"
            self.delay_manual = 0.0
            self.letra_pausada, self.momento_pausa = False, 0.0
            self.media_pausou = False
            self.silencio_desde, self.silencio_congelado = 0.0, False
            self.proximo_listen_permitido = 0.0
            self.proximo_reancoragem = time.time() + 5.0
            self.calibrando_ate = time.time() + self.JANELA_CALIBRAGEM
            self._desvio_previo = None
            self.estado = self.ESTADO_SINCRONIZADO
            self.inicio_escuta = time.time()
        log(f"Faixa definida: {titulo} - {artista} (fonte={fonte}, {len(letra or [])} linhas)", "SYNC")
        ui_signals.update_cover.emit(capa_bytes)

    def aplicar_busca_manual(self, artista, musica, letra):
        with self._lock:
            self.musica_atual, self.artista_atual = musica, artista
            self.fonte_atual = "manual"
            self.letra_original = self.letra_sincronizada = letra
            self.tempo_referencia_sistema = time.time()
            self.estado = self.ESTADO_SINCRONIZADO
            self.media_baseline = None
        ui_signals.update_cover.emit(b'')

    def pausar_relogio(self):
        if not self.letra_pausada:
            self.letra_pausada = True
            self.momento_pausa = time.time()

    def retomar_relogio(self):
        if self.letra_pausada:
            self.letra_pausada = False
            self.tempo_referencia_sistema += time.time() - self.momento_pausa
            self.momento_pausa = 0.0

    # ==========================================
    # SEGUIMENTO DE MÍDIA (troca automática de faixa)
    # ==========================================
    def _chave_de(self, titulo, artista):
        return ((titulo or "").lower().strip(), (artista or "").lower().strip())

    def _faixa_confirmada_tocando(self):
        """True se o media session confirma que a faixa atual ainda está tocando."""
        info = self.watcher.ultima_info
        if not info or not info.tocando:
            return False
        return info.chave == self._chave_de(self.musica_atual, self.artista_atual)

    def ao_snapshot_media(self, info):
        """Callback do MediaSessionWatcher — roda na thread do watcher."""
        if info is None or not self.auto_sync_ativado:
            return
        chave = info.chave
        self.watcher.preferencia_chave = chave

        # Início automático: música tocando e app parado -> começa a seguir sozinho
        if (self.estado == self.ESTADO_PARADO and not self.suprimir_auto_inicio
                and info.tocando and info.titulo):
            log(f"Auto-inicio: {info.titulo} - {info.artista}", "MEDIA")
            self.iniciar_escuta()

        if self.estado == self.ESTADO_SINCRONIZADO:
            faixa_atual_chave = self._chave_de(self.musica_atual, self.artista_atual)
            if self.media_baseline is None:
                self.media_baseline = chave
            elif chave != self.media_baseline and chave != faixa_atual_chave:
                log(f"Troca de faixa: {self.musica_atual} -> {info.titulo}", "MEDIA")
                self.media_baseline = chave
                self._seguir_metadata(info)
                return
            if not info.tocando and not self.letra_pausada:
                log(f"Player pausado: congelando letra em {(time.time() - self.tempo_referencia_sistema):.1f}s", "MEDIA")
                self.pausar_relogio()
                self.media_pausou = True
            elif info.tocando and self.letra_pausada and self.media_pausou:
                log("Player retomado: descongelando letra", "MEDIA")
                self.retomar_relogio()
                self.media_pausou = False
            if info.tocando and not self.letra_pausada and self.fonte_atual in ("media", "shazam"):
                agora = time.time()
                esperado = (agora - self.tempo_referencia_sistema) + self.delay_manual
                desvio = esperado - info.posicao
                em_calibragem = agora < self.calibrando_ate
                limite = self.TOLERANCIA_MINIMA if not em_calibragem else 0.5
                if abs(desvio) > limite and agora >= self.proximo_reancoragem:
                    if info.posicao < 1.0 and esperado > 15.0:
                        # Player reportou posição ~0 enquanto toca (bug de alguns
                        # players/anúncios): ignorar para não rebobinar a letra
                        log(f"Posição suspeita ignorada: player={info.posicao:.1f}s esperado={esperado:.1f}s", "MEDIA")
                        self._desvio_previo = None
                    elif abs(desvio) > self.TOLERANCIA_SEEK:
                        # Seek real. Fora da calibragem, exigir 2 leituras
                        # consecutivas em desacordo: uma isolada pode ser um
                        # relatório atrasado do player.
                        confirmado = em_calibragem or (
                            self._desvio_previo is not None
                            and agora - self._desvio_previo[0] <= 6.0
                            and abs(self._desvio_previo[1]) > self.TOLERANCIA_SEEK)
                        if not confirmado:
                            self._desvio_previo = (agora, desvio)
                        else:
                            log(f"Re-ancoragem por seek: {esperado:.1f}s -> {info.posicao:.1f}s", "MEDIA")
                            self.tempo_referencia_sistema = agora + self.delay_manual - info.posicao
                            self.proximo_reancoragem = agora + (1.5 if em_calibragem else 10.0)
                            self._desvio_previo = None
                    else:
                        # Deriva moderada: na calibragem (início da faixa), corrige
                        # tudo de uma vez; depois, aproxima aos poucos para não
                        # dar solavancos visíveis na letra.
                        fator = 1.0 if em_calibragem else self.CORRECAO_PARCIAL
                        log(f"Ajuste de sincronia ({'calibragem' if em_calibragem else 'parcial'}): desvio {desvio:+.2f}s", "MEDIA")
                        self.tempo_referencia_sistema += desvio * fator
                        self.proximo_reancoragem = agora + (1.5 if em_calibragem else 5.0)
                        self._desvio_previo = None
                elif abs(desvio) <= limite:
                    self._desvio_previo = None
        elif self.estado == self.ESTADO_RECONHECENDO:
            mesma_faixa_em_cooldown = (self.faixa_anterior is not None
                                       and chave == self._chave_de(*self.faixa_anterior)
                                       and time.time() < self.cooldown_ate)
            if (info.tocando and info.titulo and not self._media_busy
                    and time.time() >= self.proximo_listen_permitido
                    and not mesma_faixa_em_cooldown):
                self._seguir_metadata(info)

    def _seguir_metadata(self, info):
        """Troca instantânea via metadados do player (sem Shazam)."""
        self._media_busy = True
        try:
            letra = self.buscar_letra_lrclib(info.artista, info.titulo)
            if letra:
                # A busca demora ~1-2s; nesse intervalo o player pode ter emitido
                # uma posição mais recente. Ancorar sempre com a leitura mais nova.
                info_fresca = self.watcher.ultima_info
                if info_fresca and info_fresca.chave == info.chave:
                    info = info_fresca
                log(f"Letra via metadados: {info.titulo} - {info.artista} ({len(letra)} linhas, pos {info.posicao:.1f}s)", "MEDIA")
                # Sem delay_manual: o ajuste fino pertence à faixa anterior;
                # a nova faixa começa com âncora limpa baseada na posição do player.
                ref = time.time() - info.posicao
                self.definir_faixa(info.titulo, info.artista, tempo_referencia=ref,
                                   letra=letra, capa_bytes=info.capa_bytes, fonte="media")
            elif self.estado == self.ESTADO_SINCRONIZADO:
                log(f"Sem letra no LRCLib para {info.titulo} - {info.artista}", "MEDIA")
                self.iniciar_transicao("metadados mudaram sem letra")
        finally:
            self._media_busy = False

    # ==========================================
    # HEURÍSTICA DE SILÊNCIO (fallback sem media session)
    # ==========================================
    def audio_silencioso(self):
        if self.estado != self.ESTADO_SINCRONIZADO or not self.auto_sync_ativado:
            return
        agora = time.time()
        if self.silencio_desde == 0.0:
            self.silencio_desde = agora
        duracao = agora - self.silencio_desde
        if duracao >= self.SILENCIO_PARA_TRANSICAO:
            self.iniciar_transicao("silêncio prolongado")
        elif duracao >= self.SILENCIO_PARA_PAUSA and not self.letra_pausada:
            self.pausar_relogio()
            self.silencio_congelado = True

    def audio_ativo(self):
        estava_silencioso = self.silencio_desde > 0.0
        self.silencio_desde, self.silencio_congelado = 0.0, False
        if estava_silencioso and self.letra_pausada and not self.media_pausou:
            self.retomar_relogio()
        if (estava_silencioso and self.auto_sync_ativado
                and self.estado == self.ESTADO_SINCRONIZADO and not self.watcher.disponivel):
            self.iniciar_transicao("áudio retomou após silêncio")

    async def reconhecer_snippet(self, audio_bytes):
        try:
            resultado = await self.shazam.recognize(audio_bytes)
            if resultado and 'track' in resultado:
                track = resultado['track']
                cover = track.get('images', {}).get('coverart', '')
                return track['title'], track['subtitle'], resultado.get('matches', [{}])[0].get('offset', 0.0), cover
        except Exception: pass
        return None, None, 0.0, ""

    def buscar_letra_lrclib(self, artista, musica):
        headers = {"User-Agent": "FrontLineLyricsApp/0.0.2"}
        def extrair_linhas(synced_lyrics):
            linhas = []
            padrao = re.compile(r'\[(\d{2,}):(\d{2}(?:\.\d{1,3})?)\](.*)')
            for linha in synced_lyrics.split('\n'):
                match = padrao.match(linha)
                if match:
                    tempo = (int(match.group(1)) * 60) + float(match.group(2))
                    texto = match.group(3).strip()
                    if texto: linhas.append({"tempo": tempo, "letra": texto})
            return linhas
        musica_limpa = re.sub(r'\([^)]*\)', '', musica).strip()
        artista_limpo = artista.split('feat.')[0].split('&')[0].strip()
        buscas = [{"track_name": musica_limpa, "artist_name": artista_limpo}, f"{musica_limpa} {artista_limpo}", musica_limpa]
        try:
            r = requests.get("https://lrclib.net/api/get", params=buscas[0], headers=headers, timeout=5)
            if r.status_code == 200 and r.json().get("syncedLyrics"):
                linhas = extrair_linhas(r.json()["syncedLyrics"])
                if linhas:
                    linhas.append({"tempo": linhas[-1]["tempo"] + 5.0, "letra": "End"})
                    return linhas
        except Exception: pass
        for query in buscas[1:]:
            try:
                r = requests.get("https://lrclib.net/api/search", params={"q": query}, headers=headers, timeout=7)
                if r.status_code == 200:
                    for item in r.json():
                        if isinstance(item, dict) and item.get("syncedLyrics"):
                            linhas = extrair_linhas(item["syncedLyrics"])
                            if linhas:
                                linhas.append({"tempo": linhas[-1]["tempo"] + 5.0, "letra": "End"})
                                return linhas
            except Exception: pass
        return None

    def gerar_traducao(self, idioma_alvo):
        if not self.letra_original: return False
        if idioma_alvo in self.traducoes_cacheadas: return True 
        try:
            if idioma_alvo == "romanized":
                linhas_traduzidas = []
                for item in self.letra_original:
                    letra_rom = anyascii(item['letra']).capitalize()
                    linhas_traduzidas.append({"tempo": item['tempo'], "letra": letra_rom})
                self.traducoes_cacheadas[idioma_alvo] = linhas_traduzidas
                return True
            else:
                texto_completo = "\n".join([item['letra'] for item in self.letra_original])
                texto_traduzido = GoogleTranslator(source='auto', target=idioma_alvo).translate(texto_completo).split('\n')
                linhas_traduzidas = []
                for i, item in enumerate(self.letra_original):
                    letra_trad = texto_traduzido[i] if i < len(texto_traduzido) else item['letra']
                    linhas_traduzidas.append({"tempo": item['tempo'], "letra": letra_trad})
                self.traducoes_cacheadas[idioma_alvo] = linhas_traduzidas
                return True
        except Exception: return False

    def obter_estado_atual(self):
        linha_atual, linha_anterior, linha_futura = "", "", ""
        if self.estado == self.ESTADO_SINCRONIZADO and self.letra_sincronizada:
            tempo_base = self.momento_pausa if self.letra_pausada else time.time()
            tempo_decorrido = (tempo_base - self.tempo_referencia_sistema) + self.delay_manual
            # Fim da letra só dispara troca se o player NÃO confirmar a mesma faixa
            # ainda tocando (evita loop quando os versos terminam antes do áudio)
            if (self.auto_sync_ativado and not self.letra_pausada
                    and tempo_decorrido > self.letra_sincronizada[-1]['tempo'] + self.GRACA_FIM_LETRA
                    and not self._faixa_confirmada_tocando()):
                self.iniciar_transicao("fim_letra")
            for i, item in enumerate(self.letra_sincronizada):
                if tempo_decorrido >= item['tempo']:
                    linha_atual = item['letra']
                    linha_anterior = self.letra_sincronizada[i-1]['letra'] if i > 0 else ""
                    linha_futura = self.letra_sincronizada[i+1]['letra'] if i + 1 < len(self.letra_sincronizada) else ""
                else: break
            if linha_atual == "End":
                linha_atual = "♪"
            overlay_atual = linha_atual or "♫"
        elif self.estado == self.ESTADO_SINCRONIZADO:
            overlay_atual = "Synced lyrics not available."
        elif self.estado == self.ESTADO_RECONHECENDO and self.musica_atual:
            overlay_atual = f"Synchronizing lyrics for '{self.musica_atual}'..."
        else:
            overlay_atual = "Waiting for the next song..."
        return {
            "letra_atual": overlay_atual,
            "letra_anterior": linha_anterior,
            "letra_futura": linha_futura,
            "tamanho_fonte": self.overlay_font_size,
            "modo_fantasma": self.modo_fantasma
        }

manager = MusicManager()

async def async_worker_verificacao(manager):
    loop = asyncio.get_event_loop()
    while manager.servidor_rodando:
        try:
            if manager.estado == MusicManager.ESTADO_PARADO or manager._media_busy:
                await asyncio.sleep(1)
                continue

            if manager.estado == MusicManager.ESTADO_SINCRONIZADO:
                # Monitoramento leve: detecta pausas/fim via silêncio (rede de
                # segurança para players sem media session)
                if not manager.auto_sync_ativado:
                    await asyncio.sleep(2)
                    continue
                current_session = manager.session_id
                try:
                    _, rms = await loop.run_in_executor(None, manager.gravar_audio_com_nivel, 1)
                except Exception:
                    manager.device_info = manager._configurar_loopback()
                    await asyncio.sleep(2)
                    continue
                if manager.session_id != current_session or manager.estado != MusicManager.ESTADO_SINCRONIZADO:
                    continue
                if rms >= manager.LIMIAR_SILENCIO:
                    manager.audio_ativo()
                else:
                    manager.audio_silencioso()
                await asyncio.sleep(1.5)
                continue

            # ESTADO_RECONHECENDO
            if time.time() < manager.proximo_listen_permitido:
                await asyncio.sleep(0.5)
                continue
            current_session = manager.session_id
            t_inicio_gravacao = time.time()
            try:
                audio_bytes, rms = await loop.run_in_executor(None, manager.gravar_audio_com_nivel, 4)
            except Exception:
                manager.device_info = manager._configurar_loopback()
                await asyncio.sleep(2)
                continue
            if manager.session_id != current_session or manager.estado != MusicManager.ESTADO_RECONHECENDO:
                continue
            if rms < manager.LIMIAR_SILENCIO:
                await asyncio.sleep(2)
                continue
            nova_musica, novo_artista, offset_shazam, url_capa = await manager.reconhecer_snippet(audio_bytes)
            if nova_musica and manager.estado == MusicManager.ESTADO_RECONHECENDO:
                if (manager.faixa_anterior and time.time() < manager.cooldown_ate
                        and (nova_musica, novo_artista) == manager.faixa_anterior):
                    await asyncio.sleep(2)
                    continue
                capa_bytes = b''
                if url_capa:
                    try:
                        res = requests.get(url_capa, timeout=3)
                        if res.status_code == 200: capa_bytes = res.content
                    except Exception: pass
                letra = await loop.run_in_executor(None, manager.buscar_letra_lrclib, novo_artista, nova_musica)
                if manager.session_id != current_session or manager.estado != MusicManager.ESTADO_RECONHECENDO:
                    continue
                manager.definir_faixa(nova_musica, novo_artista,
                                      tempo_referencia=t_inicio_gravacao - offset_shazam,
                                      letra=letra, capa_bytes=capa_bytes, fonte="shazam")
            await asyncio.sleep(2)
        except Exception:
            await asyncio.sleep(2)

clientes_conectados = set()

async def ws_handler(websocket):
    clientes_conectados.add(websocket)
    try: await websocket.wait_closed()
    finally: clientes_conectados.remove(websocket)

async def broadcast_estado_ui(manager):
    while manager.servidor_rodando:
        if clientes_conectados:
            mensagem = json.dumps(manager.obter_estado_atual())
            websockets.broadcast(clientes_conectados, mensagem)
        await asyncio.sleep(0.1)

async def main_background(manager, porta):
    manager.watcher.start()
    asyncio.create_task(async_worker_verificacao(manager))
    asyncio.create_task(broadcast_estado_ui(manager))
    async with websockets.serve(ws_handler, "localhost", porta):
        await asyncio.Future()

def start_background_loop(porta):
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    loop.run_until_complete(main_background(manager, porta))

# ==========================================
# DEEP STAGE / GLASS UI
# ==========================================
STYLESHEET = """
QWidget#Main { 
    background-color: qradialgradient(spread:pad, cx:0.5, cy:0.1, radius:1, fx:0.5, fy:0.1, stop:0 #1a0b2e, stop:1 #020202);
    color: #ffffff; 
    font-family: 'Segoe UI', sans-serif; 
}
QFrame#GlassCard { 
    background-color: rgba(255, 255, 255, 0.05); 
    border-radius: 12px; 
    border: 1px solid rgba(255, 255, 255, 0.1); 
}
QFrame#CompactCard {
    background-color: rgba(0, 0, 0, 0.2);
    border-radius: 8px;
    border: 1px solid rgba(255, 255, 255, 0.05);
}
QLabel#SongTitle { font-weight: bold; color: white; }
QLabel#ArtistName { color: #d1a3ff; font-weight: bold; }
QLabel#MiniLabel { color: #aaaaaa; font-size: 10px; }

QPushButton { 
    background-color: rgba(255, 255, 255, 0.1); 
    border-radius: 6px; 
    padding: 10px; 
    font-weight: bold; 
    color: white;
}
QPushButton:hover { background-color: rgba(255, 255, 255, 0.15); }

/* Estados dos botões principais */
QPushButton#BtnListen { background-color: rgba(30, 9, 57, 0.2); border: 1px solid #6A0DAD; }
QPushButton#BtnListenActive { background-color: #7A28CB; color: white; border: 2px inset #a955ff; }

/* Botão de busca manual quando ativado (aderência) */
QPushButton#BtnManual:checked {
    background-color: rgba(0, 0, 0, 0.4);
    border: 1px inset rgba(255, 255, 255, 0.1);
    color: #aaaaaa;
}

/* Play/Pause e Search Execute dentro do deck */
QPushButton#BtnDeckAction { background-color: rgba(255, 255, 255, 0.15); border-radius: 15px; padding: 5px; font-size: 14px; }
QPushButton#BtnDeckAction:hover { background-color: rgba(255, 255, 255, 0.25); }
QPushButton#BtnDeckAction:checked { background-color: #a955ff; color: white; }

QLineEdit, QComboBox { 
    background-color: rgba(0, 0, 0, 0.3); 
    border: 1px solid rgba(255, 255, 255, 0.1); 
    border-radius: 6px; 
    padding: 6px; 
    color: white; 
    font-size: 11px;
}
QComboBox#LangCombo { max-width: 80px; }

/* Checkboxes (Auto-Sync e Lock) */
QCheckBox { color: white; font-size: 11px; font-weight: bold; }
QCheckBox::indicator { width: 14px; height: 14px; border-radius: 3px; border: 1px solid rgba(255,255,255,0.3); background-color: rgba(0,0,0,0.3); }
QCheckBox::indicator:checked { background-color: #a955ff; }

/* Lista de Letras Manual Sync */
QListWidget#LyricsList {
    background-color: rgba(0, 0, 0, 0.75);
    border-radius: 12px;
    padding: 5px;
    color: white;
    font-size: 10px;
    border: 1px solid rgba(90,90,90,0.1);
}
QListWidget#LyricsList::item {
    padding: 10px;
    border-bottom: 1px solid rgba(255,255,255,0.05);
}
QListWidget#LyricsList::item:hover {
    background-color: rgba(169, 85, 255, 0.5);
    border-radius: 6px;
}
/* Scrollbar da lista */
QScrollBar:vertical {
    border: none;
    background: rgba(0,0,0,0.2);
    width: 6px;
    border-radius: 3px;
}
QScrollBar::handle:vertical {
    background: rgba(255,255,255,0.3);
    border-radius: 3px;
}
QScrollBar::handle:vertical:hover {
    background: rgba(169, 85, 255, 0.8);
}
"""
class ControlWindow(QWidget):
    def __init__(self, manager, porta_servidor):
        super().__init__()
        self.manager = manager
        self.porta_servidor = porta_servidor
        self.overlay_process = None
        self.ultima_musica_traduzida = None
        
        self.setObjectName("Main")
        self.setWindowTitle("FrontLine Lyrics")
        self.setStyleSheet(STYLESHEET)
        
        self.setFixedSize(320, 600) 
        
        caminho_ico = self.obter_caminho_asset("logo.ico")
        if os.path.exists(caminho_ico):
            self.setWindowIcon(QIcon(caminho_ico))

        # --- BANDEJA DO SISTEMA (minimizar para a tray) ---
        self._aviso_tray_mostrado = False
        self.tray_icon = QSystemTrayIcon(QIcon(caminho_ico) if os.path.exists(caminho_ico) else self.windowIcon(), self)
        self.tray_icon.setToolTip("FrontLine Lyrics")
        menu_tray = QMenu()
        self.acao_abrir = QAction("Open FrontLine Lyrics", menu_tray)
        self.acao_abrir.triggered.connect(self.mostrar_do_tray)
        self.acao_sair = QAction("Quit", menu_tray)
        self.acao_sair.triggered.connect(self.encerrar_aplicacao)
        menu_tray.addAction(self.acao_abrir)
        menu_tray.addSeparator()
        menu_tray.addAction(self.acao_sair)
        self.tray_icon.setContextMenu(menu_tray)
        self.tray_icon.activated.connect(self.tray_ativado)
        self.tray_icon.show()

        self.main_layout = QVBoxLayout(self)
        self.main_layout.setContentsMargins(15, 15, 15, 10)
        
        # ==========================================
        # 1. DECK PRINCIPAL
        # ==========================================
        self.header_frame = QFrame()
        self.header_frame.setObjectName("GlassCard")
        self.header_frame.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        header_layout = QVBoxLayout(self.header_frame)
        header_layout.setContentsMargins(5, 10, 5, 10)
        header_layout.setSpacing(8)
        
        # Capa e Lista (se sobrepõem)
        self.lbl_capa = QLabel("")
        self.lbl_capa.setFixedSize(260, 260) 
        self.lbl_capa.setAlignment(Qt.AlignmentFlag.AlignCenter)
        
        self.list_lyrics = QListWidget()
        self.list_lyrics.setObjectName("LyricsList")
        self.list_lyrics.setFixedSize(260, 260)
        self.list_lyrics.hide()
        
        capa_layout = QHBoxLayout()
        capa_layout.addStretch()
        capa_layout.addWidget(self.lbl_capa)
        capa_layout.addWidget(self.list_lyrics)
        capa_layout.addStretch()
        header_layout.addLayout(capa_layout)

        self.lbl_musica = QLabel("Ready")
        self.lbl_musica.setObjectName("SongTitle")
        self.lbl_musica.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.lbl_musica.setStyleSheet("font-size: 22px;") 
        
        self.lbl_artista = QLabel("Press Listen to start")
        self.lbl_artista.setObjectName("ArtistName")
        self.lbl_artista.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.lbl_artista.setStyleSheet("font-size: 16px;") 
        
        header_layout.addWidget(self.lbl_musica)
        header_layout.addWidget(self.lbl_artista)
        
        self.atualizar_capa_ui(None)
        
        config_deck_layout = QHBoxLayout()
        lbl_lang = QLabel("Language:")
        lbl_lang.setObjectName("MiniLabel")
        self.cb_lang = QComboBox()
        self.cb_lang.setObjectName("LangCombo")
        self.cb_lang.addItems(["Original", "Pt-Br", "Espanol", "English", "Romanized"])
        self.cb_auto_sync = QCheckBox("Auto-Sync")
        self.cb_auto_sync.setChecked(True)

        config_deck_layout.addStretch()
        config_deck_layout.addWidget(lbl_lang)
        config_deck_layout.addWidget(self.cb_lang)
        config_deck_layout.addSpacing(15)
        config_deck_layout.addWidget(self.cb_auto_sync)
        config_deck_layout.addStretch()
        header_layout.addLayout(config_deck_layout)

        self.search_container = QWidget()
        search_layout = QHBoxLayout(self.search_container)
        search_layout.setContentsMargins(10, 0, 10, 0)
        self.ipt_artista = QLineEdit(); self.ipt_artista.setPlaceholderText("Artist")
        self.ipt_musica = QLineEdit(); self.ipt_musica.setPlaceholderText("Song")
        search_layout.addWidget(self.ipt_artista)
        search_layout.addWidget(self.ipt_musica)
        self.search_container.hide()
        header_layout.addWidget(self.search_container)

        action_layout = QHBoxLayout()
        
        self.btn_pause = QPushButton()
        self.btn_pause.setObjectName("BtnDeckAction")
        self.btn_pause.setFixedSize(60, 30)
        self.btn_pause.setDisabled(True)
        caminho_playpause = self.obter_caminho_asset("playpause.ico", subpasta="icons")
        if os.path.exists(caminho_playpause):
            self.btn_pause.setIcon(QIcon(caminho_playpause))
        else:
            self.btn_pause.setText("⏯")
            
        # --- ATUALIZAÇÃO: Botão MANUAL SYNC ajustado ---
        self.btn_manual_sync = QPushButton("Manual Sync")
        self.btn_manual_sync.setObjectName("BtnDeckAction")
        self.btn_manual_sync.setFixedSize(120, 30)
        self.btn_manual_sync.setDisabled(True)
        self.btn_manual_sync.setCheckable(True)
            
        self.btn_exec_search = QPushButton("SYNC")
        self.btn_exec_search.setObjectName("BtnDeckAction")
        self.btn_exec_search.setFixedSize(80, 30)
        self.btn_exec_search.hide()

        action_layout.addStretch()
        action_layout.addWidget(self.btn_pause)
        action_layout.addWidget(self.btn_manual_sync)
        action_layout.addWidget(self.btn_exec_search)
        action_layout.addStretch()
        header_layout.addLayout(action_layout)

        self.main_layout.addWidget(self.header_frame)

        # ==========================================
        # 2. BOTÕES PRINCIPAIS
        # ==========================================
        ctrl_btns = QHBoxLayout()
        self.btn_listen = QPushButton("LISTEN")
        self.btn_listen.setObjectName("BtnListen")
        
        self.btn_manual_search = QPushButton("MANUAL SEARCH")
        self.btn_manual_search.setObjectName("BtnManual")
        self.btn_manual_search.setCheckable(True)
        
        self.btn_stop = QPushButton("RESET")
        
        ctrl_btns.addWidget(self.btn_listen, 2)
        ctrl_btns.addWidget(self.btn_manual_search, 2)
        ctrl_btns.addWidget(self.btn_stop, 1)
        self.main_layout.addLayout(ctrl_btns)

        # ==========================================
        # 3. OVERLAY CONFIGS
        # ==========================================
        self.frame_overlay = QFrame()
        self.frame_overlay.setObjectName("CompactCard")
        overlay_layout = QHBoxLayout(self.frame_overlay)
        overlay_layout.setContentsMargins(10, 5, 10, 5)
        
        lbl_font = QLabel("Font:")
        lbl_font.setObjectName("MiniLabel")
        self.slider_fonte = QSlider(Qt.Orientation.Horizontal)
        self.slider_fonte.setRange(14, 60); self.slider_fonte.setValue(26)
        self.slider_fonte.setFixedWidth(60)
        
        self.cb_ghost = QCheckBox("Lock")
        self.btn_reload = QPushButton("RELOAD")
        self.btn_reload.setStyleSheet("padding: 4px; font-size: 10px;")
        
        overlay_layout.addWidget(lbl_font)
        overlay_layout.addWidget(self.slider_fonte)
        overlay_layout.addSpacing(10)
        overlay_layout.addWidget(self.cb_ghost)
        overlay_layout.addStretch()
        overlay_layout.addWidget(self.btn_reload)
        self.main_layout.addWidget(self.frame_overlay)
        
        credits_html = """
        <div style='text-align: center; font-size: 11px; line-height: 1.2;'>
            <span style='color: #888888;'>v0.0.2</span><br>
            <a href="https://github.com/juliocax" style="color: #a955ff; text-decoration: none; font-weight: bold;">Created by Julio</a>
        </div>
        """
        self.lbl_credits = QLabel(credits_html)
        self.lbl_credits.setOpenExternalLinks(True)
        self.lbl_credits.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.main_layout.addWidget(self.lbl_credits)

        # ==========================================
        # CONEXÕES
        # ==========================================
        self.btn_listen.clicked.connect(self.action_start_listen)
        self.btn_manual_search.toggled.connect(self.action_toggle_search_mode)
        self.btn_stop.clicked.connect(self.action_stop)
        
        self.btn_pause.clicked.connect(self.action_pause)
        self.btn_manual_sync.toggled.connect(self.action_toggle_lyrics_list)
        self.list_lyrics.itemClicked.connect(self.action_jump_to_lyric)
        self.btn_exec_search.clicked.connect(self.action_buscar_manual)
        
        self.cb_auto_sync.toggled.connect(self.action_toggle_autosync)
        self.cb_lang.currentTextChanged.connect(self.aplicar_traducao_ui)
        self.slider_fonte.valueChanged.connect(self.action_mudar_fonte)
        self.cb_ghost.toggled.connect(self.action_toggle_ghost)
        self.btn_reload.clicked.connect(self.iniciar_subprocesso_overlay)
        
        ui_signals.update_cover.connect(self.atualizar_capa_ui)
        ui_signals.song_finished.connect(self.ao_musica_terminar)
        ui_signals.search_error.connect(lambda msg: self.lbl_artista.setText(msg))

        self.iniciar_subprocesso_overlay()
        self.timer = QTimer(); self.timer.timeout.connect(self.update_ui_loop); self.timer.start(500)

    # --- FUNÇÕES UTILITÁRIAS E DE UI ---
    def obter_caminho_asset(self, filename, subpasta="assets"):
        if getattr(sys, 'frozen', False):
            base_dir = sys._MEIPASS 
        else:
            base_dir = os.path.dirname(os.path.abspath(__file__))
        return os.path.join(base_dir, subpasta, filename)

    def iniciar_subprocesso_overlay(self):
        if self.overlay_process: self.overlay_process.terminate()
        caminho = os.path.join(os.path.dirname(sys.executable) if getattr(sys, 'frozen', False) else os.path.dirname(os.path.abspath(__file__)), "FrontLineOverlay.exe")
        try: self.overlay_process = subprocess.Popen([caminho, str(self.porta_servidor)])
        except: pass

    def encerrar_aplicacao(self):
        if self.overlay_process: self.overlay_process.terminate()
        QApplication.quit()

    def closeEvent(self, event):
        self.encerrar_aplicacao()

    def changeEvent(self, event):
        super().changeEvent(event)
        if event.type() == QEvent.Type.WindowStateChange and self.isMinimized():
            QTimer.singleShot(0, self.hide)
            if not self._aviso_tray_mostrado:
                self.tray_icon.showMessage("FrontLine Lyrics",
                                           "Still running in the tray. Click the icon to reopen.",
                                           QSystemTrayIcon.MessageIcon.Information, 2500)
                self._aviso_tray_mostrado = True

    def tray_ativado(self, motivo):
        if motivo == QSystemTrayIcon.ActivationReason.Trigger:
            self.mostrar_do_tray()

    def mostrar_do_tray(self):
        self.showNormal()
        self.activateWindow()
        self.raise_()

    def atualizar_capa_ui(self, image_bytes):
        pix = QPixmap()
        if not image_bytes: 
            caminho_logo = self.obter_caminho_asset("logocapa.png", subpasta="icons")
            if os.path.exists(caminho_logo):
                pix.load(caminho_logo)
            else:
                self.lbl_capa.setText("Cover/Logo missing")
                return
        else:
            pix.loadFromData(image_bytes)
            
        self.lbl_capa.setPixmap(pix.scaled(260, 260, Qt.AspectRatioMode.KeepAspectRatioByExpanding, Qt.TransformationMode.SmoothTransformation))

    def update_button_style(self, btn, is_active):
        btn.setObjectName("BtnListenActive" if is_active else "BtnListen")
        btn.style().unpolish(btn)
        btn.style().polish(btn)

    # --- LÓGICA DE UI E SINCRONIZAÇÃO ---
    def action_start_listen(self):
        self.manager.iniciar_escuta()
        self.ultima_musica_traduzida = None
        
        self.update_button_style(self.btn_listen, True)
        self.lbl_musica.setStyleSheet("font-size: 22px;")
        self.lbl_musica.setText("Listening...")
        self.lbl_artista.setStyleSheet("font-size: 16px;")
        self.lbl_artista.setText("Please play a song")
        
        self.list_lyrics.hide()
        self.lbl_capa.show()
        self.atualizar_capa_ui(None)
        
        self.btn_pause.setDisabled(True)
        self.btn_manual_sync.setDisabled(True)
        self.btn_manual_sync.setChecked(False)
        
    def ao_musica_terminar(self):
        if self.manager.auto_sync_ativado:
            self.lbl_musica.setStyleSheet("font-size: 22px;")
            self.lbl_musica.setText("Between tracks...")
            self.lbl_artista.setStyleSheet("font-size: 16px;")
            self.lbl_artista.setText("Detecting the next song")

    def action_toggle_search_mode(self, checked):
        self.lbl_musica.setVisible(not checked)
        self.lbl_artista.setVisible(not checked)
        
        self.search_container.setVisible(checked)
        self.btn_pause.setVisible(not checked)
        self.btn_manual_sync.setVisible(not checked)
        self.btn_exec_search.setVisible(checked)

    def action_toggle_autosync(self, checked):
        self.manager.auto_sync_ativado = checked

    def action_stop(self):
        self.manager.reset_state()
        self.manager.suprimir_auto_inicio = True
        self.ultima_musica_traduzida = None
        self.lbl_musica.setStyleSheet("font-size: 22px;")
        self.lbl_musica.setText("Ready")
        self.lbl_artista.setStyleSheet("font-size: 16px;")
        self.lbl_artista.setText("Press Listen to start")
        
        self.list_lyrics.hide()
        self.lbl_capa.show()
        self.atualizar_capa_ui(None)
        
        self.ipt_artista.clear()
        self.ipt_musica.clear()
        self.cb_lang.setCurrentIndex(0)
        
        self.update_button_style(self.btn_listen, False)
        self.btn_manual_search.setChecked(False) 
        self.btn_pause.setDisabled(True)
        self.btn_manual_sync.setDisabled(True)
        self.btn_manual_sync.setChecked(False)

    def action_pause(self):
        if not self.manager.letra_sincronizada: return
        if not self.manager.letra_pausada:
            self.manager.pausar_relogio()
            self.btn_pause.setStyleSheet("background-color: #a955ff;")
        else:
            self.manager.retomar_relogio()
            self.btn_pause.setStyleSheet("")

    def action_toggle_lyrics_list(self, checked):
        if checked:
            self.lbl_capa.hide()
            self.list_lyrics.clear()
            
            self.list_lyrics.setWordWrap(True)
            
            if self.manager.letra_sincronizada:
                for item in self.manager.letra_sincronizada:
                    list_item = QListWidgetItem(item['letra'])
                    list_item.setData(Qt.ItemDataRole.UserRole, item['tempo'])
                    list_item.setTextAlignment(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter)
                    
                    self.list_lyrics.addItem(list_item)
            self.list_lyrics.show()
        else:
            self.list_lyrics.hide()
            self.lbl_capa.show()

    def action_jump_to_lyric(self, item):
        tempo_alvo = item.data(Qt.ItemDataRole.UserRole)
        self.manager.tempo_referencia_sistema = time.time() - tempo_alvo
        if self.manager.letra_pausada:
            self.manager.letra_pausada = False
            self.btn_pause.setStyleSheet("")
        self.btn_manual_sync.setChecked(False)

    def aplicar_traducao_ui(self):
        lang = {"Original": "original", "Pt-Br": "pt", "Espanol": "es", "English": "en", "Romanized": "romanized"}.get(self.cb_lang.currentText(), "original")
        if lang == "original": 
            self.manager.letra_sincronizada = self.manager.letra_original
        elif self.manager.gerar_traducao(lang): 
            self.manager.letra_sincronizada = self.manager.traducoes_cacheadas[lang]
            if self.btn_manual_sync.isChecked():
                self.action_toggle_lyrics_list(True)

    def action_buscar_manual(self):
        art, mus = self.ipt_artista.text(), self.ipt_musica.text()
        if not art or not mus: return
        self.btn_manual_search.setChecked(False)
        self.manager.reset_state()
        self.manager.suprimir_auto_inicio = True
        self.ultima_musica_traduzida = None
        self.lbl_musica.setStyleSheet("font-size: 22px;")
        self.lbl_musica.setText("Searching...")
        self.lbl_artista.setStyleSheet("font-size: 16px;")
        self.lbl_artista.setText("Fetching lyrics online...")
        self.list_lyrics.hide()
        self.lbl_capa.show()
        self.atualizar_capa_ui(None) 
        def worker():
            letra = self.manager.buscar_letra_lrclib(art, mus)
            if letra:
                self.manager.aplicar_busca_manual(art, mus, letra)
            else: ui_signals.search_error.emit("Lyrics not found!")
        threading.Thread(target=worker, daemon=True).start()

    def action_mudar_fonte(self, val): self.manager.overlay_font_size = val
    def action_toggle_ghost(self, checked): self.manager.modo_fantasma = checked

    def update_ui_loop(self):
        if self.manager.estado == MusicManager.ESTADO_RECONHECENDO and self.btn_listen.objectName() != "BtnListenActive":
            self.update_button_style(self.btn_listen, True)
        if self.manager.musica_atual and self.btn_listen.objectName() == "BtnListenActive":
            self.update_button_style(self.btn_listen, False)
        if self.manager.estado == MusicManager.ESTADO_RECONHECENDO and not self.manager.musica_atual:
            tempo_espera = time.time() - self.manager.inicio_escuta
            if tempo_espera > 22:
                self.lbl_artista.setText("Analyzing audio details...")
            elif tempo_espera > 12:
                self.lbl_artista.setText("Still trying to catch the beat...")
            elif tempo_espera > 5:
                self.lbl_artista.setText("Audio is tricky!")
        elif self.manager.musica_atual:
            mus = self.manager.musica_atual
            art = self.manager.artista_atual
            if len(mus) > 22:
                self.lbl_musica.setStyleSheet("font-size: 16px;")
                self.lbl_musica.setText(mus[:35] + "..." if len(mus) > 35 else mus)
            else:
                self.lbl_musica.setStyleSheet("font-size: 22px;")
                self.lbl_musica.setText(mus)
            if len(art) > 25:
                self.lbl_artista.setStyleSheet("font-size: 13px;")
                self.lbl_artista.setText(art[:40] + "..." if len(art) > 40 else art)
            else:
                self.lbl_artista.setStyleSheet("font-size: 16px;")
                self.lbl_artista.setText(art)
            if self.manager.musica_atual != self.ultima_musica_traduzida and self.manager.letra_original:
                self.aplicar_traducao_ui()
                self.ultima_musica_traduzida = self.manager.musica_atual
        if self.manager.letra_sincronizada: 
            self.btn_pause.setDisabled(False)
            self.btn_manual_sync.setDisabled(False)

def encontrar_porta_livre():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(('', 0)); return s.getsockname()[1]

if __name__ == "__main__":
    import ctypes
    try:
        myappid = 'juliocax.FrontLineLyrics.app.0.0.2'
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(myappid)
    except Exception:
        pass

    app = QApplication(sys.argv)
    porta = encontrar_porta_livre()
    threading.Thread(target=start_background_loop, args=(porta,), daemon=True).start()
    window = ControlWindow(manager, porta)
    window.show()
    sys.exit(app.exec())
