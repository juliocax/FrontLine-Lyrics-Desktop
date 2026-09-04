import pytest

from engine import lyrics


# clean_lrclib_query

@pytest.mark.parametrize("artist,song,expected", [
    ("Drake feat. Rihanna", "Take Care (Official Audio)", ("Drake", "Take Care")),
    ("Artist A & Artist B", "Song Title - Live", ("Artist A", "Song Title")),
    ("Solo Artist", "Just A Song", ("Solo Artist", "Just A Song")),
    ("Artist, Featured Artist", "Track (Remastered)", ("Artist", "Track")),
])
def test_clean_lrclib_query_strips_noise(artist, song, expected):
    assert lyrics.clean_lrclib_query(artist, song) == expected


def test_clean_lrclib_query_falls_back_to_raw_when_everything_is_stripped():
    # If cleaning would leave nothing, keep the (whitespace-normalized) raw text
    # instead of returning an empty string.
    artist, song = lyrics.clean_lrclib_query("(only parens)", "(only parens)")
    assert artist and song


# names_are_close

@pytest.mark.parametrize("query,candidate,expected", [
    ("Drake", "drake", True),
    ("The Weeknd", "the weeknd", True),
    ("Beyonce", "Beyonce Knowles", True),  # substring-ish / shared long word
    ("Foo", "Bar", False),
    ("", "Anything", False),
])
def test_names_are_close(query, candidate, expected):
    assert lyrics.names_are_close(query, candidate) is expected


def test_names_are_close_does_not_normalize_accents():
    # Documents current behavior: "Beyonce" vs "Beyoncé" don't match because
    # accents aren't stripped before comparing. Worth knowing if lyric
    # matching seems to miss accented artist/track names.
    assert lyrics.names_are_close("Beyonce", "Beyoncé Knowles") is False


# parse_synced_lrc

def test_parse_synced_lrc_basic():
    lrc = "[00:12.50]Hello there\n[00:15.00]Second line\n"
    parsed = lyrics.parse_synced_lrc(lrc)
    assert parsed[0] == {"timestamp": 12.5, "text": "Hello there"}
    assert parsed[1] == {"timestamp": 15.0, "text": "Second line"}


def test_parse_synced_lrc_appends_end_sentinel():
    lrc = "[00:10.00]Only line\n"
    parsed = lyrics.parse_synced_lrc(lrc)
    assert parsed[-1]["text"] == "End"
    assert parsed[-1]["timestamp"] == pytest.approx(15.0)


def test_parse_synced_lrc_skips_blank_lines():
    lrc = "[00:10.00]\n[00:12.00]Real line\n"
    parsed = lyrics.parse_synced_lrc(lrc)
    assert all(item["text"] for item in parsed if item["text"] != "End")


def test_parse_synced_lrc_handles_minutes_over_59():
    # LRC timestamps can exceed 59 minutes for very long files.
    lrc = "[75:03.00]Late line\n"
    parsed = lyrics.parse_synced_lrc(lrc)
    assert parsed[0]["timestamp"] == 75 * 60 + 3.0


def test_parse_synced_lrc_empty_input_returns_empty_list():
    assert lyrics.parse_synced_lrc("") == []


# pick_lrclib_search_hit

def test_pick_lrclib_search_hit_prefers_artist_and_title_match():
    results = [
        {"artistName": "Someone Else", "trackName": "Take Care", "syncedLyrics": "x"},
        {"artistName": "Drake", "trackName": "Take Care", "syncedLyrics": "y"},
    ]
    hit = lyrics.pick_lrclib_search_hit(results, "Drake", "Take Care")
    assert hit["artistName"] == "Drake"


def test_pick_lrclib_search_hit_skips_entries_without_synced_lyrics():
    results = [
        {"artistName": "Drake", "trackName": "Take Care", "syncedLyrics": ""},
        {"artistName": "Drake", "trackName": "Take Care", "syncedLyrics": "has lyrics"},
    ]
    hit = lyrics.pick_lrclib_search_hit(results, "Drake", "Take Care")
    assert hit["syncedLyrics"] == "has lyrics"


def test_pick_lrclib_search_hit_returns_none_when_nothing_matches():
    assert lyrics.pick_lrclib_search_hit([], "Drake", "Take Care") is None
    assert lyrics.pick_lrclib_search_hit("not-a-list", "Drake", "Take Care") is None


# fetch_lyrics_lrclib (network mocked)

def test_fetch_lyrics_lrclib_uses_exact_get_endpoint(requests_mock):
    requests_mock.get(
        "https://lrclib.net/api/get",
        json={"syncedLyrics": "[00:10.00]primeira linha\n"},
    )
    result = lyrics.fetch_lyrics_lrclib("Some Artist", "Some Song")
    assert result[0]["text"] == "primeira linha"


def test_fetch_lyrics_lrclib_falls_back_to_search(requests_mock):
    requests_mock.get("https://lrclib.net/api/get", status_code=404)
    requests_mock.get(
        "https://lrclib.net/api/search",
        json=[{
            "artistName": "Some Artist",
            "trackName": "Some Song",
            "syncedLyrics": "[00:05.00]found via search\n",
        }],
    )
    result = lyrics.fetch_lyrics_lrclib("Some Artist", "Some Song")
    assert result[0]["text"] == "found via search"


def test_fetch_lyrics_lrclib_returns_none_when_nothing_found(requests_mock):
    requests_mock.get("https://lrclib.net/api/get", status_code=404)
    requests_mock.get("https://lrclib.net/api/search", json=[])
    assert lyrics.fetch_lyrics_lrclib("Nobody", "Nothing") is None


# fetch_lyrics_from_candidates

def test_fetch_lyrics_from_candidates_tries_pairs_in_order(requests_mock):
    # First candidate (Shazam name) misses, second (SMTC name) hits.
    requests_mock.get(
        "https://lrclib.net/api/get",
        [
            {"status_code": 404},
            {"json": {"syncedLyrics": "[00:01.00]second pair hit\n"}},
        ],
    )
    requests_mock.get("https://lrclib.net/api/search", json=[])
    result = lyrics.fetch_lyrics_from_candidates([
        ("Shazam Artist", "Shazam Title"),
        ("SMTC Artist", "SMTC Title"),
    ])
    assert result[0]["text"] == "second pair hit"


def test_fetch_lyrics_from_candidates_skips_empty_pairs():
    assert lyrics.fetch_lyrics_from_candidates([("", ""), ("", "")]) is None
