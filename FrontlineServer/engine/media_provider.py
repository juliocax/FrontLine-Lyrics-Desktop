"""Select the existing platform media-session watcher.

Do not import Windows WinRT modules on Linux. Keep the public watcher contract
unchanged; MusicManager remains responsible for responding to snapshots.
"""

import sys

if sys.platform == "win32":
    from engine.media_session import MediaSessionWatcher
elif sys.platform.startswith("linux"):
    from engine.platform.linux.mpris_session import MediaSessionWatcher
else:
    raise RuntimeError(f"FrontLine media session unsupported on {sys.platform!r}")

__all__ = ["MediaSessionWatcher"]
