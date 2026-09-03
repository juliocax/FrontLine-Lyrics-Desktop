using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Linq;
using System.Collections.ObjectModel;
using System.Windows.Data;
using System.Windows.Input;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FrontLineOverlay
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hwnd, int index, int newStyle);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr newStyle);
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Win32Point pt);
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

        private static void SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newStyle)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, index, newStyle);
            else SetWindowLong32(hwnd, index, newStyle.ToInt32());
        }

        private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        private const byte VK_MEDIA_PREV_TRACK = 0xB1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const int MaxWsMessageBytes = 512_000;
        private const int CoverDecodeWidth = 260;
        private const int SkipIconDecodeWidth = 64;
        private const int MaxPythonRestarts = 5;

        [StructLayout(LayoutKind.Sequential)] public struct Win32Point { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        private static Mutex? _appMutex;

        private bool isGhostMode = false;
        private bool isResizing = false;
        private bool isManualSyncMode = false;
        private string serverPort = "8765";
        private string currentAppStatus = "IDLE";
        private string currentCoverUrl = "";
        private ClientWebSocket? _webSocket;
        private double bgOpacity = 0.8;
        private bool keepOriginalWithTranslation = false;
        private string lastCurrentLyricsOriginal = "";
        private bool lastIsTranslatedActive = false;

        private readonly DispatcherTimer _mouseTracker = new() { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly DispatcherTimer _ghostTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly DispatcherTimer _opacityPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };

        private Process? pythonServerProcess;
        private readonly CancellationTokenSource _shutdown = new();
        private bool _shuttingDown;
        private int _pythonRestartCount;
        private string? _lastFullLyricsJson;
        private string? _autoHoldKey;
        private DateTime _autoHoldUntilUtc = DateTime.MinValue;

        // Persistência (fonte / Auto / posição): ideia de Warith Adetayo,
        // gravada em ApplicationData.LocalSettings (MSIX) ou JSON local.
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
        private bool _loadingSettings;
        private bool _suppressWindowSave;
        private bool _wantAuto;
        private bool _autoSyncedWithServer;
        private readonly ObservableCollection<SearchHistoryEntry> _searchHistory = [];
        private ICollectionView? _historyView;
        private string _historySort = "Date";
        private ListSortDirection _historyDir = ListSortDirection.Descending;

        private int helpIndex = 1;
        private readonly int maxHelpImages = 7;
        private readonly string helpFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrontLine_help_settings.txt");
        private double preHelpWidth, preHelpHeight;

        private string currentAppLanguage = "en";
        private readonly Dictionary<string, Dictionary<string, string>> uiStrings = new()
        {
            { "en", new() {
                { "Listen", "LISTEN" }, { "Search", "⌕ SEARCH" }, { "ManualSearch", "MANUAL SEARCH" },
                { "Artist", "Artist:" }, { "Song", "Song:" }, { "Find", "FIND" }, { "Cancel", "CANCEL" },
                { "Settings", "SETTINGS" }, { "ManualSync", "MANUAL SYNC" }, { "Translate", "TRANSLATE LYRICS" },
                { "UILang", "APP LANGUAGE" }, { "HelpPrev", "PREV" }, { "HelpNext", "NEXT" },
                { "HelpClose", "CLOSE" }, { "DontShow", "Don't show again" }, { "Ready", "Ready." },
                { "Listening", "Listening..." }, { "Searching", "Searching lyrics..." },
                { "SideListen", "LISTEN" }, { "SideClear", "CLEAR" }, { "Auto", "AUTO" },
                { "KeepOriginal", "Also show original lyrics" }, { "PreviewLabel", "PREVIEW" },
                { "MockPrevious", "♪ this is a preview line ♪" }, { "MockCurrent", "This is how your lyrics will look" },
                { "MockNext", "♪ next line preview ♪" }, { "MockCurrentOriginal", "(original) this is a preview line" },
                { "DonateTip", "Leave an optional tip" }, { "ReleaseNotesTooltip", "See what's new in this version" },
                { "PrevTrack", "Previous track" }, { "NextTrack", "Next track" },
                { "SearchHistory", "SEARCH HISTORY" }, { "HistoryArtist", "Artist" },
                { "HistorySong", "Song" }, { "HistoryDate", "Date" },
                { "HistoryEmpty", "No searches yet." }, { "HistoryRemove", "Remove" },
                { "FontSizeTitle", "FONT SIZE" },
                { "BgOpacityTitle", "BACKGROUND OPACITY" }
            }},
            { "pt", new() {
                { "Listen", "OUVIR" }, { "Search", "⌕ BUSCAR" }, { "ManualSearch", "BUSCA MANUAL" },
                { "Artist", "Artista:" }, { "Song", "Música:" }, { "Find", "BUSCAR" }, { "Cancel", "CANCELAR" },
                { "Settings", "CONFIGURAÇÕES" }, { "ManualSync", "SINC. MANUAL" }, { "Translate", "TRADUZIR LETRAS" },
                { "UILang", "IDIOMA DO APP" }, { "HelpPrev", "ANTERIOR" }, { "HelpNext", "PRÓXIMO" },
                { "HelpClose", "FECHAR" }, { "DontShow", "Não mostrar mais" }, { "Ready", "Pronto." },
                { "Listening", "Ouvindo..." }, { "Searching", "Buscando letra..." },
                { "SideListen", "OUVIR" }, { "SideClear", "LIMPAR" }, { "Auto", "AUTO" },
                { "KeepOriginal", "Também mostra a letra original" }, { "PreviewLabel", "PRÉVIA" },
                { "MockPrevious", "♪ esta é uma linha de exemplo ♪" }, { "MockCurrent", "É assim que sua letra vai aparecer" },
                { "MockNext", "♪ próxima linha de exemplo ♪" }, { "MockCurrentOriginal", "(original) esta é uma linha de exemplo" },
                { "DonateTip", "Deixe uma gorjeta opcional" }, { "ReleaseNotesTooltip", "Veja as novidades desta versão" },
                { "PrevTrack", "Faixa anterior" }, { "NextTrack", "Próxima faixa" },
                { "SearchHistory", "HISTÓRICO DE BUSCA" }, { "HistoryArtist", "Artista" },
                { "HistorySong", "Música" }, { "HistoryDate", "Data" },
                { "HistoryEmpty", "Nenhuma busca ainda." }, { "HistoryRemove", "Remover" },
                { "FontSizeTitle", "TAMANHO DA FONTE" },
        { "BgOpacityTitle", "OPACIDADE DO FUNDO" }
            }},
            { "es", new() {
                { "Listen", "ESCUCHAR" }, { "Search", "⌕ BUSCAR" }, { "ManualSearch", "BÚSQUEDA MANUAL" },
                { "Artist", "Artista:" }, { "Song", "Canción:" }, { "Find", "BUSCAR" }, { "Cancel", "CANCELAR" },
                { "Settings", "AJUSTES" }, { "ManualSync", "SINC. MANUAL" }, { "Translate", "TRADUCIR LETRAS" },
                { "UILang", "IDIOMA DE LA APP" }, { "HelpPrev", "ANTERIOR" }, { "HelpNext", "SIGUIENTE" },
                { "HelpClose", "CERRAR" }, { "DontShow", "No mostrar de novo" }, { "Ready", "Listo." },
                { "Listening", "Escuchando..." }, { "Searching", "Buscando letra..." },
                { "SideListen", "ESCUCHAR" }, { "SideClear", "LIMPIAR" }, { "Auto", "AUTO" },
                { "KeepOriginal", "También muestra la letra original" }, { "PreviewLabel", "VISTA PREVIA" },
                { "MockPrevious", "♪ esta es una línea de ejemplo ♪" }, { "MockCurrent", "Así se verá tu letra" },
                { "MockNext", "♪ próxima línea de ejemplo ♪" }, { "MockCurrentOriginal", "(original) esta es una línea de ejemplo" },
                { "DonateTip", "Deja una propina opcional" }, { "ReleaseNotesTooltip", "Ver las novedades de esta versión" },
                { "PrevTrack", "Pista anterior" }, { "NextTrack", "Pista siguiente" },
                { "SearchHistory", "HISTORIAL DE BÚSQUEDA" }, { "HistoryArtist", "Artista" },
                { "HistorySong", "Canción" }, { "HistoryDate", "Fecha" },
                { "HistoryEmpty", "Aún no hay búsquedas." }, { "HistoryRemove", "Quitar" },{ "FontSizeTitle", "TAMAÑO DE FUENTE" },{ "BgOpacityTitle", "OPACIDAD DE FONDO" }
            }}
        };

        private void SldBgOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            bgOpacity = e.NewValue;
            UpdateOpacityPreview();
        }

        private void UpdateOpacityPreview()
        {
            // Guarda de segurança: o Slider pode disparar ValueChanged durante o InitializeComponent,
            // antes de todos os elementos nomeados terem sido atribuídos (mesmo motivo do guard em SldFontSize).
            if (OpacityPreviewOverlay == null) return;

            byte alpha = (byte)(255 * bgOpacity);
            OpacityPreviewOverlay.Background = new SolidColorBrush(Color.FromArgb(alpha, 5, 5, 5));

            var t = uiStrings[currentAppLanguage];

            if (currentAppStatus == "IDLE")
            {
                // Sem música tocando ainda: usa uma letra fictícia só pra ilustrar a aparência.
                LblPreviewPrevious.Text = t["MockPrevious"];
                LblPreviewCurrent.Text = t["MockCurrent"];
                LblPreviewNext.Text = t["MockNext"];
                LblPreviewCurrentOriginal.Text = t["MockCurrentOriginal"];
                LblPreviewCurrentOriginal.Visibility = keepOriginalWithTranslation ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                // Já tem letra real na tela (tocando ou não): usa o conteúdo real em vez de mockup.
                // Isso vale igual pra "só a letra" quanto pro modo idle -- a lógica de esconder
                // fundo/menu abaixo não depende do status.
                LblPreviewPrevious.Text = LblPrevious.Text;
                LblPreviewCurrent.Text = LblCurrent.Text;
                LblPreviewNext.Text = LblNext.Text;
                LblPreviewCurrentOriginal.Text = LblCurrentOriginal.Text;
                LblPreviewCurrentOriginal.Visibility = LblCurrentOriginal.Visibility;
            }

            SetSettingsPreviewMode(true);
            OpacityPreviewOverlay.Visibility = Visibility.Visible;
            _opacityPreviewTimer.Stop();
            _opacityPreviewTimer.Start();
        }

        /// <summary>
        /// Um Popup do WPF sempre desenha por cima do conteúdo da janela dona, então não dá pra
        /// "tapar" o menu com um elemento dentro do RootGrid. Em vez disso, durante a prévia a
        /// gente esconde o MainBorder (fundo real/menu lateral) e recolhe os grupos do menu que
        /// não são o próprio slider de opacidade, deixando só "OPACIDADE DO FUNDO" + slider
        /// flutuando -- assim sobra só a prévia na tela pro usuário avaliar a transparência.
        /// </summary>
        private void SetSettingsPreviewMode(bool previewing)
        {
            MainBorder.Visibility = previewing ? Visibility.Hidden : Visibility.Visible;
            SettingsExtras.Visibility = previewing ? Visibility.Collapsed : Visibility.Visible;
            SettingsExtras2.Visibility = previewing ? Visibility.Collapsed : Visibility.Visible;
            LblSettingsTitle.Visibility = previewing ? Visibility.Collapsed : Visibility.Visible;
            if (SearchInputPanel != null)
            {
                bool searchOpen = SearchInputPanel.Visibility == Visibility.Visible;
                SearchInputPanel.Opacity = (previewing && searchOpen) ? 0 : 1;
                SearchInputPanel.IsHitTestVisible = !(previewing && searchOpen);
            }
        }

        private void OpacityPreviewTimer_Tick(object? sender, EventArgs e)
        {
            _opacityPreviewTimer.Stop();
            OpacityPreviewOverlay.Visibility = Visibility.Collapsed;
            SetSettingsPreviewMode(false);
        }

        private void SettingsPopup_Closed(object? sender, EventArgs e)
        {
            _opacityPreviewTimer.Stop();
            OpacityPreviewOverlay.Visibility = Visibility.Collapsed;
            SetSettingsPreviewMode(false);
        }

        private void SldFontSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            ApplyFontScale(e.NewValue);
            if (!_loadingSettings) ScheduleSave();
        }

        private void ApplyFontScale(double fontSize)
        {
            double scale = fontSize / 26.0;
            if (MainBorder != null)
                MainBorder.LayoutTransform = new ScaleTransform(scale, scale);
            if (SearchContentBorder != null)
                SearchContentBorder.LayoutTransform = new ScaleTransform(scale, scale);
        }

        private void BtnResetFont_Click(object sender, RoutedEventArgs e)
        {
            if (SldFontSize != null)
                SldFontSize.Value = 26; // Dispara o ValueChanged automaticamente, resetando o scale
        }

        public MainWindow()
        {
            const string appName = "FrontLineLyrics_UniqueMutex";
            _appMutex = new Mutex(true, appName, out bool createdNew);
            if (!createdNew)
            {
                Application.Current.Shutdown();
                return;
            }

            CrashReporter.Install();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownEngine();
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    CrashReporter.Log(ex, "AppDomain.UnhandledException", terminating: e.IsTerminating);
                ShutdownEngine();
            };

            InitializeComponent();
            this.StateChanged += MainWindow_StateChanged;
            Loaded += MainWindow_Loaded;
            LocationChanged += (_, _) => ScheduleSave();
            SizeChanged += (_, _) => ScheduleSave();
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); PersistSettings(); };

            RestoreFontAndAuto();
            InitSearchHistory();

            _mouseTracker.Tick += MouseTracker_Tick;
            _mouseTracker.Start();
            _ghostTimer.Tick += GhostTimer_Tick;
            _opacityPreviewTimer.Tick += OpacityPreviewTimer_Tick;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && int.TryParse(args[1], out _))
                serverPort = args[1];

            RestoreWindowPlacement();

            try
            {
                string logoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "logo.png");
                if (File.Exists(logoPath))
                {
                    this.Icon = BitmapFrame.Create(new Uri(logoPath, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                }
            }
            catch (Exception ex) { CrashReporter.Log(ex, "LoadIcon"); }

            try
            {
                string donateBtnPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "black-button.png");
                BitmapImage? donateBmp = null;

                if (File.Exists(donateBtnPath))
                {
                    // Arquivo solto na pasta assets (Build Action = Content, Copy to Output Directory = Copy if newer).
                    donateBmp = new BitmapImage();
                    donateBmp.BeginInit();
                    donateBmp.UriSource = new Uri(donateBtnPath, UriKind.Absolute);
                    donateBmp.CacheOption = BitmapCacheOption.OnLoad;
                    donateBmp.EndInit();
                    if (donateBmp.CanFreeze) donateBmp.Freeze();
                }
                else
                {
                    // Fallback: caso o arquivo esteja embutido no .exe como Resource em vez de copiado solto.
                    try { donateBmp = new BitmapImage(new Uri("pack://application:,,,/assets/black-button.png")); }
                    catch { donateBmp = null; }
                }

                if (donateBmp != null) ImgDonateButton.Source = donateBmp;
            }
            catch (Exception ex) { CrashReporter.Log(ex, "LoadDonate"); }

            LoadSkipIcons();
            LoadCoverArt("");
            UpdateVisualState(false);
            ApplyUILanguage(currentAppLanguage);

            StartPythonServer();
            _ = Task.Run(() => ConnectWebSocket(), _shutdown.Token);

            try
            {
                if (File.Exists(helpFilePath) && File.ReadAllText(helpFilePath).Trim() == "skip")
                    return;
            }
            catch (Exception ex) { CrashReporter.Log(ex, "HelpSettings"); }
            OpenHelpScreen();
        }

        private void StartPythonServer()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string serverExePath = Path.Combine(baseDir, "FrontlineServer", "FrontlineServer.exe");

                if (!File.Exists(serverExePath))
                {
                    string solutionDir = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\.."));
                    serverExePath = Path.Combine(solutionDir, "FrontlineServer", "dist", "FrontlineServer", "FrontlineServer.exe");
                }

                if (!File.Exists(serverExePath))
                {
                    CrashReporter.Info($"Motor de áudio ausente: {serverExePath}");
                    MessageBox.Show($"O motor de áudio não foi encontrado em:\n{serverExePath}", "Erro de Inicialização", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Não redirecionamos stdout/stderr aqui: o Python agora grava log em arquivo
                // (%LOCALAPPDATA%\FrontLineLyrics\logs\python_session.log). Redirecionar sem
                // nunca ler o pipe faz o buffer encher e trava a escrita no processo filho.
                ProcessStartInfo psi = new()
                {
                    FileName = serverExePath,
                    Arguments = serverPort,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = false,
                    RedirectStandardOutput = false
                };

                pythonServerProcess = new() { StartInfo = psi, EnableRaisingEvents = true };
                pythonServerProcess.Exited += PythonServer_Exited;
                pythonServerProcess.Start();
                CrashReporter.LogBreadcrumb("FrontlineServer.start", $"pid={pythonServerProcess.Id}");
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "StartPythonServer");
                MessageBox.Show($"Falha ao iniciar o processo do servidor: {ex.Message}", "Erro Crítico", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PythonServer_Exited(object? sender, EventArgs e)
        {
            if (_shuttingDown) return;
            int code = -1;
            try { code = pythonServerProcess?.ExitCode ?? -1; } catch { }
            CrashReporter.LogPythonExit(code);

            if (_pythonRestartCount >= MaxPythonRestarts)
            {
                CrashReporter.Info("FrontlineServer: limite de reinícios atingido.");
                return;
            }

            _pythonRestartCount++;
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_shuttingDown) return;
                    CrashReporter.Info($"Reiniciando FrontlineServer (tentativa {_pythonRestartCount}).");
                    StartPythonServer();
                });
            }
            catch (Exception ex) { CrashReporter.Log(ex, "PythonServer_Exited"); }
        }

        private void ShutdownEngine()
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            try { _shutdown.Cancel(); } catch { }
            KillPythonServer();
            try { _webSocket?.Abort(); } catch { }
            try { _appMutex?.ReleaseMutex(); _appMutex?.Dispose(); } catch { }
            _appMutex = null;
        }

        private void KillPythonServer()
        {
            var proc = pythonServerProcess;
            pythonServerProcess = null;
            if (proc == null) return;
            try { proc.Exited -= PythonServer_Exited; } catch { }
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(true);
                    proc.WaitForExit(2000);
                }
                proc.Dispose();
            }
            catch (Exception ex) { CrashReporter.Log(ex, "KillPythonServer"); }
        }

        protected override void OnClosed(EventArgs e)
        {
            _saveTimer.Stop();
            PersistSettings();
            _mouseTracker.Stop();
            _ghostTimer.Stop();
            _opacityPreviewTimer.Stop();
            ShutdownEngine();
            base.OnClosed(e);
        }

        private void ApplyUILanguage(string lang)
        {
            if (!uiStrings.TryGetValue(lang, out var t)) return;
            currentAppLanguage = lang;

            BtnListenBig.Content = t["Listen"];
            BtnSearchBig.Content = t["Search"];
            BtnListenSide.Content = t["SideListen"];
            BtnResetSide.Content = t["SideClear"];
            BtnAutoSide.Content = t["Auto"];
            LblManualSearchTitle.Text = t["ManualSearch"];
            LblArtistSearch.Text = t["Artist"];
            LblSongSearch.Text = t["Song"];
            BtnFindSearch.Content = t["Find"];
            BtnCancelSearch.Content = t["Cancel"];
            LblSettingsTitle.Text = t["Settings"];
            BtnManualSyncToggle.Content = t["ManualSync"];
            LblTranslateTitle.Text = t["Translate"];
            BtnAppLangToggle.Content = t["UILang"] + (BtnAppLangToggle.IsChecked == true ? " ▲" : " ▼");
            BtnHelpPrev.Content = t["HelpPrev"];
            ChkDontShowAgain.Content = t["DontShow"];
            LblKeepOriginal.Text = t["KeepOriginal"];
            LblPreviewTag.Text = t["PreviewLabel"];
            TxtDonateLabel.Text = t["DonateTip"];
            BtnReleaseNotes.ToolTip = t["ReleaseNotesTooltip"];
            BtnPrevTrack.ToolTip = t["PrevTrack"];
            BtnNextTrack.ToolTip = t["NextTrack"];
            LblSearchHistoryTitle.Text = t["SearchHistory"];
            LblSearchHistoryEmpty.Text = t["HistoryEmpty"];
            LblFontSizeTitle.Text = t["FontSizeTitle"];
            LblBgOpacityTitle.Text = t["BgOpacityTitle"];
            RefreshHistoryHeaders();
            RefreshHistoryDateLabels();

            UpdateHelpImage();

            if (currentAppStatus == "IDLE") LblArtistName.Text = t["Ready"];
            else if (currentAppStatus == "LISTENING") LblLoadingText.Text = t["Listening"];
            else if (currentAppStatus == "SEARCHING") LblLoadingText.Text = t["Searching"];
        }

        private void BtnAppLang_Click(object sender, RoutedEventArgs e) { if (sender is Button b && b.Tag is string lang) ApplyUILanguage(lang); }

        private void BtnAppLangToggle_Click(object sender, RoutedEventArgs e)
        {
            AppLangGrid.Visibility = BtnAppLangToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            BtnAppLangToggle.Content = uiStrings[currentAppLanguage]["UILang"] + (BtnAppLangToggle.IsChecked == true ? " ▲" : " ▼");
        }

        private void MouseTracker_Tick(object? sender, EventArgs e)
        {
            if (currentAppStatus == "IDLE" || HelpOverlay.Visibility == Visibility.Visible || BtnMenu.IsChecked == true || SearchInputPanel.Visibility == Visibility.Visible || isResizing)
            {
                _ghostTimer.Stop();
                if (isGhostMode) SetGhostMode(false);
                return;
            }

            GetCursorPos(out Win32Point p);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (GetWindowRect(hwnd, out RECT rect))
            {
                bool isMouseOver = (p.X >= rect.Left && p.X <= rect.Right && p.Y >= rect.Top && p.Y <= rect.Bottom);
                if (isMouseOver)
                {
                    _ghostTimer.Stop();
                    if (isGhostMode) SetGhostMode(false);
                }
                else
                {
                    if (!isGhostMode && !_ghostTimer.IsEnabled) _ghostTimer.Start();
                }
            }
        }

        private void GhostTimer_Tick(object? sender, EventArgs e) { _ghostTimer.Stop(); if (currentAppStatus != "IDLE") SetGhostMode(true); }

        private void SetGhostMode(bool enable)
        {
            if (isGhostMode == enable || isResizing) return;
            UpdateVisualState(enable);
        }

        private void UpdateVisualState(bool enableGhost)
        {
            if (isResizing) return;
            isGhostMode = enableGhost;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int GWL_EXSTYLE = -20;
            long extendedStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            const long transparentBit = 0x20L;

            if (currentAppStatus == "IDLE" || SearchInputPanel.Visibility == Visibility.Visible)
            {
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(extendedStyle & ~transparentBit));
                MainBorder.Background = new SolidColorBrush(Color.FromArgb(255, 10, 10, 10));
                MainBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));
                SidePanelCol.Width = new GridLength(35, GridUnitType.Star);
                ContentPanelCol.Width = new GridLength(65, GridUnitType.Star);
                SidePanel.Visibility = Visibility.Visible;
                ResizeGrip.Visibility = Visibility.Visible;
                TopRightControls.Visibility = Visibility.Visible;
                PlayingControls.Visibility = Visibility.Collapsed;
            }
            else if (isGhostMode)
            {
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(extendedStyle | transparentBit));

                byte alpha = (byte)(255 * bgOpacity);
                MainBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 5, 5, 5));
                MainBorder.BorderBrush = Brushes.Transparent;
                SidePanelCol.Width = new GridLength(0);
                ContentPanelCol.Width = new GridLength(100, GridUnitType.Star);
                SidePanel.Visibility = Visibility.Collapsed;
                ResizeGrip.Visibility = Visibility.Collapsed;
                TopRightControls.Visibility = Visibility.Collapsed;
                PlayingControls.Visibility = Visibility.Collapsed;
            }
            else
            {
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(extendedStyle & ~transparentBit));
                MainBorder.Background = new SolidColorBrush(Color.FromArgb(255, 10, 10, 10));
                MainBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));
                SidePanelCol.Width = new GridLength(35, GridUnitType.Star);
                ContentPanelCol.Width = new GridLength(65, GridUnitType.Star);
                SidePanel.Visibility = Visibility.Visible;
                ResizeGrip.Visibility = Visibility.Visible;
                TopRightControls.Visibility = Visibility.Visible;
                PlayingControls.Visibility = Visibility.Visible;
            }
        }

        private void ResizeGrip_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (currentAppStatus != "IDLE" && SearchInputPanel.Visibility != Visibility.Visible)
            {
                isResizing = true;
                SidePanelCol.Width = new GridLength(0);
                ContentPanelCol.Width = new GridLength(100, GridUnitType.Star);
                SidePanel.Visibility = Visibility.Collapsed;
                PlayingControls.Visibility = Visibility.Collapsed;
                TopRightControls.Visibility = Visibility.Collapsed;
                MainBorder.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * bgOpacity), 5, 5, 5));
                MainBorder.BorderBrush = Brushes.Transparent;
            }
        }

        private void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            isResizing = false;
            if (currentAppStatus != "IDLE" && SearchInputPanel.Visibility != Visibility.Visible)
                UpdateVisualState(isGhostMode);
        }

        private async Task ConnectWebSocket()
        {
            var buffer = new byte[8192];
            using var message = new MemoryStream();

            while (!_shutdown.IsCancellationRequested)
            {
                _webSocket = new ClientWebSocket();
                try
                {
                    await _webSocket.ConnectAsync(new Uri($"ws://127.0.0.1:{serverPort}"), _shutdown.Token);
                    CrashReporter.LogBreadcrumb("websocket.connected", serverPort);
                    message.SetLength(0);
                    while (_webSocket.State == WebSocketState.Open && !_shutdown.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _shutdown.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                            break;

                        message.Write(buffer, 0, result.Count);
                        if (message.Length > MaxWsMessageBytes)
                        {
                            CrashReporter.Info($"Mensagem WS descartada ({message.Length} bytes).");
                            message.SetLength(0);
                            continue;
                        }
                        if (!result.EndOfMessage)
                            continue;

                        string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                        message.SetLength(0);
                        UpdateUI(json);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (!_shuttingDown) CrashReporter.Log(ex, "ConnectWebSocket");
                }
                if (_shutdown.IsCancellationRequested) break;
                try { await Task.Delay(2000, _shutdown.Token); }
                catch (OperationCanceledException) { break; }
            }
        }

        private void HoldAutoForCurrentTrack()
        {
            string artist = (LblArtistName.Text ?? "").Trim();
            string song = (LblSongTitle.Text ?? "").Trim();
            _autoHoldKey = $"{artist}|{song}".ToLowerInvariant();
            _autoHoldUntilUtc = DateTime.UtcNow.AddSeconds(2);
            CrashReporter.LogBreadcrumb("auto.hold", _autoHoldKey);
        }

        private void ReleaseAutoHold()
        {
            _autoHoldKey = null;
            _autoHoldUntilUtc = DateTime.MinValue;
        }

        private bool ShouldSuppressAutoStatus(string status, string artist, string song)
        {
            bool cooldown = DateTime.UtcNow < _autoHoldUntilUtc;
            bool held = !string.IsNullOrEmpty(_autoHoldKey);
            if (!cooldown && !held) return false;
            if (status == "IDLE") return false;

            if (status is "LISTENING" or "SEARCHING")
                return cooldown || held;

            string key = $"{artist.Trim()}|{song.Trim()}".ToLowerInvariant();
            if (held && key == _autoHoldKey)
                return true;

            ReleaseAutoHold();
            return false;
        }

        private void UpdateUI(string jsonMessage)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonMessage);
                JsonElement root = doc.RootElement;
                string status = root.GetProperty("status").GetString() ?? "IDLE";
                string currentLyrics = root.TryGetProperty("current_lyrics", out var clp) ? clp.GetString() ?? "" : "";
                bool isTranslating = root.TryGetProperty("is_translating", out var tp) && tp.GetBoolean();
                bool autoMode = root.TryGetProperty("auto_mode", out var amp) && amp.GetBoolean();
                string currentLyricsOriginal = root.TryGetProperty("current_lyrics_original", out var clo) ? clo.GetString() ?? "" : "";
                bool isTranslatedActive = root.TryGetProperty("is_translated_active", out var ita) && ita.GetBoolean();
                string currentLanguage = root.TryGetProperty("current_language", out var cl) ? cl.GetString() ?? "original" : "original";
                string song = root.TryGetProperty("song", out var songEl) ? songEl.GetString() ?? "" : "";
                string artist = root.TryGetProperty("artist", out var artEl) ? artEl.GetString() ?? "" : "";
                string previousLyrics = root.TryGetProperty("previous_lyrics", out var prevEl) ? prevEl.GetString() ?? "" : "";
                string nextLyrics = root.TryGetProperty("next_lyrics", out var nextEl) ? nextEl.GetString() ?? "" : "";
                string? coverUrl = root.TryGetProperty("cover_art", out var c) ? c.GetString() : null;
                string? fullLyricsRaw = null;
                if (root.TryGetProperty("full_lyrics", out var full) && full.ValueKind is JsonValueKind.Array or JsonValueKind.String)
                    fullLyricsRaw = full.GetRawText();

                if (_shuttingDown || Dispatcher.HasShutdownStarted) return;

                Dispatcher.Invoke(() => {
                    if (ShouldSuppressAutoStatus(status, artist, song))
                    {
                        CrashReporter.LogBreadcrumb("auto.suppress", status);
                        status = "IDLE";
                        currentLyrics = previousLyrics = nextLyrics = "";
                        song = "";
                        coverUrl = "";
                        fullLyricsRaw = null;
                    }

                    currentAppStatus = status;
                    LblTranslating.Visibility = isTranslating ? Visibility.Visible : Visibility.Collapsed;
                    ApplyAutoModeFromServer(autoMode);

                    // Mesmo princípio pro idioma de tradução: só um botão fica "marcado" por
                    // vez, sempre o que bate com current_language do servidor -- inclusive
                    // depois de uma música nova no modo Auto, quando o idioma preferido é
                    // reaplicado automaticamente sem o usuário precisar apertar nada de novo.
                    TransOriginal.IsChecked = currentLanguage == "original";
                    TransRomanized.IsChecked = currentLanguage == "romanized";
                    TransEn.IsChecked = currentLanguage == "en";
                    TransEs.IsChecked = currentLanguage == "es";
                    TransFr.IsChecked = currentLanguage == "fr";
                    TransPt.IsChecked = currentLanguage == "pt";

                    // Sincronização manual só faz sentido quando existe uma letra sincronizada
                    // realmente tocando (status SYNCED). Fora disso, não há o que sincronizar.
                    bool manualSyncAvailable = status == "SYNCED";
                    BtnManualSyncToggle.Visibility = manualSyncAvailable ? Visibility.Visible : Visibility.Collapsed;
                    if (!manualSyncAvailable && isManualSyncMode)
                    {
                        isManualSyncMode = false;
                        FullLyricsList.Visibility = Visibility.Collapsed;
                        LyricsNormalView.Visibility = Visibility.Visible;
                    }

                    if (status == "IDLE")
                    {
                        LblSongTitle.Visibility = Visibility.Collapsed;
                        LblArtistName.Text = uiStrings[currentAppLanguage]["Ready"];
                        currentCoverUrl = ""; LoadCoverArt("");
                        HomeControls.Visibility = Visibility.Visible;
                        isGhostMode = false;
                        LblCurrent.Text = ""; LblPrevious.Text = ""; LblNext.Text = "";
                        LblCurrentOriginal.Text = ""; LblCurrentOriginal.Visibility = Visibility.Collapsed;
                        lastCurrentLyricsOriginal = ""; lastIsTranslatedActive = false;
                        if (_lastFullLyricsJson != null)
                        {
                            _lastFullLyricsJson = null;
                            FullLyricsList.ItemsSource = null;
                        }
                        LoadingSpinner.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        LblSongTitle.Visibility = Visibility.Visible;
                        HomeControls.Visibility = Visibility.Collapsed;
                        LblSongTitle.Text = song;
                        LblArtistName.Text = string.IsNullOrEmpty(artist) ? "..." : artist;
                        if (!string.IsNullOrEmpty(coverUrl) && coverUrl != currentCoverUrl)
                        {
                            currentCoverUrl = coverUrl;
                            LoadCoverArt(coverUrl);
                        }
                    }

                    if (status == "LISTENING" || status == "SEARCHING")
                    {
                        LoadingSpinner.Visibility = Visibility.Visible;
                        LblLoadingText.Text = (status == "LISTENING") ? uiStrings[currentAppLanguage]["Listening"] : uiStrings[currentAppLanguage]["Searching"];
                        LyricsNormalView.Visibility = Visibility.Collapsed;
                    }
                    else if (status != "IDLE")
                    {
                        LoadingSpinner.Visibility = Visibility.Collapsed;
                        if (!isManualSyncMode) LyricsNormalView.Visibility = Visibility.Visible;
                        LblCurrent.Text = currentLyrics;
                        LblPrevious.Text = previousLyrics;
                        LblNext.Text = nextLyrics;
                        lastCurrentLyricsOriginal = currentLyricsOriginal;
                        lastIsTranslatedActive = isTranslatedActive;
                        RefreshOriginalLyricsDisplay();
                    }
                    if (fullLyricsRaw != null && fullLyricsRaw != _lastFullLyricsJson)
                    {
                        _lastFullLyricsJson = fullLyricsRaw;
                        FullLyricsList.ItemsSource = JsonSerializer.Deserialize<List<LyricLine>>(fullLyricsRaw);
                    }

                    if (!isResizing) UpdateVisualState(isGhostMode);
                });
            }
            catch (OutOfMemoryException ex)
            {
                CrashReporter.Log(ex, "UpdateUI.OOM");
                CrashReporter.TryRecoverMemory();
                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        AlbumCoverImg.Source = null;
                        FullLyricsList.ItemsSource = null;
                        _lastFullLyricsJson = null;
                    });
                }
                catch { }
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "UpdateUI");
            }
        }

        private void LoadSkipIcons()
        {
            try
            {
                ApplySkipGlyph(ImgPrevTrack, LoadSkipIcon("double_arrow_left"));
                ApplySkipGlyph(ImgNextTrack, LoadSkipIcon("double_arrow_right"));
            }
            catch (Exception ex) { CrashReporter.Log(ex, "LoadSkipIcons"); }
        }

        private static void ApplySkipGlyph(System.Windows.Shapes.Rectangle target, ImageSource? source)
        {
            if (source == null)
            {
                target.OpacityMask = null;
                target.Fill = Brushes.Transparent;
                return;
            }
            // OpacityMask usa o alfa do arquivo: PNG preto ou branco no fundo
            // transparente vira a seta na cor do botão (#E0E0E0).
            var mask = new ImageBrush(source) { Stretch = Stretch.Uniform };
            if (mask.CanFreeze) mask.Freeze();
            target.OpacityMask = mask;
            target.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        }

        private static ImageSource? LoadSkipIcon(string stem)
        {
            foreach (string name in new[] { stem + ".png", stem + ".svg" })
            {
                string? disk = FindAssetFile(name);
                if (disk == null) continue;
                ImageSource? src = LoadSkipFile(disk);
                if (src != null) return src;
            }

            foreach (string name in new[] { stem + ".png", stem + ".svg" })
            {
                ImageSource? packed = TryLoadPackedSkip(name);
                if (packed != null) return packed;
            }

            CrashReporter.Info($"Ícone de skip ausente: {stem}.png / {stem}.svg");
            return null;
        }

        private static ImageSource? LoadSkipFile(string path)
        {
            try
            {
                if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                    return DecodeBitmap(new Uri(path, UriKind.Absolute), SkipIconDecodeWidth);

                Color fill = Color.FromRgb(0xE0, 0xE0, 0xE0);
                return SvgGlyph.TryLoadFile(path, fill);
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "LoadSkipFile");
                return null;
            }
        }

        private static ImageSource? TryLoadPackedSkip(string fileName)
        {
            foreach (string uri in new[]
            {
                $"pack://application:,,,/assets/{fileName}",
                $"pack://siteoforigin:,,,/assets/{fileName}",
            })
            {
                try
                {
                    if (fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    {
                        var info = Application.GetResourceStream(new Uri(uri, UriKind.Absolute));
                        if (info?.Stream == null) continue;
                        using var reader = new StreamReader(info.Stream);
                        return SvgGlyph.TryParse(reader.ReadToEnd(), Color.FromRgb(0xE0, 0xE0, 0xE0));
                    }
                    return DecodeBitmap(new Uri(uri, UriKind.Absolute), SkipIconDecodeWidth);
                }
                catch { }
            }
            return null;
        }

        private static string? FindAssetFile(string fileName)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in AssetSearchDirs())
            {
                string path = Path.Combine(dir, fileName);
                if (seen.Add(path) && File.Exists(path))
                    return path;
            }
            return null;
        }

        private static IEnumerable<string> AssetSearchDirs()
        {
            string? dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 7 && !string.IsNullOrEmpty(dir); i++)
            {
                yield return Path.Combine(dir, "assets");
                yield return Path.Combine(dir, "Frontline", "assets");
                dir = Directory.GetParent(dir)?.FullName;
            }
        }

        private void LoadCoverArt(string? url)
        {
            try
            {
                if (string.IsNullOrEmpty(url))
                {
                    string logoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "logo.png");
                    if (File.Exists(logoPath))
                        AlbumCoverImg.Source = DecodeBitmap(new Uri(logoPath, UriKind.Absolute));
                    return;
                }

                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && url.Length > 400_000)
                {
                    CrashReporter.Info("Capa data-URI ignorada (grande demais).");
                    return;
                }

                AlbumCoverImg.Source = DecodeBitmap(new Uri(url, UriKind.Absolute));
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "LoadCoverArt");
            }
        }

        private static BitmapImage DecodeBitmap(Uri uri, int? decodePixelWidth = CoverDecodeWidth)
        {
            BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodePixelWidth is > 0)
                bitmap.DecodePixelWidth = decodePixelWidth.Value;
            bitmap.EndInit();
            if (bitmap.CanFreeze) bitmap.Freeze();
            return bitmap;
        }

        private async void SendCommand(string action, string? lang = null, string? artist = null, string? song = null, double? time = null, bool? autoOn = null)
        {
            try
            {
                if (_webSocket != null && _webSocket.State == WebSocketState.Open)
                {
                    var p = new Dictionary<string, object?> { ["action"] = action };
                    if (lang != null) p["lang"] = lang;
                    if (artist != null) p["artist"] = artist;
                    if (song != null) p["song"] = song;
                    if (time != null) p["time"] = time;
                    if (autoOn != null) p["on"] = autoOn.Value;
                    if (action == "RESET") p["hold_auto"] = true;
                    await _webSocket.SendAsync(
                        new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p))),
                        WebSocketMessageType.Text, true, _shutdown.Token);
                }
            }
            catch (Exception ex) { CrashReporter.Log(ex, "SendCommand"); }
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Maximized) BtnRestore.Visibility = Visibility.Visible;
            else if (this.WindowState == WindowState.Normal) BtnRestore.Visibility = Visibility.Collapsed;
        }

        private void BtnRestore_Click(object sender, RoutedEventArgs e) { this.WindowState = WindowState.Normal; }
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (!isGhostMode) DragMove(); }
        private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e) { Width = Math.Max(MinWidth, Width + e.HorizontalChange); Height = Math.Max(MinHeight, Height + e.VerticalChange); }
        private void BtnMinimize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            SendCommand("QUIT");
            ShutdownEngine();
            Application.Current.Shutdown();
        }

        private void BtnSearchShow_Click(object sender, RoutedEventArgs e)
        {
            OpenManualSearchScreen();
        }

        private void OpenManualSearchScreen()
        {
            try
            {
                BtnMenu.IsChecked = false;
                bool autoOn = _wantAuto || BtnAutoSide.IsChecked == true;
                bool busy = currentAppStatus != "IDLE";
                if (autoOn)
                    TurnAutoOff();
                if (busy)
                {
                    HoldAutoForCurrentTrack();
                    SendCommand("RESET");
                }
                SearchInputPanel.Visibility = Visibility.Visible;
                ResizeGrip.Visibility = Visibility.Visible;
                TopRightControls.Visibility = Visibility.Visible;
                RefreshHistoryEmptyState();
                Dispatcher.BeginInvoke(() =>
                {
                    try { TxtArtist.Focus(); } catch { }
                });
            }
            catch (Exception ex) { CrashReporter.Log(ex, "OpenManualSearchScreen"); }
        }

        private void TurnAutoOff()
        {
            _wantAuto = false;
            BtnAutoSide.IsChecked = false;
            AppSettings.SetBool("AutoMode", false);
            if (_autoSyncedWithServer)
                SendCommand("AUTO_SET", autoOn: false);
        }

        private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                BtnManualSearch_Execute(sender, e);
        }

        private void BtnManualSearch_Execute(object sender, RoutedEventArgs e)
        {
            string artist = SearchHistoryStore.Clamp(TxtArtist.Text);
            string song = SearchHistoryStore.Clamp(TxtSong.Text);
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(song))
                return;
            TxtArtist.Text = artist;
            TxtSong.Text = song;
            RememberSearch(artist, song);
            ReleaseAutoHold();
            SendCommand("MANUAL_SEARCH", null, artist, song);
            SearchInputPanel.Visibility = Visibility.Collapsed;
        }

        private void BtnSearchCancel_Click(object sender, RoutedEventArgs e)
        {
            SearchInputPanel.Visibility = Visibility.Collapsed;
            SearchHistoryList.SelectedItem = null;
        }
        private void BtnManualSync_Toggle(object sender, RoutedEventArgs e) { isManualSyncMode = !isManualSyncMode; FullLyricsList.Visibility = isManualSyncMode ? Visibility.Visible : Visibility.Collapsed; LyricsNormalView.Visibility = isManualSyncMode ? Visibility.Collapsed : Visibility.Visible; BtnMenu.IsChecked = false; }
        private void FullLyricsList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (FullLyricsList.SelectedItem is LyricLine s) { SendCommand("SET_SYNC_TIME", null, null, null, s.Timestamp); isManualSyncMode = false; FullLyricsList.Visibility = Visibility.Collapsed; LyricsNormalView.Visibility = Visibility.Visible; FullLyricsList.SelectedItem = null; } }
        private void BtnListen_Click(object sender, RoutedEventArgs e)
        {
            ReleaseAutoHold();
            SendCommand("LISTEN");
        }
        private void SendMediaKey(byte virtualKey)
        {
            keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
            keybd_event(virtualKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        private void BtnPrevTrack_Click(object sender, RoutedEventArgs e) { SendMediaKey(VK_MEDIA_PREV_TRACK); }
        private void BtnNextTrack_Click(object sender, RoutedEventArgs e) { SendMediaKey(VK_MEDIA_NEXT_TRACK); }
        private void BtnAuto_Click(object sender, RoutedEventArgs e)
        {
            // Religar/desligar Auto cancela o hold do Limpar: o usuário pediu o Auto de novo.
            ReleaseAutoHold();
            _wantAuto = BtnAutoSide.IsChecked == true;
            AppSettings.SetBool("AutoMode", _wantAuto);
            if (_autoSyncedWithServer)
                SendCommand("AUTO_TOGGLE");
        }
        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            HoldAutoForCurrentTrack();
            SendCommand("RESET");
            BtnMenu.IsChecked = false;
        }
        private void BtnTrans_Click(object sender, RoutedEventArgs e) { if (sender is ToggleButton b && b.Tag is string l) SendCommand("TRANSLATE", l); BtnMenu.IsChecked = false; }
        private void ChkKeepOriginal_Click(object sender, RoutedEventArgs e)
        {
            keepOriginalWithTranslation = ChkKeepOriginal.IsChecked == true;
            RefreshOriginalLyricsDisplay();
        }

        private void RefreshOriginalLyricsDisplay()
        {
            bool show = keepOriginalWithTranslation && lastIsTranslatedActive && !string.IsNullOrWhiteSpace(lastCurrentLyricsOriginal);
            LblCurrentOriginal.Text = lastCurrentLyricsOriginal;
            LblCurrentOriginal.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
        private void BtnStartHelp_Click(object sender, RoutedEventArgs e) { OpenHelpScreen(); }

        private void OpenHelpScreen()
        {
            _suppressWindowSave = true;
            preHelpWidth = Width; preHelpHeight = Height;
            Width = 1000; Height = 550;
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;
            helpIndex = 1;
            UpdateHelpImage();
            HelpOverlay.Visibility = Visibility.Visible;
        }

        private void BtnHelpNext_Click(object sender, RoutedEventArgs e) { if (helpIndex < maxHelpImages) { helpIndex++; UpdateHelpImage(); } else BtnHelpClose_Click(null, null); }
        private void BtnHelpPrev_Click(object sender, RoutedEventArgs e) { if (helpIndex > 1) { helpIndex--; UpdateHelpImage(); } }
        private void BtnHelpClose_Click(object? sender, RoutedEventArgs? e)
        {
            try { File.WriteAllText(helpFilePath, ChkDontShowAgain.IsChecked == true ? "skip" : "show"); }
            catch (Exception ex) { CrashReporter.Log(ex, "HelpClose"); }
            HelpOverlay.Visibility = Visibility.Collapsed;
            Width = preHelpWidth; Height = preHelpHeight;
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;
            _suppressWindowSave = false;
            PersistSettings();
        }

        private void UpdateHelpImage()
        {
            var t = uiStrings[currentAppLanguage];
            BtnHelpNext.Content = (helpIndex == maxHelpImages) ? t["HelpClose"] : t["HelpNext"];
            BtnHelpCloseCenter.Visibility = (helpIndex == maxHelpImages) ? Visibility.Collapsed : Visibility.Visible;

            List<HelpBox> boxes = [];
            string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "tutorial.json");

            if (File.Exists(jsonPath))
            {
                try
                {
                    string jsonString = File.ReadAllText(jsonPath, Encoding.UTF8);
                    var tutorialData = JsonSerializer.Deserialize<Dictionary<string, List<TutorialBoxData>>>(jsonString);
                    if (tutorialData != null && tutorialData.TryGetValue(helpIndex.ToString(), out var items))
                    {
                        foreach (var item in items)
                        {
                            string textToShow = currentAppLanguage == "en" ? item.TextEN : currentAppLanguage == "es" ? item.TextES : item.TextPT;
                            boxes.Add(new HelpBox { Text = textToShow, Left = item.Left, Top = item.Top, Width = item.Width });
                        }
                    }
                }
                catch (Exception ex) { CrashReporter.Log(ex, "TutorialJson"); }
            }

            HelpTextContainer.ItemsSource = boxes;
            string img = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", $"help{helpIndex}.png");
            try
            {
                if (File.Exists(img)) HelpImage.Source = DecodeBitmap(new Uri(img), decodePixelWidth: null);
                else HelpImage.Source = null;
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "HelpImage");
                HelpImage.Source = null;
            }
        }
        private void BtnDonate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://www.buymeacoffee.com/juliocax") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "Donate");
                MessageBox.Show("Não foi possível abrir o link: " + ex.Message);
            }
        }

        private void BtnReleaseNotes_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://buymeacoffee.com/juliocax/frontline-lyrics-1-2-0") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "ReleaseNotes");
                MessageBox.Show("Não foi possível abrir o link: " + ex.Message);
            }
        }

        private void InitSearchHistory()
        {
            try
            {
                _searchHistory.Clear();
                foreach (var row in SearchHistoryStore.Load())
                    _searchHistory.Add(row);
                SearchHistoryList.ItemsSource = _searchHistory;
                _historyView = CollectionViewSource.GetDefaultView(_searchHistory);
                ApplyHistorySort();
                RefreshHistoryDateLabels();
                RefreshHistoryEmptyState();
            }
            catch (Exception ex) { CrashReporter.Log(ex, "InitSearchHistory"); }
        }

        private void RememberSearch(string artist, string song)
        {
            try
            {
                string key = $"{artist}|{song}".ToLowerInvariant();
                var existing = _searchHistory.FirstOrDefault(r => r.Key == key);
                if (existing != null)
                    _searchHistory.Remove(existing);
                var row = new SearchHistoryEntry
                {
                    Artist = artist,
                    Song = song,
                    SearchedAtUtc = DateTime.UtcNow,
                };
                row.Normalize();
                row.RefreshDateLabel(currentAppLanguage);
                _searchHistory.Insert(0, row);
                while (_searchHistory.Count > SearchHistoryStore.MaxEntries)
                    _searchHistory.RemoveAt(_searchHistory.Count - 1);
                PersistSearchHistory();
                ApplyHistorySort();
                RefreshHistoryEmptyState();
            }
            catch (Exception ex) { CrashReporter.Log(ex, "RememberSearch"); }
        }

        private void PersistSearchHistory() => SearchHistoryStore.Save(_searchHistory);

        private void RefreshHistoryEmptyState()
        {
            if (LblSearchHistoryEmpty == null) return;
            LblSearchHistoryEmpty.Visibility = _searchHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshHistoryDateLabels()
        {
            foreach (var row in _searchHistory)
                row.RefreshDateLabel(currentAppLanguage);
            _historyView?.Refresh();
        }

        private void RefreshHistoryHeaders()
        {
            if (!uiStrings.TryGetValue(currentAppLanguage, out var t)) return;
            if (ColHistoryArtist != null)
                ColHistoryArtist.Header = HeaderWithArrow(t["HistoryArtist"], "Artist");
            if (ColHistorySong != null)
                ColHistorySong.Header = HeaderWithArrow(t["HistorySong"], "Song");
            if (ColHistoryDate != null)
                ColHistoryDate.Header = HeaderWithArrow(t["HistoryDate"], "Date");
        }

        private string HeaderWithArrow(string title, string column)
        {
            if (_historySort != column) return title;
            return _historyDir == ListSortDirection.Ascending ? title + " ▲" : title + " ▼";
        }

        private void ApplyHistorySort()
        {
            if (_historyView == null) return;
            string prop = _historySort switch
            {
                "Artist" => nameof(SearchHistoryEntry.Artist),
                "Song" => nameof(SearchHistoryEntry.Song),
                _ => nameof(SearchHistoryEntry.SearchedAtUtc),
            };
            _historyView.SortDescriptions.Clear();
            _historyView.SortDescriptions.Add(new SortDescription(prop, _historyDir));
            RefreshHistoryHeaders();
        }

        private void HistoryHeader_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader header) return;
            if (header.Role == GridViewColumnHeaderRole.Padding) return;
            if (header.Column == null || header.Column == ColHistoryDelete) return;
            string column = header.Column == ColHistoryArtist ? "Artist"
                          : header.Column == ColHistorySong ? "Song"
                          : "Date";
            if (_historySort == column)
                _historyDir = _historyDir == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            else
            {
                _historySort = column;
                _historyDir = column == "Date" ? ListSortDirection.Descending : ListSortDirection.Ascending;
            }
            ApplyHistorySort();
        }

        private void HistoryList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (FindParentButton(e.OriginalSource as DependencyObject) != null)
                return;
            var item = FindParentListViewItem(e.OriginalSource as DependencyObject);
            if (item?.DataContext is SearchHistoryEntry row)
                RunHistorySearch(row);
        }

        private static ListViewItem? FindParentListViewItem(DependencyObject? node)
        {
            while (node != null)
            {
                if (node is ListViewItem item) return item;
                node = VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        private void RunHistorySearch(SearchHistoryEntry row)
        {
            TxtArtist.Text = row.Artist;
            TxtSong.Text = row.Song;
            BtnManualSearch_Execute(this, new RoutedEventArgs());
        }

        private void BtnHistoryDelete_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement fe || fe.DataContext is not SearchHistoryEntry row)
                return;
            _searchHistory.Remove(row);
            PersistSearchHistory();
            RefreshHistoryEmptyState();
        }

        private static Button? FindParentButton(DependencyObject? node)
        {
            while (node != null)
            {
                if (node is Button b) return b;
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        private void ApplyAutoModeFromServer(bool autoMode)
        {
            if (!_autoSyncedWithServer)
            {
                _autoSyncedWithServer = true;
                BtnAutoSide.IsChecked = _wantAuto;
                if (_wantAuto != autoMode)
                    SendCommand("AUTO_TOGGLE");
                return;
            }

            BtnAutoSide.IsChecked = autoMode;
            if (_wantAuto != autoMode)
            {
                _wantAuto = autoMode;
                AppSettings.SetBool("AutoMode", _wantAuto);
            }
        }

        private void RestoreFontAndAuto()
        {
            _loadingSettings = true;
            try
            {
                double font = AppSettings.GetDouble("FontSize", 26);
                if (SldFontSize != null)
                    SldFontSize.Value = Math.Clamp(font, SldFontSize.Minimum, SldFontSize.Maximum);
                _wantAuto = AppSettings.GetBool("AutoMode", false);
                if (BtnAutoSide != null)
                    BtnAutoSide.IsChecked = _wantAuto;
            }
            catch (Exception ex) { CrashReporter.Log(ex, "RestoreFontAndAuto"); }
            finally { _loadingSettings = false; }
        }

        private void RestoreWindowPlacement()
        {
            // Só restaura se o retângulo ainda cabe na área virtual (monitor
            // pode ter sido desconectado). Contribuição de Warith Adetayo.
            try
            {
                double w = AppSettings.GetDouble("WindowWidth", double.NaN);
                double h = AppSettings.GetDouble("WindowHeight", double.NaN);
                double x = AppSettings.GetDouble("WindowLeft", double.NaN);
                double y = AppSettings.GetDouble("WindowTop", double.NaN);

                if (!double.IsNaN(w) && w >= MinWidth && w <= SystemParameters.VirtualScreenWidth)
                    Width = w;
                if (!double.IsNaN(h) && h >= MinHeight && h <= SystemParameters.VirtualScreenHeight)
                    Height = h;

                if (double.IsNaN(x) || double.IsNaN(y)) return;

                bool visivel = x + Width >= SystemParameters.VirtualScreenLeft + 60
                            && x <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60
                            && y + Height >= SystemParameters.VirtualScreenTop + 20
                            && y <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 20;
                if (!visivel) return;

                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = x;
                Top = y;
            }
            catch (Exception ex) { CrashReporter.Log(ex, "RestoreWindowPlacement"); }
        }

        private void ScheduleSave()
        {
            if (_loadingSettings || !IsLoaded || _suppressWindowSave) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void PersistSettings()
        {
            if (_loadingSettings) return;
            try
            {
                if (SldFontSize != null)
                    AppSettings.SetDouble("FontSize", SldFontSize.Value);
                AppSettings.SetBool("AutoMode", _wantAuto);

                if (_suppressWindowSave) return;
                if (HelpOverlay?.Visibility == Visibility.Visible) return;
                if (WindowState != WindowState.Normal) return;

                AppSettings.SetDouble("WindowLeft", Left);
                AppSettings.SetDouble("WindowTop", Top);
                AppSettings.SetDouble("WindowWidth", Width);
                AppSettings.SetDouble("WindowHeight", Height);
            }
            catch (Exception ex) { CrashReporter.Log(ex, "PersistSettings"); }
        }
    }



    public class LyricLine
    {
        [JsonPropertyName("timestamp")] public double Timestamp { get; set; }
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    }

    public class TutorialBoxData
    {
        public string TextPT { get; set; } = string.Empty;
        public string TextEN { get; set; } = string.Empty;
        public string TextES { get; set; } = string.Empty;
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
    }

    public class HelpBox : INotifyPropertyChanged
    {
        private double _left; private double _top;
        public string Text { get; set; } = string.Empty;
        public double Width { get; set; }
        public double Left { get => _left; set { _left = value; OnPropertyChanged(); } }
        public double Top { get => _top; set { _top = value; OnPropertyChanged(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
    }


}
