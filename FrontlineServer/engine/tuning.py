"""
Tuning constants for the "listen / auto-follow / sync" pipeline.

Everything here is a knob, not logic. Splitting these out keeps
music_manager.py and workers.py free of magic numbers and gives future
contributors a single place to look when they want to tweak timing.
"""

# --- Silence gate for the audio capture worker ---------------------------
# Below this RMS the snippet is considered silence and Shazam is not called
# (song-selection screen, transition, muted ad, etc.).
SILENCE_RMS_THRESHOLD = 180.0

# --- Shazam snippet lengths -----------------------------------------------
# Live audio (crowd noise, reverb, mixed sets) needs a longer snippet.
# Shazam accepts up to ~12s; we stay comfortably under that.
SHAZAM_LIVE_SECONDS = 8.0
SHAZAM_QUICK_SECONDS = 4.0

# --- Live re-fingerprint worker (keeps the Shazam-driven clock honest) ---
LIVE_FINGERPRINT_PERIOD = 45.0
LIVE_FINGERPRINT_SECONDS = 6.0
LIVE_DRIFT_TOLERANCE = 3.0
LIVE_DRIFT_CONFIRM = 2

# --- Auto mode retry/backoff while nothing is recognized yet -------------
AUTO_MIN_RETRY_DELAY = 2.0
AUTO_MAX_RETRY_DELAY = 20.0
AUTO_BACKOFF_MULTIPLIER = 1.7

# --- "Give up and go back to listening" timers ----------------------------
NOT_FOUND_GIVEUP_SECONDS = 10.0
END_OF_LYRICS_GRACE = 2.0
PREV_TRACK_COOLDOWN = 25.0

# --- SMTC clock servo (seek/pause re-anchoring) ---------------------------
# Ported from Warith Adetayo's PR #2: ~12s calibration window with
# single-sample correction, then partial drift correction (~35%) after
# that; seeks bigger than SEEK_TOLERANCE need two samples outside the
# window before we trust them.
SEEK_TOLERANCE = 4.0
MIN_DRIFT = 0.8
CALIBRATION_WINDOW = 12.0
PARTIAL_CORRECTION = 0.35
