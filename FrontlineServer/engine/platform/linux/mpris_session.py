"""MPRIS watcher for FrontLine Linux (standalone integration stage).

Uses the D-Bus session bus via dbus-next. Does not import any Windows modules.
The public watcher contract mirrors engine.media_session.MediaSessionWatcher.

Stage-1 limitation: cover art is not fetched; capa_bytes remains b"".
Not yet connected to MusicManager or the websocket server.
"""

from __future__ import annotations

import asyncio
import logging
import math
import threading
from typing import Optional

from dbus_next import Message, MessageType
from dbus_next.aio import MessageBus


PREFIX = "org.mpris.MediaPlayer2."
PATH = "/org/mpris/MediaPlayer2"
PLAYER_IFACE = "org.mpris.MediaPlayer2.Player"
PROPS_IFACE = "org.freedesktop.DBus.Properties"
DBUS_NAME = "org.freedesktop.DBus"
DBUS_PATH = "/org/freedesktop/DBus"
IGNORED_SERVICES = {"org.mpris.MediaPlayer2.playerctld"}
MEDIA_SESSION_DISPONIVEL = True  # Availability is checked at runtime in start().


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


def _seconds(value) -> float:
    """Convert MPRIS microseconds to FrontLine seconds, rejecting invalid values."""
    try:
        number = float(value) / 1_000_000
    except (ValueError, TypeError, OverflowError):
        return 0.0
    return number if math.isfinite(number) and 0 <= number <= 12 * 3600 else 0.0


async def _call(bus, destination, path, interface, member, signature="", body=None):
    reply = await asyncio.wait_for(
        bus.call(Message(destination=destination, path=path, interface=interface,
                         member=member, signature=signature, body=body or [])),
        timeout=3.0,
    )
    if reply.message_type != MessageType.METHOD_RETURN:
        raise RuntimeError(f"{destination}: {reply.error_name}: {reply.body}")
    return reply.body


class MediaSessionWatcher:
    INTERVALO_SONDAGEM = 1.0

    def __init__(self, callback):
        self.callback = callback
        self.preferencia_chave = None
        self.ultima_info: Optional[MediaInfo] = None
        self._rodando = False
        self._thread = None
        self._chave_ignorada = None

    @property
    def disponivel(self):
        return self._rodando and self._thread is not None and self._thread.is_alive()

    def start(self):
        # Do not overlap two D-Bus polling threads when stop/start happens quickly.
        if self._thread is not None and self._thread.is_alive():
            if self._rodando:
                return
            raise RuntimeError("Previous MPRIS watcher thread is still stopping")
        self._rodando = True
        self._thread = threading.Thread(target=self._executar_loop, daemon=True, name="mpris-session")
        self._thread.start()

    def stop(self):
        self._rodando = False
        thread = self._thread
        if thread is not None and thread.is_alive() and thread is not threading.current_thread():
            thread.join(timeout=10.0)
            if thread.is_alive():
                logging.warning("MPRIS watcher thread did not stop within 10 seconds")

    def ignorar_faixa_atual(self):
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
        try:
            asyncio.run(self._sondar_para_sempre())
        except Exception:
            logging.exception("MediaSessionWatcher Linux encerrou por erro")
        finally:
            self._rodando = False

    async def _sondar_para_sempre(self):
        bus = None
        falhas = 0
        try:
            while self._rodando:
                try:
                    if bus is None:
                        bus = await MessageBus().connect()
                    info = await self._coletar(bus)
                    if not self._rodando:
                        break
                    if info is not None or self.ultima_info is not None:
                        self.ultima_info = info
                        try:
                            self.callback(info)
                        except Exception:
                            logging.exception("Falha no callback MPRIS")
                    falhas = 0
                except Exception:
                    falhas += 1
                    logging.exception("Falha ao consultar MPRIS (n=%d)", falhas)
                    if bus is not None:
                        bus.disconnect()
                        bus = None
                    if not self._rodando:
                        break
                    await asyncio.sleep(min(8.0, 2 ** min(falhas - 1, 3)))
                    continue
                await asyncio.sleep(self.INTERVALO_SONDAGEM)
        finally:
            if bus is not None:
                bus.disconnect()

    async def _coletar(self, bus):
        names = (await _call(bus, DBUS_NAME, DBUS_PATH, DBUS_NAME, "ListNames"))[0]
        players = sorted(name for name in names if name.startswith(PREFIX) and name not in IGNORED_SERVICES)
        candidatos = []
        for name in players:
            try:
                payload = (await _call(bus, name, PATH, PROPS_IFACE, "GetAll", "s", [PLAYER_IFACE]))[0]
                values = {key: variant.value for key, variant in payload.items()}
                md = {key: variant.value for key, variant in values.get("Metadata", {}).items()}
                titulo = str(md.get("xesam:title") or "").strip()
                if not titulo:
                    continue
                raw_artists = md.get("xesam:artist") or []
                artista = ", ".join(str(a) for a in raw_artists) if isinstance(raw_artists, (tuple, list)) else str(raw_artists)
                tocando = values.get("PlaybackStatus") == "Playing"
                # Some implementations omit Position from GetAll; query it explicitly.
                try:
                    position = (await _call(bus, name, PATH, PROPS_IFACE, "Get", "ss", [PLAYER_IFACE, "Position"]))[0].value
                except Exception:
                    position = values.get("Position", 0)
                candidatos.append((tocando, (titulo.lower().strip(), artista.lower().strip()) == self.preferencia_chave,
                                   MediaInfo(titulo, artista, _seconds(position),
                                             _seconds(md.get("mpris:length", 0)), tocando, name)))
            except Exception:
                logging.debug("Falha ao consultar player MPRIS %s", name, exc_info=True)
        if not candidatos:
            return None
        candidatos.sort(key=lambda item: (not item[0], not item[1]))
        return candidatos[0][2]


if __name__ == "__main__":
    import time

    logging.basicConfig(level=logging.INFO, format="%(levelname)s: %(message)s")

    def show(info):
        if info is None:
            print("Nenhum player com música disponível", flush=True)
        else:
            print(f"{info.app} | {info.artista} - {info.titulo} | "
                  f"{'Playing' if info.tocando else 'Paused/Stopped'} | "
                  f"{info.posicao:.2f}s / {info.duracao:.2f}s", flush=True)

    watcher = MediaSessionWatcher(show)
    watcher.start()
    print("Watcher MPRIS iniciado. Teste reprodução, pausa, troca de faixa e seek. Ctrl+C para sair.")
    try:
        while True:
            time.sleep(0.25)
    except KeyboardInterrupt:
        watcher.stop()
        print("Watcher encerrado.")
