using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrontLineOverlay
{
    /// <summary>
    /// Playlists de Festival Mode salvas localmente. Propositalmente separado
    /// de SearchHistory.cs (aquele é o histórico de busca manual, este é a
    /// tela de "meus festivais salvos").
    /// Arquivo: %LOCALAPPDATA%\FrontLineLyrics\festival-playlists.json
    /// </summary>
    internal static class FestivalPlaylistStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        public static List<FestivalPlaylistEntry> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return [];
                var json = File.ReadAllText(FilePath);
                var rows = JsonSerializer.Deserialize<List<FestivalPlaylistEntry>>(json, JsonOpts);
                return rows?.OrderByDescending(r => r.UpdatedAtUtc).ToList() ?? [];
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "FestivalPlaylistStore.Load");
                return [];
            }
        }

        private static void SaveAll(IEnumerable<FestivalPlaylistEntry> items)
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(items.ToList(), JsonOpts));
            }
            catch (Exception ex) { CrashReporter.Log(ex, "FestivalPlaylistStore.SaveAll"); }
        }

        /// <summary>Insere ou atualiza por Id e persiste a lista inteira.
        /// Chamar ao sair do Festival Mode (ou a qualquer momento em "editar depois").</summary>
        public static void Upsert(FestivalPlaylistEntry entry)
        {
            var all = Load();
            int idx = all.FindIndex(e => e.Id == entry.Id);
            entry.UpdatedAtUtc = DateTime.UtcNow;
            if (idx >= 0) all[idx] = entry; else all.Add(entry);
            SaveAll(all);
        }

        public static void Delete(string id)
        {
            var all = Load();
            all.RemoveAll(e => e.Id == id);
            SaveAll(all);
        }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FrontLineLyrics", "festival-playlists.json");
    }

    public sealed class FestivalPlaylistEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("artist")] public string Artist { get; set; } = "";
        [JsonPropertyName("createdAt")] public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        [JsonPropertyName("updatedAt")] public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        [JsonPropertyName("songs")] public List<FestivalSongEntry> Songs { get; set; } = new();
    }

    public sealed class FestivalSongEntry
    {
        [JsonPropertyName("artist")] public string Artist { get; set; } = "";
        [JsonPropertyName("song")] public string Song { get; set; } = "";
    }
}
