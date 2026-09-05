"""
Lyric translation.

Translating a whole song one line at a time, one source at a time, is slow
and fragile (any single source rate-limiting or breaking stalls everything).
Instead we "race" three free translation sources per line and keep whichever
answers first, and dispatch every line of the song in parallel too.

This module owns the thread pools and the racing/validation logic.
MusicManager.generate_translation() (in music_manager.py) still owns the
song-level orchestration (iterating lines, caching by target language)
because that part needs the manager's state.
"""

import asyncio
import logging
import time
from concurrent.futures import FIRST_COMPLETED, ThreadPoolExecutor, wait as futures_wait
from typing import Optional, Tuple

from anyascii import anyascii
from deep_translator import GoogleTranslator, MyMemoryTranslator

import os
os.environ.setdefault("translators_default_region", "EN")
import translators as ts

# One executor per line (fans a whole song out in parallel) and one for the
# 3 racing sources within a single line.
LINE_DISPATCH_EXECUTOR = ThreadPoolExecutor(max_workers=8, thread_name_prefix="translate-line")
TRANSLATION_EXECUTOR = ThreadPoolExecutor(max_workers=24, thread_name_prefix="translate-src")
LINE_TRANSLATE_TIMEOUT = 8.0

_HTTP_ERROR_CODE_STRINGS = {"400", "401", "403", "404", "408", "409", "429", "500", "502", "503", "504"}
_ERROR_PAGE_MARKERS = (
    "server error", "that's all we know", "error 500", "error 404", "error 429",
    "bad gateway", "service unavailable", "too many requests",
)


def looks_like_bogus_translation(candidate: str, original_text: str) -> bool:
    """True if `candidate` looks like error-page/HTTP-status garbage rather than a real translation."""
    stripped = candidate.strip()
    original_stripped = original_text.strip()

    if stripped in _HTTP_ERROR_CODE_STRINGS and original_stripped not in _HTTP_ERROR_CODE_STRINGS:
        return True

    low = stripped.lower()
    if any(marker in low for marker in _ERROR_PAGE_MARKERS):
        return True
    if len(stripped) > 120 and len(stripped) > 4 * max(len(original_stripped), 1):
        return True

    return False


def romanize_line(text: str) -> str:
    try:
        return anyascii(text).capitalize()
    except Exception:
        return text


def translate_line_raced(text: str, target_language: str) -> Tuple[Optional[str], Optional[str]]:
    """Translate one line by racing 3 free sources in parallel (not one after
    another) and keeping the FIRST valid answer. Losing sources are not
    cancelled (the thread is already running), just ignored -- so racing in
    parallel never makes a line slower than the fastest source, and if one
    source goes down or hangs (like Google's scraping did), the others cover
    for it with no extra delay. Returns (translated_text, winning_source_name)
    or (None, None) if all three fail within the timeout.
    """

    def _google():
        return GoogleTranslator(source='auto', target=target_language).translate(text)

    def _mymemory():
        return MyMemoryTranslator(source='auto', target=target_language).translate(text)

    def _translators_pkg():
        return ts.translate_text(
            text, translator='bing', from_language='auto', to_language=target_language,
            timeout=6.0, if_print_warning=False,
        )

    futures = {
        TRANSLATION_EXECUTOR.submit(_google): "google",
        TRANSLATION_EXECUTOR.submit(_mymemory): "mymemory",
        TRANSLATION_EXECUTOR.submit(_translators_pkg): "translators/bing",
    }

    deadline = time.monotonic() + LINE_TRANSLATE_TIMEOUT
    pending = set(futures.keys())

    while pending:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            break
        done, pending = futures_wait(pending, timeout=remaining, return_when=FIRST_COMPLETED)
        for fut in done:
            source_name = futures[fut]
            try:
                result = fut.result()
            except Exception as e:
                logging.warning(f"Fonte de tradução '{source_name}' falhou: {e}")
                continue
            if result and str(result).strip():
                candidate = str(result).strip()
                if looks_like_bogus_translation(candidate, text):
                    logging.warning(
                        f"Fonte '{source_name}' devolveu algo que parece erro/boilerplate "
                        f"em vez de tradução ('{candidate[:60]}...'); ignorando."
                    )
                    continue
                return candidate, source_name
            logging.warning(f"Fonte de tradução '{source_name}' retornou vazio.")

    return None, None


async def apply_translation_in_background(manager, target_lang: str):
    """Translate in the background without blocking the event loop or the
    WebSocket listener. Used both when the user picks a language button and
    when fresh lyrics are loaded (Auto or manual) and need the "sticky"
    preferred language re-applied."""
    manager.is_translating = True
    ok = await asyncio.to_thread(manager.apply_language, target_lang)
    manager.last_translation_error = None if ok else target_lang
    manager.is_translating = False
