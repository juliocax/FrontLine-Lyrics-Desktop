"""faulthandler + crash file para o FrontlineServer.exe.

Partner Center classifica dumps deste processo (PyInstaller + WinRT) como
Uncategorized: não há PDB. Este módulo grava a stack nativa em
python_fault.log e as exceções Python em python_crash.log.

NÃO chama logging.basicConfig — o FrontlineServer.py já tem RotatingFileHandler
em frontline_server.log. instalar() é idempotente no faulthandler e envolve o
sys.excepthook *atual*, então chame:

  1. no topo, ANTES de importar media_session/winrt
  2. de novo depois de atribuir sys.excepthook no servidor
"""

from __future__ import annotations

import faulthandler
import logging
import os
import sys
import threading
import traceback
from pathlib import Path

_fault_fp = None


def _dir_logs() -> Path:
    base = os.environ.get("LOCALAPPDATA") or str(Path.home())
    d = Path(base) / "FrontLineLyrics" / "logs"
    d.mkdir(parents=True, exist_ok=True)
    return d


def _rotacionar(path: Path, max_bytes: int = 2_000_000) -> None:
    try:
        if path.exists() and path.stat().st_size >= max_bytes:
            bak = path.with_suffix(path.suffix + ".1")
            if bak.exists():
                bak.unlink()
            path.replace(bak)
    except OSError:
        pass


def instalar() -> Path:
    global _fault_fp
    logdir = _dir_logs()
    crash_py = logdir / "python_crash.log"
    fault_py = logdir / "python_fault.log"
    _rotacionar(crash_py)
    _rotacionar(fault_py)

    if _fault_fp is None:
        try:
            _fault_fp = open(fault_py, "a", encoding="utf-8")
            faulthandler.enable(file=_fault_fp, all_threads=True)
        except Exception:
            logging.exception("faulthandler não pôde ser ligado")

    _envolver_excepthook(crash_py)

    if not getattr(threading.excepthook, "_frontline", False):
        def _thread_hook(args: threading.ExceptHookArgs):
            logging.critical(
                "thread %s\n%s",
                args.thread.name if args.thread else "?",
                "".join(traceback.format_exception(args.exc_type, args.exc_value, args.exc_traceback)),
            )
        _thread_hook._frontline = True  # type: ignore[attr-defined]
        threading.excepthook = _thread_hook

    return logdir


def _envolver_excepthook(crash_py: Path) -> None:
    atual = sys.excepthook
    if getattr(atual, "_frontline_wrapped", False):
        return

    def _excepthook(exc_type, exc, tb):
        texto = "".join(traceback.format_exception(exc_type, exc, tb))
        logging.critical("sys.excepthook\n%s", texto)
        try:
            with crash_py.open("a", encoding="utf-8") as fh:
                fh.write(texto)
                fh.write("\n")
        except OSError:
            pass
        atual(exc_type, exc, tb)

    _excepthook._frontline_wrapped = True  # type: ignore[attr-defined]
    sys.excepthook = _excepthook
