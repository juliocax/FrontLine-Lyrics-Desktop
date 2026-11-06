using System;
using System.Collections.Generic;
using System.Windows;
using Frontline.Themes.Seasonal;

namespace FrontLineOverlay
{
    public partial class MainWindow
    {
        private void InitExpandedSettingsTelemetry()
        {
            Loaded += (_, _) => UpdateTelemetrySettingsDisclosure();
            if (ChkTelemetry != null)
                ChkTelemetry.Click += (_, _) => UpdateTelemetrySettingsDisclosure();
        }

        private void UpdateTelemetrySettingsDisclosure()
        {
            if (LblTelemetryHint == null) return;
            LblTelemetryHint.Text = SettingsText(
                "Optional. Shares appearance, translation, window and control settings, app version and system language at most once daily. Never sends songs, lyrics, searches or API keys.",
                "Opcional. Compartilha configurações de aparência, tradução, janela e controles, versão do app e idioma do sistema, no máximo uma vez ao dia. Não envia músicas, letras, buscas ou chaves de API.",
                "Opcional. Comparte ajustes de apariencia, traducción, ventana y controles, versión de la app e idioma del sistema, como máximo una vez al día. No envía canciones, letras, búsquedas ni claves API.",
                "Opsional. Membagikan pengaturan tampilan, terjemahan, jendela dan kontrol, versi aplikasi serta bahasa sistem paling banyak sekali sehari. Tidak mengirim lagu, lirik, pencarian atau kunci API.");
        }

        internal Dictionary<string, object> CaptureSettingsTelemetry()
        {
            Dispatcher.VerifyAccess();
            string selected = _themeId;
            var seasonal = selected == "auto" ? (_appliedTheme ?? ResolveSeasonalTheme()) : SeasonalTheme.None;
            string theme = selected == "auto" ? Telemetry.SeasonalThemeId(seasonal) : Telemetry.SettingText(selected);
            string shortcut = Telemetry.FormatLockShortcut(lockHotKeyVk, lockHotkeyModifiers);
            Rect bounds = WindowState == WindowState.Normal
                ? new Rect(new Point(Telemetry.SettingNumber(Left), Telemetry.SettingNumber(Top)),
                    new Size(Math.Max(0, Telemetry.SettingNumber(ActualWidth, 700)), Math.Max(0, Telemetry.SettingNumber(ActualHeight, 300))))
                : RestoreBounds;
            if (bounds.IsEmpty) bounds = new Rect(0, 0, 700, 300);

            return new Dictionary<string, object>
            {
                ["settings_schema"] = 2,
                ["theme"] = theme,
                ["theme_mode"] = selected == "auto" ? "auto" : "manual",
                ["theme_selected"] = Telemetry.SettingText(selected),
                ["lyric_font"] = Telemetry.SettingText(_lyricFontId),
                ["lyric_color"] = Telemetry.SettingText(_lyricColorId),
                ["lyric_weight"] = Telemetry.SettingText(_lyricWeightId),
                ["lyric_style"] = Telemetry.SettingText(_lyricStyleId),
                ["lyric_bold"] = Telemetry.Flag(LyricWeights.Resolve(_lyricWeightId).ToOpenTypeWeight() >= FontWeights.Bold.ToOpenTypeWeight()),
                ["lyric_italic"] = Telemetry.Flag(LyricStyles.Resolve(_lyricStyleId) == FontStyles.Italic),
                ["lyric_font_size"] = Telemetry.SettingNumber(SldFontSize?.Value ?? 26, 26),
                ["lyric_lines"] = _lyricLineCount == 0 ? "all" : _lyricLineCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["lyric_alignment"] = Telemetry.SettingText(_lyricAlignment),
                ["background_opacity"] = Math.Clamp(Telemetry.SettingNumber(bgOpacity, 1.0), 0, 1),
                ["show_theme_behind_lyrics"] = Telemetry.Flag(_showThemeBehindLyrics),
                ["translation_language"] = Telemetry.SettingText(_settingsPreferredTranslation),
                ["show_original_lyrics"] = Telemetry.Flag(keepOriginalWithTranslation),
                ["auto_mode"] = Telemetry.Flag(_wantAuto),
                ["manual_sync_mode"] = Telemetry.Flag(isManualSyncMode),
                ["overlay_locked"] = Telemetry.Flag(isLocked),
                ["ui_language"] = Telemetry.SettingText(currentAppLanguage, 8),
                ["lock_shortcut_set"] = shortcut == "none" ? "no" : "yes",
                ["lock_shortcut"] = shortcut,
                ["telemetry_enabled"] = Telemetry.Flag(Telemetry.Enabled),
                ["window_width"] = Telemetry.SettingNumber(bounds.Width, 700),
                ["window_height"] = Telemetry.SettingNumber(bounds.Height, 300),
                ["window_left"] = Telemetry.SettingNumber(bounds.Left),
                ["window_top"] = Telemetry.SettingNumber(bounds.Top),
                ["window_state"] = WindowState.ToString().ToLowerInvariant(),
            };
        }
    }
}
