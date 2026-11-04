"""
Lyric translation.

MyMemory is the only enabled translation provider on Windows and Linux.
Google and Bing are disabled following repeated errors and rate limiting.

MusicManager.generate_translation() still handles each song's line order,
per-language cache, and the fallback to original text for failed lines.
"""

import asyncio
import logging
from concurrent.futures import ThreadPoolExecutor
from typing import Optional, Tuple

from anyascii import anyascii
from deep_translator import MyMemoryTranslator

LINE_DISPATCH_EXECUTOR = ThreadPoolExecutor(max_workers=2, thread_name_prefix="translate-mymemory")

MYMEMORY_TARGET_LANGUAGES = {"pt": "pt-BR", "es": "es-ES", "fr": "fr-FR", "en": "en-GB"}

MYMEMORY_SOURCE_LANGUAGE = "en-GB"


def _short_provider_error(error: Exception) -> str:
    """Avoid logging MyMemory's entire language catalog on every failed line."""
    message = str(error).strip().splitlines()
    return (message[0][:240] if message else type(error).__name__)

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
    """Translate a line using MyMemory only.

    Keep the original function signature because MusicManager imports it in
    both Windows and Linux. Google and Bing are intentionally not called.
    """
    if not text or not text.strip():
        return text, "mymemory"

    my_target = MYMEMORY_TARGET_LANGUAGES.get(target_language.lower(), target_language)
    try:
        result = MyMemoryTranslator(
            source=MYMEMORY_SOURCE_LANGUAGE, target=my_target
        ).translate(text)
    except Exception as error:
        logging.warning("MyMemory falhou: %s", _short_provider_error(error))
        return None, None

    if not result or not str(result).strip():
        logging.warning("MyMemory retornou tradução vazia.")
        return None, None

    candidate = str(result).strip()
    if looks_like_bogus_translation(candidate, text):
        logging.warning("MyMemory retornou resposta inválida; mantendo original se necessário.")
        return None, None
    return candidate, "mymemory"


async def apply_translation_in_background(manager, target_lang: str):
    """Translate in the background without blocking the event loop or the
    WebSocket listener. Used both when the user picks a language button and
    when fresh lyrics are loaded (Auto or manual) and need the "sticky"
    preferred language re-applied."""
    manager.is_translating = True
    ok = await asyncio.to_thread(manager.apply_language, target_lang)
    manager.last_translation_error = None if ok else target_lang
    manager.is_translating = False
