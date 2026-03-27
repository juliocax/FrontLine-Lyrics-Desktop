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
import pyaudiowpatch as pyaudio
from shazamio import Shazam
from deep_translator import GoogleTranslator
from anyascii import anyascii
from PyQt6.QtWidgets import (QApplication, QWidget, QVBoxLayout, QHBoxLayout, 
                             QLabel, QPushButton, QLineEdit, QComboBox, 
                             QListWidget, QListWidgetItem, QFrame, QSlider,
                             QSizePolicy, QCheckBox)
from PyQt6.QtCore import Qt, QTimer, pyqtSignal, QObject
from PyQt6.QtGui import QFont, QIcon, QPixmap, QImage

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
    def __init__(self):
        self.shazam = Shazam()
        self.session_id = time.time()
        self.servidor_rodando = True   
        self.pyaudio_instance = pyaudio.PyAudio()
        self.device_info = self._configurar_loopback()
        self.overlay_font_size = 26
        self.modo_fantasma = False
        self.auto_sync_ativado = False
        self.inicio_escuta = 0.0
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

    def gravar_audio_memoria(self, duracao):
        if not self.device_info: raise Exception("Audio device error.")
        CHUNK, canais, taxa = 512, self.device_info["maxInputChannels"], int(self.device_info["defaultSampleRate"])
        stream = self.pyaudio_instance.open(format=pyaudio.paInt16, channels=canais, rate=taxa,
                                            frames_per_buffer=CHUNK, input=True, input_device_index=self.device_info["index"])
        frames = [stream.read(CHUNK) for _ in range(0, int(taxa / CHUNK * duracao))]
        stream.stop_stream()
        stream.close()
        audio_buffer = io.BytesIO()
        wf = wave.open(audio_buffer, 'wb')
        wf.setnchannels(canais)
        wf.setsampwidth(self.pyaudio_instance.get_sample_size(pyaudio.paInt16))
        wf.setframerate(taxa)
        wf.writeframes(b''.join(frames))
        wf.close()
        return audio_buffer.getvalue() 

    def reset_state(self):
        self.session_id = time.time() 
        self.artista_atual, self.musica_atual = None, None
        self.tempo_referencia_sistema = 0.0 
        self.letra_original, self.letra_sincronizada = [], []
        self.traducoes_cacheadas = {} 
        self.idioma_atual = "original"
        self.delay_manual = 0.0 
        self.escutando, self.busca_concluida, self.letra_pausada = False, False, False
        self.status_busca = "Ready. Click Listen."
        self.momento_pausa = 0.0
        self.inicio_escuta = time.time()
        ui_signals.update_cover.emit(b'')

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
        if not self.escutando or (self.escutando and not self.musica_atual):
            overlay_atual = "Waiting for the next song..."
        elif self.musica_atual and not self.busca_concluida:
            overlay_atual = f"Synchronizing lyrics for '{self.musica_atual}'..."
        elif self.busca_concluida and not self.letra_sincronizada:
            overlay_atual = "Synced lyrics not available."
        elif self.letra_sincronizada:
            tempo_base = self.momento_pausa if self.letra_pausada else time.time()
            tempo_decorrido = (tempo_base - self.tempo_referencia_sistema) + self.delay_manual
            if self.auto_sync_ativado and tempo_decorrido > self.letra_sincronizada[-1]['tempo'] + 2.0:
                ui_signals.song_finished.emit()
            for i, item in enumerate(self.letra_sincronizada):
                if tempo_decorrido >= item['tempo']:
                    linha_atual = item['letra']
                    linha_anterior = self.letra_sincronizada[i-1]['letra'] if i > 0 else ""
                    linha_futura = self.letra_sincronizada[i+1]['letra'] if i + 1 < len(self.letra_sincronizada) else ""
                else: break
            overlay_atual = linha_atual or "♫"
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
        if not manager.escutando or manager.busca_concluida:
            await asyncio.sleep(1)
            continue
        current_session = manager.session_id
        t_inicio_gravacao = time.time()
        try:
            audio_bytes = await loop.run_in_executor(None, manager.gravar_audio_memoria, 4)
        except Exception:
            manager.device_info = manager._configurar_loopback()
            await asyncio.sleep(2)
            continue
        if manager.session_id != current_session or not manager.escutando: continue
        nova_musica, novo_artista, offset_shazam, url_capa = await manager.reconhecer_snippet(audio_bytes)
        if nova_musica and manager.escutando:
            manager.musica_atual, manager.artista_atual = nova_musica, novo_artista
            if url_capa:
                try:
                    res = requests.get(url_capa, timeout=3)
                    if res.status_code == 200: ui_signals.update_cover.emit(res.content)
                except Exception: pass
            letra = await loop.run_in_executor(None, manager.buscar_letra_lrclib, novo_artista, nova_musica)
            manager.busca_concluida = True
            if letra:
                manager.letra_original = manager.letra_sincronizada = letra
                manager.tempo_referencia_sistema = t_inicio_gravacao - offset_shazam
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
        self.cb_auto_sync.setChecked(False)

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
        ui_signals.song_finished.connect(self.iniciar_timer_autosync)
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

    def closeEvent(self, event):
        if self.overlay_process: self.overlay_process.terminate()
        QApplication.quit()

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
        self.manager.reset_state()
        self.manager.escutando = True
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
        
    def iniciar_timer_autosync(self):
        if self.manager.auto_sync_ativado:
            self.lbl_musica.setStyleSheet("font-size: 22px;")
            self.lbl_musica.setText("Waiting 3s...")
            self.lbl_artista.setStyleSheet("font-size: 16px;")
            self.lbl_artista.setText("Fade out transition")
            QTimer.singleShot(3000, self.action_start_listen)

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
            self.manager.letra_pausada = True
            self.manager.momento_pausa = time.time()
            self.btn_pause.setStyleSheet("background-color: #a955ff;")
        else:
            self.manager.letra_pausada = False
            tempo_pausado = time.time() - self.manager.momento_pausa
            self.manager.tempo_referencia_sistema += tempo_pausado
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
            self.manager.busca_concluida = True
            if letra:
                self.manager.letra_original = self.manager.letra_sincronizada = letra
                self.manager.musica_atual, self.manager.artista_atual = mus, art
                self.manager.escutando = True; self.manager.tempo_referencia_sistema = time.time()
            else: ui_signals.search_error.emit("Lyrics not found!")
        threading.Thread(target=worker, daemon=True).start()

    def action_mudar_fonte(self, val): self.manager.overlay_font_size = val
    def action_toggle_ghost(self, checked): self.manager.modo_fantasma = checked

    def update_ui_loop(self):
        if self.manager.musica_atual and self.btn_listen.objectName() == "BtnListenActive":
            self.update_button_style(self.btn_listen, False)
        if self.manager.escutando and not self.manager.musica_atual:
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
