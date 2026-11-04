using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Interactivity;

namespace FrontLine_Linux;

public partial class MainWindow
{
    private const string LinuxAppVersion = "1.0.0";
    private sealed class OnboardingState
    {
        public bool WelcomeShown { get; set; }
        public bool TelemetryEnabled { get; set; } = true;
        public string LastSeenVersion { get; set; } = "";
        public DateTime LastTelemetryUtc { get; set; }
    }

    private static string OnboardingFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FrontLineLyrics", "linux_onboarding.json");
    private OnboardingState _onboarding = new();
    private bool _onboardingStarted;

    private void LoadOnboarding()
    {
        try
        {
            if (File.Exists(OnboardingFile))
                _onboarding = JsonSerializer.Deserialize<OnboardingState>(File.ReadAllText(OnboardingFile)) ?? new();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Onboarding load: " + ex.Message); }
        TelemetryToggle.IsChecked = _onboarding.TelemetryEnabled;
    }

    private void SaveOnboarding()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OnboardingFile)!);
            string temp = OnboardingFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_onboarding));
            File.Move(temp, OnboardingFile, true);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Onboarding save: " + ex.Message); }
    }

    private async Task RunStartupDialogsAsync()
    {
        if (_onboardingStarted) return;
        _onboardingStarted = true;
        LoadOnboarding();
        try
        {
            bool firstStart = !_onboarding.WelcomeShown;
            if (firstStart)
            {
                var dialog = new WelcomeDialog(_appLanguage, _onboarding.TelemetryEnabled);
                await dialog.ShowDialog(this);
                if (dialog.Completed)
                {
                    _onboarding.WelcomeShown = true;
                    _onboarding.TelemetryEnabled = dialog.TelemetryEnabled;
                    TelemetryToggle.IsChecked = dialog.TelemetryEnabled;
                    if (_appLanguage != dialog.SelectedLanguage)
                    {
                        _appLanguage = dialog.SelectedLanguage;
                        SelectComboTag(AppLanguageCombo, _appLanguage);
                        ApplyLanguage();
                        SavePreferences();
                    }
                    _onboarding.LastSeenVersion = LinuxAppVersion;
                    SaveOnboarding();
                }
            }
            else if (_onboarding.LastSeenVersion != LinuxAppVersion)
            {
                await new ReleaseNotesDialog(_appLanguage).ShowDialog(this);
                _onboarding.LastSeenVersion = LinuxAppVersion;
                SaveOnboarding();
            }
            if (_onboarding.WelcomeShown && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNAP"))
                && await SnapPermissionsDialog.AnyRequiredMissingAsync())
                await new SnapPermissionsDialog(_appLanguage).ShowDialog(this);
            if (_onboarding.WelcomeShown) _ = LinuxTelemetry.SendIfDueAsync(_onboarding.TelemetryEnabled,
                _onboarding.LastTelemetryUtc, _appLanguage, _lyricFont,
                (LyricColorCombo.SelectedItem as Avalonia.Controls.ComboBoxItem)?.Tag?.ToString() ?? "default",
                _visibleLineCount, () =>
                {
                    _onboarding.LastTelemetryUtc = DateTime.UtcNow;
                    SaveOnboarding();
                });
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Startup UI: " + ex); }
    }

    private async void OpenSnapPermissions_Click(object? sender, RoutedEventArgs e)
    {
        await new SnapPermissionsDialog(_appLanguage).ShowDialog(this);
        await RefreshSnapPermissionsStatusAsync();
    }

    private async Task RefreshSnapPermissionsStatusAsync()
    {
        if (SnapPermissionsSection is null) return;
        bool inSnap = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNAP"));
        SnapPermissionsSection.IsVisible = inSnap;
        if (!inSnap) return;
        bool mpris = await SnapPermissionsDialog.IsConnectedAsync("mpris");
        bool audio = await SnapPermissionsDialog.IsConnectedAsync("audio-record");
        SnapMusicPermissionStatus.Text = DialogUi.L(_appLanguage,
            mpris ? "Music player: allowed" : "Music player: permission needed",
            mpris ? "Reprodutor: permitido" : "Reprodutor: precisa de autorização",
            mpris ? "Reproductor: permitido" : "Reproductor: requiere permiso",
            mpris ? "Pemutar musik: diizinkan" : "Pemutar musik: perlu izin");
        SnapAudioPermissionStatus.Text = DialogUi.L(_appLanguage,
            audio ? "Music recognition: allowed" : "Music recognition: permission needed",
            audio ? "Reconhecimento de áudio: permitido" : "Reconhecimento de áudio: precisa de autorização",
            audio ? "Reconocimiento de audio: permitido" : "Reconocimiento de audio: requiere permiso",
            audio ? "Pengenalan audio: diizinkan" : "Pengenalan audio: perlu izin");
        var good = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8DDDA6"));
        var warn = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F5C779"));
        SnapMusicPermissionStatus.Foreground = mpris ? good : warn;
        SnapAudioPermissionStatus.Foreground = audio ? good : warn;
    }

    private void TelemetryToggle_Click(object? sender, RoutedEventArgs e)
    {
        _onboarding.TelemetryEnabled = TelemetryToggle.IsChecked == true;
        SaveOnboarding();
    }

    private async void WhatsNew_Click(object? sender, RoutedEventArgs e)
    {
        await new ReleaseNotesDialog(_appLanguage).ShowDialog(this);
    }
}
