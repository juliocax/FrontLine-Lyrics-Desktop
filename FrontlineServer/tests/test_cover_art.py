from pathlib import Path

from engine import cover_art


def test_fetch_cover_art_prefers_deezer(mocker):
    deezer = mocker.patch.object(cover_art, "_search_deezer", return_value="https://example.com/cover.jpg")
    itunes = mocker.patch.object(cover_art, "_search_itunes")
    mocker.patch.object(cover_art, "_cached_uri", return_value="")
    mocker.patch.object(cover_art, "_download_to_cache", return_value="file:///tmp/cover.jpg")
    assert cover_art.fetch_cover_art("Some Artist", "Some Song") == "file:///tmp/cover.jpg"
    deezer.assert_called_once_with("Some Artist", "Some Song")
    itunes.assert_not_called()


def test_fetch_cover_art_falls_back_to_itunes(mocker):
    mocker.patch.object(cover_art, "_cached_uri", return_value="")
    mocker.patch.object(cover_art, "_search_deezer", return_value="")
    itunes = mocker.patch.object(cover_art, "_search_itunes", return_value="https://example.com/itunes.jpg")
    mocker.patch.object(cover_art, "_download_to_cache", return_value="")
    assert cover_art.fetch_cover_art("Some Artist", "Some Song") == "https://example.com/itunes.jpg"
    itunes.assert_called_once()


def test_fetch_cover_art_returns_empty_string_when_no_results(mocker):
    mocker.patch.object(cover_art, "_cached_uri", return_value="")
    mocker.patch.object(cover_art, "_search_deezer", return_value="")
    mocker.patch.object(cover_art, "_search_itunes", return_value="")
    assert cover_art.fetch_cover_art("Nobody", "Nothing") == ""


def test_fetch_cover_art_reuses_cached_uri(mocker):
    mocker.patch.object(cover_art, "_cached_uri", return_value="file:///cached.jpg")
    lookup = mocker.patch.object(cover_art, "_search_deezer")
    assert cover_art.fetch_cover_art("Some Artist", "Some Song") == "file:///cached.jpg"
    lookup.assert_not_called()


def test_smtc_thumbnail_to_file_uri_writes_and_returns_uri(tmp_path, monkeypatch):
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    uri = cover_art.smtc_thumbnail_to_file_uri(b"fake-jpeg-bytes", ("song", "artist"))
    assert uri.startswith("file://")
    assert Path.from_uri(uri).exists()


def test_smtc_thumbnail_to_file_uri_reuses_cached_file(tmp_path, monkeypatch):
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    key = ("song", "artist")
    first = cover_art.smtc_thumbnail_to_file_uri(b"bytes-v1", key)
    second = cover_art.smtc_thumbnail_to_file_uri(b"bytes-v2", key)
    assert first == second


def test_smtc_thumbnail_to_file_uri_empty_bytes_returns_empty_string():
    assert cover_art.smtc_thumbnail_to_file_uri(b"", ("song", "artist")) == ""
