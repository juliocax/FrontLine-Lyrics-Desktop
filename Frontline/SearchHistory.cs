using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrontLineOverlay
{
    /// <summary>
    /// Histórico da busca manual. Lista cresce; não vai em LocalSettings
    /// (teto ~8 KB). Arquivo ao lado das outras preferências:
    /// %LOCALAPPDATA%\FrontLineLyrics\search-history.json
    /// </summary>
    internal static class SearchHistoryStore
    {
        public const int MaxEntries = 80;
        private const int MaxFieldChars = 200;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };

        public static List<SearchHistoryEntry> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return [];
                var json = File.ReadAllText(FilePath);
                var rows = JsonSerializer.Deserialize<List<SearchHistoryEntry>>(json, JsonOpts);
                if (rows == null) return [];
                foreach (var row in rows)
                    row.Normalize();
                return rows
                    .Where(r => r.IsValid)
                    .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(r => r.SearchedAtUtc).First())
                    .OrderByDescending(r => r.SearchedAtUtc)
                    .Take(MaxEntries)
                    .ToList();
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "SearchHistory.Load");
                return [];
            }
        }

        public static void Save(IEnumerable<SearchHistoryEntry> items)
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                var rows = items
                    .Where(r => r.IsValid)
                    .Take(MaxEntries)
                    .Select(r => new SearchHistoryEntry
                    {
                        Artist = r.Artist,
                        Song = r.Song,
                        SearchedAtUtc = r.SearchedAtUtc,
                    })
                    .ToList();
                File.WriteAllText(FilePath, JsonSerializer.Serialize(rows, JsonOpts));
            }
            catch (Exception ex) { CrashReporter.Log(ex, "SearchHistory.Save"); }
        }

        public static string Clamp(string? value)
        {
            string t = (value ?? "").Trim();
            if (t.Length <= MaxFieldChars) return t;
            return t[..MaxFieldChars];
        }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FrontLineLyrics", "search-history.json");
    }

    public sealed class SearchHistoryEntry : INotifyPropertyChanged
    {
        [JsonPropertyName("artist")] public string Artist { get; set; } = "";
        [JsonPropertyName("song")] public string Song { get; set; } = "";
        [JsonPropertyName("at")] public DateTime SearchedAtUtc { get; set; }

        [JsonIgnore] public string Key => $"{Artist.Trim()}|{Song.Trim()}".ToLowerInvariant();
        [JsonIgnore]
        public bool IsValid =>
            !string.IsNullOrWhiteSpace(Artist) && !string.IsNullOrWhiteSpace(Song)
            && SearchedAtUtc.Year >= 2000;

        private string _dateLabel = "";
        [JsonIgnore]
        public string DateLabel
        {
            get => _dateLabel;
            set { if (_dateLabel == value) return; _dateLabel = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Normalize()
        {
            Artist = SearchHistoryStore.Clamp(Artist);
            Song = SearchHistoryStore.Clamp(Song);
            if (SearchedAtUtc.Kind == DateTimeKind.Unspecified)
                SearchedAtUtc = DateTime.SpecifyKind(SearchedAtUtc, DateTimeKind.Utc);
            else if (SearchedAtUtc.Kind == DateTimeKind.Local)
                SearchedAtUtc = SearchedAtUtc.ToUniversalTime();
        }

        public void RefreshDateLabel(string lang)
        {
            var local = SearchedAtUtc.ToLocalTime();
            DateLabel = lang switch
            {
                "pt" => local.ToString("dd/MM/yyyy HH:mm"),
                "es" => local.ToString("dd/MM/yyyy HH:mm"),
                _ => local.ToString("yyyy-MM-dd HH:mm"),
            };
        }
    }
}
