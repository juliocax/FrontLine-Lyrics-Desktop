"""
Lyric translation.

MyMemory is the only enabled translation provider on Windows and Linux.
Google and Bing are disabled following repeated errors and rate limiting.

MusicManager.generate_translation() still handles each song's line order,
per-language cache, and the fallback to original text for failed lines.
"""

import asyncio
import logging
import re
import threading
import time
import unicodedata
from collections import Counter
from functools import lru_cache
from concurrent.futures import ThreadPoolExecutor
from typing import Optional, Tuple

from anyascii import anyascii
from engine.japanese_pack import create_converter, installed_dictionary
from deep_translator import MyMemoryTranslator
from deep_translator.constants import MY_MEMORY_LANGUAGES_TO_CODES
from langdetect import DetectorFactory, detect, detect_langs
from langdetect.lang_detect_exception import LangDetectException

LINE_DISPATCH_EXECUTOR = ThreadPoolExecutor(max_workers=2, thread_name_prefix="translate-mymemory")
_mymemory_lock = threading.Lock()
_mymemory_next_request = 0.0
_mymemory_blocked_until = 0.0

MYMEMORY_TARGET_LANGUAGES = {
    "en": "en-GB",
    "pt": "pt-BR",
    "pb": "pt-BR",
    "pt-pt": "pt-PT",
    "zh": "zh-CN",
    "zt": "zh-TW",
    "es": "es-ES",
    "fr": "fr-FR",
    "de": "de-DE",
    "it": "it-IT",
    "nl": "nl-NL",
    "id": "id-ID",
    "ja": "ja-JP",
    "ko": "ko-KR",
    "zh-cn": "zh-CN",
    "zh-tw": "zh-TW",
    "th": "th-TH",
    "ar": "ar-SA",
    "he": "he-IL",
    "el": "el-GR",
    "hi": "hi-IN",
    "bn": "bn-IN",
    "fa": "fa-IR",
    "ru": "ru-RU",
    "uk": "uk-UA",
    "pl": "pl-PL",
    "tr": "tr-TR",
    "vi": "vi-VN",
    "sv": "sv-SE",
    "fi": "fi-FI",
    "ro": "ro-RO",
    "da": "da-DK",
    "cs": "cs-CZ",
    "hu": "hu-HU",
}
DetectorFactory.seed = 0


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

    if any(char.isalpha() for char in original_stripped):
        if not any(char.isalnum() for char in stripped):
            return True
        if re.match(r"^(?:tradução|translation)(?:\s+(?:e|and)\s+(?:sincronia|sync))?\s*:", stripped, re.I):
            if not re.match(r"^(?:tradução|translation)\b", original_stripped, re.I):
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


_THAI_GROUPS = {
    "เพื่อนเล่น": ("เพื่อน", "เล่น"),
    "เล่นเพื่อน": ("เล่น", "เพื่อน"),
}


def _is_thai_letter(char: str) -> bool:
    return "\u0e00" <= char <= "\u0e7f" and unicodedata.category(char)[0] in "LM"


@lru_cache(maxsize=1)
def _thai_converter():
    try:
        from pythainlp.tokenize import word_tokenize
        from pythainlp.transliterate import romanize
    except (ImportError, OSError) as error:
        logging.warning("PyThaiNLP indisponível: %s", error)
        return None

    def convert(text: str) -> str:
        tokens = word_tokenize(text, engine="newmm", keep_whitespace=False)
        words = (word for token in tokens for word in _THAI_GROUPS.get(token, (token,)))
        return " ".join(romanize(word, engine="royin") or word for word in words)

    return convert


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
            if _is_thai_letter(char):
                end = index + 1
                while end < len(text) and _is_thai_letter(text[end]):
                    end += 1
                original = text[index:end]
                thai = _thai_converter()
                converted = thai(original) if thai else anyascii(original)
                if index and text[index - 1].isalnum():
                    parts.append(" ")
                parts.append(converted or original)
                if end < len(text) and text[end].isalnum():
                    parts.append(" ")
                index = end
                continue
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


_LATIN_WRITING_LANGUAGES = frozenset("""
az ca cs da de en eo es et eu fi fr ga gl hu id it lt lv ms nb nl pb pl pt pt-pt
ro sk sl sq sv sw tl tr vi
""".split())


def uses_latin_writing(code: Optional[str]) -> bool:
    return code in _LATIN_WRITING_LANGUAGES


def has_non_latin_letters(text: str) -> bool:
    return any(char.isalpha() and not unicodedata.name(char, "").startswith("LATIN ")
               for char in text)


_SCRIPT_DEFAULTS = {
    "kana": "ja", "hangul": "ko", "thai": "th", "hebrew": "he",
    "greek": "el",
}
_LANGUAGE_SCRIPTS = {
    "ja": {"kana", "han"}, "ko": {"hangul", "han"},
    "zh": {"han"}, "zt": {"han"}, "zh-cn": {"han"}, "zh-tw": {"han"},
    "th": {"thai"}, "he": {"hebrew"}, "el": {"greek"}, "bn": {"bengali"},
    "ar": {"arabic"}, "fa": {"arabic"}, "ur": {"arabic"},
    "hi": {"devanagari"}, "ru": {"cyrillic"}, "uk": {"cyrillic"},
    "bg": {"cyrillic"}, "ky": {"cyrillic"},
}


def _letter_script(char):
    if _is_kana(char):
        return "kana"
    if _is_han(char):
        return "han"
    if _is_hangul(char):
        return "hangul"
    return unicodedata.name(char, "").split(" ", 1)[0].lower()


def classify_lyric_sources(lines, selected=None):
    """Group verses by their dominant script, applying hints only where compatible."""
    by_script = {}
    for line in dict.fromkeys(text.strip() for text in lines if text and text.strip()):
        counts = Counter(_letter_script(ch) for ch in line if ch.isalpha())
        if not counts:
            continue
        # Kanji beside kana belongs to the Japanese phrase.
        if counts["kana"]:
            counts["kana"] += counts.pop("han", 0)
        script = counts.most_common(1)[0][0]
        by_script.setdefault(script, []).append(line)

    def compatible(code, script):
        if script == "latin":
            return uses_latin_writing(code)
        return script in _LANGUAGE_SCRIPTS.get(code, ())

    groups = {}
    for script, verses in by_script.items():
        if compatible(selected, script):
            source = selected
        elif script == "han" and "kana" in by_script:
            source = "ja"
        elif script in _SCRIPT_DEFAULTS:
            source = _SCRIPT_DEFAULTS[script]
        else:
            detected = _confident_script_language(verses, script)
            source = detected if compatible(detected, script) else None
        groups.setdefault(source, []).extend(verses)
    return groups


def _confident_script_language(verses, script):
    text = " ".join(verses)[:12000]
    if sum(char.isalpha() for char in text) < 24:
        return None
    if script == "latin":
        words = set(re.findall(r"[^\W\d_]+", text.casefold()))
        if len(words) < 7:
            return None
    if script not in {"latin", "han", "bengali", "arabic", "devanagari", "cyrillic"}:
        return None
    try:
        best, *others = detect_langs(text)
    except (LangDetectException, ValueError):
        return None
    if best.prob < 0.95 or (others and best.prob - others[0].prob < 0.80):
        return None
    return best.lang


def detect_lyrics_code(lines) -> Optional[str]:
    """Detect the language of the entire lyric."""

    text = " ".join(dict.fromkeys(line.strip() for line in lines if line and line.strip()))
    if not text:
        return None
    try:
        return detect(text[:12000])
    except LangDetectException:
        return None

def mymemory_locale(code: Optional[str]) -> Optional[str]:
    if not code:
        return None
    preferred = MYMEMORY_TARGET_LANGUAGES.get(code)
    if preferred:
        return preferred
    return next((locale for locale in MY_MEMORY_LANGUAGES_TO_CODES.values()
                 if locale.split("-")[0].lower() == code), None)


def detect_lyrics_language(lines) -> Optional[str]:
    return mymemory_locale(detect_lyrics_code(lines))


def _translate_with_mymemory(text, source, target):
    global _mymemory_next_request, _mymemory_blocked_until
    with _mymemory_lock:
        now = time.monotonic()
        if now < _mymemory_blocked_until:
            return None
        delay = _mymemory_next_request - now
        if delay > 0:
            time.sleep(delay)
        _mymemory_next_request = time.monotonic() + 0.35
        try:
            return MyMemoryTranslator(source=source, target=target).translate(text)
        except Exception as error:
            message = str(error).lower()
            if (type(error).__name__ == "TooManyRequests" or "too many requests" in message
                    or "quota" in message or "429" in message):
                _mymemory_blocked_until = time.monotonic() + 60
                logging.warning("MyMemory limit reached; pausing requests for 60 seconds.")
                return None
            raise


def translate_line_raced(text: str, target_language: str, source_language: Optional[str] = None) -> Tuple[Optional[str], Optional[str]]:
    """Translate a line using MyMemory only.

    Keep the original function signature because MusicManager imports it in
    both Windows and Linux. Google and Bing are intentionally not called.
    """
    if not text or not text.strip():
        return text, "mymemory"

    my_target = MYMEMORY_TARGET_LANGUAGES.get(target_language.lower(), target_language)
    source_language = source_language or detect_lyrics_language((text,))
    if source_language is None:
        return None, None
    if source_language == my_target:
        return text, "original"
    try:
        result = _translate_with_mymemory(text, source_language, my_target)
    except Exception as error:
        logging.warning("MyMemory falhou: %s", _short_provider_error(error))
        return None, None

    if not result or not str(result).strip():
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
    manager._background_translation_count = getattr(manager, "_background_translation_count", 0) + 1
    manager.is_translating = True
    manager.last_translation_error = None
    ok = False
    try:
        ok = await asyncio.to_thread(manager.apply_language, target_lang)
    finally:
        manager.last_translation_error = None if ok else target_lang
        manager._background_translation_count -= 1
        manager.is_translating = manager._background_translation_count > 0
