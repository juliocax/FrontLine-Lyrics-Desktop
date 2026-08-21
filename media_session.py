"""
MediaSessionWatcher — acompanha as sessões de mídia do Windows
(GlobalSystemMediaTransportControlsSessionManager) para saber, em tempo real,
qual música está tocando em QUALQUER player (Spotify, YouTube, Apple Music...).

Isso permite trocar a letra instantaneamente quando a faixa muda, congelar o
relógio da letra quando o player pausa e re-ancorar a sincronia em caso de seek.
"""
import time
import threading
import asyncio
from datetime import datetime, timezone

try:
    import winrt.windows.media.control as wmc
    import winrt.windows.storage.streams as wss
    MEDIA_SESSION_DISPONIVEL = True
except ImportError:
    MEDIA_SESSION_DISPONIVEL = False

TAMANHO_MAX_CAPA = 2_000_000


class MediaInfo:
    __slots__ = ("titulo", "artista", "posicao", "duracao", "tocando", "app", "capa_bytes")

    def __init__(self, titulo, artista, posicao, duracao, tocando, app, capa_bytes=b""):
        self.titulo = titulo
        self.artista = artista
        self.posicao = posicao
        self.duracao = duracao
        self.tocando = tocando
        self.app = app
        self.capa_bytes = capa_bytes

    @property
    def chave(self):
        return (self.titulo.lower().strip(), self.artista.lower().strip())


class MediaSessionWatcher:
    """Sonda o sistema ~1x/segundo e entrega snapshots via callback.

    O callback roda na thread interna do watcher; pode fazer I/O bloqueante
    (busca de letra, download de capa) sem travar a UI nem o loop asyncio principal.
    """

    INTERVALO_SONDAGEM = 1.0

    def __init__(self, callback):
        self.callback = callback
        self.preferencia_chave = None
        self.ultima_info = None
        self._rodando = False
        self._thread = None
        self._chave_capa_cacheada = None
        self._capa_cacheada = b""

    @property
    def disponivel(self):
        return MEDIA_SESSION_DISPONIVEL and self._rodando

    def start(self):
        if not MEDIA_SESSION_DISPONIVEL or self._rodando:
            return
        self._rodando = True
        self._thread = threading.Thread(target=self._executar_loop, daemon=True)
        self._thread.start()

    def stop(self):
        self._rodando = False

    def _executar_loop(self):
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        try:
            loop.run_until_complete(self._sondar_para_sempre())
        except Exception:
            self._rodando = False

    async def _sondar_para_sempre(self):
        gerenciador = None
        while self._rodando:
            try:
                if gerenciador is None:
                    gerenciador = await wmc.GlobalSystemMediaTransportControlsSessionManager.request_async()
                info = await self._coletar(gerenciador)
                if info is not None or self.ultima_info is not None:
                    self.ultima_info = info
                    try:
                        self.callback(info)
                    except Exception:
                        pass
            except Exception:
                gerenciador = None
            await asyncio.sleep(self.INTERVALO_SONDAGEM)

    async def _coletar(self, gerenciador):
        sessoes = gerenciador.get_sessions()
        candidatos = []
        for i in range(sessoes.size):
            sessao = sessoes.get_at(i)
            try:
                props = await sessao.try_get_media_properties_async()
            except Exception:
                continue
            if not props.title:
                continue
            status = sessao.get_playback_info().playback_status
            timeline = sessao.get_timeline_properties()
            tocando = status == wmc.GlobalSystemMediaTransportControlsSessionPlaybackStatus.PLAYING

            posicao = timeline.position.total_seconds() if timeline.position else 0.0
            if tocando and timeline.last_updated_time:
                # Alguns players só atualizam a posição a cada ~60s; extrapolar
                # com last_updated_time é o que mantém a posição contínua.
                delta = (datetime.now(timezone.utc) - timeline.last_updated_time).total_seconds()
                if 0 <= delta < 3600:
                    posicao += delta

            duracao = 0.0
            try:
                if timeline.end_time and timeline.start_time:
                    duracao = max(0.0, (timeline.end_time - timeline.start_time).total_seconds())
            except Exception:
                pass

            chave = (props.title.lower().strip(), props.artist.lower().strip())
            capa = await self._capa_da_sessao(chave, props)

            candidatos.append((tocando, chave == self.preferencia_chave, props.title,
                               props.artist, posicao, duracao, sessao.source_app_user_model_id, capa))

        if not candidatos:
            return None
        # Prefere: tocando > faixa atual conhecida > primeira da lista
        candidatos.sort(key=lambda c: (not c[0], not c[1]))
        _, _, titulo, artista, posicao, duracao, app, capa = candidatos[0]
        return MediaInfo(titulo, artista, posicao, duracao, candidatos[0][0], app, capa)

    async def _capa_da_sessao(self, chave, props):
        if chave == self._chave_capa_cacheada:
            return self._capa_cacheada
        capa = b""
        try:
            if props.thumbnail:
                stream = await props.thumbnail.open_read_async()
                tamanho = min(stream.size, TAMANHO_MAX_CAPA)
                reader = wss.DataReader(stream)
                await reader.load_async(tamanho)
                capa = bytes(reader.read_buffer(tamanho))
                reader.detach_stream()
                stream.close()
        except Exception:
            capa = b""
        self._chave_capa_cacheada = chave
        self._capa_cacheada = capa
        return capa
