using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            header.Children.Add(CreateExtensionIcon());
            var heading = Label(title, 26);
            heading.FontWeight = FontWeights.SemiBold;
            heading.VerticalAlignment = VerticalAlignment.Center;
            heading.Margin = new Thickness(14, 0, 0, 0);
            header.Children.Add(heading);
            var close = AddonsButton("");
            close.Content = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M1,1 L11,11 M11,1 L1,11"),
                Width = 12, Height = 12, Stretch = Stretch.Fill,
                Stroke = Brushes.White, StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            close.HorizontalContentAlignment = HorizontalAlignment.Center;
            close.VerticalContentAlignment = VerticalAlignment.Center;
            close.Template = (ControlTemplate)FindResource("AddonsCloseTemplate");
            close.Width = close.Height = 32;
            close.FontSize = 20;
            close.Padding = new Thickness(0);
            close.VerticalAlignment = VerticalAlignment.Center;
            close.Margin = new Thickness(12, 0, 0, 0);
            close.ToolTip = SettingsText("Close", "Fechar", "Cerrar", "Tutup");
            System.Windows.Automation.AutomationProperties.SetName(close, (string)close.ToolTip);
            close.Click += (_, _) => CloseAddonsPage();
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(close, Dock.Right);
            dock.Children.Add(close);
            dock.Children.Add(header);
            DockPanel.SetDock(dock, Dock.Top);
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var layout = new DockPanel();
            layout.Children.Add(dock);
            layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            var card = new StackPanel { Margin = new Thickness(16) };
            var name = Label(SettingsText("Japanese romanization", "Romanização japonesa",
                "Romanización japonesa", "Romanisasi bahasa Jepang"), 17);
            name.FontWeight = FontWeights.SemiBold;
            card.Children.Add(name);
            var description = Label(SettingsText(
                "Adds a Japanese dictionary to improve kana and kanji readings.",
                "Adiciona um dicionário para melhorar a leitura de kana e kanji.",
                "Añade un diccionario para mejorar la lectura de kana y kanji.",
                "Menambahkan kamus untuk meningkatkan pembacaan kana dan kanji."), 13);
            description.Margin = new Thickness(0, 6, 0, 8);
            card.Children.Add(description);
            var status = Label("", 12);
            card.Children.Add(status);
            var progress = AddonActivityBar();
            card.Children.Add(progress);
            var action = AddonsButton("");
            action.HorizontalAlignment = HorizontalAlignment.Left;
            card.Children.Add(action);
            body.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
                Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Top,
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

            CancellationTokenSource? download = null;
            action.Click += async (_, _) =>
            {
                if (download != null)
                {
                    action.IsEnabled = false;
                    status.Text = SettingsText("Canceling…", "Cancelando…", "Cancelando…", "Membatalkan…");
                    download.Cancel();
                    return;
                }
                if (_addonsBusy) return;
                _addonsBusy = true;
                SetAddonActivity(progress, true);
                close.IsEnabled = false;
                bool removing = JapanesePackInstalled();
                action.IsEnabled = !removing;
                if (!removing)
                {
                    download = new CancellationTokenSource();
                    action.Content = SettingsText("Cancel download", "Cancelar download", "Cancelar descarga", "Batalkan unduhan");
                }
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
                        string workspace = System.IO.Path.Combine(AddonsDirectory, $".japanese-install-{Guid.NewGuid():N}");
                        Directory.CreateDirectory(workspace);
                        string errorFile = System.IO.Path.Combine(workspace, "error.txt");
                        try
                        {
                            var start = new ProcessStartInfo(executable, "--install-japanese-pack")
                            {
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            start.Environment["FRONTLINE_ADDONS_DIR"] = AddonsDirectory;
                            start.Environment["FRONTLINE_ADDON_ERROR_FILE"] = errorFile;
                            start.Environment["FRONTLINE_ADDON_STAGE_DIR"] = workspace;
                            using var installer = Process.Start(start)
                                ?? throw new InvalidOperationException("Unable to start the installer.");
                            ChildProcessJob.Attach(installer);
                            await WaitForAddonProcessAsync(installer, download!.Token);
                            if (installer.ExitCode != 0 || !JapanesePackInstalled())
                            {
                                string detail = File.Exists(errorFile)
                                    ? File.ReadAllText(errorFile).Trim() : $"Installer exit code: {installer.ExitCode}";
                                throw new IOException(detail);
                            }
                        }
                        finally
                        {
                            try { Directory.Delete(workspace, recursive: true); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CrashReporter.Log(ex, "JapaneseAddonCleanup"); }
                        }
                    }
                    if (!removing && _settingsPreferredTranslation == "romanized" && currentAppStatus == "SYNCED")
                        RequestSettingsTranslation(LblArtistName.Text, LblSongTitle.Text);
                    Refresh();
                }
                catch (OperationCanceledException)
                {
                    Refresh();
                    if (!JapanesePackInstalled())
                        status.Text = SettingsText("Download canceled.", "Download cancelado.", "Descarga cancelada.", "Unduhan dibatalkan.");
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
                    download?.Dispose();
                    download = null;
                    _addonsBusy = false;
                    SetAddonActivity(progress, false);
                    close.IsEnabled = true;
                    action.IsEnabled = true;
                }
            };
            Refresh();
            AddOfflineTranslationCard(body, close);
            if (WindowState == WindowState.Normal)
            {
                _preAddonsSize = new Size(Width, Height);
                Width = Math.Max(Width, Math.Min(865, SystemParameters.WorkArea.Width));
                Height = Math.Max(Height, Math.Min(470, SystemParameters.WorkArea.Height));
            }
            var background = (TryFindResource("Settings.Panel") as SolidColorBrush)?.Color ?? Color.FromRgb(20, 21, 27);
            AddonsPage.Background = new SolidColorBrush(Color.FromRgb(background.R, background.G, background.B));
            AddonsPageContent.Content = layout;
            AddonsPage.Visibility = Visibility.Visible;
            AddonsPage.Focus();
        }

        private static async Task WaitForAddonProcessAsync(Process process, CancellationToken cancellationToken)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                }
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) throw;
            }
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
                            "Basic romanization is active. For better Japanese readings, download the dictionary in Settings → Translation → Manage add-ons.",
                            "A romanização básica está ativa. Para melhorar as leituras em japonês, baixe o dicionário em Configurações → Tradução → Gerenciar complementos.",
                            "La romanización básica está activa. Descarga el diccionario en Ajustes → Traducción → Gestionar complementos.",
                            "Romanisasi dasar aktif. Unduh kamus di Pengaturan → Terjemahan → Kelola add-on."),
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

        private static ProgressBar AddonActivityBar() => new()
        {
            Height = 4, Margin = new Thickness(0, 7, 0, 4),
            Foreground = new SolidColorBrush(Color.FromRgb(169, 137, 224)),
            Visibility = Visibility.Collapsed
        };

        private static void SetAddonActivity(ProgressBar bar, bool active)
        {
            bar.IsIndeterminate = active;
            bar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
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
