"""Linux system-audio loopback capture via PipeWire's PulseAudio interface.

Uses the current default sink's .monitor, never the microphone. `parecord`
streams raw signed 16-bit stereo PCM to stdout; Python wraps it as WAV bytes.
No temporary files, ffmpeg, or Windows dependencies are required.
"""

import array
import io
import logging
import math
import os
import select
import time
import shutil
import subprocess
import sys
import wave
from typing import Any, Dict, Optional


class AudioCapture:
    RATE = 48000
    CHANNELS = 2
    SAMPLE_WIDTH = 2  # signed 16-bit PCM

    def __init__(self):
        if not shutil.which("parecord") or not shutil.which("pactl"):
            raise RuntimeError("Instale pulseaudio-utils (parecord e pactl).")
        self.device_info: Optional[Dict[str, Any]] = self.configure_loopback()

    def configure_loopback(self) -> Optional[Dict[str, Any]]:
        """Discover the default output's monitor, never fall back to a microphone."""
        try:
            sink = subprocess.run(
                ["pactl", "get-default-sink"],
                capture_output=True, text=True, check=True, timeout=5,
            ).stdout.strip()
            if not sink:
                raise RuntimeError("Nenhuma saída padrão encontrada")
            monitor = f"{sink}.monitor"
            sources = subprocess.run(
                ["pactl", "list", "short", "sources"],
                capture_output=True, text=True, check=True, timeout=5,
            ).stdout
            if monitor not in [line.split("\t")[1] for line in sources.splitlines()
                               if len(line.split("\t")) >= 2]:
                raise RuntimeError(f"Monitor da saída não encontrado: {monitor}")
            return {"name": monitor, "defaultSampleRate": self.RATE,
                    "maxInputChannels": self.CHANNELS}
        except (OSError, subprocess.SubprocessError, RuntimeError) as exc:
            logging.warning("Monitor de áudio Linux indisponível: %s", exc)
            return None

    def record_to_memory(self, duration: float) -> bytes:
        """Record `duration` seconds and return a PCM16 WAV in memory."""
        if not (0 < duration <= 120) or not math.isfinite(duration):
            raise ValueError("A duração deve estar entre 0 e 120 segundos")
        # Re-evaluate on every capture so changing default speakers is supported.
        info = self.configure_loopback()
        self.device_info = info
        if info is None:
            raise RuntimeError("Monitor da saída padrão indisponível")

        cmd = [
            "parecord", "--raw", f"--device={info['name']}",
            f"--rate={self.RATE}", f"--channels={self.CHANNELS}",
            "--format=s16le",
        ]
        # Count audio frames, not process wall-clock time: startup/negotiation
        # with PipeWire can take seconds and must not shorten the recording.
        frame_bytes = self.CHANNELS * self.SAMPLE_WIDTH
        target_frames = max(1, round(duration * self.RATE))
        target_bytes = target_frames * frame_bytes
        # Startup allowance + recording time; no infinite wait on a stalled source.
        deadline = time.monotonic() + duration + 8.0
        proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        collected = bytearray()
        try:
            assert proc.stdout is not None
            while len(collected) < target_bytes:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError(
                        f"Capture incompleta: {len(collected) // frame_bytes} "
                        f"de {target_frames} quadros recebidos"
                    )
                readable, _, _ = select.select([proc.stdout], [], [], min(remaining, 1.0))
                if not readable:
                    if proc.poll() is not None:
                        raise RuntimeError(f"parecord terminou antes da captura completa (código {proc.returncode})")
                    continue
                chunk = os.read(proc.stdout.fileno(), min(65536, target_bytes - len(collected)))
                if not chunk:
                    raise RuntimeError(
                        f"parecord encerrou a saída antes da captura completa "
                        f"({len(collected) // frame_bytes}/{target_frames} quadros)"
                    )
                collected.extend(chunk)

            output = io.BytesIO()
            with wave.open(output, "wb") as wav:
                wav.setnchannels(self.CHANNELS)
                wav.setsampwidth(self.SAMPLE_WIDTH)
                wav.setframerate(self.RATE)
                wav.writeframes(collected)
            return output.getvalue()
        finally:
            if proc.poll() is None:
                proc.terminate()
            try:
                proc.communicate(timeout=2)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.communicate()

    @staticmethod
    def rms(audio_bytes: bytes) -> float:
        """Same silence-gate RMS semantics as the original Windows implementation."""
        try:
            with wave.open(io.BytesIO(audio_bytes), "rb") as wav:
                if wav.getsampwidth() != 2:
                    return 0.0
                raw = wav.readframes(wav.getnframes())
            samples = array.array("h")
            samples.frombytes(raw[:len(raw) - len(raw) % 2])
            if sys.byteorder != "little":
                samples.byteswap()
            if not samples:
                return 0.0
            return math.sqrt(sum(s * s for s in samples) / len(samples))
        except (ValueError, EOFError, wave.Error):
            return 0.0
