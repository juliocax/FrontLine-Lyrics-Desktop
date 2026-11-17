"""Unit tests use fake recognition and media-session clients."""

import importlib
import sys
from types import ModuleType
from unittest.mock import MagicMock, patch

import pytest


@pytest.fixture(scope="module")
def music_manager_module():
    recognition = ModuleType("engine.recognition")
    recognition.ShazamRecognizer = MagicMock()
    media_provider = ModuleType("engine.media_provider")
    media_provider.MediaSessionWatcher = MagicMock()

    with patch.dict(sys.modules, {
        "engine.recognition": recognition,
        "engine.media_provider": media_provider,
    }):
        yield importlib.import_module("engine.music_manager")


@pytest.fixture
def auto_hold_class(music_manager_module):
    return music_manager_module.AutoHold


@pytest.fixture
def manager(music_manager_module, monkeypatch):
    monkeypatch.setattr(music_manager_module.offline_translation, "installed", lambda: {})
    with patch.object(music_manager_module, "ShazamRecognizer", return_value=MagicMock()), \
         patch.object(music_manager_module, "MediaSessionWatcher", return_value=MagicMock()):
        instance = music_manager_module.MusicManager(enable_audio=False)
    return instance
