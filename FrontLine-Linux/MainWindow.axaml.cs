using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace FrontLine_Linux;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private Task? _connectionTask;

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => _connectionTask ??= ConnectionLoopAsync(_stop.Token);
        Closed += (_, _) =>
        {
            _stop.Cancel();
            _socket?.Abort();
        };
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
                Dispatcher.UIThread.Post(() => SetConnection(true));
                await ReceiveLoopAsync(socket, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Servidor pode estar desligado durante o desenvolvimento.
            }
            finally
            {
                if (ReferenceEquals(_socket, socket)) _socket = null;
                if (!token.IsCancellationRequested)
                    Dispatcher.UIThread.Post(() => SetConnection(false));
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var stream = new System.IO.MemoryStream();
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            stream.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Text) return;
                if (stream.Length + result.Count > 512_000) throw new InvalidOperationException("Estado WebSocket muito grande");
                stream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            using var doc = JsonDocument.Parse(stream.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
            var root = doc.RootElement;
            var status = GetString(root, "status") ?? "IDLE";
            var song = GetString(root, "song");
            var artist = GetString(root, "artist");
            var previous = GetString(root, "previous_lyrics") ?? "";
            var current = GetString(root, "current_lyrics") ?? "";
            var next = GetString(root, "next_lyrics") ?? "";
            Dispatcher.UIThread.Post(() =>
            {
                StatusText.Text = status;
                SongText.Text = string.IsNullOrWhiteSpace(song) ? "Nenhuma música identificada" : song;
                ArtistText.Text = artist ?? "";
                PreviousText.Text = previous;
                CurrentText.Text = string.IsNullOrWhiteSpace(current)
                    ? (status == "LISTENING" ? "Ouvindo..." : status == "SEARCHING" ? "Buscando letra..." : "Sua letra sincronizada aparecerá aqui")
                    : current;
                NextText.Text = next;
            });
        }
    }

    private static string? GetString(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private void SetConnection(bool connected)
    {
        ConnectionText.Text = connected ? "● Conectado" : "● Servidor desconectado";
        ListenButton.IsEnabled = connected;
        ClearButton.IsEnabled = connected;
        if (!connected) StatusText.Text = "OFFLINE";
    }

    private async Task SendCommandAsync(string action)
    {
        await _sendLock.WaitAsync();
        try
        {
            var socket = _socket;
            if (socket?.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { action }));
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _stop.Token);
        }
        catch (Exception) when (!_stop.IsCancellationRequested)
        {
            SetConnection(false);
        }
        finally { _sendLock.Release(); }
    }

    private async void Listen_Click(object? sender, RoutedEventArgs e) => await SendCommandAsync("LISTEN");
    private async void Clear_Click(object? sender, RoutedEventArgs e) => await SendCommandAsync("RESET");
}
