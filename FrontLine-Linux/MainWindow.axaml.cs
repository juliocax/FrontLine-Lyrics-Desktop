using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.WebSockets;
using System.Net.Http;
using Avalonia.Media.Imaging;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace FrontLine_Linux;

public partial class MainWindow : Window
{
    private sealed class ManualSearchEntry
    {
        public string Artist { get; set; } = "";
        public string Song { get; set; } = "";
        public override string ToString() => $"{Song}  —  {Artist}";
    }
    private readonly ObservableCollection<ManualSearchEntry> _searchHistory = new();
    private bool _handlingHistorySelection;
    private bool _loadingPreferences;
    private sealed class UserPreferences
    {
        public UserPreferences() { }
        public double Opacity { get; set; } = 0.4;
        public double FontSize { get; set; } = 29;
        public int VisibleLines { get; set; } = 3;
        public string LyricColor { get; set; } = "#FFD132";
        public bool ShowOriginal { get; set; }
        public string Language { get; set; } = "en";
    }
    private static string PreferencesFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FrontLineLyrics", "linux_settings.json");
    private void SavePreferences()
    {
        if (_loadingPreferences || OpacitySlider is null || FontSizeSlider is null) return;
        try
        {
            var settings = new UserPreferences
            {
                Opacity = _backgroundOpacity,
                FontSize = FontSizeSlider.Value,
                VisibleLines = _visibleLineCount,
                LyricColor = (LyricColorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "#FFD132",
                ShowOriginal = _showOriginal,
                Language = _appLanguage
            };
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesFile)!);
            var temporary = PreferencesFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
            File.Move(temporary, PreferencesFile, true);
        }
        catch (Exception ex) { Debug.WriteLine($"Settings save: {ex.Message}"); }
    }
    private void LoadPreferences()
    {
        if (!File.Exists(PreferencesFile)) return;
        try
        {
            var settings = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(PreferencesFile));
            if (settings is null) return;
            _loadingPreferences = true;
            _backgroundOpacity = Math.Clamp(settings.Opacity, 0, 1);
            OpacitySlider.Value = _backgroundOpacity;
            FontSizeSlider.Value = Math.Clamp(settings.FontSize, 14, 70);
            _visibleLineCount = settings.VisibleLines is 1 or 3 or 5 ? settings.VisibleLines : 3;
            foreach (var item in LinesCombo.Items.OfType<ComboBoxItem>())
                if (item.Tag?.ToString() == _visibleLineCount.ToString()) { LinesCombo.SelectedItem = item; break; }
            foreach (var item in LyricColorCombo.Items.OfType<ComboBoxItem>())
                if (item.Tag?.ToString() == settings.LyricColor) { LyricColorCombo.SelectedItem = item; break; }
            _showOriginal = settings.ShowOriginal;
            ShowOriginalToggle.IsChecked = _showOriginal;
            _appLanguage = settings.Language is "en" or "pt" or "es" or "id" ? settings.Language : "en";
            _updatingSettings = true;
            foreach (var item in AppLanguageCombo.Items.OfType<ComboBoxItem>())
                if (item.Tag?.ToString() == _appLanguage) { AppLanguageCombo.SelectedItem = item; break; }
            _updatingSettings = false;
            _loadingPreferences = false;
            FontSizeSlider_Changed(this, null!);
            LyricColorCombo_Changed(this, null!);
            RefreshLyricView();
        }
        catch (Exception ex) { Debug.WriteLine($"Settings load: {ex.Message}"); }
        finally { _loadingPreferences = false; _updatingSettings = false; }
    }
    private static string HistoryFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FrontLineLyrics", "linux_manual_search_history.json");

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private string _status = "IDLE";
    private bool _connected, _searchOpen, _settingsOpen, _hover = true, _autoSyncing;
    private bool _autoMode;
    private CancellationTokenSource? _hoverExitDelay;
    private bool _movingWindow;
    private double _backgroundOpacity = 0.4;
    private string _settingsTab = "lyrics";
    private string _appLanguage = "en";
    private string _currentTranslation = "original";
    private bool _updatingSettings;
    private string? _pendingTranslation;
    private bool _translationStarted;
    private DateTime _translationRequestedUtc;
    private string? _translationUiError;
    private static readonly string ReleaseNotesUrl = "https://buymeacoffee.com/juliocax/frontline-lyrics-0-1-0";
    private static readonly string HelpUrl = "https://juliocax.github.io/FrontLine-Lyrics-Desktop/documentation_v0.1.0.html";
    private static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    private string _lastCover = "";
    private int _coverRequestVersion;
    private Bitmap? _coverBitmap;
    private readonly List<double> _lyricTimes = new();
    private readonly List<string> _lyricTexts = new();
    private int _currentIndex = -1;
    private int _visibleLineCount = 3;
    private bool _showOriginal;
    private bool _translatedActive;
    private bool _playerClock;
    private string _originalCurrent = "";

    public MainWindow()
    {
        InitializeComponent();
        SearchHistoryList.ItemsSource = _searchHistory;
        LoadSearchHistory();
        LoadPreferences();
        Opened += (_, _) => _ = ConnectionLoopAsync(_stop.Token);
        Closed += async (_, _) => await ShutdownConnectionAsync();
        UpdatePanels();
        ShowSettingsTab("lyrics");
        ApplyLanguage();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        UpdateResponsiveLayout();
    }

    private async Task ShutdownConnectionAsync()
    {
        // Normal close handshake (avoid abrupt Abort causing noisy backend logs).
        try
        {
            var socket = _socket;
            if (socket is { State: WebSocketState.Open })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,
                    "Application closing", timeout.Token);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"WebSocket closing: {ex.Message}"); }
        finally { _stop.Cancel(); }
    }

    private async Task ConnectionLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(new Uri("ws://127.0.0.1:8765"), token);
                _socket = socket;
                Dispatcher.UIThread.Post(() => SetConnected(true));
                await ReceiveAsync(socket, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception) { /* reconecta sem expor detalhes internos */ }
            finally
            {
                if (ReferenceEquals(_socket, socket)) _socket = null;
                if (!token.IsCancellationRequested)
                    Dispatcher.UIThread.Post(() => SetConnected(false));
            }
            try { await Task.Delay(2000, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            stream.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Text) return;
                if (stream.Length + result.Count > 512_000) return;
                stream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            using var doc = JsonDocument.Parse(stream.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
            var root = doc.RootElement;
            var status = ReadString(root, "status") ?? "IDLE";
            var title = ReadString(root, "song") ?? "";
            var artist = ReadString(root, "artist") ?? "";
            var previous = ReadString(root, "previous_lyrics") ?? "";
            var current = ReadString(root, "current_lyrics") ?? "";
            var next = ReadString(root, "next_lyrics") ?? "";
            var cover = ReadString(root, "cover_art") ?? "";
            var currentLang = ReadString(root, "current_language") ?? "original";
            var translationError = ReadString(root, "translation_error");
            var isTranslating = root.TryGetProperty("is_translating", out var translatingElement)
                && translatingElement.ValueKind == JsonValueKind.True;
            var index = root.TryGetProperty("current_lyric_index", out var idxElement) && idxElement.TryGetInt32(out int idx) ? idx : -1;
            var playerClock = root.TryGetProperty("player_clock", out var clockValue) && clockValue.ValueKind == JsonValueKind.True;
            var translatedActive = root.TryGetProperty("is_translated_active", out var activeElement) && activeElement.ValueKind == JsonValueKind.True;
            var originalCurrent = ReadString(root, "current_lyrics_original") ?? "";
            List<(double timestamp, string text)>? lines = null;
            if (root.TryGetProperty("full_lyrics", out var full) && full.ValueKind == JsonValueKind.Array)
            {
                lines = new();
                foreach (var line in full.EnumerateArray())
                {
                    if (!line.TryGetProperty("timestamp", out var ts) || !ts.TryGetDouble(out var t)) continue;
                    if (!double.IsFinite(t) || t < 0) continue;
                    lines.Add((t, ReadString(line, "text") ?? ""));
                }
            }
            var auto = root.TryGetProperty("auto_mode", out var autoValue) && autoValue.ValueKind == JsonValueKind.True;
            Dispatcher.UIThread.Post(() =>
            {
                _currentIndex = index;
                _playerClock = playerClock;
                _translatedActive = translatedActive;
                _originalCurrent = originalCurrent;
                if (lines is not null)
                {
                    _lyricTimes.Clear(); _lyricTexts.Clear();
                    foreach (var line in lines) { _lyricTimes.Add(line.timestamp); _lyricTexts.Add(line.text); }
                }
                _status = status;
                _currentTranslation = currentLang;
                if (_pendingTranslation is not null)
                {
                    if (isTranslating) _translationStarted = true;
                    bool success = currentLang == _pendingTranslation && !isTranslating;
                    bool failed = _translationStarted && !isTranslating &&
                                  translationError == _pendingTranslation;
                    bool timedOut = DateTime.UtcNow - _translationRequestedUtc > TimeSpan.FromSeconds(90);
                    if (success || failed || timedOut)
                    {
                        _translationUiError = failed || timedOut ? _pendingTranslation : null;
                        _pendingTranslation = null;
                        _translationStarted = false;
                    }
                }
                // Nunca forçar o seletor ao idioma antigo durante uma tradução em andamento.
                if (_pendingTranslation is null && TranslationCombo is not null &&
                    TranslationCombo.SelectedItem is ComboBoxItem sel && sel.Tag?.ToString() != currentLang)
                {
                    _updatingSettings = true;
                    foreach (var entry in TranslationCombo.Items.OfType<ComboBoxItem>())
                        if (entry.Tag?.ToString() == currentLang) { TranslationCombo.SelectedItem = entry; break; }
                    _updatingSettings = false;
                }
                UpdateTranslationFeedback();
                _autoMode = auto;
                if (_lastCover != cover)
                {
                    _lastCover = cover;
                    _ = LoadCoverAsync(cover);
                }
                SongText.Text = title;
                ArtistText.Text = artist;
                PreviousText.Text = previous;
                CurrentText.Text = current;
                NextText.Text = next;
                RefreshLyricView();
                UpdateLoadingLabel();
                _autoSyncing = true;
                AutoSwitch.IsChecked = auto;
                LoadingAutoSwitch.IsChecked = auto;
                _autoSyncing = false;
                UpdatePanels();
            });
        }
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private void SetConnected(bool connected)
    {
        _connected = connected;
        MenuListenButton.IsEnabled = connected;
        ListenButton.IsEnabled = connected;
        ClearButton.IsEnabled = connected;
    }

    private void UpdateResponsiveLayout()
    {
        if (LyricsPanel is null || SettingsContentGrid is null) return;
        double w = Math.Max(550, Bounds.Width);
        double h = Math.Max(275, Bounds.Height);
        double factor = Math.Clamp(Math.Min(w / 1100.0, h / 570.0), 0.58, 1.0);
        MenuLogo.Width = 260 * factor;
        MenuLogo.Height = 250 * factor;
        LoadingLogo.Width = 220 * factor;
        LoadingLogo.Height = 220 * factor;
        // Amplia a capa quando há espaço, mas mantém distância dos controles.
        CoverPanel.MaxWidth = Math.Min(320, Math.Max(155, w * 0.29));
        CoverPanel.MaxHeight = Math.Min(360, Math.Max(125, h - 142));
        // A tipografia das letras NÃO é escalada com a janela.
        double contentHeight = Math.Max(75, h - 170);
        LyricsSettingsScroll.MaxHeight = contentHeight;
        TranslateSettingsScroll.MaxHeight = contentHeight;
        GeneralSettingsScroll.MaxHeight = contentHeight;
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        var lyrics = _status == "SYNCED";
        var loading = _status is "LISTENING" or "SEARCHING";
        MenuPanel.IsVisible = !lyrics && !loading;
        LoadingPanel.IsVisible = loading;
        LyricsPanel.IsVisible = lyrics;
        if (LyricsTranslationBanner is not null)
            LyricsTranslationBanner.IsVisible = lyrics && _pendingTranslation is not null;
        SearchPanel.IsVisible = _searchOpen;
        SettingsPanel.IsVisible = _settingsOpen;
        // Em modo somente letra: imagem e botões somem, mas texto nunca recebe Opacity.
        var showingControls = _hover || _searchOpen || _settingsOpen || !lyrics;
        ShazamSyncTools.IsVisible = lyrics && showingControls && !_playerClock && _lyricTimes.Count > 0;
        CoverPanel.IsVisible = showingControls;
        LyricsControls.IsVisible = showingControls;
        // Sem mouse: ocupa toda a largura, sem reservar coluna para a capa.
        LyricsPanel.ColumnDefinitions[0].Width = showingControls
            ? new GridLength(Math.Clamp(Bounds.Width * 0.24, 125, 250)) : new GridLength(0);
        LyricsPanel.ColumnDefinitions[1].Width = showingControls
            ? new GridLength(1, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        LyricsPanel.ColumnSpacing = showingControls ? (Bounds.Width < 750 ? 8 : 24) : 0;
        LyricsPanel.Margin = showingControls ? new Thickness(Bounds.Width < 750 ? 14 : 28, 52, Bounds.Width < 750 ? 14 : 28, 18) : new Thickness(12);
        ResizeGrip.IsVisible = showingControls || _settingsOpen || _searchOpen;
        ResizeHint.IsVisible = showingControls || _settingsOpen || _searchOpen;
        TopControls.IsVisible = showingControls && !_settingsOpen;
        WindowBackground.Background = new SolidColorBrush(Color.FromArgb(
            (byte)(255 * (showingControls ? 1.0 : 0.0)), 9, 9, 9));
        // O contorno nunca aparece sobre o vídeo no modo somente letras.
        WindowBackground.BorderThickness = new Thickness(0);
        LyricsBackdrop.Background = new SolidColorBrush(Color.FromArgb(
            (byte)(255 * (showingControls ? 0.0 : _backgroundOpacity)), 0, 0, 0));
    }

    // Arraste em qualquer área não interativa: menu, listening e letras.
    private void RefreshLyricView()
    {
        if (CurrentText is null) return;
        PreviousText.IsVisible = _visibleLineCount >= 3;
        NextText.IsVisible = _visibleLineCount >= 3;
        Previous2Text.IsVisible = _visibleLineCount == 5;
        Next2Text.IsVisible = _visibleLineCount == 5;
        Previous2Text.Text = _currentIndex >= 2 && _currentIndex - 2 < _lyricTexts.Count ? _lyricTexts[_currentIndex - 2] : "";
        Next2Text.Text = _currentIndex >= 0 && _currentIndex + 2 < _lyricTexts.Count ? _lyricTexts[_currentIndex + 2] : "";
        OriginalText.Text = _originalCurrent;
        OriginalText.IsVisible = _showOriginal && _translatedActive && !string.IsNullOrWhiteSpace(_originalCurrent);
    }

    private void LinesCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (LinesCombo?.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var count))
        {
            _visibleLineCount = count;
            RefreshLyricView();
            SavePreferences();
        }
    }
    private void ShowOriginal_Changed(object? sender, RoutedEventArgs e)
    {
        _showOriginal = ShowOriginalToggle?.IsChecked == true;
        RefreshLyricView();
        SavePreferences();
    }
    private async Task ShiftSyncAsync(int delta)
    {
        if (_playerClock || _currentIndex < 0 || _lyricTimes.Count == 0) return;
        var target = Math.Clamp(_currentIndex + delta, 0, _lyricTimes.Count - 1);
        if (target == _currentIndex) return;
        // Ajusta referência temporal via protocolo existente, sem controlar o player de vídeo.
        await SendAsync(new { action = "SET_SYNC_TIME", time = _lyricTimes[target] });
    }
    private async void SyncPrevious_Click(object? sender, RoutedEventArgs e) => await ShiftSyncAsync(-1);
    private async void SyncNext_Click(object? sender, RoutedEventArgs e) => await ShiftSyncAsync(1);

    private void Root_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _searchOpen) return;
        var visual = e.Source as Visual;
        while (visual is not null && !ReferenceEquals(visual, Root))
        {
            if (visual is Button or ToggleSwitch or Slider or TextBox or ComboBox or ScrollViewer)
                return;
            visual = visual.GetVisualParent();
        }
        MoveWindow(e);
    }

    private void Root_PointerEntered(object? sender, PointerEventArgs e)
    {
        _hoverExitDelay?.Cancel();
        if (!_hover) { _hover = true; UpdatePanels(); }
    }

    private async void Root_PointerExited(object? sender, PointerEventArgs e)
    {
        if (_movingWindow) return;
        _hoverExitDelay?.Cancel();
        // Cada evento de saída cria seu próprio cancelamento. O evento de
        // entrada pode cancelar o atraso, mas somente o dono o descarta.
        var delay = new CancellationTokenSource();
        _hoverExitDelay = delay;
        try
        {
            await Task.Delay(180, delay.Token);
            if (!_movingWindow && !delay.IsCancellationRequested)
            {
                _hover = false;
                UpdatePanels();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_hoverExitDelay, delay))
                _hoverExitDelay = null;
            delay.Dispose();
        }
    }

    private void LyricsBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Arrastar pelo texto / centro não interfere nos botões, que ficam fora deste Border.
        if (_settingsOpen || _searchOpen || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        MoveWindow(e);
    }

    private void MoveWindow(PointerPressedEventArgs e)
    {
        if (_movingWindow) return;
        _hoverExitDelay?.Cancel();
        _movingWindow = true;
        try { BeginMoveDrag(e); }
        finally { _movingWindow = false; }
        e.Handled = true;
    }

    private void DragHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) MoveWindow(e);
    }

    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(WindowEdge.SouthEast, e);
    }

    private async Task LoadCoverAsync(string value)
    {
        var version = ++_coverRequestVersion;
        Bitmap? bitmap = null;
        try
        {
            byte[]? bytes = null;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                bytes = await CoverHttp.GetByteArrayAsync(uri, _stop.Token);
            else if (uri is not null && uri.IsFile && File.Exists(uri.LocalPath))
            {
                var info = new FileInfo(uri.LocalPath);
                if (info.Length > 0 && info.Length < 5_000_000)
                    bytes = await File.ReadAllBytesAsync(uri.LocalPath, _stop.Token);
            }
            else if (Path.IsPathFullyQualified(value) && File.Exists(value))
            {
                var info = new FileInfo(value);
                if (info.Length > 0 && info.Length < 5_000_000)
                    bytes = await File.ReadAllBytesAsync(value, _stop.Token);
            }
            else if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var comma = value.IndexOf(',');
                if (comma > 0 && value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                    bytes = Convert.FromBase64String(value[(comma + 1)..]);
            }
            else if (!string.IsNullOrWhiteSpace(value) && value.Length < 7_000_000)
            {
                // Backend pode devolver JPEG/PNG diretamente em base64, sem data:image/.
                var raw = value.Trim();
                try { bytes = Convert.FromBase64String(raw); }
                catch (FormatException) { /* formato de capa ainda não reconhecido */ }
            }
            if (bytes is { Length: > 0 and < 5_000_000 })
            {
                using var stream = new MemoryStream(bytes);
                bitmap = new Bitmap(stream);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Falha ao carregar cover_art: {ex.Message}"); }

        var candidate = bitmap;
        Dispatcher.UIThread.Post(() =>
        {
            if (_stop.IsCancellationRequested || version != _coverRequestVersion)
            {
                candidate?.Dispose();
                return;
            }
            _coverBitmap?.Dispose();
            _coverBitmap = candidate;
            CoverImage.Source = candidate;
            CoverImage.IsVisible = candidate is not null;
            CoverFallback.IsVisible = candidate is null;
        });
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F10)
        {
            // Recuperação para GNOME/Wayland caso não seja possível arrastar sem moldura.
            WindowDecorations = WindowDecorations == Avalonia.Controls.WindowDecorations.None ? Avalonia.Controls.WindowDecorations.Full : Avalonia.Controls.WindowDecorations.None;
            e.Handled = true;
        }
    }

    private async Task SendAsync(object payload)
    {
        await _sendLock.WaitAsync();
        try
        {
            var ws = _socket;
            if (ws?.State != WebSocketState.Open) return;
            var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, _stop.Token);
        }
        catch (Exception) when (!_stop.IsCancellationRequested) { Dispatcher.UIThread.Post(() => SetConnected(false)); }
        finally { _sendLock.Release(); }
    }
    private async void Listen_Click(object? sender, RoutedEventArgs e) => await SendAsync(new { action = "LISTEN" });
    private async void Clear_Click(object? sender, RoutedEventArgs e) => await SendAsync(new { action = "RESET" });
    private async void AutoSwitch_Changed(object? sender, RoutedEventArgs e)
    {
        if (_autoSyncing) return;
        var on = sender is ToggleSwitch toggle && toggle.IsChecked == true;
        await SendAsync(new { action = "AUTO_SET", on });
    }
    private async void PreviousTrack_Click(object? sender, RoutedEventArgs e) => await PlayerctlAsync("previous");
    private async void NextTrack_Click(object? sender, RoutedEventArgs e) => await PlayerctlAsync("next");
    private static async Task PlayerctlAsync(string operation)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("playerctl", operation) { UseShellExecute = false, CreateNoWindow = true });
            if (process is not null) await process.WaitForExitAsync();
        }
        catch (Exception) { /* ausência de playerctl ou player compatível */ }
    }
    private void OpenSearch_Click(object? sender, RoutedEventArgs e) { _searchOpen = true; UpdatePanels(); }
    private void CloseSearch_Click(object? sender, RoutedEventArgs e) { _searchOpen = false; UpdatePanels(); }
    private async void DoSearch_Click(object? sender, RoutedEventArgs e)
    {
        var artist = SearchArtist.Text?.Trim();
        var song = SearchSong.Text?.Trim();
        if (string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(song)) return;
        await ExecuteManualSearchAsync(artist, song);
    }
    private void SearchInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var artist = SearchArtist.Text?.Trim();
        var song = SearchSong.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(song))
            _ = ExecuteManualSearchAsync(artist, song);
    }
    private async Task ExecuteManualSearchAsync(string artist, string song)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(song)) return;
        AddSearchHistory(artist, song);
        _searchOpen = false;
        UpdatePanels();
        await SendAsync(new { action = "MANUAL_SEARCH", artist, song });
    }
    private void LoadSearchHistory()
    {
        try
        {
            if (!File.Exists(HistoryFile)) return;
            var rows = JsonSerializer.Deserialize<List<ManualSearchEntry>>(File.ReadAllText(HistoryFile));
            if (rows is null) return;
            foreach (var row in rows.Take(50))
                if (!string.IsNullOrWhiteSpace(row.Artist) && !string.IsNullOrWhiteSpace(row.Song))
                    _searchHistory.Add(row);
        }
        catch (Exception ex) { Debug.WriteLine($"History load: {ex.Message}"); }
    }
    private void SaveSearchHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
            File.WriteAllText(HistoryFile, JsonSerializer.Serialize(_searchHistory.ToList()));
        }
        catch (Exception ex) { Debug.WriteLine($"History save: {ex.Message}"); }
    }
    private void AddSearchHistory(string artist, string song)
    {
        var old = _searchHistory.FirstOrDefault(x =>
            string.Equals(x.Artist, artist, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Song, song, StringComparison.OrdinalIgnoreCase));
        if (old is not null) _searchHistory.Remove(old);
        _searchHistory.Insert(0, new ManualSearchEntry { Artist = artist, Song = song });
        while (_searchHistory.Count > 50) _searchHistory.RemoveAt(_searchHistory.Count - 1);
        SaveSearchHistory();
    }
    private void SearchHistory_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_handlingHistorySelection || SearchHistoryList.SelectedItem is not ManualSearchEntry row) return;
        _handlingHistorySelection = true;
        SearchHistoryList.SelectedItem = null;
        _handlingHistorySelection = false;
        SearchArtist.Text = row.Artist;
        SearchSong.Text = row.Song;
        _ = ExecuteManualSearchAsync(row.Artist, row.Song);
    }
    private void ClearSearchHistory_Click(object? sender, RoutedEventArgs e)
    {
        _searchHistory.Clear();
        SaveSearchHistory();
    }

    private void Settings_Click(object? sender, RoutedEventArgs e) { _settingsOpen = true; UpdatePanels(); UpdateTranslationFeedback(); }
    private void CloseSettings_Click(object? sender, RoutedEventArgs e) { _settingsOpen = false; UpdatePanels(); UpdateTranslationFeedback(); }
    private void OpacitySlider_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (OpacitySlider is null) return;
        _backgroundOpacity = OpacitySlider.Value;
        UpdatePanels();
        SavePreferences();
    }
    private void ShowSettingsTab(string tab)
    {
        _settingsTab = tab;
        if (LyricsSettingsContent is null) return;
        LyricsSettingsContent.IsVisible = tab == "lyrics";
        TranslateSettingsScroll.IsVisible = tab == "translate";
        GeneralSettingsScroll.IsVisible = tab == "general";
        SettingsLyricsTab.Background = new SolidColorBrush(Color.Parse(tab == "lyrics" ? "#51427B" : "#202020"));
        SettingsTranslateTab.Background = new SolidColorBrush(Color.Parse(tab == "translate" ? "#51427B" : "#202020"));
        SettingsGeneralTab.Background = new SolidColorBrush(Color.Parse(tab == "general" ? "#51427B" : "#202020"));
    }
    private void SettingsLyricsTab_Click(object? sender, RoutedEventArgs e) => ShowSettingsTab("lyrics");
    private void SettingsTranslateTab_Click(object? sender, RoutedEventArgs e) => ShowSettingsTab("translate");
    private void SettingsGeneralTab_Click(object? sender, RoutedEventArgs e) => ShowSettingsTab("general");
    private void FontSizeSlider_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (CurrentText is null || FontSizeSlider is null) return;
        CurrentText.FontSize = FontSizeSlider.Value;
        PreviousText.FontSize = Math.Max(12, FontSizeSlider.Value * .61);
        NextText.FontSize = Math.Max(12, FontSizeSlider.Value * .61);
        SavePreferences();
    }
    private void LyricColorCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (CurrentText is null || LyricColorCombo?.SelectedItem is not ComboBoxItem item) return;
        var hex = item.Tag?.ToString() ?? "#FFD132";
        CurrentText.Foreground = new SolidColorBrush(Color.Parse(hex));
        SavePreferences();
    }
    private async void TranslationCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSettings || TranslationCombo?.SelectedItem is not ComboBoxItem item) return;
        var lang = item.Tag?.ToString() ?? "original";
        if (_pendingTranslation == lang) return;
        _translationUiError = null;
        _pendingTranslation = lang;
        _translationStarted = false;
        _translationRequestedUtc = DateTime.UtcNow;
        UpdateTranslationFeedback();
        await SendAsync(new { action = "TRANSLATE", lang });
    }
    private string LocalizedTranslationProgress()
    {
        var name = _pendingTranslation switch
        {
            "pt" => "Português", "fr" => "Français", "es" => "Español",
            "en" => "English", "romanized" => "Romanization", _ => _pendingTranslation ?? ""
        };
        return _appLanguage switch
        {
            "pt" => $"Traduzindo para {name}...",
            "es" => $"Traduciendo a {name}...",
            "id" => $"Menerjemahkan ke {name}...",
            _ => $"Translating to {name}..."
        };
    }

    private void UpdateTranslationFeedback()
    {
        if (TranslationNotice is null || LyricsTranslationBanner is null) return;
        var pending = _pendingTranslation is not null;
        var message = pending ? LocalizedTranslationProgress() : _translationUiError is null ? "" :
            _appLanguage switch
            {
                "pt" => "Não foi possível traduzir. A letra anterior foi mantida.",
                "es" => "No fue posible traducir. Se mantuvo la letra anterior.",
                "id" => "Terjemahan gagal. Lirik sebelumnya dipertahankan.",
                _ => "Translation failed. Previous lyrics were kept."
            };
        TranslationNotice.Text = message;
        LyricsTranslationText.Text = pending ? message : "";
        TranslationNoticeCard.IsVisible = pending || _translationUiError is not null;
        SettingsTranslationProgress.IsVisible = pending;
        LyricsTranslationBanner.IsVisible = pending && _status == "SYNCED" && !_settingsOpen;
    }

    private void AppLanguageCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSettings || AppLanguageCombo?.SelectedItem is not ComboBoxItem item) return;
        _appLanguage = item.Tag?.ToString() ?? "en";
        ApplyLanguage();
        SavePreferences();
    }
    private void ApplyTooltips()
    {
        foreach (var b in this.GetVisualDescendants().OfType<Control>())
        {
            var key = b.Tag?.ToString();
            if (key is null || !key.StartsWith("tip:", StringComparison.Ordinal)) continue;
            var name = key.Substring(4);
            var tip = LocalizedTip(name, _appLanguage);
            if (!string.IsNullOrEmpty(tip)) ToolTip.SetTip(b, tip);
        }
    }

    private static string LocalizedTip(string key, string lang)
    {
        string[] values = key switch
        {
            "listen" => new[] { "Listen to the playing song", "Ouvir a música em reprodução", "Escuchar la canción actual", "Dengarkan lagu yang diputar" },
            "clear" => new[] { "Clear current lyrics", "Limpar a letra atual", "Borrar la letra actual", "Hapus lirik saat ini" },
            "search" => new[] { "Search for a song manually", "Buscar uma música manualmente", "Buscar una canción manualmente", "Cari lagu secara manual" },
            "back" => new[] { "Return to previous screen", "Voltar à tela anterior", "Volver a la pantalla anterior", "Kembali ke layar sebelumnya" },
            "find" => new[] { "Find synchronized lyrics", "Buscar letra sincronizada", "Buscar letra sincronizada", "Cari lirik tersinkron" },
            "settings" => new[] { "Open settings", "Abrir configurações", "Abrir ajustes", "Buka pengaturan" },
            "close_settings" => new[] { "Close settings", "Fechar configurações", "Cerrar ajustes", "Tutup pengaturan" },
            "help" => new[] { "Open the tutorial", "Abrir o tutorial", "Abrir el tutorial", "Buka panduan" },
            "release" => new[] { "Read version 0.1.0 release notes", "Ler novidades da versão 0.1.0", "Leer novedades de la versión 0.1.0", "Baca catatan rilis versi 0.1.0" },
            "prev_track" => new[] { "Previous track", "Faixa anterior", "Pista anterior", "Lagu sebelumnya" },
            "next_track" => new[] { "Next track", "Próxima faixa", "Pista siguiente", "Lagu berikutnya" },
            "tab_lyrics" => new[] { "Lyric display settings", "Configurações das letras", "Ajustes de letras", "Pengaturan lirik" },
            "tab_translation" => new[] { "Translation settings", "Configurações da tradução", "Ajustes de traducción", "Pengaturan terjemahan" },
            "tab_general" => new[] { "General settings", "Configurações gerais", "Ajustes generales", "Pengaturan umum" },
            "sync_prev" => new[] { "Sync with previous line", "Sincronizar com a linha anterior", "Sincronizar con la línea anterior", "Sinkronkan ke baris sebelumnya" },
            "sync_next" => new[] { "Sync with next line", "Sincronizar com a próxima linha", "Sincronizar con la línea siguiente", "Sinkronkan ke baris berikutnya" },
            "minimize" => new[] { "Minimize window", "Minimizar janela", "Minimizar ventana", "Minimalkan jendela" },
            "close" => new[] { "Close the application", "Fechar o aplicativo", "Cerrar la aplicación", "Tutup aplikasi" },
            "auto" => new[] { "Automatically follow player", "Acompanhar player automaticamente", "Seguir el reproductor automáticamente", "Ikuti pemutar otomatis" },
            "clear_history" => new[] { "Clear manual search history", "Limpar histórico de buscas manuais", "Borrar historial de búsquedas manuales", "Hapus riwayat pencarian manual" },
            "show_original" => new[] { "Also show original lyric below translation", "Também mostrar a letra original abaixo da tradução", "Mostrar también la letra original debajo de la traducción", "Tampilkan juga lirik asli di bawah terjemahan" },
            _ => Array.Empty<string>()
        };
        if (values.Length == 0) return "";
        int index = lang switch { "pt" => 1, "es" => 2, "id" => 3, _ => 0 };
        return values[index];
    }

    private void ApplyLanguage()
    {
        if (SettingsHeader is null) return;
        var pt = _appLanguage == "pt";
        var es = _appLanguage == "es";
        var id = _appLanguage == "id";
        SettingsHeader.Text = pt ? "CONFIGURAÇÕES" : es ? "AJUSTES" : id ? "PENGATURAN" : "SETTINGS";
        SettingsLyricsTab.Content = pt ? "LETRAS" : es ? "LETRAS" : id ? "LIRIK" : "LYRICS";
        SettingsTranslateTab.Content = pt ? "TRADUÇÃO" : es ? "TRADUCCIÓN" : id ? "TERJEMAH" : "TRANSLATION";
        SettingsSubtitle.Text = pt ? "Deixe as letras com a sua cara" : es ? "Personaliza tu experiencia" : id ? "Sesuaikan pengalaman Anda" : "Make the lyrics yours";
        SettingsGeneralTab.Content = pt ? "GERAL" : es ? "GENERAL" : id ? "UMUM" : "GENERAL";
        OpacityLabel.Text = pt ? "OPACIDADE DO FUNDO" : es ? "OPACIDAD DEL FONDO" : id ? "OPASITAS LATAR" : "BACKGROUND OPACITY";
        FontSizeLabel.Text = pt ? "TAMANHO DA LETRA" : es ? "TAMAÑO DE LETRA" : id ? "UKURAN HURUF" : "LYRIC FONT SIZE";
        ColorLabel.Text = pt ? "COR DA LETRA ATUAL" : es ? "COLOR DE LA LETRA" : id ? "WARNA LIRIK" : "CURRENT LYRIC COLOR";
        TranslateLabel.Text = pt ? "TRADUÇÃO E ROMANIZAÇÃO" : es ? "TRADUCCIÓN Y ROMANIZACIÓN" : id ? "TERJEMAHAN DAN ROMANISASI" : "TRANSLATION AND ROMANIZATION";
        SearchTitle.Text = pt ? "BUSCA MANUAL" : es ? "BÚSQUEDA MANUAL" : id ? "PENCARIAN MANUAL" : "MANUAL SEARCH";
        SearchDescription.Text = pt ? "Encontre uma música por artista e título" : es ? "Busca por artista y título" : id ? "Cari lagu berdasarkan artis dan judul" : "Find a song by artist and title";
        SearchHistoryTitle.Text = pt ? "PESQUISAS RECENTES" : es ? "BÚSQUEDAS RECIENTES" : id ? "PENCARIAN TERAKHIR" : "RECENT SEARCHES";
        SearchHistoryHint.Text = pt ? "Clique para pesquisar esta música" : es ? "Haz clic para buscar esta canción" : id ? "Klik untuk mencari lagu ini" : "Click to search this song again";
        SearchHistoryClear.Content = pt ? "LIMPAR" : es ? "BORRAR" : id ? "HAPUS" : "CLEAR";
        SearchSubmit.Content = pt ? "BUSCAR" : es ? "BUSCAR" : id ? "CARI" : "SEARCH";
        SearchBack.Content = pt ? "VOLTAR" : es ? "VOLVER" : id ? "KEMBALI" : "BACK";
        SearchArtist.PlaceholderText = pt ? "Artista" : es ? "Artista" : id ? "Artis" : "Artist";
        SearchSong.PlaceholderText = pt ? "Música" : es ? "Canción" : id ? "Lagu" : "Song";
        LinesLabel.Text = pt ? "LINHAS VISÍVEIS" : es ? "LÍNEAS VISIBLES" : id ? "BARIS LIRIK" : "VISIBLE LYRIC LINES";
        LanguageLabel.Text = pt ? "IDIOMA DO APLICATIVO" : es ? "IDIOMA DE LA APLICACIÓN" : id ? "BAHASA APLIKASI" : "APP LANGUAGE";
        ShowOriginalToggle.Content = _appLanguage switch
        {
            "pt" => "Mostrar original abaixo da tradução",
            "es" => "Mostrar original debajo de la traducción",
            "id" => "Tampilkan lirik asli di bawah terjemahan",
            _ => "Show original below translation"
        };
        EnjoyingAppLabel.Text = pt ? "Gostando do app?" : es ? "¿Te gusta la app?" : id ? "Suka aplikasi ini?" : "Enjoying the app?";
        SupportLabel.Text = pt ? "Apoiar" : es ? "Apoyar" : id ? "Dukung" : "Support";
        RateLabel.Text = pt ? "Avaliar" : es ? "Calificar" : id ? "Nilai" : "Rate";
        FeedbackLabel.Text = pt ? "Opinião" : es ? "Opinión" : id ? "Masukan" : "Feedback";
        Avalonia.Controls.ToolTip.SetTip(SupportButton, pt ? "Apoiar o desenvolvimento no Buy Me a Coffee" : es ? "Apoyar el desarrollo en Buy Me a Coffee" : id ? "Dukung pengembangan di Buy Me a Coffee" : "Support development on Buy Me a Coffee");
        Avalonia.Controls.ToolTip.SetTip(RateButton, pt ? "Disponível após a publicação na loja Ubuntu" : es ? "Disponible después del lanzamiento en Ubuntu" : id ? "Tersedia setelah rilis di toko Ubuntu" : "Available after publication in the Ubuntu store");
        Avalonia.Controls.ToolTip.SetTip(FeedbackButton, pt ? "Aguardando endereço do formulário" : es ? "Esperando enlace al formulario" : id ? "Menunggu tautan formulir" : "Waiting for feedback form link");
        ApplyTooltips();
        UpdateLoadingLabel();
        UpdateTranslationFeedback();
    }
    private void UpdateLoadingLabel()
    {
        if (LoadingText is null) return;
        var searching = _status == "SEARCHING";
        LoadingText.Text = _appLanguage switch
        {
            "pt" => searching ? "Buscando letras..." : "Ouvindo...",
            "es" => searching ? "Buscando letras..." : "Escuchando...",
            "id" => searching ? "Mencari lirik..." : "Mendengarkan...",
            _ => searching ? "Searching lyrics..." : "Listening..."
        };
    }
    private static void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"Failed to open browser: {ex.Message}"); }
    }
    private void ReleaseNotes_Click(object? sender, RoutedEventArgs e) => OpenLink(ReleaseNotesUrl);
    private void Help_Click(object? sender, RoutedEventArgs e) => OpenLink(HelpUrl);
    private void Support_Click(object? sender, RoutedEventArgs e) => OpenLink("https://buymeacoffee.com/juliocax");

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
