"""Resolve the data directory consistently for the server and its workers."""
import os
from pathlib import Path
import sys


def addons_directory():
    override = os.environ.get("FRONTLINE_ADDONS_DIR")
    if override:
        return Path(override).expanduser()
    if sys.platform.startswith("linux"):
        data = (os.environ.get("SNAP_USER_COMMON") or os.environ.get("XDG_DATA_HOME")
                or Path.home() / ".local" / "share")
    else:
        data = os.environ.get("LOCALAPPDATA") or Path.home()
    return Path(data) / "FrontLineLyrics" / "Addons"
