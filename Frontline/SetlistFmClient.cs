using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FrontLineOverlay
{
    /// <summary>
    /// Client for the public setlist.fm API. It is only used as an initial guess
    /// for Festival Mode: the show's actual setlist is usually published later,
    /// so we list the most recently published ones for the user to choose from.
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

        public static async Task<List<SetlistFmResult>> FetchRecentSetlists(
            string apiKey, string artistName, string? country = null, string? venue = null, string? year = null, int limit = 10)
        {
            var results = new List<SetlistFmResult>();
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(artistName))
                return results;

            try
            {
                var q = new List<string>
                {
                    "artistName=" + Uri.EscapeDataString(artistName.Trim()),
                    "p=1",
                };
                string? countryCode = ResolveCountryCode(country);
                if (!string.IsNullOrEmpty(countryCode))
                    q.Add("countryCode=" + Uri.EscapeDataString(countryCode));
                if (!string.IsNullOrWhiteSpace(venue))
                    q.Add("venueName=" + Uri.EscapeDataString(venue.Trim()));
                string? yearValue = NormalizeYear(year);
                if (!string.IsNullOrEmpty(yearValue))
                    q.Add("year=" + yearValue);

                using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "search/setlists?" + string.Join("&", q));
                req.Headers.TryAddWithoutValidation("x-api-key", apiKey.Trim());

                using var resp = await Http.SendAsync(req);
                if ((int)resp.StatusCode == 401)
                    throw new InvalidOperationException("unauthorized");
                if ((int)resp.StatusCode == 404 || !resp.IsSuccessStatusCode)
                    return results;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (!doc.RootElement.TryGetProperty("setlist", out var setlists) || setlists.ValueKind != JsonValueKind.Array)
                    return results;

                foreach (var item in setlists.EnumerateArray().OrderByDescending(EventKey))
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

                    string venueName = ReadVenue(item, out string city);
                    string dateRaw = item.TryGetProperty("eventDate", out var d) ? d.GetString() ?? "" : "";
                    string dateLabel = FormatEventDate(dateRaw);
                    string title = string.IsNullOrEmpty(venueName) ? artist : artist + " — " + venueName;
                    string meta = dateLabel;
                    if (!string.IsNullOrEmpty(city)) meta += string.IsNullOrEmpty(meta) ? city : " · " + city;
                    meta += " · " + songs.Count + " ♪";

                    results.Add(new SetlistFmResult
                    {
                        Artist = artist,
                        Venue = venueName,
                        Location = city,
                        EventDate = dateLabel,
                        Title = title,
                        Meta = meta,
                        SuggestedName = string.IsNullOrEmpty(dateLabel) ? artist : artist + " (" + dateLabel + ")",
                        Songs = songs.Select(s => new FestivalSongEntry { Artist = artist, Song = s }).ToList(),
                    });
                    if (results.Count >= limit) break;
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "SetlistFmClient.FetchRecentSetlists");
            }
            return results;
        }

        private static string? NormalizeYear(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var digits = new string(raw.Trim().Where(char.IsDigit).ToArray());
            if (digits.Length >= 4)
                digits = digits[^4..];
            if (digits.Length != 4) return null;
            if (!int.TryParse(digits, out int y) || y < 1950 || y > 2100) return null;
            return digits;
        }

        private static string? ResolveCountryCode(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string t = Fold(raw.Trim());
            if (t.Length == 2 && t.All(char.IsLetter))
                return t.ToUpperInvariant();
            return CountryAliases.TryGetValue(t, out string? code) ? code : null;
        }

        private static string Fold(string value)
        {
            var n = value.Normalize(NormalizationForm.FormD);
            var chars = n.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark);
            return string.Concat(chars).ToLowerInvariant();
        }

        private static readonly Dictionary<string, string> CountryAliases = new(StringComparer.Ordinal)
        {
            ["brazil"] = "BR",
            ["brasil"] = "BR",
            ["brasile"] = "BR",
            ["portugal"] = "PT",
            ["united states"] = "US",
            ["usa"] = "US",
            ["estados unidos"] = "US",
            ["eeuu"] = "US",
            ["eua"] = "US",
            ["america"] = "US",
            ["united kingdom"] = "GB",
            ["uk"] = "GB",
            ["inglaterra"] = "GB",
            ["england"] = "GB",
            ["great britain"] = "GB",
            ["gra-bretanha"] = "GB",
            ["reino unido"] = "GB",
            ["spain"] = "ES",
            ["espana"] = "ES",
            ["espanha"] = "ES",
            ["mexico"] = "MX",
            ["méxico"] = "MX",
            ["argentina"] = "AR",
            ["chile"] = "CL",
            ["colombia"] = "CO",
            ["colômbia"] = "CO",
            ["peru"] = "PE",
            ["uruguay"] = "UY",
            ["uruguai"] = "UY",
            ["paraguay"] = "PY",
            ["paraguai"] = "PY",
            ["bolivia"] = "BO",
            ["bolívia"] = "BO",
            ["venezuela"] = "VE",
            ["canada"] = "CA",
            ["canadá"] = "CA",
            ["germany"] = "DE",
            ["alemanha"] = "DE",
            ["alemania"] = "DE",
            ["deutschland"] = "DE",
            ["france"] = "FR",
            ["franca"] = "FR",
            ["frança"] = "FR",
            ["italy"] = "IT",
            ["italia"] = "IT",
            ["itália"] = "IT",
            ["netherlands"] = "NL",
            ["holanda"] = "NL",
            ["paises baixos"] = "NL",
            ["belgium"] = "BE",
            ["belgica"] = "BE",
            ["bélgica"] = "BE",
            ["switzerland"] = "CH",
            ["suica"] = "CH",
            ["suíça"] = "CH",
            ["suiza"] = "CH",
            ["austria"] = "AT",
            ["áustria"] = "AT",
            ["ireland"] = "IE",
            ["irlanda"] = "IE",
            ["sweden"] = "SE",
            ["suecia"] = "SE",
            ["suécia"] = "SE",
            ["norway"] = "NO",
            ["noruega"] = "NO",
            ["denmark"] = "DK",
            ["dinamarca"] = "DK",
            ["finland"] = "FI",
            ["finlandia"] = "FI",
            ["finlândia"] = "FI",
            ["poland"] = "PL",
            ["polonia"] = "PL",
            ["polônia"] = "PL",
            ["czech republic"] = "CZ",
            ["chequia"] = "CZ",
            ["republica tcheca"] = "CZ",
            ["japan"] = "JP",
            ["japao"] = "JP",
            ["japão"] = "JP",
            ["japon"] = "JP",
            ["south korea"] = "KR",
            ["korea"] = "KR",
            ["coreia"] = "KR",
            ["coreia do sul"] = "KR",
            ["china"] = "CN",
            ["australia"] = "AU",
            ["austrália"] = "AU",
            ["new zealand"] = "NZ",
            ["nova zelandia"] = "NZ",
            ["nova zelândia"] = "NZ",
            ["india"] = "IN",
            ["índia"] = "IN",
            ["south africa"] = "ZA",
            ["africa do sul"] = "ZA",
            ["áfrica do sul"] = "ZA",
            ["russia"] = "RU",
            ["rússia"] = "RU",
            ["turkey"] = "TR",
            ["turquia"] = "TR",
            ["greece"] = "GR",
            ["grecia"] = "GR",
            ["grécia"] = "GR",
            ["hungary"] = "HU",
            ["hungria"] = "HU",
            ["romania"] = "RO",
            ["romenia"] = "RO",
            ["romênia"] = "RO",
            ["croatia"] = "HR",
            ["croacia"] = "HR",
            ["croácia"] = "HR",
            ["serbia"] = "RS",
            ["servia"] = "RS",
            ["sérvia"] = "RS",
            ["iceland"] = "IS",
            ["islandia"] = "IS",
            ["islândia"] = "IS",
            ["indonesia"] = "ID",
            ["indonésia"] = "ID",
            ["philippines"] = "PH",
            ["filipinas"] = "PH",
            ["thailand"] = "TH",
            ["tailandia"] = "TH",
            ["tailândia"] = "TH",
            ["singapore"] = "SG",
            ["singapura"] = "SG",
            ["malaysia"] = "MY",
            ["malasia"] = "MY",
            ["malásia"] = "MY",
            ["uae"] = "AE",
            ["emirates"] = "AE",
            ["emirados"] = "AE",
            ["israel"] = "IL",
            ["egypt"] = "EG",
            ["egito"] = "EG",
            ["egipto"] = "EG",
        };

        private static string ReadVenue(JsonElement item, out string city)
        {
            city = "";
            if (!item.TryGetProperty("venue", out var venue) || venue.ValueKind != JsonValueKind.Object)
                return "";
            string name = venue.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (venue.TryGetProperty("city", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                string cityName = c.TryGetProperty("name", out var cn) ? cn.GetString() ?? "" : "";
                string country = "";
                if (c.TryGetProperty("country", out var co) && co.ValueKind == JsonValueKind.Object
                    && co.TryGetProperty("code", out var code))
                    country = code.GetString() ?? "";
                city = string.IsNullOrEmpty(country) ? cityName : cityName + ", " + country;
            }
            return name;
        }

        private static string FormatEventDate(string raw)
        {
            // API: dd-MM-yyyy
            if (DateTime.TryParseExact(raw, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return dt.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
            return raw;
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

    internal sealed class SetlistFmResult
    {
        public string Artist { get; set; } = "";
        public string Venue { get; set; } = "";
        public string Location { get; set; } = "";
        public string EventDate { get; set; } = "";
        public string Title { get; set; } = "";
        public string Meta { get; set; } = "";
        public string SuggestedName { get; set; } = "";
        public List<FestivalSongEntry> Songs { get; set; } = [];
    }
}
