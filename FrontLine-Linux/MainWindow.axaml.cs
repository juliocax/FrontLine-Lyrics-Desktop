using System;
using System.Diagnostics;
using System.IO;
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
using Avalonia.Threading;

namespace FrontLine_Linux;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private string _status = "IDLE";
    private bool _connected, _searchOpen, _settingsOpen, _hover = true, _autoSyncing;
    private bool _autoMode;
    private CancellationTokenSource? _hoverExitDelay;
    private bool _movingWindow;
    private double _backgroundOpacity = 0.4;
    private static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    private string _lastCover = "";
    private int _coverRequestVersion;
    private Bitmap? _coverBitmap;

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => _ = ConnectionLoopAsync(_stop.Token);
        Closed += (_, _) => { _stop.Cancel(); _socket?.Abort(); };
        UpdatePanels();
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
            var auto = root.TryGetProperty("auto_mode", out var autoValue) && autoValue.ValueKind == JsonValueKind.True;
            Dispatcher.UIThread.Post(() =>
            {
                _status = status;
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
                LoadingText.Text = status == "SEARCHING" ? "Searching lyrics..." : "Listening...";
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

    private void UpdatePanels()
    {
        var lyrics = _status == "SYNCED";
        var loading = _status is "LISTENING" or "SEARCHING";
        MenuPanel.IsVisible = !lyrics && !loading;
        LoadingPanel.IsVisible = loading;
        LyricsPanel.IsVisible = lyrics;
        SearchPanel.IsVisible = _searchOpen;
        SettingsPanel.IsVisible = _settingsOpen;
        // Em modo somente letra: imagem e botões somem, mas texto nunca recebe Opacity.
        var showingControls = _hover || _searchOpen || _settingsOpen || !lyrics;
        CoverPanel.IsVisible = showingControls;
        LyricsControls.IsVisible = showingControls;
        // Sem mouse: ocupa toda a largura, sem reservar coluna para a capa.
        LyricsPanel.ColumnDefinitions[0].Width = showingControls
            ? new GridLength(0.82, GridUnitType.Star) : new GridLength(0);
        LyricsPanel.ColumnDefinitions[1].Width = showingControls
            ? new GridLength(1.6, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        LyricsPanel.ColumnSpacing = showingControls ? 24 : 0;
        LyricsPanel.Margin = showingControls ? new Thickness(28) : new Thickness(12);
        ResizeGrip.IsVisible = showingControls;
        ResizeHint.IsVisible = showingControls;
        TopControls.IsVisible = showingControls;
        WindowBackground.Background = new SolidColorBrush(Color.FromArgb(
            (byte)(255 * (showingControls ? 1.0 : 0.0)), 9, 9, 9));
        // O contorno nunca aparece sobre o vídeo no modo somente letras.
        WindowBackground.BorderThickness = new Thickness(0);
        LyricsBackdrop.Background = new SolidColorBrush(Color.FromArgb(
            (byte)(255 * (showingControls ? 0.0 : _backgroundOpacity)), 0, 0, 0));
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
            else if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var comma = value.IndexOf(',');
                if (comma > 0 && value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                    bytes = Convert.FromBase64String(value[(comma + 1)..]);
            }
            else if (!string.IsNullOrWhiteSpace(value) && value.Length < 7_000_000)
            {
                var raw = value.Trim();
                try { bytes = Convert.FromBase64String(raw); }
                catch (FormatException) { /* */ }
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
        catch (Exception) { /* */ }
    }
    private void OpenSearch_Click(object? sender, RoutedEventArgs e) { _searchOpen = true; UpdatePanels(); }
    private void CloseSearch_Click(object? sender, RoutedEventArgs e) { _searchOpen = false; UpdatePanels(); }
    private async void DoSearch_Click(object? sender, RoutedEventArgs e)
    {
        var artist = SearchArtist.Text?.Trim();
        var song = SearchSong.Text?.Trim();
        if (string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(song)) return;
        _searchOpen = false; UpdatePanels();
        await SendAsync(new { action = "MANUAL_SEARCH", artist, song });
    }
    private void Settings_Click(object? sender, RoutedEventArgs e) { _settingsOpen = true; UpdatePanels(); }
    private void CloseSettings_Click(object? sender, RoutedEventArgs e) { _settingsOpen = false; UpdatePanels(); }
    private void OpacitySlider_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (OpacitySlider is null) return;
        _backgroundOpacity = OpacitySlider.Value;
        UpdatePanels();
    }
    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
