using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FrontLine_Linux;

internal static class LinuxTelemetry
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static int _sending;
    private static string Clean(string? s, int max = 40)
    {
        string value = (s ?? "").Trim();
        return value.Length <= max ? value : value[..max];
    }

    internal sealed record Preferences(string UiLanguage, string LyricFont, string LyricColor,
        int Lines, double Opacity, double FontSize, string Alignment, bool Bold, bool Italic,
        bool? AutoMode, bool? HasTranslationModels, bool? HasJapaneseDictionary);

    internal static bool IsDue(bool optedIn, DateTime lastSentUtc) =>
        optedIn && DateTime.UtcNow - lastSentUtc.ToUniversalTime() >= TimeSpan.FromHours(24);

    internal static async Task SendIfDueAsync(bool optedIn, DateTime lastSentUtc,
        Preferences preferences, Action success)
    {
        string key = (Environment.GetEnvironmentVariable("FRONTLINE_APTABASE_APP_KEY")
            ?? LinuxTelemetryKey.Value).Trim();
        string? host = key.StartsWith("A-EU-", StringComparison.Ordinal) ? "https://eu.aptabase.com"
            : key.StartsWith("A-US-", StringComparison.Ordinal) ? "https://us.aptabase.com" : null;
        if (host is null || !IsDue(optedIn, lastSentUtc)) return;
        if (Interlocked.Exchange(ref _sending, 1) != 0) return;
        try
        {
            long epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string sessionId = (epoch * 100_000_000L + Random.Shared.NextInt64(0, 100_000_000L))
                .ToString(CultureInfo.InvariantCulture);
            var props = new Dictionary<string, object>
            {
                ["theme"] = "default", ["theme_mode"] = "manual",
                ["lyric_font"] = Clean(preferences.LyricFont), ["lyric_color"] = Clean(preferences.LyricColor),
                ["lyric_lines"] = preferences.Lines.ToString(CultureInfo.InvariantCulture),
                ["ui_language"] = Clean(preferences.UiLanguage, 8), ["platform"] = "linux",
                ["background_opacity"] = preferences.Opacity,
                ["lyric_font_size"] = preferences.FontSize,
                ["lyric_alignment"] = Clean(preferences.Alignment, 8),
                ["lyric_bold"] = preferences.Bold ? 1 : 0,
                ["lyric_italic"] = preferences.Italic ? 1 : 0
            };
            if (preferences.AutoMode is bool autoMode) props["auto_mode"] = autoMode ? 1 : 0;
            if (preferences.HasTranslationModels is bool models) props["has_translation_models"] = models ? 1 : 0;
            if (preferences.HasJapaneseDictionary is bool dictionary) props["has_japanese_dictionary"] = dictionary ? 1 : 0;
            var payload = new[]
            {
                new
                {
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                    sessionId,
                    eventName = "settings_snapshot",
                    systemProps = new
                    {
                        locale = CultureInfo.CurrentUICulture.Name,
                        osName = "Linux",
#if DEBUG
                        isDebug = true,
#else
                        isDebug = false,
#endif
                        appVersion = MainWindow.LinuxAppVersion, sdkVersion = "frontline-http@1"
                    },
                    props
                }
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, host + "/api/v0/events")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("App-Key", key);
            using var response = await Client.SendAsync(req);
            if (response.IsSuccessStatusCode) success();
            else System.Diagnostics.Debug.WriteLine("Telemetry status: " + (int)response.StatusCode);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Telemetry: " + ex.Message); }
        finally { Volatile.Write(ref _sending, 0); }
    }
}
