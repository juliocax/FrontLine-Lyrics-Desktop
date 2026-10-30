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

        private static string Clean(string? s, int max = 40)
        {
            s = (s ?? "").Trim();
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private static string CurrentLyricColor(string fallback)
        {
            try
            {
                var app = Application.Current;
                if (app == null) return fallback;
                return app.Dispatcher.Invoke(() =>
                {
                    var window = app.Windows.OfType<MainWindow>().FirstOrDefault();
                    if (window?.CmbLyricColor?.SelectedItem is ComboBoxItem option)
                    {
                        if (option.Tag is string tag && !string.IsNullOrWhiteSpace(tag)) return Clean(tag);
                        if (option.Content is string label && !string.IsNullOrWhiteSpace(label)) return Clean(label);
                    }
                    if (window?.LblPreviewCurrent?.Foreground is SolidColorBrush brush)
                        return brush.Color.ToString();
                    return fallback;
                });
            }
            catch { return fallback; }
        }

        private static (string Theme, string Mode) CurrentTheme(string fallback)
        {
            string selected = AppSettings.GetString("AppTheme",
                AppSettings.GetBool("SeasonalEnabled", true) ? "auto" : "default");
            try
            {
                var app = Application.Current;
                if (app != null)
                {
                    selected = app.Dispatcher.Invoke(() =>
                    {
                        var window = app.Windows.OfType<MainWindow>().FirstOrDefault();
                        return window?.CmbUserTheme?.SelectedItem is ComboBoxItem item
                               && item.Tag is string id && !string.IsNullOrWhiteSpace(id)
                            ? id : selected;
                    });
                }
            }
            catch { /*  */ }

            if (string.Equals(selected, "auto", StringComparison.OrdinalIgnoreCase))
            {
                string seasonal = SeasonalThemeProvider.GetCurrent() switch
                {
                    SeasonalTheme.Halloween => "halloween",
                    SeasonalTheme.Christmas => "christmas",
                    SeasonalTheme.NewYear => "newyear",
                    _ => "default"
                };
                return (seasonal, "auto");
            }

            if (string.Equals(selected, "default", StringComparison.OrdinalIgnoreCase))
                return ("default", "manual");

            return (!string.IsNullOrWhiteSpace(selected) ? Clean(selected) : Clean(fallback), "manual");
        }

        // Read the live selection first (settings are saved with a short delay).
        // Fall back to persisted values for older installations or if the window is unavailable.
        private static (int Lines, string Alignment) CurrentLyricLayout()
        {
            double savedLines = AppSettings.GetDouble("LyricLineCount", 3);
            int lines = savedLines == 1 || savedLines == 5 ? (int)savedLines : 3;
            string alignment = AppSettings.GetString("LyricAlignment", "center").ToLowerInvariant();
            if (alignment != "left" && alignment != "right") alignment = "center";

            try
            {
                var app = Application.Current;
                if (app != null)
                {
                    return app.Dispatcher.Invoke(() =>
                    {
                        var window = app.Windows.OfType<MainWindow>().FirstOrDefault();
                        if (window?.CmbLyricLines?.SelectedItem is ComboBoxItem lineItem &&
                            int.TryParse(lineItem.Tag?.ToString(), out int selectedLines) &&
                            (selectedLines == 1 || selectedLines == 3 || selectedLines == 5))
                            lines = selectedLines;

                        if (window?.CmbLyricAlignment?.SelectedItem is ComboBoxItem alignItem)
                        {
                            string? selected = alignItem.Tag?.ToString()?.ToLowerInvariant();
                            if (selected == "left" || selected == "center" || selected == "right")
                                alignment = selected;
                        }
                        return (lines, alignment);
                    });
                }
            }
            catch { /* Keep the persisted values. */ }
            return (lines, alignment);
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
                long epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string sessionId = (epoch * 100_000_000L + Random.Shared.NextInt64(0, 100_000_000L))
                    .ToString(CultureInfo.InvariantCulture);

                var currentTheme = CurrentTheme(snap.Theme);
                var lyricLayout = CurrentLyricLayout();

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
                    props = new Dictionary<string, object>
                    {
                        ["theme"] = currentTheme.Theme,
                        ["theme_mode"] = currentTheme.Mode,
                        ["lyric_font"] = Clean(snap.LyricFont),
                        ["lyric_color"] = Clean(CurrentLyricColor(snap.LyricColor)),
                        ["lyric_lines"] = lyricLayout.Lines,
                        ["lyric_alignment"] = lyricLayout.Alignment,
                        ["ui_language"] = Clean(snap.UiLanguage, 8),
                        ["lock_shortcut_set"] = snap.LockShortcut == null ? "no" : "yes",
                        ["lock_shortcut"] = snap.LockShortcut == null ? "none" : Clean(snap.LockShortcut)
                    }
                };

                var json = JsonSerializer.Serialize(new[] { evt });
                using var req = new HttpRequestMessage(HttpMethod.Post, Host + "/api/v0/events")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("App-Key", AppKey);

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
