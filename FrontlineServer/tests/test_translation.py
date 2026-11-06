import pytest

from engine import translation


@pytest.mark.parametrize("candidate,original,expected", [
    ("429", "Hello there", True),
    ("Server error", "Hello there", True),
    ("Too Many Requests", "Hi", True),
    ("Olá", "Hello", False),
    ("429", "429", False),
])
def test_looks_like_bogus_translation(candidate, original, expected):
    assert translation.looks_like_bogus_translation(candidate, original) is expected


def test_looks_like_bogus_translation_flags_wildly_longer_output():
    assert translation.looks_like_bogus_translation("x" * 200, "Hi") is True


def test_looks_like_bogus_translation_allows_normal_length_growth():
    assert translation.looks_like_bogus_translation("Eu te amo demais, meu bem", "I love you") is False


def test_romanize_line_converts_and_capitalizes():
    result = translation.romanize_line("こんにちは")
    assert result and result[0] == result[0].upper()


def test_romanize_line_falls_back_to_original_on_error(mocker):
    mocker.patch.object(translation, "anyascii", side_effect=RuntimeError("boom"))
    assert translation.romanize_line("some text") == "some text"


def test_translate_line_uses_only_mymemory(mocker):
    provider = mocker.patch.object(translation, "MyMemoryTranslator")
    provider.return_value.translate.return_value = "Olá"
    assert translation.translate_line_raced("Hello", "pt") == ("Olá", "mymemory")
    provider.assert_called_once_with(source="en-GB", target="pt-BR")
    provider.return_value.translate.assert_called_once_with("Hello")


@pytest.mark.parametrize("language,target", [("pt", "pt-BR"), ("es", "es-ES"),
                                             ("fr", "fr-FR"), ("en", "en-GB"),
                                             ("de", "de")])
def test_translate_line_language_mapping(mocker, language, target):
    provider = mocker.patch.object(translation, "MyMemoryTranslator")
    provider.return_value.translate.return_value = "translated"
    assert translation.translate_line_raced("Hello", language) == ("translated", "mymemory")
    provider.assert_called_once_with(source="en-GB", target=target)


def test_translate_line_skips_bogus_answers(mocker):
    provider = mocker.patch.object(translation, "MyMemoryTranslator")
    provider.return_value.translate.return_value = "429"
    assert translation.translate_line_raced("Hello", "pt") == (None, None)


def test_translate_line_returns_none_on_provider_error(mocker):
    provider = mocker.patch.object(translation, "MyMemoryTranslator")
    provider.return_value.translate.side_effect = RuntimeError("down")
    assert translation.translate_line_raced("Hello", "pt") == (None, None)


def test_translate_line_does_not_call_provider_for_blank_text(mocker):
    provider = mocker.patch.object(translation, "MyMemoryTranslator")
    assert translation.translate_line_raced("  ", "pt") == ("  ", "mymemory")
    provider.assert_not_called()


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
