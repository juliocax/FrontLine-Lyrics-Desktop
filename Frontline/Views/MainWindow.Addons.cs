using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

namespace FrontLineOverlay
{
    public partial class MainWindow
    {
        private const string JapaneseNoticeSetting = "HideJapaneseRomanizationPackNotice";
        private static readonly string[] DictionaryFiles =
            { "mecabrc", "sys.dic", "unk.dic", "matrix.bin", "char.bin", "dicrc" };

        private static string AddonsDirectory
        {
            get
            {
                bool packaged;
                try { _ = Windows.ApplicationModel.Package.Current.Id; packaged = true; }
                catch (InvalidOperationException) { packaged = false; }
                catch (System.Runtime.InteropServices.COMException) { packaged = false; }
                return packaged
                    ? System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "Addons")
                    : UnpackagedAddonsDirectory();
            }
        }

        private static string UnpackagedAddonsDirectory() => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FrontLineLyrics", "Addons");

        private static string JapaneseDictionary => System.IO.Path.Combine(AddonsDirectory, "ja-unidic-1.0.8");

        private string? _addonsServerExecutable;
        private string? _japaneseNoticeTrack;
        private bool _japaneseNoticeQueued;
        private bool _japanesePackMissing;
        private bool _addonsBusy;
        private Size? _preAddonsSize;

        private static bool JapanesePackInstalled() => DictionaryFiles.All(
            file => File.Exists(System.IO.Path.Combine(JapaneseDictionary, file)));

        private void BtnOpenAddons_Click(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;
            BtnMenu.IsChecked = false;
            string title = SettingsText("Add-ons", "Complementos", "Complementos", "Add-on");
            var panel = new StackPanel();
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 24) };
            header.Children.Add(CreateExtensionIcon());
            var heading = Label(title, 26);
            heading.FontWeight = FontWeights.SemiBold;
            heading.VerticalAlignment = VerticalAlignment.Center;
            heading.Margin = new Thickness(14, 0, 0, 0);
            header.Children.Add(heading);
            var close = AddonsButton("×");
            close.Width = close.Height = 32;
            close.FontSize = 20;
            close.Padding = new Thickness(0);
            close.VerticalAlignment = VerticalAlignment.Top;
            close.Margin = new Thickness(12, 0, 0, 0);
            close.ToolTip = SettingsText("Close", "Fechar", "Cerrar", "Tutup");
            System.Windows.Automation.AutomationProperties.SetName(close, (string)close.ToolTip);
            close.Click += (_, _) => CloseAddonsPage();
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(close, Dock.Right);
            dock.Children.Add(close);
            dock.Children.Add(header);
            panel.Children.Add(dock);

            var card = new StackPanel { Margin = new Thickness(16) };
            var name = Label(SettingsText("Japanese romanization", "Romanização japonesa",
                "Romanización japonesa", "Romanisasi bahasa Jepang"), 17);
            name.FontWeight = FontWeights.SemiBold;
            card.Children.Add(name);
            var description = Label(SettingsText(
                "UniDic Lite dictionary. Works offline after installation.",
                "Dicionário UniDic Lite. Funciona offline após a instalação.",
                "Diccionario UniDic Lite. Funciona sin conexión tras la instalación.",
                "Kamus UniDic Lite. Berfungsi offline setelah pemasangan."), 13);
            description.Margin = new Thickness(0, 6, 0, 8);
            card.Children.Add(description);
            var status = Label("", 12);
            card.Children.Add(status);
            var action = AddonsButton("");
            action.HorizontalAlignment = HorizontalAlignment.Left;
            card.Children.Add(action);
            panel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
                Child = card
            });

            void Refresh()
            {
                bool installed = JapanesePackInstalled();
                action.Content = installed
                    ? SettingsText("Remove", "Remover", "Quitar", "Hapus")
                    : SettingsText("Download", "Baixar", "Descargar", "Unduh");
                status.Text = installed
                    ? SettingsText("Installed · about 250 MB", "Instalado · cerca de 250 MB",
                        "Instalado · unos 250 MB", "Terpasang · sekitar 250 MB")
                    : SettingsText("47 MB download · about 250 MB installed",
                        "47 MB para baixar · cerca de 250 MB instalado",
                        "47 MB de descarga · unos 250 MB instalado",
                        "Unduhan 47 MB · sekitar 250 MB setelah dipasang");
            }

            action.Click += async (_, _) =>
            {
                _addonsBusy = true;
                close.IsEnabled = false;
                action.IsEnabled = false;
                bool removing = JapanesePackInstalled();
                status.Text = removing
                    ? SettingsText("Removing…", "Removendo…", "Quitando…", "Menghapus…")
                    : SettingsText("Downloading and verifying…", "Baixando e verificando…",
                        "Descargando y verificando…", "Mengunduh dan memverifikasi…");
                try
                {
                    if (removing)
                    {
                        SuppressJapaneseNoticeForCurrentTrack();
                        await RemoveJapaneseDictionaryAsync();
                    }
                    else
                    {
                        var executable = _addonsServerExecutable
                            ?? throw new InvalidOperationException("FrontlineServer is unavailable.");
                        Directory.CreateDirectory(AddonsDirectory);
                        string errorFile = System.IO.Path.Combine(AddonsDirectory, $"install-{Guid.NewGuid():N}.txt");
                        try
                        {
                            var start = new ProcessStartInfo(executable, "--install-japanese-pack")
                            {
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            start.Environment["FRONTLINE_ADDONS_DIR"] = AddonsDirectory;
                            start.Environment["FRONTLINE_ADDON_ERROR_FILE"] = errorFile;
                            using var installer = Process.Start(start)
                                ?? throw new InvalidOperationException("Unable to start the installer.");
                            ChildProcessJob.Attach(installer);
                            await installer.WaitForExitAsync();
                            if (installer.ExitCode != 0 || !JapanesePackInstalled())
                            {
                                string detail = File.Exists(errorFile)
                                    ? File.ReadAllText(errorFile).Trim() : $"Installer exit code: {installer.ExitCode}";
                                throw new IOException(detail);
                            }
                        }
                        finally
                        {
                            try { File.Delete(errorFile); }
                            catch (IOException ex) { CrashReporter.Log(ex, "JapaneseAddonCleanup"); }
                        }
                    }
                    if (!removing && _settingsPreferredTranslation == "romanized" && currentAppStatus == "SYNCED")
                        RequestSettingsTranslation(LblArtistName.Text, LblSongTitle.Text);
                    Refresh();
                }
                catch (Exception error)
                {
                    CrashReporter.Log(error, "JapaneseRomanizationPack");
                    Refresh();
                    status.Text = SettingsText("Could not finish. Try again.", "Não foi possível concluir. Tente novamente.",
                        "No se pudo completar. Inténtalo de nuevo.", "Tidak dapat menyelesaikan. Coba lagi.") + "\n" + error.Message;
                }
                finally
                {
                    _addonsBusy = false;
                    close.IsEnabled = true;
                    action.IsEnabled = true;
                }
            };
            Refresh();
            if (WindowState == WindowState.Normal)
            {
                _preAddonsSize = new Size(Width, Height);
                Width = Math.Max(Width, Math.Min(760, SystemParameters.WorkArea.Width));
                Height = Math.Max(Height, Math.Min(470, SystemParameters.WorkArea.Height));
            }
            AddonsPageContent.Content = panel;
            AddonsPage.Visibility = Visibility.Visible;
            AddonsPage.Focus();
        }

        private async Task RemoveJapaneseDictionaryAsync()
        {
            var server = pythonServerProcess;
            if (server != null)
                server.Exited -= PythonServer_Exited;
            try
            {
                if (server != null)
                {
                    if (!server.HasExited)
                        server.Kill(entireProcessTree: true);
                    await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }

                if (_shuttingDown) return;
                string dictionary = JapaneseDictionary;
                await Task.Run(() =>
                {
                    if (Directory.Exists(dictionary))
                        Directory.Delete(dictionary, recursive: true);
                });
            }
            finally
            {
                if (!_shuttingDown)
                {
                    if (server != null && !server.HasExited)
                    {
                        server.Exited += PythonServer_Exited;
                    }
                    else
                    {
                        pythonServerProcess = null;
                        server?.Dispose();
                        _autoSyncedWithServer = false;
                        _settingsTranslationKey = "";
                        _settingsPendingTranslation = null;
                        StartPythonServer();
                    }
                }
            }
        }

        private void SuppressJapaneseNoticeForCurrentTrack() =>
            _japaneseNoticeTrack = $"{LblArtistName.Text}|{LblSongTitle.Text}";

        private void UpdateJapanesePackNotice(bool missing, string artist, string song)
        {
            _japanesePackMissing = missing;
            if (!missing || _settingsPreferredTranslation != "romanized")
                return;

            string track = $"{artist}|{song}";
            if (_japaneseNoticeQueued || _japaneseNoticeTrack == track ||
                AppSettings.GetBool(JapaneseNoticeSetting, false))
                return;

            _japaneseNoticeQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _japaneseNoticeQueued = false;
                if (_shuttingDown || !_japanesePackMissing || JapanesePackInstalled() || AddonsPage.Visibility == Visibility.Visible || DarkDialogOverlay.Visibility == Visibility.Visible ||
                    _settingsPreferredTranslation != "romanized" ||
                    track != $"{LblArtistName.Text}|{LblSongTitle.Text}")
                    return;

                _japaneseNoticeTrack = track;
                BtnMenu.IsChecked = false;
                SettingsPopup.IsOpen = false;
                ChkHideJapaneseNotice.IsChecked = false;
                ChkHideJapaneseNotice.Content = SettingsText("Don't show this message again",
                    "Não mostrar esta mensagem novamente", "No volver a mostrar este mensaje",
                    "Jangan tampilkan pesan ini lagi");
                ChkHideJapaneseNotice.Visibility = Visibility.Visible;
                try
                {
                    ShowDarkDialog(
                        SettingsText("Japanese romanization", "Romanização japonesa",
                            "Romanización japonesa", "Romanisasi bahasa Jepang"),
                        SettingsText(
                            "Basic romanization is active. For better Japanese readings, download the dictionary in Settings → General → Manage add-ons.",
                            "A romanização básica está ativa. Para melhorar as leituras em japonês, baixe o dicionário em Configurações → Geral → Gerenciar complementos.",
                            "La romanización básica está activa. Descarga el diccionario en Ajustes → General → Gestionar complementos.",
                            "Romanisasi dasar aktif. Unduh kamus di Pengaturan → Umum → Kelola add-on."),
                        SettingsText("Got it", "Ciente", "Entendido", "Mengerti"), "", showCancel: false);
                    if (ChkHideJapaneseNotice.IsChecked == true)
                        AppSettings.SetBool(JapaneseNoticeSetting, true);
                }
                finally
                {
                    ChkHideJapaneseNotice.Visibility = Visibility.Collapsed;
                }
            }));
        }

        private void CloseAddonsPage()
        {
            if (_addonsBusy) return;
            AddonsPage.Visibility = Visibility.Collapsed;
            AddonsPageContent.Content = null;
            if (_preAddonsSize is Size previous)
            {
                Width = previous.Width;
                Height = previous.Height;
                _preAddonsSize = null;
            }
            ScheduleSave();
        }

        private void AddonsPage_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            CloseAddonsPage();
            e.Handled = true;
        }

        private static TextBlock Label(string text, double size) => new()
        {
            Text = text, FontSize = size, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap
        };

        private Button AddonsButton(string text) => new()
        {
            Style = TryFindResource("GlassButton") as Style,
            Content = text, Padding = new Thickness(15, 7, 15, 7),
            Margin = new Thickness(0, 14, 0, 0)
        };

        private FrameworkElement CreateExtensionIcon()
        {
            var color = GetThemeLogoColor();
            string? file = FindAssetFile("extension.svg");
            if (file != null)
            {
                try
                {
                    var glyph = SvgGlyph.TryLoadFile(file, color);
                    if (glyph != null)
                        return new Image { Width = 62, Height = 62, Source = glyph, Stretch = Stretch.Uniform };
                }
                catch (Exception ex) { CrashReporter.Log(ex, "AddonsIcon"); }
            }

            return new System.Windows.Shapes.Path
            {
                Width = 62, Height = 62, Stretch = Stretch.Uniform,
                Fill = new SolidColorBrush(color),
                Data = Geometry.Parse("M3,3 H10 C10,-1 16,-1 16,3 H21 V10 C25,10 25,16 21,16 V21 H14 C14,17 8,17 8,21 H3 V14 C7,14 7,8 3,8 Z")
            };
        }
    }
}
