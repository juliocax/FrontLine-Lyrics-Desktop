"""
Long-running asyncio background tasks that drive MusicManager.

Each function here takes the shared `manager` (a music_manager.MusicManager)
as its first argument and loops for as long as `manager.server_running` is
True. They're spawned once from ws_server.main_background().
"""

import asyncio
import logging
import time
from typing import List, Tuple

import engine.cover_art as cover_art
import engine.lyrics as lyrics
import engine.tuning as tuning
from engine.recognition import live_fingerprint_drift
from engine.smtc_policy import sane_media_position


async def background_verification_worker(manager):
    """Continuously captures audio and tries to identify what's playing."""
    loop = asyncio.get_event_loop()
    while manager.server_running:
        if (not manager.is_listening or manager.search_completed
                or manager.manual_mode or manager._media_busy):
            await asyncio.sleep(1)
            continue

        current_session = manager.session_id
        record_start_time = time.time()
        snippet_s = (
            tuning.SHAZAM_LIVE_SECONDS if manager._listen_use_shazam else tuning.SHAZAM_QUICK_SECONDS
        )

        try:
            audio_bytes = await loop.run_in_executor(
                None, manager.audio.record_to_memory, snippet_s
            )
        except Exception as e:
            logging.warning(f"Audio capture failed, resetting device: {e}")
            if manager._listen_use_shazam and manager._fallback_listen_to_smtc("falha na captura"):
                continue
            manager.audio.device_info = manager.audio.configure_loopback()
            await asyncio.sleep(2)
            continue

        if (manager.session_id != current_session or not manager.is_listening
                or manager.manual_mode or manager.search_completed or manager._media_busy):
            continue

        rms = await loop.run_in_executor(None, manager.audio.rms, audio_bytes)
        # Live audio: quiet verse / speech / crowd noise. Don't drop the
        # snippet -- the previous PyQt client sent it to Shazam anyway.
        if rms < tuning.SILENCE_RMS_THRESHOLD and not manager._listen_use_shazam:
            manager.pending_candidate = None
            manager.pending_candidate_count = 0
            if manager.auto_mode:
                manager.retry_delay = min(tuning.AUTO_MAX_RETRY_DELAY, manager.retry_delay * tuning.AUTO_BACKOFF_MULTIPLIER)
                await asyncio.sleep(manager.retry_delay)
            else:
                await asyncio.sleep(2)
            continue

        new_song, new_artist, shazam_offset, new_cover = await manager.recognizer.recognize(audio_bytes)

        if (manager.session_id != current_session or not manager.is_listening
                or manager.manual_mode or manager.search_completed or manager._media_busy):
            continue

        if not new_song:
            manager.pending_candidate = None
            manager.pending_candidate_count = 0
            if manager._listen_use_shazam and manager._fallback_listen_to_smtc("sem match"):
                continue
            if manager.auto_mode:
                manager.retry_delay = min(tuning.AUTO_MAX_RETRY_DELAY, manager.retry_delay * tuning.AUTO_BACKOFF_MULTIPLIER)
                await asyncio.sleep(manager.retry_delay)
            else:
                await asyncio.sleep(2)
            continue

        candidate = (new_song, new_artist)

        if manager.auto_mode and not manager._listen_use_shazam:
            if manager.pending_candidate == candidate:
                manager.pending_candidate_count += 1
            else:
                manager.pending_candidate = candidate
                manager.pending_candidate_count = 1

            if manager.pending_candidate_count < 2:
                manager.retry_delay = tuning.AUTO_MIN_RETRY_DELAY
                await asyncio.sleep(manager.retry_delay)
                continue

        if manager._in_previous_track_cooldown(new_song, new_artist):
            await asyncio.sleep(2)
            continue

        manager.current_song, manager.current_artist = new_song, new_artist
        manager.current_cover = new_cover
        manager.pending_candidate = None
        manager.pending_candidate_count = 0
        manager.retry_delay = tuning.AUTO_MIN_RETRY_DELAY

        # LRCLIB: on Spotify the SMTC title is the track's. On a live
        # YouTube stream it's the VIDEO's ("Artist Live at ...") -- if that
        # goes first, the search misses or picks another song. Live: try the
        # Shazam name first instead.
        queries: List[Tuple[str, str]] = [(new_artist, new_song)]
        info = manager.watcher.ultima_info
        if info is not None and info.tocando and info.titulo:
            smtc_pair = (info.artista, info.titulo)
            if manager._session_trusts_song_clock(info):
                queries.insert(0, smtc_pair)
            else:
                queries.append(smtc_pair)

        found_lyrics = await loop.run_in_executor(
            None, lyrics.fetch_lyrics_from_candidates, queries
        )

        if manager.session_id == current_session and not manager.search_completed:
            if found_lyrics:
                offset = sane_media_position(shazam_offset, fallback=0.0) or 0.0
                chave = manager._track_key(new_song, new_artist)
                fallback = record_start_time - offset
                use_shazam_clock = (
                    manager._listen_use_shazam
                    and not manager._session_trusts_song_clock()
                )
                if use_shazam_clock:
                    reference_time = fallback
                    logging.info(
                        "Ao vivo: âncora Shazam offset=%.1fs (servo de minutagem desligado)",
                        offset,
                    )
                else:
                    reference_time = manager._anchor_reference(chave, fallback)
                manager._set_track(
                    new_song,
                    new_artist,
                    reference_time=reference_time,
                    lyrics=found_lyrics,
                    cover=new_cover,
                    source="shazam",
                    lock_shazam_clock=use_shazam_clock,
                )
            else:
                manager.search_completed = True
                manager.not_found_since = time.time()
                manager._listen_use_shazam = False
                logging.info(
                    f"LRCLib sem letra após Shazam: {new_song} - {new_artist}"
                )
        await asyncio.sleep(2)


async def live_refingerprint_worker(manager):
    """Live mode: re-Shazam every ~45s and only re-anchor if the offset really drifted.

    Doesn't use SMTC. No match = don't touch anything. Two big drifts in a
    row = re-anchor. A different song = track change within the set.
    """
    loop = asyncio.get_event_loop()
    while manager.server_running:
        await asyncio.sleep(2)
        if (
            not manager._clock_from_shazam
            or not manager.is_listening
            or not manager.search_completed
            or not manager.synced_lyrics
            or manager.manual_mode
            or manager.clock_paused
            or manager._live_fp_busy
            or manager._listen_use_shazam
        ):
            continue
        if time.time() < manager._next_live_fingerprint:
            continue

        manager._live_fp_busy = True
        manager._next_live_fingerprint = time.time() + tuning.LIVE_FINGERPRINT_PERIOD
        session = manager.session_id
        record_start = time.time()
        try:
            audio_bytes = await loop.run_in_executor(
                None, manager.audio.record_to_memory, tuning.LIVE_FINGERPRINT_SECONDS
            )
            if (
                session != manager.session_id
                or not manager._clock_from_shazam
                or not manager.search_completed
            ):
                continue
            new_song, new_artist, raw_offset, new_cover = await manager.recognizer.recognize(
                audio_bytes
            )
            if session != manager.session_id or not manager._clock_from_shazam:
                continue
            if not new_song:
                logging.info("Ao vivo: re-fingerprint sem match; relógio intacto")
                continue

            offset = sane_media_position(raw_offset, fallback=0.0) or 0.0
            same_title = lyrics.names_are_close(manager.current_song or "", new_song or "")
            same_artist = (not new_artist or not manager.current_artist
                           or lyrics.names_are_close(manager.current_artist, new_artist))
            if not (same_title and same_artist):
                logging.info(
                    "Ao vivo: re-fingerprint outra faixa %s - %s", new_song, new_artist
                )
                found_lyrics = await loop.run_in_executor(
                    None, lyrics.fetch_lyrics_lrclib, new_artist, new_song
                )
                if session != manager.session_id:
                    continue
                if found_lyrics:
                    manager._set_track(
                        new_song,
                        new_artist,
                        reference_time=record_start - offset,
                        lyrics=found_lyrics,
                        cover=new_cover or manager.current_cover,
                        source="shazam",
                        lock_shazam_clock=True,
                    )
                continue

            now = time.time()
            drift = live_fingerprint_drift(manager._elapsed_now(), now, record_start, offset)
            if abs(drift) <= tuning.LIVE_DRIFT_TOLERANCE:
                manager._live_fp_drift_hits = 0
                logging.info("Ao vivo: re-fingerprint ok (deriva %.2fs)", drift)
                continue

            manager._live_fp_drift_hits += 1
            logging.info(
                "Ao vivo: deriva fingerprint %.2fs (hit %s/%s)",
                drift,
                manager._live_fp_drift_hits,
                tuning.LIVE_DRIFT_CONFIRM,
            )
            if manager._live_fp_drift_hits < tuning.LIVE_DRIFT_CONFIRM:
                continue
            manager._live_fp_drift_hits = 0
            new_ref = record_start - offset
            with manager._lock:
                manager.system_reference_time = new_ref
            logging.info("Ao vivo: reancorou no offset Shazam %.1fs", offset)
        except Exception:
            logging.exception("Ao vivo: falha no re-fingerprint")
        finally:
            manager._live_fp_busy = False


async def run_manual_search(manager, artist: str, song: str, current_session: float):
    """Runs a manual text search for lyrics and album cover art."""
    loop = asyncio.get_event_loop()

    found_lyrics = await loop.run_in_executor(None, lyrics.fetch_lyrics_lrclib, artist, song)
    found_cover = await loop.run_in_executor(None, cover_art.fetch_cover_art, artist, song)

    if manager.session_id == current_session:
        manager.search_completed = True

        if found_cover:
            manager.current_cover = found_cover

        if found_lyrics:
            chave = manager._track_key(song, artist)
            manager._set_track(
                song,
                artist,
                reference_time=manager._anchor_reference(chave, time.time()),
                lyrics=found_lyrics,
                cover=found_cover or manager.current_cover,
                source="manual",
            )
