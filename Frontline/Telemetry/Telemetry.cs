using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Linq;
using Frontline.Themes.Seasonal;

namespace FrontLineOverlay
{
    internal sealed record TelemetrySnapshot(string Theme, string LyricFont, string UiLanguage, string? LockShortcut, string LyricColor = "default");

    internal static class Telemetry
    {

        private static string AppKey => TelemetryKey.Value;

        private const string EventName = "settings_snapshot";
        private const string SdkVersion = "frontline-http@1";
        private static readonly TimeSpan MinInterval = TimeSpan.FromHours(24);

        public const string KeyEnabled = "TelemetryEnabled";
        public const string KeyWelcomeShown = "WelcomeShown";
        private const string KeyLastSent = "TelemetryLastSentUtc";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
        private static int _sending;

        public static bool Enabled
        {
            get => AppSettings.GetBool(KeyEnabled, true);
            set => AppSettings.SetBool(KeyEnabled, value);
        }

        public static bool WelcomeShown
        {
            get => AppSettings.GetBool(KeyWelcomeShown, false);
            set => AppSettings.SetBool(KeyWelcomeShown, value);
        }

        private static bool IsConfigured => !string.IsNullOrWhiteSpace(AppKey);

        private static string? Host =>
            AppKey.StartsWith("A-EU-", StringComparison.Ordinal) ? "https://eu.aptabase.com" :
            AppKey.StartsWith("A-US-", StringComparison.Ordinal) ? "https://us.aptabase.com" : null;

        private static string AppVersion =>
            typeof(Telemetry).Assembly.GetName().Version?.ToString(3) ?? "1.5.0";

        private static bool IsDue()
        {
            var raw = AppSettings.GetString(KeyLastSent, "");
            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last))
                return true;
            var age = DateTime.UtcNow - last.ToUniversalTime();
            return age >= MinInterval || age < TimeSpan.FromHours(-1);
        }

        internal static string SettingText(string? value, int max = 40)
        {
            value = (value ?? "").Trim();
            return value.Length <= max ? value : value.Substring(0, max);
        }

        internal static string Flag(bool value) => value ? "yes" : "no";

        internal static double SettingNumber(double value, double fallback = 0) =>
            Math.Round(double.IsFinite(value) ? value : fallback, 2);

        internal static string SeasonalThemeId(SeasonalTheme theme) => theme switch
        {
            SeasonalTheme.Halloween => "halloween",
            SeasonalTheme.Christmas => "christmas",
            SeasonalTheme.NewYear => "newyear",
            _ => "default"
        };

        internal static string FormatLockShortcut(uint key, uint modifiers)
        {
            if (key == 0 || key > 255) return "none";
            var parts = new List<string>();
            if ((modifiers & 2) != 0) parts.Add("Ctrl");
            if ((modifiers & 1) != 0) parts.Add("Alt");
            if ((modifiers & 4) != 0) parts.Add("Shift");
            if ((modifiers & 8) != 0) parts.Add("Win");
            parts.Add(System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)key).ToString());
            return SettingText(string.Join(" + ", parts));
        }

    
        private static Dictionary<string, object> PersistedSettings(TelemetrySnapshot snap)
        {
            string selected = AppSettings.GetString("AppTheme", AppSettings.GetBool("SeasonalEnabled", true) ? "auto" : "default");
            string theme = selected == "auto" ? SeasonalThemeId(SeasonalThemeProvider.GetCurrent()) : SettingText(selected);
            double lines = AppSettings.GetDouble("LyricLineCount", 3);
            string alignment = AppSettings.GetString("LyricAlignment", "center").ToLowerInvariant();
            if (alignment != "left" && alignment != "right") alignment = "center";
            string weight = AppSettings.GetString("LyricWeight", LyricWeights.DefaultId);
            string style = AppSettings.GetString("LyricStyle", LyricStyles.DefaultId);
            double savedKey = AppSettings.GetDouble("LockHotkeyVk", 0);
            double savedMods = AppSettings.GetDouble("LockHotkeyMods", 0);
            uint key = double.IsFinite(savedKey) && savedKey >= 0 && savedKey <= 255 ? (uint)savedKey : 0;
            uint mods = double.IsFinite(savedMods) && savedMods >= 0 && savedMods <= uint.MaxValue ? (uint)savedMods : 0;
            string shortcut = key != 0 ? FormatLockShortcut(key, mods) :
                string.IsNullOrWhiteSpace(snap.LockShortcut) ? "none" : SettingText(snap.LockShortcut);
            return new Dictionary<string, object>
            {
                ["settings_schema"] = 2,
                ["theme"] = string.IsNullOrWhiteSpace(theme) ? SettingText(snap.Theme) : theme,
                ["theme_mode"] = selected == "auto" ? "auto" : "manual",
                ["theme_selected"] = SettingText(selected),
                ["lyric_font"] = SettingText(AppSettings.GetString("LyricFont", snap.LyricFont)),
                ["lyric_color"] = SettingText(AppSettings.GetString("LyricColor", snap.LyricColor)),
                ["lyric_weight"] = SettingText(weight),
                ["lyric_style"] = SettingText(style),
                ["lyric_bold"] = Flag(LyricWeights.Resolve(weight).ToOpenTypeWeight() >= FontWeights.Bold.ToOpenTypeWeight()),
                ["lyric_italic"] = Flag(LyricStyles.Resolve(style) == FontStyles.Italic),
                ["lyric_font_size"] = SettingNumber(AppSettings.GetDouble("FontSize", 26), 26),
                ["lyric_lines"] = lines == 0 ? "all" : lines == 1 || lines == 5 ? ((int)lines).ToString(CultureInfo.InvariantCulture) : "3",
                ["lyric_alignment"] = alignment,
                ["background_opacity"] = Math.Clamp(SettingNumber(AppSettings.GetDouble("BgOpacity", 1.0), 1.0), 0, 1),
                ["show_theme_behind_lyrics"] = Flag(AppSettings.GetBool("ShowThemeBehindLyrics", true)),
                ["translation_language"] = SettingText(AppSettings.GetString("PreferredTranslation", "original")),
                ["show_original_lyrics"] = Flag(AppSettings.GetBool("KeepOriginalWithTranslation", false)),
                ["auto_mode"] = Flag(AppSettings.GetBool("AutoMode", false)),
                ["manual_sync_mode"] = "no",
                ["overlay_locked"] = Flag(AppSettings.GetBool("OverlayLocked", false)),
                ["ui_language"] = SettingText(AppSettings.GetString("AppLanguage", snap.UiLanguage), 8),
                ["lock_shortcut_set"] = shortcut == "none" ? "no" : "yes",
                ["lock_shortcut"] = shortcut,
                ["telemetry_enabled"] = Flag(Enabled),
                ["window_width"] = SettingNumber(AppSettings.GetDouble("WindowWidth", 700), 700),
                ["window_height"] = SettingNumber(AppSettings.GetDouble("WindowHeight", 300), 300),
                ["window_left"] = SettingNumber(AppSettings.GetDouble("WindowLeft", 0)),
                ["window_top"] = SettingNumber(AppSettings.GetDouble("WindowTop", 0)),
                ["window_state"] = "unknown",
            };
        }

        private static Dictionary<string, object> CurrentSettings(TelemetrySnapshot snap)
        {
            var properties = PersistedSettings(snap);
            try
            {
                var app = Application.Current;
                if (app == null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished) return properties;
                var live = app.Dispatcher.Invoke(() =>
                {
                    var window = app.Windows.OfType<MainWindow>().FirstOrDefault();
                    return window?.CaptureSettingsTelemetry();
                });
                if (live != null)
                    foreach (var item in live)
                        if (properties.ContainsKey(item.Key)) properties[item.Key] = item.Value;
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine("[Telemetry] settings fallback: " + ex.GetType().Name);
#endif
            }
            return properties;
        }

        public static async Task SendSnapshotIfDueAsync(TelemetrySnapshot snap)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[Telemetry] check: configured={IsConfigured}, host={Host}, enabled={Enabled}, welcome={WelcomeShown}, due={IsDue()}");
#endif
            if (!IsConfigured || Host == null) return;
            if (!Enabled || !WelcomeShown) return;
            if (!IsDue()) return;
            if (Interlocked.Exchange(ref _sending, 1) == 1) return;

            try
            {
                if (!Enabled || !WelcomeShown || !IsDue()) return;
                long epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string sessionId = (epoch * 100_000_000L + Random.Shared.NextInt64(0, 100_000_000L))
                    .ToString(CultureInfo.InvariantCulture);

                var currentSettings = CurrentSettings(snap);

                var evt = new
                {
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                    sessionId,
                    eventName = EventName,
                    systemProps = new
                    {
                        locale = CultureInfo.CurrentUICulture.Name,
                        osName = "Windows",
#if DEBUG
                        isDebug = true,
#else
                        isDebug = false,
#endif
                        appVersion = AppVersion,
                        sdkVersion = SdkVersion
                    },
                    props = currentSettings
                };

                var json = JsonSerializer.Serialize(new[] { evt });
                using var req = new HttpRequestMessage(HttpMethod.Post, Host + "/api/v0/events")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("App-Key", AppKey);

                if (!Enabled || !WelcomeShown) return;
                using var res = await Http.SendAsync(req).ConfigureAwait(false);
#if DEBUG
                System.Diagnostics.Debug.WriteLine("[Telemetry] " + (int)res.StatusCode + " " + json);
#endif
                if (res.IsSuccessStatusCode)
                    AppSettings.SetString(KeyLastSent, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine("[Telemetry] erro: " + ex.Message);
#endif
            }
            finally { Volatile.Write(ref _sending, 0); }
        }
    }
}
