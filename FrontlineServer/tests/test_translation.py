import pytest

from engine import translation

@pytest.mark.parametrize("candidate,original,expected", [
    ("429", "Hello there", True),          # HTTP status code, not a translation
    ("Server error", "Hello there", True),  # error-page boilerplate
    ("Too Many Requests", "Hi", True),
    ("Olá", "Hello", False),                 # a real, plausible translation
    ("429", "429", False),                   # original really was "429" (e.g. a year/number line)
])
def test_looks_like_bogus_translation(candidate, original, expected):
    assert translation.looks_like_bogus_translation(candidate, original) is expected


def test_looks_like_bogus_translation_flags_wildly_longer_output():
    # A translation many times longer than the original line is suspicious
    # (probably a scraped error page, not a lyric line).
    original = "Hi"
    candidate = "x" * 200
    assert translation.looks_like_bogus_translation(candidate, original) is True


def test_looks_like_bogus_translation_allows_normal_length_growth():
    # Some languages are legitimately longer than English for the same line.
    original = "I love you"
    candidate = "Eu te amo demais, meu bem"
    assert translation.looks_like_bogus_translation(candidate, original) is False


# romanize_line

def test_romanize_line_converts_and_capitalizes():
    result = translation.romanize_line("こんにちは")
    assert result  # anyascii should produce a non-empty ASCII-ish string
    assert result[0] == result[0].upper()


def test_romanize_line_falls_back_to_original_on_error(mocker):
    mocker.patch.object(translation, "anyascii", side_effect=RuntimeError("boom"))
    assert translation.romanize_line("some text") == "some text"


# -- translate_line_raced (network mocked at the source-function level) ----

def test_translate_line_raced_returns_first_valid_source(mocker):
    mocker.patch.object(
        translation.GoogleTranslator, "translate", return_value="Olá"
    )
    fake_mymemory = mocker.patch.object(translation, "MyMemoryTranslator")
    fake_mymemory.return_value.translate.side_effect = RuntimeError("mymemory down")
    mocker.patch.object(translation.ts, "translate_text", return_value="Olá (bing)")

    text, source = translation.translate_line_raced("Hello", "pt")
    assert text is not None
    assert source in ("google", "translators/bing")


def test_translate_line_raced_skips_bogus_answers(mocker):
    # Google returns HTTP-error-looking garbage; MyMemory returns something real.
    # MyMemoryTranslator validates its `target` in __init__ (real network
    # call at that point too), so the whole class is replaced here rather
    # than just patching .translate.
    mocker.patch.object(
        translation.GoogleTranslator, "translate", return_value="429"
    )
    fake_mymemory = mocker.patch.object(translation, "MyMemoryTranslator")
    fake_mymemory.return_value.translate.return_value = "Olá"
    mocker.patch.object(
        translation.ts, "translate_text", side_effect=RuntimeError("bing down"),
    )

    text, source = translation.translate_line_raced("Hello", "pt")
    assert text == "Olá"
    assert source == "mymemory"


def test_translate_line_raced_returns_none_when_all_sources_fail(mocker):
    mocker.patch.object(
        translation.GoogleTranslator, "translate", side_effect=RuntimeError("down")
    )
    fake_mymemory = mocker.patch.object(translation, "MyMemoryTranslator")
    fake_mymemory.return_value.translate.side_effect = RuntimeError("down")
    mocker.patch.object(
        translation.ts, "translate_text", side_effect=RuntimeError("down")
    )

    text, source = translation.translate_line_raced("Hello", "pt")
    assert text is None
    assert source is None


# apply_translation_in_background (async orchestration)

@pytest.mark.asyncio
async def test_apply_translation_in_background_success(mocker):
    manager = mocker.Mock()
    manager.apply_language.return_value = True

    await translation.apply_translation_in_background(manager, "pt")

    manager.apply_language.assert_called_once_with("pt")
    assert manager.last_translation_error is None
    assert manager.is_translating is False


@pytest.mark.asyncio
async def test_apply_translation_in_background_records_error_on_failure(mocker):
    manager = mocker.Mock()
    manager.apply_language.return_value = False

    await translation.apply_translation_in_background(manager, "pt")

    assert manager.last_translation_error == "pt"
    assert manager.is_translating is False
