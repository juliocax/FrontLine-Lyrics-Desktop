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
    private bool _loadingPreferences;
    private sealed class UserPreferences
    {
        public UserPreferences() { }
        public double Opacity { get; set; } = 0.4;
        public double FontSize { get; set; } = 29;
        public int VisibleLines { get; set; } = 3;
        public string Alignment { get; set; } = "center";
        public string LyricColor { get; set; } = "#FFD132";
        public string LyricFont { get; set; } = "Poppins";
        public bool Bold { get; set; } = true;
        public bool Italic { get; set; }
        public bool ShowOriginal { get; set; }
        public string Language { get; set; } = "en";
        public string Translation { get; set; } = "original";
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
                Alignment = _lyricAlignment,
                LyricColor = (LyricColorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "#FFD132",
                ShowOriginal = _showOriginal,
                Language = _appLanguage,
                Translation = _preferredTranslation,
                LyricFont = _lyricFont,
                Bold = _lyricBold,
                Italic = _lyricItalic
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
            _lyricAlignment = settings.Alignment is "left" or "center" or "right" ? settings.Alignment : "center";
            ApplyLyricAlignment();
            _showOriginal = settings.ShowOriginal;
            ShowOriginalToggle.IsChecked = _showOriginal;
            _lyricFont = string.IsNullOrWhiteSpace(settings.LyricFont) ? "Poppins" : settings.LyricFont;
            _lyricBold = settings.Bold;
            _lyricItalic = settings.Italic;
            LyricBoldToggle.IsChecked = _lyricBold;
            LyricItalicToggle.IsChecked = _lyricItalic;
            SetFontSelection(_lyricFont);
            ApplyLyricTypography();
            _appLanguage = settings.Language is "en" or "pt" or "es" or "id" ? settings.Language : "en";
            _updatingSettings = true;
            foreach (var item in AppLanguageCombo.Items.OfType<ComboBoxItem>())
                if (item.Tag?.ToString() == _appLanguage) { AppLanguageCombo.SelectedItem = item; break; }
            _preferredTranslation = settings.Translation is "original" or "romanized" or "en" or "es" or "fr" or "pt"
                ? settings.Translation : "original";
            SelectComboTag(TranslationCombo, _preferredTranslation);
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
    private string _preferredTranslation = "original";
    private string _autoTranslationKey = "";
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
    private string _lyricAlignment = "center";
    private bool _showOriginal;
    private string _lyricFont = "Poppins";
    private bool _lyricBold = true;
    private bool _lyricItalic;
    private bool _translatedActive;
    private bool _playerClock;
    private string _originalCurrent = "";

    public MainWindow()
    {
        InitializeComponent();
        SearchHistoryList.ItemsSource = _searchHistory;
        _loadingPreferences = true;
        PopulateFontChoices();
        _loadingPreferences = false;
        LoadSearchHistory();
        LoadPreferences();
        Opened += (_, _) => { _ = ConnectionLoopAsync(_stop.Token); _ = RunStartupDialogsAsync(); };
        Closed += async (_, _) => await ShutdownConnectionAsync();
        UpdatePanels();
        ShowSettingsTab("lyrics");
        ApplyLanguage();
        RefreshSegmentSelections();
        ApplyLyricAlignment();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        UpdateResponsiveLayout();
    }

    private async Task ShutdownConnectionAsync()
    {
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
                if (status != "SYNCED")
                {
                    _pendingTranslation = null;
                    _translationStarted = false;
                }
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
                if (TranslationCombo?.SelectedItem is ComboBoxItem selected &&
                    selected.Tag?.ToString() != _preferredTranslation)
                {
                    _updatingSettings = true;
                    SelectComboTag(TranslationCombo, _preferredTranslation);
                    _updatingSettings = false;
                }
                bool hasLyrics = status == "SYNCED" &&
                    (!string.IsNullOrWhiteSpace(current) || lines is { Count: > 0 });
                if (hasLyrics && _pendingTranslation is null && _preferredTranslation != currentLang)
                {
                    var key = $"{artist.Trim()}|{title.Trim()}|{_preferredTranslation}";
                    if (_autoTranslationKey != key)
                    {
                        _autoTranslationKey = key;
                        _translationUiError = null;
                        _pendingTranslation = _preferredTranslation;
                        _translationStarted = false;
                        _translationRequestedUtc = DateTime.UtcNow;
                        _ = SendAsync(new { action = "TRANSLATE", lang = _preferredTranslation });
                    }
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
        CoverPanel.MaxWidth = Math.Min(365, Math.Max(180, w * 0.33));
        CoverPanel.MaxHeight = Math.Min(405, Math.Max(145, h - 115));
        SettingsPanel.MaxHeight = 535;
        SettingsPanel.Width = 360;
        double contentHeight = 340;
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
        SearchPanel.IsVisible = _searchOpen;
        SettingsPopup.IsOpen = _settingsOpen;
        var showingControls = _hover || _searchOpen || _settingsOpen || (!lyrics && !loading);
        ShazamSyncTools.IsVisible = lyrics && showingControls && _lyricTimes.Count > 0;
        // Em LISTENING/SEARCHING: somente indicador e texto na tela sem hover.
        LoadingLogo.IsVisible = showingControls;
        LoadingControls.IsVisible = showingControls;
        LoadingPanel.ColumnDefinitions[0].Width = showingControls
            ? new GridLength(0.8, GridUnitType.Star) : new GridLength(0);
        LoadingPanel.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        CoverPanel.IsVisible = showingControls;
        LyricsControls.IsVisible = showingControls;
        LyricsPanel.ColumnDefinitions[0].Width = showingControls
            ? new GridLength(Math.Clamp(Bounds.Width * 0.27, 145, 290)) : new GridLength(0);
        LyricsPanel.ColumnDefinitions[1].Width = showingControls
            ? new GridLength(1, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        LyricsPanel.ColumnSpacing = showingControls ? (Bounds.Width < 750 ? 8 : 24) : 0;
        var rightPadding = Bounds.Width < 750 ? 14 : 28;
        LyricsPanel.Margin = showingControls
            ? new Thickness(Bounds.Width < 750 ? 14 : 28, 52, rightPadding, 18)
            : new Thickness(12);
        ResizeGrip.IsVisible = showingControls || _settingsOpen || _searchOpen;
        ResizeHint.IsVisible = showingControls || _settingsOpen || _searchOpen;
        TopControls.IsVisible = showingControls && !_settingsOpen;
        WindowBackground.Background = new SolidColorBrush(Color.FromArgb(
            (byte)(255 * (showingControls ? 1.0 : 0.0)), 9, 9, 9));
        WindowBackground.BorderThickness = new Thickness(0);
        LyricsBackdrop.Background = new SolidColorBrush(Color.FromArgb(
            (byte)(255 * (showingControls ? 0.0 : _backgroundOpacity)), 0, 0, 0));
    }

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

    private void RefreshSegmentSelections()
    {
        if (LinesSegment is null) return;
        HighlightSegment(LinesSegment, _visibleLineCount.ToString());
        HighlightSegment(AlignmentSegment, _lyricAlignment);
        HighlightSegment(TranslationSegment, _preferredTranslation);
        HighlightSegment(LanguageSegment, _appLanguage);
    }
    private static void HighlightSegment(Panel panel, string chosen)
    {
        foreach (var button in panel.Children.OfType<Button>())
        {
            var active = button.Tag?.ToString() == chosen;
            button.Background = new SolidColorBrush(Color.Parse(active ? "#59418B" : "#18141E"));
            button.BorderBrush = new SolidColorBrush(Color.Parse(active ? "#B49BFF" : "#34303C"));
            button.BorderThickness = new Thickness(active ? 2 : 1);
        }
    }
    private static void SelectComboTag(ComboBox combo, string tag)
    {
        foreach (var choice in combo.Items.OfType<ComboBoxItem>())
            if (choice.Tag?.ToString() == tag) { combo.SelectedItem = choice; return; }
    }
    private void LineChoice_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button) SelectComboTag(LinesCombo, button.Tag?.ToString() ?? "3");
    }
    private void AlignmentChoice_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var selected = button.Tag?.ToString() ?? "center";
        if (selected is not ("left" or "center" or "right")) return;
        _lyricAlignment = selected;
        ApplyLyricAlignment();
        RefreshSegmentSelections();
        SavePreferences();
    }
    private void ApplyLyricAlignment()
    {
        var alignment = _lyricAlignment switch
        {
            "left" => TextAlignment.Left,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Center
        };
        foreach (var item in new[] { Previous2Text, PreviousText, CurrentText, OriginalText, NextText, Next2Text })
            item.TextAlignment = alignment;
    }

    private void TranslationChoice_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button) SelectComboTag(TranslationCombo, button.Tag?.ToString() ?? "original");
        RefreshSegmentSelections();
    }
    private void LanguageChoice_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button) SelectComboTag(AppLanguageCombo, button.Tag?.ToString() ?? "en");
    }

    private void LinesCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (LinesCombo?.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var count))
        {
            _visibleLineCount = count;
            RefreshLyricView();
            RefreshSegmentSelections();
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
        if (_currentIndex < 0 || _lyricTimes.Count == 0) return;
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
        if (_settingsOpen && e.Source is Visual settingsSource)
        {
            for (Visual? element = settingsSource; element is not null; element = element.GetVisualParent())
                if (ReferenceEquals(element, SettingsPanel)) return;
        }
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
        var errors = new List<string>();
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNAP")))
        {
            try
            {
                var method = operation == "next" ? "Next" : "Previous";
                var info = new ProcessStartInfo("dbus-send")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                foreach (var arg in new[] {
                    "--session", "--print-reply", "--dest=org.mpris.MediaPlayer2.spotify",
                    "/org/mpris/MediaPlayer2", "org.mpris.MediaPlayer2.Player." + method })
                    info.ArgumentList.Add(arg);
                using var process = Process.Start(info);
                if (process != null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    var stderr = process.StandardError.ReadToEndAsync();
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync(timeout.Token);
                    await stdout;
                    if (process.ExitCode == 0) return;
                    errors.Add($"dbus-send {method}: exit={process.ExitCode}: {await stderr}");
                }
            }
            catch (Exception ex) { errors.Add("dbus-send: " + ex.Message); }
            // Don't call playerctl inside the snap: ListNames is blocked by AppArmor.
            WritePlayerCommandErrors(errors);
            return;
        }
        foreach (var args in new[] { $"--player=spotify {operation}", operation })
        {
            try
            {
                var info = new ProcessStartInfo("playerctl", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using var process = Process.Start(info);
                if (process is null) { errors.Add("playerctl não iniciou"); continue; }
                var stderr = process.StandardError.ReadToEndAsync();
                var stdout = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                await stdout;
                if (process.ExitCode == 0) return;
                errors.Add($"playerctl {args}: exit={process.ExitCode}: {await stderr}");
            }
            catch (Exception ex) { errors.Add($"playerctl {args}: {ex.Message}"); }
        }
        WritePlayerCommandErrors(errors);
    }
    private static void WritePlayerCommandErrors(List<string> errors)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrontLineLyrics");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "playerctl.log"),
                $"{DateTimeOffset.Now:O} {string.Join(" | ", errors)}{Environment.NewLine}");
        }
        catch { /*  */ }
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
    private void OpenHistoryEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ManualSearchEntry row }) return;
        SearchArtist.Text = row.Artist;
        SearchSong.Text = row.Song;
        _ = ExecuteManualSearchAsync(row.Artist, row.Song);
    }
    private void ClearSearchHistory_Click(object? sender, RoutedEventArgs e)
    {
        _searchHistory.Clear();
        SaveSearchHistory();
    }

    private void DeleteHistoryEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ManualSearchEntry entry })
        {
            _searchHistory.Remove(entry);
            SaveSearchHistory();
            e.Handled = true;
        }
    }

    private void Settings_Click(object? sender, RoutedEventArgs e) { _settingsOpen = true; UpdatePanels(); UpdateTranslationFeedback(); _ = RefreshSnapPermissionsStatusAsync(); }
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
        SettingsLyricsTab.Background = new SolidColorBrush(Color.Parse(tab == "lyrics" ? "#353535" : "#171717"));
        SettingsTranslateTab.Background = new SolidColorBrush(Color.Parse(tab == "translate" ? "#353535" : "#171717"));
        SettingsGeneralTab.Background = new SolidColorBrush(Color.Parse(tab == "general" ? "#353535" : "#171717"));
    }
    private void SettingsLyricsTab_Click(object? sender, RoutedEventArgs e) => ShowSettingsTab("lyrics");
    private void SettingsTranslateTab_Click(object? sender, RoutedEventArgs e) => ShowSettingsTab("translate");
    private void SettingsGeneralTab_Click(object? sender, RoutedEventArgs e) { ShowSettingsTab("general"); _ = RefreshSnapPermissionsStatusAsync(); }
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
        if (hex.StartsWith("grad:", StringComparison.Ordinal))
        {
            var stops = hex switch
            {
                "grad:sunset" => new[] { "#FFCC65", "#FE6387", "#B25DFF" },
                "grad:ocean" => new[] { "#6DE6FF", "#2874FF", "#A080FF" },
                "grad:aurora" => new[] { "#7AFBC5", "#60C9FF", "#D9A5FF" },
                _ => new[] { "#FE62BD", "#FFE78A", "#80FEF7" }
            };
            var brush = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative) };
            for (int i = 0; i < stops.Length; i++) brush.GradientStops.Add(new GradientStop(Color.Parse(stops[i]), (double)i / (stops.Length - 1)));
            CurrentText.Foreground = brush;
        }
        else CurrentText.Foreground = new SolidColorBrush(Color.Parse(hex));
        SavePreferences();
    }
    private sealed class FontItem
    {
        public string Name { get; set; } = "";
        public string Family { get; set; } = "";
        public override string ToString() => Name;
    }
    private void PopulateFontChoices()
    {
        if (LyricFontCombo is null) return;
        LyricFontCombo.Items.Add(new FontItem { Name = "Default (Poppins)", Family = "Poppins" });
        try
        {
            var uri = new Uri("avares://FrontLine-Linux/assets/fonts/font-manifest.json");
            using var stream = Avalonia.Platform.AssetLoader.Open(uri);
            var fonts = JsonSerializer.Deserialize<List<FontItem>>(stream);
            if (fonts is not null)
                foreach (var font in fonts.Where(x => !string.IsNullOrWhiteSpace(x.Family)))
                    LyricFontCombo.Items.Add(font);
        }
        catch (Exception ex) { Debug.WriteLine($"Font manifest not yet bundled: {ex.Message}"); }
        LyricFontCombo.SelectedIndex = 0;
    }
    private void SetFontSelection(string family)
    {
        for (int i = 0; i < LyricFontCombo.Items.Count; i++)
            if (LyricFontCombo.Items[i] is FontItem font && font.Family == family)
            {
                LyricFontCombo.SelectedIndex = i;
                return;
            }
        LyricFontCombo.SelectedIndex = 0;
    }
    private void ApplyLyricTypography()
    {
        if (CurrentText is null) return;
        var font = _lyricFont == "Default" ? new FontFamily("Poppins") : new FontFamily($"avares://FrontLine-Linux/assets/fonts/#{_lyricFont}");
        foreach (var text in new[] { Previous2Text, PreviousText, CurrentText, OriginalText, NextText, Next2Text })
        {
            text.FontFamily = font;
            text.FontStyle = _lyricItalic ? FontStyle.Italic : FontStyle.Normal;
        }
        CurrentText.FontWeight = _lyricBold ? FontWeight.Bold : FontWeight.Normal;
    }
    private void LyricFontCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (LyricFontCombo?.SelectedItem is not FontItem item) return;
        _lyricFont = item.Family;
        ApplyLyricTypography();
        SavePreferences();
    }
    private void LyricTypography_Changed(object? sender, RoutedEventArgs e)
    {
        _lyricBold = LyricBoldToggle.IsChecked == true;
        _lyricItalic = LyricItalicToggle.IsChecked == true;
        ApplyLyricTypography();
        SavePreferences();
    }

    private void TranslationCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSettings || TranslationCombo?.SelectedItem is not ComboBoxItem item) return;
        var lang = item.Tag?.ToString() ?? "original";
        if (_preferredTranslation == lang && _pendingTranslation == lang) return;
        _preferredTranslation = lang;
        _autoTranslationKey = "";
        _translationUiError = null;
        _pendingTranslation = null;
        _translationStarted = false;
        SavePreferences();
        RefreshSegmentSelections();
        if (_status != "SYNCED" ||
            (_lyricTexts.Count == 0 && string.IsNullOrWhiteSpace(CurrentText.Text)))
        {
            UpdateTranslationFeedback();
            return;
        }
        _autoTranslationKey = $"{ArtistText.Text?.Trim()}|{SongText.Text?.Trim()}|{lang}";
        if (_currentTranslation != lang)
        {
            _pendingTranslation = lang;
            _translationRequestedUtc = DateTime.UtcNow;
            _ = SendAsync(new { action = "TRANSLATE", lang });
        }
        UpdateTranslationFeedback();
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
        if (TranslationNotice is null) return;
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
        TranslationNoticeCard.IsVisible = pending || _translationUiError is not null;
        TranslationNotice.IsVisible = !pending && _translationUiError is not null;
        SettingsTranslationProgress.IsVisible = pending;
    }

    private void AppLanguageCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSettings || AppLanguageCombo?.SelectedItem is not ComboBoxItem item) return;
        _appLanguage = item.Tag?.ToString() ?? "en";
        ApplyLanguage();
        RefreshSegmentSelections();
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

    private void ApplySettingsChoiceTooltips()
    {
        foreach (var button in AlignmentSegment.Children.OfType<Button>())
        {
            var description = button.Tag?.ToString() switch
            {
                "left" => _appLanguage switch { "pt" => "Esquerda", "es" => "Izquierda", "id" => "Kiri", _ => "Left" },
                "right" => _appLanguage switch { "pt" => "Direita", "es" => "Derecha", "id" => "Kanan", _ => "Right" },
                _ => _appLanguage switch { "pt" => "Centralizado", "es" => "Centrado", "id" => "Tengah", _ => "Center" }
            };
            ToolTip.SetTip(button, description);
        }
        foreach (var button in LinesSegment.Children.OfType<Button>())
            ToolTip.SetTip(button, LocalizedTip("lines", _appLanguage) + ": " + button.Content);
        foreach (var button in TranslationSegment.Children.OfType<Button>())
            ToolTip.SetTip(button, LocalizedTip("translation", _appLanguage) + ": " + button.Content);
        foreach (var button in LanguageSegment.Children.OfType<Button>())
            ToolTip.SetTip(button, LocalizedTip("app_language", _appLanguage) + ": " + button.Content);
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
            "release" => new[] { "Read release notes", "Ler notas da versão", "Leer notas de la versión", "Baca catatan rilis" },
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
            "remove_history" => new[] { "Remove this search", "Remover esta pesquisa", "Eliminar esta búsqueda", "Hapus pencarian ini" },
            "open_history" => new[] { "Search this song", "Pesquisar esta música", "Buscar esta canción", "Cari lagu ini" },
            "show_original" => new[] { "Also show original lyric below translation", "Também mostrar a letra original abaixo da tradução", "Mostrar también la letra original debajo de la traducción", "Tampilkan juga lirik asli di bawah terjemahan" },
            "opacity" => new[] { "Adjust overlay background opacity", "Ajustar a opacidade do fundo", "Ajustar la opacidad del fondo", "Atur opasitas latar" },
            "font_size" => new[] { "Adjust lyric text size", "Ajustar o tamanho da letra", "Ajustar el tamaño de la letra", "Atur ukuran teks lirik" },
            "lines" => new[] { "Choose how many lyric lines appear", "Escolher quantas linhas aparecem", "Elegir cuántas líneas aparecen", "Pilih jumlah baris lirik" },
            "color" => new[] { "Choose text color or gradient", "Escolher a cor ou o degradê da letra", "Elegir el color o degradado de la letra", "Pilih warna atau gradasi lirik" },
            "font" => new[] { "Choose a custom lyric font", "Escolher uma fonte para a letra", "Elegir una fuente para la letra", "Pilih font khusus untuk lirik" },
            "translation" => new[] { "Choose translation or romanization", "Escolher tradução ou romanização", "Elegir traducción o romanización", "Pilih terjemahan atau romanisasi" },
            "app_language" => new[] { "Choose the interface language", "Escolher o idioma da interface", "Elegir el idioma de la interfaz", "Pilih bahasa antarmuka" },
            "bold" => new[] { "Display current lyrics in bold", "Exibir letra atual em negrito", "Mostrar la letra actual en negrita", "Tampilkan lirik dalam huruf tebal" },
            "italic" => new[] { "Display current lyrics in italics", "Exibir letra atual em itálico", "Mostrar la letra actual en cursiva", "Tampilkan lirik dalam huruf miring" },
            "telemetry" => new[] { "Toggle optional anonymous telemetry", "Ativar ou desativar telemetria anônima", "Activar o desactivar telemetría anónima", "Aktifkan atau nonaktifkan telemetri anonim" },
            "whatsnew" => new[] { "View version changes", "Ver novidades desta versão", "Ver novedades de esta versión", "Lihat perubahan versi ini" },
            "resize" => new[] { "Drag to resize the window", "Arraste para redimensionar a janela", "Arrastra para cambiar el tamaño", "Seret untuk mengubah ukuran jendela" },
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
        SettingsGeneralTab.Content = pt ? "GERAL" : es ? "GENERAL" : id ? "UMUM" : "GENERAL";
        OpacityLabel.Text = pt ? "OPACIDADE DO FUNDO" : es ? "OPACIDAD DEL FONDO" : id ? "OPASITAS LATAR" : "BACKGROUND OPACITY";
        FontSizeLabel.Text = pt ? "TAMANHO DA LETRA" : es ? "TAMAÑO DE LETRA" : id ? "UKURAN HURUF" : "LYRIC FONT SIZE";
        FontFamilyLabel.Text = pt ? "FONTE DA LETRA" : es ? "FUENTE DE LETRA" : id ? "FONT LIRIK" : "LYRIC FONT";
        LyricBoldToggle.Content = pt ? "Negrito" : es ? "Negrita" : id ? "Tebal" : "Bold";
        LyricItalicToggle.Content = pt ? "Itálico" : es ? "Cursiva" : id ? "Miring" : "Italic";
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
        AlignmentLabel.Text = pt ? "ALINHAMENTO" : es ? "ALINEACIÓN" : id ? "PERATAAN LIRIK" : "LYRIC ALIGNMENT";
        var listen = pt ? "OUVIR" : es ? "ESCUCHAR" : id ? "DENGARKAN" : "LISTEN";
        var search = pt ? "BUSCAR" : es ? "BUSCAR" : id ? "CARI" : "SEARCH";
        var clear = pt ? "LIMPAR" : es ? "BORRAR" : id ? "HAPUS" : "CLEAR";
        MenuListenLabel.Text = listen;
        LoadingListenLabel.Text = listen;
        LyricsListenLabel.Text = listen;
        MenuSearchLabel.Text = search;
        LoadingClearButton.Content = clear;
        ClearButton.Content = clear;
        LoadingAutoLabel.Text = LyricsAutoLabel.Text = "AUTO";
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
        Avalonia.Controls.ToolTip.SetTip(FeedbackButton, pt ? "Enviar opiniões pelo formulário" : es ? "Enviar comentarios mediante el formulario" : id ? "Kirim masukan melalui formulir" : "Send feedback through the form");
        ApplyTooltips();
        ApplySettingsChoiceTooltips();
        OpacityDescription.Text = pt ? "Veja o vídeo através do fundo" : es ? "Permite ver el video detrás" : id ? "Lihat video di balik lirik" : "See video through the background";
        FontSizeDescription.Text = pt ? "Ajuste a letra enquanto a observa" : es ? "Ajusta el texto mientras lo ves" : id ? "Atur ukuran sambil melihat lirik" : "Adjust the size while viewing the lyrics";
        RefreshSegmentSelections();
        UpdateLoadingLabel();
        UpdateTranslationFeedback();
        TelemetryHeading.Text = pt ? "ESTATÍSTICAS ANÔNIMAS" : es ? "ESTADÍSTICAS ANÓNIMAS" : id ? "STATISTIK ANONIM" : "ANONYMOUS SETTINGS STATISTICS";
        TelemetryDescription.Text = pt ? "Opcional. Envia preferências, versão e idioma do sistema ao Aptabase no máximo uma vez por dia. Nunca envia músicas, letras ou pesquisas."
            : es ? "Opcional. Envía ajustes, versión e idioma del sistema a Aptabase como máximo una vez al día. Nunca envía canciones, letras ni búsquedas."
            : id ? "Opsional. Mengirim pengaturan, versi dan bahasa sistem ke Aptabase paling banyak sekali sehari. Tidak mengirim lagu, lirik, atau pencarian."
            : "Optional. Sends settings, app version and system language to Aptabase at most once daily. Never sends songs, lyrics or searches.";
        SnapPermissionsHeader.Text = pt ? "PERMISSÕES DE MÚSICA E ÁUDIO" : es ? "PERMISOS DE MÚSICA Y AUDIO" : id ? "IZIN MUSIK DAN AUDIO" : "MUSIC AND AUDIO PERMISSIONS";
        SnapPermissionsDescription.Text = pt ? "Para reconhecer o som do computador e controlar o Spotify, o FrontLine precisa da sua autorização. Você pode mudar isso quando quiser."
            : es ? "Para reconocer el audio del equipo y controlar Spotify, FrontLine necesita tu permiso. Puedes cambiarlo cuando quieras."
            : id ? "Untuk mengenali suara komputer dan mengontrol Spotify, FrontLine memerlukan izin Anda."
            : "To recognize computer audio and control Spotify, FrontLine needs your permission. You can change it at any time.";
        OpenSnapPermissionsButton.Content = pt ? "Ver permissões e instruções" : es ? "Ver permisos e instrucciones" : id ? "Lihat izin dan panduan" : "View permissions and instructions";
        _ = RefreshSnapPermissionsStatusAsync();
        TelemetryToggle.Content = pt ? "Enviar estatísticas anônimas" : es ? "Enviar estadísticas anónimas" : id ? "Kirim statistik anonim" : "Share anonymous statistics";
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
    private void Feedback_Click(object? sender, RoutedEventArgs e) => OpenLink("https://forms.gle/VZnno2gPw5F1H5os5");

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
