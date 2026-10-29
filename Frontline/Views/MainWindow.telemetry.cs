using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace FrontLineOverlay
{
    public partial class MainWindow
    {
        private DispatcherTimer? _telemetryTimer;

        private static readonly Dictionary<string, Dictionary<string, string>> TelemetryStrings = new()
        {
            ["en"] = new()
            {
                ["Title"] = "SEND ANONYMOUS USAGE STATS",
                ["Hint"] = "Theme, lyric font, app language and lock shortcut. Never songs, lyrics or anything personal."
            },
            ["pt"] = new()
            {
                ["Title"] = "ENVIAR ESTATÍSTICAS ANÔNIMAS",
                ["Hint"] = "Tema, fonte da letra, idioma do app e atalho de bloqueio. Nunca músicas, letras ou algo pessoal."
            },
            ["es"] = new()
            {
                ["Title"] = "ENVIAR ESTADÍSTICAS ANÓNIMAS",
                ["Hint"] = "Tema, fuente de la letra, idioma de la app y atajo de bloqueo. Nunca canciones, letras ni nada personal."
            },
            ["id"] = new()
            {
                ["Title"] = "KIRIM STATISTIK ANONIM",
                ["Hint"] = "Tema, font lirik, bahasa aplikasi, dan pintasan kunci. Tidak pernah lagu, lirik, atau data pribadi."
            },
        };

        private string TelemetryText(string key) =>
            TelemetryStrings.TryGetValue(currentAppLanguage, out var d) && d.TryGetValue(key, out var v)
                ? v
                : TelemetryStrings["en"][key];

        private void ApplyTelemetryLanguage()
        {
            if (LblTelemetryTitle != null) LblTelemetryTitle.Text = TelemetryText("Title");
            if (LblTelemetryHint != null) LblTelemetryHint.Text = TelemetryText("Hint");
        }

        private void InitTelemetry()
        {
            try
            {
                if (ChkTelemetry != null) ChkTelemetry.IsChecked = Telemetry.Enabled;
                ApplyTelemetryLanguage();
                Dispatcher.BeginInvoke(new Action(ShowWelcomeIfNeeded), DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex) { CrashReporter.Log(ex, "InitTelemetry"); }
        }

        private void ShowWelcomeIfNeeded()
        {
            try
            {
                if (!Telemetry.WelcomeShown)
                {
                    string saved = AppSettings.GetString("AppLanguage", "");
                    string initial = string.IsNullOrEmpty(saved) ? WelcomeWindow.DetectSystemLanguage() : saved;

                    var win = new WelcomeWindow(initial, Telemetry.Enabled, HelpWebsiteUrl) { Owner = this };
                    win.ShowDialog();

                    Telemetry.Enabled = win.TelemetryEnabled;
                    Telemetry.WelcomeShown = true;
                    if (ChkTelemetry != null) ChkTelemetry.IsChecked = win.TelemetryEnabled;
                    if (win.SelectedLanguage != currentAppLanguage) ApplyUILanguage(win.SelectedLanguage);
                }
            }
            catch (Exception ex) { CrashReporter.Log(ex, "ShowWelcome"); }

            StartTelemetryTimer();
        }

        private void StartTelemetryTimer()
        {
            if (_telemetryTimer != null) return;

            _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(4) };
            _telemetryTimer.Tick += (_, _) => _ = SendTelemetryAsync();
            _telemetryTimer.Start();

            var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            first.Tick += (_, _) => { first.Stop(); _ = SendTelemetryAsync(); };
            first.Start();
        }

        private Task SendTelemetryAsync()
        {
            try { return Telemetry.SendSnapshotIfDueAsync(BuildTelemetrySnapshot()); }
            catch (Exception ex) { CrashReporter.Log(ex, "SendTelemetry"); return Task.CompletedTask; }
        }

        private TelemetrySnapshot BuildTelemetrySnapshot() => new(
            Theme: _appliedUserTheme?.Id ?? "default",   
            LyricFont: _lyricFontId,                    
            UiLanguage: currentAppLanguage,
            LockShortcut: LockShortcutText());

        private string? LockShortcutText()
        {
            if (lockHotKeyVk == 0) return null;
            var parts = new List<string>();
            if ((lockHotkeyModifiers & 0x0002) != 0) parts.Add("Ctrl");
            if ((lockHotkeyModifiers & 0x0001) != 0) parts.Add("Alt");
            if ((lockHotkeyModifiers & 0x0004) != 0) parts.Add("Shift");
            parts.Add(KeyInterop.KeyFromVirtualKey((int)lockHotKeyVk).ToString());
            return string.Join("+", parts);
        }

        private void ChkTelemetry_Click(object sender, RoutedEventArgs e)
        {
            Telemetry.Enabled = ChkTelemetry.IsChecked == true;
        }
    }
}
