using System;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace FrontLineOverlay
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        private bool isGhostMode = false;
        private bool isConnected = false;
        private string serverPort = "8765";

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1)
            {
                serverPort = args[1];
            }

            SetGhostMode(false);
            Task.Run(() => ConnectWebSocket());
        }

        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!isGhostMode) DragMove();
        }

        private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
        {
            this.Width = Math.Max(300, this.Width + e.HorizontalChange);
            this.Height = Math.Max(100, this.Height + e.VerticalChange);
        }

        private void SetGhostMode(bool enable)
        {
            isGhostMode = enable;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int WS_EX_TRANSPARENT = 0x00000020;
            int GWL_EXSTYLE = -20;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

            if (enable)
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);
                MainBorder.Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)); // Translúcido
                ResizeGrip.Visibility = Visibility.Collapsed;
            }
            else
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle & ~WS_EX_TRANSPARENT);
                MainBorder.Background = new SolidColorBrush(Color.FromArgb(240, 30, 30, 30)); // Escuro sólido para editar
                ResizeGrip.Visibility = Visibility.Visible;
            }
        }

        private async Task ConnectWebSocket()
        {
            while (true)
            {
                using (ClientWebSocket ws = new ClientWebSocket())
                {
                    try
                    {
                        await ws.ConnectAsync(new Uri($"ws://localhost:{serverPort}"), CancellationToken.None);
                        isConnected = true;

                        byte[] buffer = new byte[8192];
                        while (ws.State == WebSocketState.Open)
                        {
                            WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                            if (result.MessageType == WebSocketMessageType.Close) break;

                            string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            UpdateUI(message);
                        }
                    }
                    catch (Exception)
                    {
                        isConnected = false;
                        Dispatcher.Invoke(() => LblAtual.Text = "Looking for FrontLine panel...");
                    }
                }
                await Task.Delay(2000);
            }
        }

        private void UpdateUI(string jsonMessage)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(jsonMessage))
                {
                    JsonElement root = doc.RootElement;
                    string atual = root.GetProperty("letra_atual").GetString() ?? "";
                    string anterior = root.GetProperty("letra_anterior").GetString() ?? "";
                    string futura = root.GetProperty("letra_futura").GetString() ?? "";
                    int fontSize = root.GetProperty("tamanho_fonte").GetInt32();
                    bool fantasma = root.GetProperty("modo_fantasma").GetBoolean();

                    Dispatcher.Invoke(() =>
                    {
                        LblAtual.Text = atual;
                        LblAnterior.Text = anterior;
                        LblFutura.Text = futura;
                        LblAtual.FontSize = fontSize;
                        LblAnterior.FontSize = Math.Max(10, fontSize * 0.6);
                        LblFutura.FontSize = Math.Max(10, fontSize * 0.6);

                        if (isGhostMode != fantasma) SetGhostMode(fantasma);
                    });
                }
            }
            catch (Exception ex)
            {
                string logMessage = $"[{DateTime.Now:HH:mm:ss}] Erro de JSON: {ex.Message} | Mensagem recebida: {jsonMessage}\n";
                File.AppendAllText("overlay_error_log.txt", logMessage);
            }
        }
    }
}