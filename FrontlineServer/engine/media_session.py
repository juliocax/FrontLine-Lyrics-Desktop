"""
MediaSessionWatcher — lê as sessões de mídia do Windows
(GlobalSystemMediaTransportControlsSessionManager) para saber, em tempo real,
qual música está tocando em qualquer player (Spotify, YouTube, Apple Music...).

Origem: contribuição de Warith Adetayo (@WarithAdetayo) no PR #2 do repositório
público FrontLine-Lyrics-Desktop. Portado para o servidor headless (FrontlineServer)
na reescrita C# + Python.

Co-authored-by: Warith Adetayo <warithadetayo.awa@gmail.com>
"""
import logging
import threading
import asyncio
from datetime import datetime, timezone

try:
    import winrt.windows.media.control as wmc
    import winrt.windows.storage.streams as wss
    MEDIA_SESSION_DISPONIVEL = True
except ImportError:
    MEDIA_SESSION_DISPONIVEL = False

# 512 KiB de JPEG/PNG
TAMANHO_MAX_CAPA = 512_000


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
    (busca de letra) sem travar o loop asyncio principal do FrontlineServer.
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
        self._chave_ignorada = None

    @property
    def disponivel(self):
        return MEDIA_SESSION_DISPONIVEL and self._rodando

    def start(self):
        if not MEDIA_SESSION_DISPONIVEL or self._rodando:
            return
        self._rodando = True
        self._thread = threading.Thread(target=self._executar_loop, daemon=True, name="media-session")
        self._thread.start()

    def stop(self):
        self._rodando = False

    def ignorar_faixa_atual(self):
        """Clear/RESET to prevent auto-restart (title, artist)."""
        if self.ultima_info is not None:
            self._chave_ignorada = self.ultima_info.chave
        return self._chave_ignorada

    def ignorar_chave(self, chave):
        self._chave_ignorada = chave
        return chave

    def limpar_ignorada(self):
        self._chave_ignorada = None

    def chave_esta_ignorada(self, chave) -> bool:
        if self._chave_ignorada is None or chave is None:
            return False
        if chave == self._chave_ignorada:
            return True
        self._chave_ignorada = None
        return False

    def _executar_loop(self):
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        try:
            loop.run_until_complete(self._sondar_para_sempre())
        except Exception:
            logging.exception("MediaSessionWatcher encerrado por erro")
            self._rodando = False
        finally:
            try:
                loop.stop()
            except Exception:
                pass
            loop.close()

    async def _sondar_para_sempre(self):
        gerenciador = None
        falhas_seguidas = 0
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
                        logging.exception("Falha no callback do MediaSessionWatcher")
                falhas_seguidas = 0
            except Exception:
                falhas_seguidas += 1
                logging.exception("MediaSessionWatcher: falha ao sondar (n=%s)", falhas_seguidas)
                gerenciador = None
                # Backoff para não girar em AccessViolation/COM falho a 1 Hz.
                await asyncio.sleep(min(8.0, self.INTERVALO_SONDAGEM * (2 ** min(falhas_seguidas, 3))))
                continue
            await asyncio.sleep(self.INTERVALO_SONDAGEM)

    async def _coletar(self, gerenciador):
        try:
            sessoes = gerenciador.get_sessions()
        except Exception:
            logging.exception("get_sessions falhou")
            return None

        candidatos = []
        tamanho = 0
        try:
            tamanho = sessoes.size
        except Exception:
            return None

        for i in range(tamanho):
            try:
                sessao = sessoes.get_at(i)
            except Exception:
                continue
            try:
                props = await sessao.try_get_media_properties_async()
            except Exception:
                continue
            if not props or not props.title:
                continue
            try:
                status = sessao.get_playback_info().playback_status
                timeline = sessao.get_timeline_properties()
            except Exception:
                continue
            tocando = status == wmc.GlobalSystemMediaTransportControlsSessionPlaybackStatus.PLAYING

            posicao = 0.0
            try:
                posicao = timeline.position.total_seconds() if timeline.position else 0.0
                if tocando and timeline.last_updated_time:
                    # Alguns players só atualizam a posição a cada ~60s; extrapolar
                    # com last_updated_time é o que mantém a posição contínua.
                    last = timeline.last_updated_time
                    if getattr(last, "tzinfo", None) is None:
                        last = last.replace(tzinfo=timezone.utc)
                    delta = (datetime.now(timezone.utc) - last).total_seconds()
                    if 0 <= delta < 3600:
                        posicao += delta
            except Exception:
                posicao = 0.0
            try:
                posicao = float(posicao)
            except (TypeError, ValueError):
                posicao = 0.0
            if posicao != posicao or posicao < 0.0 or posicao > 12 * 3600:
                posicao = 0.0

            duracao = 0.0
            try:
                if timeline.end_time and timeline.start_time:
                    duracao = max(0.0, (timeline.end_time - timeline.start_time).total_seconds())
            except Exception:
                pass

            chave = (props.title.lower().strip(), (props.artist or "").lower().strip())
            capa = await self._capa_da_sessao(chave, props)

            try:
                app_id = sessao.source_app_user_model_id
            except Exception:
                app_id = ""

            candidatos.append((tocando, chave == self.preferencia_chave, props.title,
                               props.artist or "", posicao, duracao, app_id, capa))

        if not candidatos:
            return None
        # Prefere: tocando > faixa atual conhecida > primeira da lista
        candidatos.sort(key=lambda c: (not c[0], not c[1]))
        tocando, _, titulo, artista, posicao, duracao, app, capa = candidatos[0]
        return MediaInfo(titulo, artista, posicao, duracao, tocando, app, capa)

    async def _capa_da_sessao(self, chave, props):
        if chave == self._chave_capa_cacheada:
            return self._capa_cacheada
        capa = b""
        stream = None
        reader = None
        try:
            if props.thumbnail:
                stream = await props.thumbnail.open_read_async()
                tamanho = int(min(stream.size, TAMANHO_MAX_CAPA))
                if tamanho > 0:
                    reader = wss.DataReader(stream)
                    await reader.load_async(tamanho)
                    capa = bytes(reader.read_buffer(tamanho))
        except Exception:
            logging.debug("capa indisponível para %s", chave, exc_info=True)
            capa = b""
        finally:
            try:
                if reader is not None:
                    reader.detach_stream()
            except Exception:
                pass
            try:
                if stream is not None:
                    stream.close()
            except Exception:
                pass
        self._chave_capa_cacheada = chave
        self._capa_cacheada = capa
        return capa
