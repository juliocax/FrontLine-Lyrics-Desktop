import sys
import os
import time
import asyncio
import json
import wave
import io
import traceback
import subprocess
import socket
import requests
import re
import websockets
import pyaudiowpatch as pyaudio
from shazamio import Shazam
from deep_translator import GoogleTranslator
from anyascii import anyascii

def controle_de_erros(exctype, value, tb):
    print("=== ERRO CRÍTICO ENCONTRADO ===")
    traceback.print_exception(exctype, value, tb)
sys.excepthook = controle_de_erros

def log(mensagem, categoria="SYSTEM"):
    timestamp = time.strftime("%H:%M:%S")
    print(f"[{timestamp}] [{categoria}] {mensagem}")

class MusicManager:
    def __init__(self):
        self.shazam = Shazam()
        self.servidor_rodando = True   
        self.pyaudio_instance = pyaudio.PyAudio()
        self.device_info = self._configurar_loopback()
        self.overlay_font_size = 26
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
        self.escutando, self.busca_concluida = False, False
        self.manual_mode = False 
        self.traduzindo = False
        self.inicio_escuta = time.time()

    async def reconhecer_snippet(self, audio_bytes):
        try:
            resultado = await self.shazam.recognize(audio_bytes)
            if resultado and 'track' in resultado:
                track = resultado['track']
                return track.get('title'), track.get('subtitle'), resultado.get('matches', [{}])[0].get('offset', 0.0)
        except Exception: pass
        return None, None, 0.0

    def buscar_letra_lrclib(self, artista, musica):
        headers = {"User-Agent": "FrontLineLyricsApp/1.0.0"}
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
                if linhas: return linhas + [{"tempo": linhas[-1]["tempo"] + 5.0, "letra": "End"}]
        except Exception: pass
        for query in buscas[1:]:
            try:
                r = requests.get("https://lrclib.net/api/search", params={"q": query}, headers=headers, timeout=7)
                if r.status_code == 200:
                    for item in r.json():
                        if isinstance(item, dict) and item.get("syncedLyrics"):
                            linhas = extrair_linhas(item["syncedLyrics"])
                            if linhas: return linhas + [{"tempo": linhas[-1]["tempo"] + 5.0, "letra": "End"}]
            except Exception: pass
        return None

    def gerar_traducao(self, idioma_alvo):
        if not self.letra_original: return False
        if idioma_alvo in self.traducoes_cacheadas: return True 
        try:
            if idioma_alvo.lower() == "romanized":
                linhas_traduzidas = []
                for item in self.letra_original:
                    texto = str(item['letra']) if item['letra'] else ""
                    try:
                        # Proteção forte para garantir a romanização
                        letra_rom = anyascii(texto).capitalize()
                    except Exception as e:
                        log(f"Erro ao romanizar: {e}")
                        letra_rom = texto
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

    def aplicar_idioma(self, lang):
        self.idioma_atual = lang
        if lang == "original": self.letra_sincronizada = self.letra_original
        elif self.gerar_traducao(lang): self.letra_sincronizada = self.traducoes_cacheadas[lang]

    def obter_estado_atual(self):
        linha_atual, linha_anterior, linha_futura, overlay_atual, status_geral = "", "", "", "", ""
        
        if self.traduzindo:
            status_geral = "TRANSLATING"
        elif not self.escutando: 
            status_geral = "IDLE"
        elif self.escutando and not self.musica_atual:
            status_geral = "LISTENING"
        elif self.musica_atual and not self.busca_concluida:
            status_geral = "SEARCHING"
        elif self.busca_concluida and not self.letra_sincronizada:
            status_geral = "NOT_FOUND"
            overlay_atual = "Lyrics not found."
        elif self.letra_sincronizada:
            status_geral = "SYNCED"
            tempo_decorrido = time.time() - self.tempo_referencia_sistema
            for i, item in enumerate(self.letra_sincronizada):
                if tempo_decorrido >= item['tempo']:
                    linha_atual = item['letra']
                    linha_anterior = self.letra_sincronizada[i-1]['letra'] if i > 0 else ""
                    linha_futura = self.letra_sincronizada[i+1]['letra'] if i + 1 < len(self.letra_sincronizada) else ""
                else: break
            overlay_atual = linha_atual or "♫"
            if not self.manual_mode and tempo_decorrido > self.letra_sincronizada[-1]['tempo'] + 5.0:
                self.reset_state(); status_geral = "IDLE"; overlay_atual = ""

        if status_geral != "SYNCED": linha_atual = overlay_atual

        return {
            "status": status_geral,
            "is_translating": self.traduzindo, 
            "letra_atual": linha_atual,
            "letra_anterior": linha_anterior,
            "letra_futura": linha_futura,
            "tamanho_fonte": self.overlay_font_size,
            "musica": self.musica_atual,
            "artista": self.artista_atual,
            "full_lyrics": [{"tempo": i["tempo"], "letra": i["letra"]} for i in self.letra_sincronizada] if self.letra_sincronizada else []
        }

manager = MusicManager()
clientes_conectados = set()

async def async_worker_verificacao(manager):
    loop = asyncio.get_event_loop()
    while manager.servidor_rodando:
        if not manager.escutando or manager.busca_concluida or manager.manual_mode:
            await asyncio.sleep(1)
            continue
            
        current_session = manager.session_id
        t_inicio_gravacao = time.time()
        
        try: audio_bytes = await loop.run_in_executor(None, manager.gravar_audio_memoria, 4)
        except Exception:
            manager.device_info = manager._configurar_loopback()
            await asyncio.sleep(2)
            continue
            
        if manager.session_id != current_session or not manager.escutando or manager.manual_mode: 
            continue
        
        nova_musica, novo_artista, offset_shazam = await manager.reconhecer_snippet(audio_bytes)
        if nova_musica and manager.escutando and manager.session_id == current_session:
            manager.musica_atual, manager.artista_atual = nova_musica, novo_artista
            letra = await loop.run_in_executor(None, manager.buscar_letra_lrclib, novo_artista, nova_musica)
            
            if manager.session_id == current_session:
                manager.busca_concluida = True
                if letra:
                    manager.letra_original = manager.letra_sincronizada = letra
                    manager.tempo_referencia_sistema = t_inicio_gravacao - offset_shazam
        await asyncio.sleep(2)

async def run_manual_search(manager, art, mus, current_session):
    loop = asyncio.get_event_loop()
    letra = await loop.run_in_executor(None, manager.buscar_letra_lrclib, art, mus)
    if manager.session_id == current_session: 
        manager.busca_concluida = True
        if letra:
            manager.letra_original = manager.letra_sincronizada = letra
            manager.tempo_referencia_sistema = time.time()

async def ws_handler(websocket):
    clientes_conectados.add(websocket)
    try:
        async for message in websocket:
            try:
                comando = json.loads(message)
                acao = comando.get("action")
                if acao == "LISTEN":
                    manager.reset_state()
                    manager.escutando = True
                elif acao == "RESET":
                    manager.reset_state()
                elif acao == "QUIT":
                    manager.servidor_rodando = False
                    os._exit(0)
                elif acao == "FONT_UP":
                    manager.overlay_font_size = min(80, manager.overlay_font_size + 2)
                elif acao == "FONT_DOWN":
                    manager.overlay_font_size = max(14, manager.overlay_font_size - 2)
                elif acao == "TRANSLATE":
                    lang = comando.get("lang", "original")
                    if lang == "original":
                        manager.aplicar_idioma("original")
                    else:
                        async def _bg_translate():
                            manager.traduzindo = True
                            await asyncio.to_thread(manager.aplicar_idioma, lang)
                            manager.traduzindo = False
                        asyncio.create_task(_bg_translate())
                elif acao == "MANUAL_SEARCH":
                    art, mus = comando.get("artist", ""), comando.get("song", "")
                    if art and mus:
                        manager.reset_state() 
                        manager.manual_mode = True 
                        manager.musica_atual, manager.artista_atual = mus, art
                        manager.escutando = True
                        asyncio.create_task(run_manual_search(manager, art, mus, manager.session_id))
                elif acao == "SET_SYNC_TIME":
                    novo_tempo = comando.get("time", 0.0)
                    manager.tempo_referencia_sistema = time.time() - novo_tempo
            except Exception: pass
    finally: clientes_conectados.remove(websocket)

async def broadcast_estado_ui(manager):
    while manager.servidor_rodando:
        if clientes_conectados: websockets.broadcast(clientes_conectados, json.dumps(manager.obter_estado_atual()))
        await asyncio.sleep(0.1)

async def main_background(manager, porta):
    asyncio.create_task(async_worker_verificacao(manager))
    asyncio.create_task(broadcast_estado_ui(manager))
    async with websockets.serve(ws_handler, "localhost", porta): await asyncio.Future()

def encontrar_porta_livre():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(('', 0)); return s.getsockname()[1]

if __name__ == "__main__":
    porta = encontrar_porta_livre()
    caminho_overlay = os.path.join(os.path.dirname(sys.executable) if getattr(sys, 'frozen', False) else os.path.dirname(os.path.abspath(__file__)), "FrontLineOverlay.exe")
    try: subprocess.Popen([caminho_overlay, str(porta)])
    except: pass
    try: asyncio.run(main_background(manager, porta))
    except KeyboardInterrupt: pass