import os
from engine import cover_art

from pathlib import Path

def test_fetch_cover_art_returns_first_result_cover(requests_mock):
    requests_mock.get(
        "https://api.deezer.com/search",
        json={"data": [{"album": {"cover_xl": "https://example.com/cover.jpg"}}]},
    )
    assert cover_art.fetch_cover_art("Some Artist", "Some Song") == "https://example.com/cover.jpg"

def test_fetch_cover_art_returns_empty_string_when_no_results(requests_mock):
    requests_mock.get("https://api.deezer.com/search", json={"data": []})
    assert cover_art.fetch_cover_art("Nobody", "Nothing") == ""

def test_fetch_cover_art_returns_empty_string_on_request_error(requests_mock):
    requests_mock.get("https://api.deezer.com/search", status_code=500)
    assert cover_art.fetch_cover_art("Some Artist", "Some Song") == ""


def test_smtc_thumbnail_to_file_uri_writes_and_returns_uri(tmp_path, monkeypatch):
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    uri = cover_art.smtc_thumbnail_to_file_uri(b"fake-jpeg-bytes", ("song", "artist"))
    assert uri.startswith("file://")
    written_path = Path.from_uri(uri)

    assert written_path.exists()

def test_smtc_thumbnail_to_file_uri_reuses_cached_file(tmp_path, monkeypatch):
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    key = ("song", "artist")
    first = cover_art.smtc_thumbnail_to_file_uri(b"bytes-v1", key)
    second = cover_art.smtc_thumbnail_to_file_uri(b"bytes-v2", key)
    # Same track key -> same cached path, second write doesn't overwrite.
    assert first == second

def test_smtc_thumbnail_to_file_uri_empty_bytes_returns_empty_string():
    assert cover_art.smtc_thumbnail_to_file_uri(b"", ("song", "artist")) == ""
