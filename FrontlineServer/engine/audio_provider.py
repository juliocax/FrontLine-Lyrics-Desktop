"""Select the OS-specific AudioCapture without loading Windows-only modules on Linux."""

import sys

if sys.platform == "win32":
    from engine.audio_capture import AudioCapture
elif sys.platform.startswith("linux"):
    from engine.platform.linux.audio_capture import AudioCapture
else:
    raise RuntimeError(f"Audio capture is unsupported on {sys.platform!r}")

__all__ = ["AudioCapture"]
