import time

import pytest

from engine.music_manager import AutoHold


# AutoHold
# Pure logic, no MusicManager instance needed: after RESET, Auto must not
# immediately re-lock onto the same (title, artist) key.

def test_auto_hold_starts_inactive():
    hold = AutoHold(cooldown_s=2.0)
    assert hold.ativo is False
    assert hold.deve_ignorar(("song", "artist")) is False


def test_auto_hold_blocks_the_held_key():
    hold = AutoHold(cooldown_s=2.0)
    hold.hold(("song", "artist"))
    assert hold.ativo is True
    assert hold.deve_ignorar(("song", "artist")) is True


def test_auto_hold_releases_once_key_changes():
    hold = AutoHold(cooldown_s=2.0)
    hold.hold(("song", "artist"))
    # A different key should release the hold rather than being blocked.
    assert hold.deve_ignorar(("other song", "other artist")) is False
    assert hold.ativo is False


def test_auto_hold_release_clears_state():
    hold = AutoHold(cooldown_s=2.0)
    hold.hold(("song", "artist"))
    hold.release()
    assert hold.ativo is False
    assert hold.deve_ignorar(("song", "artist")) is False


def test_auto_hold_cooldown_blocks_even_without_a_key(monkeypatch):
    # hold(None, ...) covers the "no track was playing at RESET time" case:
    # a short cooldown still applies so Auto doesn't fire instantly.
    hold = AutoHold(cooldown_s=2.0)
    now = time.monotonic()
    hold.hold(None, agora=now)
    assert hold.deve_ignorar(("anything", "anything"), agora=now + 0.5) is True
    assert hold.deve_ignorar(("anything", "anything"), agora=now + 3.0) is False


# MusicManager state helpers

def test_starts_idle(manager):
    assert manager.get_current_state()["status"] == "IDLE"


def test_track_key_normalizes_case_and_whitespace(manager):
    assert manager._track_key("  Song Title  ", "ARTIST") == ("song title", "artist")
    assert manager._track_key(None, None) == ("", "")


def test_not_in_previous_track_cooldown_when_no_previous_track(manager):
    assert manager._in_previous_track_cooldown("Song", "Artist") is False


def test_in_previous_track_cooldown_right_after_transition(manager):
    manager.previous_track = ("Song", "Artist")
    manager.cooldown_until = time.time() + 30
    assert manager._in_previous_track_cooldown("Song", "Artist") is True
    assert manager._in_previous_track_cooldown("Other Song", "Other Artist") is False


def test_previous_track_cooldown_expires(manager):
    manager.previous_track = ("Song", "Artist")
    manager.cooldown_until = time.time() - 1  # already in the past
    assert manager._in_previous_track_cooldown("Song", "Artist") is False


def test_reset_state_clears_playback_fields(manager):
    manager.current_song = "Something"
    manager.current_artist = "Someone"
    manager.synced_lyrics = [{"timestamp": 1.0, "text": "line"}]

    manager.reset_state()

    assert manager.current_song is None
    assert manager.current_artist is None
    assert manager.synced_lyrics == []
    assert manager.is_listening is False


def test_apply_language_original_uses_original_lyrics(manager):
    manager.original_lyrics = [{"timestamp": 0.0, "text": "hello"}]
    manager.synced_lyrics = [{"timestamp": 0.0, "text": "translated"}]
    manager.current_language = "pt"

    assert manager.apply_language("original") is True
    assert manager.current_language == "original"
    assert manager.synced_lyrics == manager.original_lyrics


def test_apply_language_keeps_old_language_on_translation_failure(manager, mocker):
    manager.original_lyrics = [{"timestamp": 0.0, "text": "hello"}]
    manager.current_language = "original"
    mocker.patch.object(manager, "generate_translation", return_value=False)

    assert manager.apply_language("xx") is False
    assert manager.current_language == "original"
