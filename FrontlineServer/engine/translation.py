"""
Lyric translation.

MyMemory is the only enabled translation provider on Windows and Linux.
Google and Bing are disabled following repeated errors and rate limiting.

MusicManager.generate_translation() still handles each song's line order,
per-language cache, and the fallback to original text for failed lines.
"""

import asyncio
import logging
import threading
import unicodedata
from functools import lru_cache
from concurrent.futures import ThreadPoolExecutor
from typing import Optional, Tuple

from anyascii import anyascii
from engine.japanese_pack import create_converter, installed_dictionary
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


_romanization_local = threading.local()


def _is_kana(char: str) -> bool:
    return unicodedata.name(char, "").startswith(
        ("HIRAGANA ", "KATAKANA ", "HALFWIDTH KATAKANA ")
    )


def has_japanese_kana(text: str) -> bool:
    return any(char.isalpha() and char not in "ーｰ" and _is_kana(char) for char in text)


def _is_han(char: str) -> bool:
    return unicodedata.name(char, "").startswith(
        ("CJK UNIFIED IDEOGRAPH-", "CJK COMPATIBILITY IDEOGRAPH-")
    ) or char == "〇"


def _is_japanese_character(char: str) -> bool:
    return (
        _is_kana(char)
        or char in "々〻ーｰ\u3099\u309a"
        or _is_han(char)
    )


def release_japanese_converter():
    _romanization_local.cutlet = None
    _romanization_local.dictionary = None


def _romanize_japanese(text: str) -> str:
    path = installed_dictionary()
    if path != getattr(_romanization_local, "dictionary", None):
        _romanization_local.cutlet = None
        if path:
            try:
                _romanization_local.cutlet = create_converter(path)
            except (ImportError, OSError, RuntimeError, ValueError) as error:
                logging.warning("Cutlet indisponível: %s", error)
        _romanization_local.dictionary = path
    converter = getattr(_romanization_local, "cutlet", None)
    if converter is None:
        return anyascii(text).lower() or text
    try:
        return converter.romaji(unicodedata.normalize("NFKC", text)).strip() or text
    except Exception:
        logging.warning("Cutlet falhou; usando romanização básica.", exc_info=True)
        return anyascii(text).lower() or text


@lru_cache(maxsize=1)
def _mandarin_converter():
    try:
        from opencc import OpenCC
        from pypinyin import lazy_pinyin, load_phrases_dict

        converter = OpenCC("t2s")
        readings = {
            "随": ("sui2", "zhe5"),
            "带": ("dai4", "zhe5"),
            "靠": ("kao4", "zhe5"),
            "吹": ("chui1", "zhe5"),
            "唱": ("chang4", "zhe5"),
            "睡": ("shui4", "zhao2"),
            "望": ("wang4", "zhe5"),
        }
        load_phrases_dict({
            verb + ending: [[first], [second]]
            for verb, (first, second) in readings.items()
            for ending in ("著", "着")
        })
    except (ImportError, OSError, RuntimeError, ValueError) as error:
        logging.warning("Romanização em mandarim indisponível: %s", error)
        return None

    def convert(text: str) -> str:
        return " ".join(lazy_pinyin(converter.convert(text)))

    return convert


def _is_hangul(char: str) -> bool:
    return unicodedata.name(char, "").startswith("HANGUL ")


@lru_cache(maxsize=1)
def _korean_converter():
    try:
        from koroman import romanize

        return romanize
    except ImportError as error:
        logging.warning("Koroman indisponível: %s", error)
        return None


def romanize_line(text: str, *, japanese: Optional[bool] = None) -> str:
    """Preserve Latin text and use kana to distinguish Japanese from Mandarin."""
    if japanese is None:
        japanese = has_japanese_kana(text)

    try:
        is_convertible = _is_japanese_character if japanese else _is_han
        parts = []
        index = 0
        while index < len(text):
            char = text[index]
            if _is_hangul(char):
                end = index + 1
                while end < len(text) and _is_hangul(text[end]):
                    end += 1
                original = text[index:end]
                korean = _korean_converter()
                if korean is not None:
                    parts.append(korean(unicodedata.normalize("NFKC", original)) or original)
                else:
                    parts.append(anyascii(original) or original)
                index = end
                continue
            if is_convertible(char):
                end = index + 1
                while end < len(text) and is_convertible(text[end]):
                    end += 1
                original = text[index:end]
                if japanese:
                    converted = _romanize_japanese(original)
                else:
                    mandarin = _mandarin_converter()
                    converted = mandarin(original) if mandarin else original
                if converted != original:
                    if index and text[index - 1].isalnum():
                        parts.append(" ")
                    parts.append(converted)
                    if end < len(text) and text[end].isalnum():
                        parts.append(" ")
                else:
                    parts.append(original)
                index = end
                continue

            if char.isalpha() and not unicodedata.name(char, "").startswith("LATIN "):
                char = anyascii(char) or char
            parts.append(char)
            index += 1
        return "".join(parts)
    except Exception:
        logging.warning("Falha ao romanizar linha; mantendo original.", exc_info=True)
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
