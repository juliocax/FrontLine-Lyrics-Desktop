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
using System.Windows.Input;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FrontLineOverlay
{
    public partial class MainWindow : Window
    {
        // Retornamos ao DllImport clássico para evitar a necessidade de "Unsafe Code" no projeto
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Win32Point pt);
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        private const byte VK_MEDIA_PREV_TRACK = 0xB1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

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
                { "PrevTrack", "Previous track" }, { "NextTrack", "Next track" }
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
                { "PrevTrack", "Faixa anterior" }, { "NextTrack", "Próxima faixa" }
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
                { "PrevTrack", "Pista anterior" }, { "NextTrack", "Pista siguiente" }
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
            if (MainBorder != null)
            {
                double scale = e.NewValue / 26.0;
                MainBorder.LayoutTransform = new ScaleTransform(scale, scale);
            }
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

            AppDomain.CurrentDomain.ProcessExit += (s, e) => KillPythonServer();
            AppDomain.CurrentDomain.UnhandledException += (s, e) => KillPythonServer();
            Application.Current.DispatcherUnhandledException += (s, e) => KillPythonServer();

            InitializeComponent();
            this.StateChanged += MainWindow_StateChanged;
            Loaded += MainWindow_Loaded;

            _mouseTracker.Tick += MouseTracker_Tick;
            _mouseTracker.Start();
            _ghostTimer.Tick += GhostTimer_Tick;
            _opacityPreviewTimer.Tick += OpacityPreviewTimer_Tick;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1) serverPort = args[1];

            try
            {
                string logoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "logo.png");
                if (File.Exists(logoPath))
                {
                    this.Icon = BitmapFrame.Create(new Uri(logoPath, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                }
            }
            catch { }

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
                }
                else
                {
                    // Fallback: caso o arquivo esteja embutido no .exe como Resource em vez de copiado solto.
                    try { donateBmp = new BitmapImage(new Uri("pack://application:,,,/assets/black-button.png")); }
                    catch { donateBmp = null; }
                }

                if (donateBmp != null) ImgDonateButton.Source = donateBmp;
            }
            catch { }

            LoadCoverArt("");
            UpdateVisualState(false);
            ApplyUILanguage(currentAppLanguage);

            StartPythonServer();
            Task.Run(() => ConnectWebSocket());

            if (File.Exists(helpFilePath) && File.ReadAllText(helpFilePath).Trim() == "skip") return;
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
                    MessageBox.Show($"O motor de áudio não foi encontrado em:\n{serverExePath}", "Erro de Inicialização", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Uso do "new()" simplificado que o Visual Studio estava sugerindo
                // Não redirecionamos stdout/stderr aqui: o Python agora grava log em arquivo
                // (%LOCALAPPDATA%\FrontLineLyrics\logs\frontline_server.log). Redirecionar sem
                // nunca ler o pipe faz o buffer encher e trava a escrita no processo filho quando
                // há bastante log (foi provavelmente a causa dos travamentos após novas features).
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

                pythonServerProcess = new() { StartInfo = psi };
                pythonServerProcess.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Falha ao iniciar o processo do servidor: {ex.Message}", "Erro Crítico", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void KillPythonServer()
        {
            try
            {
                if (pythonServerProcess != null && !pythonServerProcess.HasExited)
                {
                    pythonServerProcess.Kill(true);
                    pythonServerProcess.Dispose();
                }
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            KillPythonServer();
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
            int WS_EX_TRANSPARENT = 0x00000020, GWL_EXSTYLE = -20;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

            if (currentAppStatus == "IDLE")
            {
                _ = SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle & ~WS_EX_TRANSPARENT);
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
                _ = SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);

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
                _ = SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle & ~WS_EX_TRANSPARENT);
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
            if (currentAppStatus != "IDLE")
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
            if (currentAppStatus != "IDLE") UpdateVisualState(isGhostMode);
        }

        private async Task ConnectWebSocket()
        {
            while (true)
            {
                _webSocket = new ClientWebSocket();
                try
                {
                    await _webSocket.ConnectAsync(new Uri($"ws://127.0.0.1:{serverPort}"), CancellationToken.None);
                    byte[] buffer = new byte[16384];
                    while (_webSocket.State == WebSocketState.Open)
                    {
                        WebSocketReceiveResult result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        UpdateUI(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                }
                catch (Exception) { }
                await Task.Delay(2000);
            }
        }

        private void UpdateUI(string jsonMessage)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonMessage);
                JsonElement root = doc.RootElement;
                string status = root.GetProperty("status").GetString() ?? "IDLE";
                string currentLyrics = root.GetProperty("current_lyrics").GetString() ?? "";
                //int fontSize = root.GetProperty("font_size").GetInt32();
                bool isTranslating = root.TryGetProperty("is_translating", out var tp) && tp.GetBoolean();
                bool autoMode = root.TryGetProperty("auto_mode", out var amp) && amp.GetBoolean();
                string currentLyricsOriginal = root.TryGetProperty("current_lyrics_original", out var clo) ? clo.GetString() ?? "" : "";
                bool isTranslatedActive = root.TryGetProperty("is_translated_active", out var ita) && ita.GetBoolean();
                string currentLanguage = root.TryGetProperty("current_language", out var cl) ? cl.GetString() ?? "original" : "original";

                Dispatcher.Invoke(() => {
                    currentAppStatus = status;
                    //double scale = fontSize / 26.0;
                    //MainBorder.LayoutTransform = new ScaleTransform(scale, scale);
                    LblTranslating.Visibility = isTranslating ? Visibility.Visible : Visibility.Collapsed;
                    // O servidor é a fonte da verdade do modo Auto (persiste entre músicas),
                    // então os dois toggles (tela idle e painel lateral) sempre refletem o broadcast.
                    BtnAutoSide.IsChecked = autoMode;

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
                        FullLyricsList.ItemsSource = null;
                        LoadingSpinner.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        LblSongTitle.Visibility = Visibility.Visible;
                        HomeControls.Visibility = Visibility.Collapsed;
                        if (root.TryGetProperty("song", out var m) && root.TryGetProperty("artist", out var a))
                        {
                            LblSongTitle.Text = m.GetString() ?? "";
                            LblArtistName.Text = a.GetString() ?? "...";
                        }
                        if (root.TryGetProperty("cover_art", out var c))
                        {
                            string coverUrl = c.GetString() ?? "";
                            if (coverUrl != currentCoverUrl) { currentCoverUrl = coverUrl; LoadCoverArt(coverUrl); }
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
                        LblPrevious.Text = root.GetProperty("previous_lyrics").GetString() ?? "";
                        LblNext.Text = root.GetProperty("next_lyrics").GetString() ?? "";
                        lastCurrentLyricsOriginal = currentLyricsOriginal;
                        lastIsTranslatedActive = isTranslatedActive;
                        RefreshOriginalLyricsDisplay();
                    }
                    if (root.TryGetProperty("full_lyrics", out var full))
                    {
                        FullLyricsList.ItemsSource = JsonSerializer.Deserialize<List<LyricLine>>(full.GetRawText());
                    }

                    if (!isResizing) UpdateVisualState(isGhostMode);
                });
            }
            catch (Exception) { }
        }

        private void LoadCoverArt(string? url)
        {
            try
            {
                if (string.IsNullOrEmpty(url))
                {
                    string logoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "logo.png");
                    if (File.Exists(logoPath)) AlbumCoverImg.Source = new BitmapImage(new Uri(logoPath));
                    return;
                }

                BitmapImage bitmap = new();
                bitmap.BeginInit(); bitmap.UriSource = new Uri(url, UriKind.Absolute); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit();
                AlbumCoverImg.Source = bitmap;
            }
            catch { }
        }

        private async void SendCommand(string action, string? lang = null, string? artist = null, string? song = null, double? time = null)
        {
            try
            {
                if (_webSocket != null && _webSocket.State == WebSocketState.Open)
                {
                    var p = new { action, lang, artist, song, time };
                    await _webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p))), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch (Exception) { }
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
            KillPythonServer();
            Application.Current.Shutdown();
        }

        private void BtnSearchShow_Click(object sender, RoutedEventArgs e) { SearchInputPanel.Visibility = Visibility.Visible; }

        private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                BtnManualSearch_Execute(sender, e);
            }
        }

        private void BtnManualSearch_Execute(object sender, RoutedEventArgs e) { if (!string.IsNullOrWhiteSpace(TxtArtist.Text) && !string.IsNullOrWhiteSpace(TxtSong.Text)) { SendCommand("MANUAL_SEARCH", null, TxtArtist.Text, TxtSong.Text); SearchInputPanel.Visibility = Visibility.Collapsed; } }
        private void BtnManualSync_Toggle(object sender, RoutedEventArgs e) { isManualSyncMode = !isManualSyncMode; FullLyricsList.Visibility = isManualSyncMode ? Visibility.Visible : Visibility.Collapsed; LyricsNormalView.Visibility = isManualSyncMode ? Visibility.Collapsed : Visibility.Visible; BtnMenu.IsChecked = false; }
        private void FullLyricsList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (FullLyricsList.SelectedItem is LyricLine s) { SendCommand("SET_SYNC_TIME", null, null, null, s.Timestamp); isManualSyncMode = false; FullLyricsList.Visibility = Visibility.Collapsed; LyricsNormalView.Visibility = Visibility.Visible; FullLyricsList.SelectedItem = null; } }
        private void BtnListen_Click(object sender, RoutedEventArgs e) { SendCommand("LISTEN"); }
        private void SendMediaKey(byte virtualKey)
        {
            keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
            keybd_event(virtualKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        private void BtnPrevTrack_Click(object sender, RoutedEventArgs e) { SendMediaKey(VK_MEDIA_PREV_TRACK); }
        private void BtnNextTrack_Click(object sender, RoutedEventArgs e) { SendMediaKey(VK_MEDIA_NEXT_TRACK); }
        private void BtnAuto_Click(object sender, RoutedEventArgs e)
        {
            // O próximo broadcast do servidor confirma/corrige o estado real.
            SendCommand("AUTO_TOGGLE");
        }
        private void BtnReset_Click(object sender, RoutedEventArgs e) { SendCommand("RESET"); BtnMenu.IsChecked = false; }
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
        private void BtnSearchCancel_Click(object sender, RoutedEventArgs e) { SearchInputPanel.Visibility = Visibility.Collapsed; }
        private void BtnStartHelp_Click(object sender, RoutedEventArgs e) { OpenHelpScreen(); }

        private void OpenHelpScreen()
        {
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
        private void BtnHelpClose_Click(object? sender, RoutedEventArgs? e) { File.WriteAllText(helpFilePath, ChkDontShowAgain.IsChecked == true ? "skip" : "show"); HelpOverlay.Visibility = Visibility.Collapsed; Width = preHelpWidth; Height = preHelpHeight; Left = (SystemParameters.PrimaryScreenWidth - Width) / 2; Top = (SystemParameters.PrimaryScreenHeight - Height) / 2; }

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
                catch { }
            }

            HelpTextContainer.ItemsSource = boxes;
            string img = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", $"help{helpIndex}.png");
            if (File.Exists(img)) HelpImage.Source = new BitmapImage(new Uri(img));
            else HelpImage.Source = null;
        }
        private void BtnDonate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://www.buymeacoffee.com/juliocax") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Não foi possível abrir o link: " + ex.Message);
            }
        }

        private void BtnReleaseNotes_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://buymeacoffee.com/juliocax/frontline-lyrics-1-1-0") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Não foi possível abrir o link: " + ex.Message);
            }
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