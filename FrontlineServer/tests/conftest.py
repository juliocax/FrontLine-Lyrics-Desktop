"""Test fixtures that never initialize audio hardware, D-Bus or remote clients."""

from unittest.mock import MagicMock, patch

import pytest


@pytest.fixture
def manager():
    from engine import music_manager

    with patch.object(music_manager, "ShazamRecognizer", return_value=MagicMock()), \
         patch.object(music_manager, "MediaSessionWatcher", return_value=MagicMock()):
        instance = music_manager.MusicManager(enable_audio=False)
    return instance
