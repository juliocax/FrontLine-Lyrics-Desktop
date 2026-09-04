"""
System-audio (WASAPI loopback) capture.

This is the only place in the codebase that talks to PyAudio directly.
MusicManager owns one AudioCapture instance and calls into it from the
background workers (via loop.run_in_executor, since PyAudio is blocking).
"""

import array
import io
import logging
import math
import wave
from typing import Any, Dict, Optional

import pyaudiowpatch as pyaudio


class AudioCapture:
    """Records short snippets of whatever is currently playing through the speakers."""

    def __init__(self):
        self.pyaudio_instance = pyaudio.PyAudio()
        self.device_info: Optional[Dict[str, Any]] = self.configure_loopback()

    def configure_loopback(self) -> Optional[Dict[str, Any]]:
        """Pick the WASAPI loopback device that mirrors the default output."""
        try:
            wasapi_info = self.pyaudio_instance.get_host_api_info_by_type(pyaudio.paWASAPI)
            default_speakers = self.pyaudio_instance.get_device_info_by_index(
                wasapi_info["defaultOutputDevice"]
            )

            if not default_speakers["isLoopbackDevice"]:
                for loopback in self.pyaudio_instance.get_loopback_device_info_generator():
                    if default_speakers["name"] in loopback["name"]:
                        return loopback
            return default_speakers
        except Exception as e:
            logging.error(f"Error configuring loopback: {e}")
            return None

    def record_to_memory(self, duration: float) -> bytes:
        """Record system audio for `duration` seconds and return it as WAV bytes."""
        if not self.device_info:
            raise Exception("Audio device error.")

        chunk = 512
        channels = self.device_info["maxInputChannels"]
        rate = int(self.device_info["defaultSampleRate"])

        stream = self.pyaudio_instance.open(
            format=pyaudio.paInt16,
            channels=channels,
            rate=rate,
            frames_per_buffer=chunk,
            input=True,
            input_device_index=self.device_info["index"],
        )

        frames = [stream.read(chunk) for _ in range(0, int(rate / chunk * duration))]
        stream.stop_stream()
        stream.close()

        audio_buffer = io.BytesIO()
        with wave.open(audio_buffer, "wb") as wf:
            wf.setnchannels(channels)
            wf.setsampwidth(self.pyaudio_instance.get_sample_size(pyaudio.paInt16))
            wf.setframerate(rate)
            wf.writeframes(b"".join(frames))

        return audio_buffer.getvalue()

    @staticmethod
    def rms(audio_bytes: bytes) -> float:
        """Compute the RMS (energy) of an in-memory WAV buffer.

        Used as a silence gate: skips spending a Shazam recognition on
        silent/near-silent snippets (song-selection screen, transition,
        muted ad, etc.). Python 3.13 removed the 'audioop' module, so this
        is computed by hand with 'array'.
        """
        try:
            with io.BytesIO(audio_bytes) as buf, wave.open(buf, "rb") as wf:
                raw = wf.readframes(wf.getnframes())
            if not raw:
                return 0.0
            samples = array.array("h")  # int16
            samples.frombytes(raw[: len(raw) - (len(raw) % 2)])
            if not samples:
                return 0.0
            sum_sq = sum(s * s for s in samples)
            return math.sqrt(sum_sq / len(samples))
        except Exception:
            return 0.0
