"""Run with: python -m unittest test_lyrics_validation.py"""
import importlib.util
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('lyrics', 'lyrics.py')
lyrics = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lyrics)

LRC = '[00:01.00]hello\n[00:02.00]world'

class TestValidation(unittest.TestCase):
    def test_wrong_song_is_rejected(self):
        self.assertIsNone(lyrics.pick_lrclib_search_hit([
            {'trackName': "Don't Give Up", 'artistName': 'Breakdown', 'syncedLyrics': LRC}
        ], 'sombr', 'Ribs'))

    def test_matching_song_accepted(self):
        hit={'trackName':'Ribs (Live)', 'artistName':'sombr', 'syncedLyrics':LRC}
        self.assertEqual(hit, lyrics.pick_lrclib_search_hit([hit], 'sombr','Ribs'))

    def test_other_artist_cover_rejected(self):
        self.assertIsNone(lyrics.pick_lrclib_search_hit([
            {'trackName':'Ribs', 'artistName':'Lorde', 'syncedLyrics':LRC}
        ], 'sombr','Ribs'))

    def test_partial_title_rejected(self):
        self.assertIsNone(lyrics.pick_lrclib_search_hit([
            {'trackName':'Ribs and Bones', 'artistName':'sombr', 'syncedLyrics':LRC}
        ], 'sombr','Ribs'))

    def test_accents_and_punctuation(self):
        self.assertTrue(lyrics.names_are_close('Beyoncé', 'BEYONCE'))

    def test_get_mismatch_rejected(self):
        class FakeResponse:
            status_code=200
            def json(self):
                return {'trackName': "Don't Give Up", 'artistName': 'Breakdown', 'syncedLyrics': LRC}
        class FakeSession:
            def mount(self, *args): pass
            def get(self, *args, **kwargs): return FakeResponse()
            def close(self): pass
        with patch.object(lyrics.requests, 'Session', return_value=FakeSession()):
            self.assertIsNone(lyrics.fetch_lyrics_lrclib('sombr','Ribs'))

if __name__ == '__main__': unittest.main()
