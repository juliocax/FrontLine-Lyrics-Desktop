import math

import pytest

from engine import smtc_policy as sp


# smtc_trusts_song_clock

@pytest.mark.parametrize("app_id", [
    "Spotify.exe",
    "spotify",
    "AppleMusic.exe",
    "AmazonMusic.exe",
])
def test_trusts_known_streaming_apps(app_id):
    assert sp.smtc_trusts_song_clock(app_id) is True


@pytest.mark.parametrize("app_id", [
    "chrome.exe",
    "vlc.exe",
    "",
    None,
    "SomeRandomApp.exe",
])
def test_does_not_trust_unknown_or_video_apps(app_id):
    assert sp.smtc_trusts_song_clock(app_id) is False


# smtc_app_is_video_surface

def test_video_surface_detects_youtube_in_chrome():
    assert sp.smtc_app_is_video_surface("chrome.exe") is True


def test_video_surface_is_false_for_streaming_apps():
    # A streaming app should never also be flagged as a video surface.
    assert sp.smtc_app_is_video_surface("Spotify.exe") is False


def test_video_surface_is_false_for_unknown_app():
    assert sp.smtc_app_is_video_surface("SomeUnknownApp.exe") is False


# sane_media_position

@pytest.mark.parametrize("value,expected", [
    (45.5, 45.5),
    ("30", 30.0),
    (0, 0.0),
])
def test_sane_media_position_accepts_valid_values(value, expected):
    assert sp.sane_media_position(value) == expected


@pytest.mark.parametrize("value", [
    float("nan"),
    float("inf"),
    -1.0,
    sp.MAX_MEDIA_POSITION_S + 1,
    "not-a-number",
    None,
])
def test_sane_media_position_rejects_bad_values(value):
    assert sp.sane_media_position(value, fallback="fallback") == "fallback"


def test_sane_media_position_default_fallback_is_none():
    assert sp.sane_media_position(float("nan")) is None


# smtc_timeline_is_video_shaped

def test_video_shaped_by_long_duration():
    assert sp.smtc_timeline_is_video_shaped(duration=20 * 60, position=10) is True


def test_video_shaped_by_far_position():
    assert sp.smtc_timeline_is_video_shaped(duration=None, position=13 * 60) is True


def test_not_video_shaped_for_normal_track():
    assert sp.smtc_timeline_is_video_shaped(duration=210, position=45) is False


def test_video_shaped_when_position_far_past_lyrics_end():
    # Lyrics end at 180s; player says we're at 300s -> looks like a
    # different (longer) piece of content playing under the same session.
    assert sp.smtc_timeline_is_video_shaped(duration=None, position=300, lyrics_end=180) is True


# smtc_timeline_is_track_shaped

def test_track_shaped_for_typical_song():
    assert sp.smtc_timeline_is_track_shaped(duration=210, position=45) is True


def test_not_track_shaped_when_too_short():
    assert sp.smtc_timeline_is_track_shaped(duration=5, position=1) is False


def test_not_track_shaped_when_position_beyond_duration():
    assert sp.smtc_timeline_is_track_shaped(duration=200, position=400) is False


def test_track_shaped_with_unknown_duration_falls_back_to_position_check():
    assert sp.smtc_timeline_is_track_shaped(duration=None, position=60) is True
    assert sp.smtc_timeline_is_track_shaped(duration=None, position=13 * 60) is False


# should_trust_smtc_clock (the actual policy used by MusicManager)

def test_trusts_spotify_with_normal_track_timeline():
    assert sp.should_trust_smtc_clock("Spotify.exe", duration=200, position=30) is True


def test_does_not_trust_chrome_with_long_video():
    assert sp.should_trust_smtc_clock("chrome.exe", duration=1200, position=900) is False


def test_does_not_trust_video_surface_even_with_short_timeline():
    # A browser showing a short clip is still not a song clock.
    assert sp.should_trust_smtc_clock("chrome.exe", duration=180, position=30) is False


def test_video_shape_overrides_a_trusted_streaming_app():
    # Even a streaming app could theoretically report a video-shaped
    # timeline (e.g. a podcast); the video check wins.
    assert sp.should_trust_smtc_clock("Spotify.exe", duration=1200, position=900) is False


def test_unknown_app_with_track_shaped_timeline_is_trusted():
    # Unknown streaming app with a plausible song-length timeline gets the
    # benefit of the doubt, so new apps don't need an allowlist entry.
    assert sp.should_trust_smtc_clock("SomeNewMusicApp.exe", duration=180, position=60) is True


def test_unknown_app_with_video_shaped_timeline_is_not_trusted():
    assert sp.should_trust_smtc_clock("SomeNewApp.exe", duration=1500, position=1000) is False
