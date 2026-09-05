"""
FrontlineServer entrypoint.

Startup order matters here:
1. crash_guard.instalar() runs before importing anything that touches WinRT
   (music_manager -> media_session -> winrt), so a native crash in there is
   still captured.
2. The Windows-only Popen patch (hide subprocess console windows) is applied
   before we import anything that might shell out (translators, etc.), via
   the music_manager import below.
3. Logging is configured before anything else logs.
4. crash_guard.instalar() is called a second time after sys.excepthook is
   set, so it wraps *that* hook too (the faulthandler side is idempotent).
"""

import os
import sys

from engine.crash_guard import instalar as instalar_crash_guard

instalar_crash_guard()

import asyncio
import logging
import subprocess
import traceback
from logging.handlers import RotatingFileHandler

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
    ],
)
logging.info(f"Log started at: {LOG_FILE}")
logging.info("Server build: translation with 3 parallel sources (google/mymemory/translators-bing) v1")

# These pull in media_session (WinRT), pyaudiowpatch, shazamio and
# translators — only safe to import after crash_guard is installed above.
from engine.media_session import MEDIA_SESSION_DISPONIVEL
from engine.music_manager import MusicManager
import engine.ws_server as ws_server

if MEDIA_SESSION_DISPONIVEL:
    logging.info("Media Session (SMTC) available — metadata auto-follow enabled.")
else:
    logging.info("winrt not installed: Media Session auto-follow disabled (Shazam remains active).")


def global_exception_handler(exctype, value, tb):
    """Custom exception handler to log critical errors."""
    logging.critical("=== CRITICAL ERROR ENCOUNTERED ===")
    logging.critical("".join(traceback.format_exception(exctype, value, tb)))


sys.excepthook = global_exception_handler
# Wraps the hook above so it also writes python_crash.log.
instalar_crash_guard()


if __name__ == "__main__":
    # pyinstaller --noconfirm --onedir --windowed --collect-all anyascii --collect-all winrt --hidden-import winrt.windows.media.control --hidden-import winrt.windows.storage.streams --hidden-import crash_guard --hidden-import media_session --hidden-import music_manager --hidden-import audio_capture --hidden-import recognition --hidden-import lyrics --hidden-import cover_art --hidden-import translation --hidden-import smtc_policy --hidden-import tuning --hidden-import task_utils --hidden-import workers --hidden-import ws_server --name "FrontlineServer" "FrontlineServer.py"
    server_port = 8765

    if len(sys.argv) > 1:
        try:
            server_port = int(sys.argv[1])
        except ValueError:
            pass

    logging.info(f"Starting the FrontLine server in the background on port {server_port}...")

    manager = MusicManager()
    ws_server.configure(manager)

    try:
        asyncio.run(ws_server.main_background(server_port))
    except KeyboardInterrupt:
        logging.info("Server shut down manually.")
