"""
Shared fixtures.

MusicManager.__init__ touches real hardware (PyAudio) and network
(shazamio's Shazam(), which just builds a client, no request yet), so
building one per test would be slow and flaky on a CI box with no audio
device. The `manager` fixture below builds one instance and resets its
state before every test instead of constructing a fresh one each time.
"""

import pytest

from engine.music_manager import MusicManager


@pytest.fixture(scope="session")
def _shared_manager():
    # Built once per test session: this is what's slow (PyAudio device
    # enumeration, Shazam() client setup), not the state itself.
    return MusicManager()


@pytest.fixture
def manager(_shared_manager):
    # Fresh state per test, without paying PyAudio/Shazam setup cost again.
    _shared_manager.reset_state()
    _shared_manager.auto_mode = False
    _shared_manager.manual_mode = False
    _shared_manager.preferred_language = None
    _shared_manager.is_listening = False
    yield _shared_manager
