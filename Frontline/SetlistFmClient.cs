using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace FrontLineOverlay
{
    /// <summary>
    /// Cliente da API pública do setlist.fm. Só é usado como palpite inicial
    /// do Modo Festival: o setlist do show em si costuma sair depois, então
    /// pegamos o mais recente já publicado.
    /// </summary>
    internal static class SetlistFmClient
    {
        private const string BaseUrl = "https://api.setlist.fm/rest/1.0/";
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            c.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
            c.DefaultRequestHeaders.UserAgent.ParseAdd("FrontlineLyrics/1.2");
            return c;
        }

        public static async Task<List<FestivalSongEntry>?> FetchLatestSetlist(string apiKey, string artistName)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(artistName))
                return null;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    BaseUrl + "search/setlists?artistName=" + Uri.EscapeDataString(artistName.Trim()) + "&p=1");
                req.Headers.TryAddWithoutValidation("x-api-key", apiKey.Trim());

                using var resp = await Http.SendAsync(req);
                if ((int)resp.StatusCode == 404 || (int)resp.StatusCode == 401 || !resp.IsSuccessStatusCode)
                    return null;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (!doc.RootElement.TryGetProperty("setlist", out var setlists) || setlists.ValueKind != JsonValueKind.Array)
                    return null;

                var items = setlists.EnumerateArray()
                    .OrderByDescending(EventKey)
                    .ToList();

                foreach (var item in items)
                {
                    var songs = ExtractSongNames(item);
                    if (songs.Count == 0) continue;
                    string artist = artistName.Trim();
                    if (item.TryGetProperty("artist", out var art) && art.ValueKind == JsonValueKind.Object
                        && art.TryGetProperty("name", out var nameEl))
                    {
                        string? n = nameEl.GetString();
                        if (!string.IsNullOrWhiteSpace(n)) artist = n;
                    }
                    return songs.Select(s => new FestivalSongEntry { Artist = artist, Song = s }).ToList();
                }
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "SetlistFmClient.FetchLatestSetlist");
            }
            return null;
        }

        private static string EventKey(JsonElement item)
        {
            try
            {
                string date = item.TryGetProperty("eventDate", out var d) ? d.GetString() ?? "" : "";
                var parts = date.Split('-');
                if (parts.Length == 3) return parts[2] + parts[1] + parts[0];
            }
            catch { }
            return "00000000";
        }

        private static List<string> ExtractSongNames(JsonElement setlistItem)
        {
            var names = new List<string>();
            if (!setlistItem.TryGetProperty("sets", out var sets) || sets.ValueKind != JsonValueKind.Object)
                return names;
            if (!sets.TryGetProperty("set", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
                return names;

            foreach (var block in blocks.EnumerateArray())
            {
                if (!block.TryGetProperty("song", out var songs) || songs.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var song in songs.EnumerateArray())
                {
                    if (song.TryGetProperty("tape", out var tape) && tape.ValueKind == JsonValueKind.True)
                        continue;
                    string name = song.TryGetProperty("name", out var n) ? n.GetString()?.Trim() ?? "" : "";
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
            }
            return names;
        }
    }
}
