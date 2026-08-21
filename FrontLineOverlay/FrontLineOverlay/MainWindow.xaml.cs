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
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FrontLineOverlay
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        private const byte VK_MEDIA_PREV_TRACK = 0xB1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private bool isGhostMode = false;      // Lock (backend): click-through permanente translúcido
        private bool mouseNoCartao = false;    // cursor sobre o texto -> fundo visível e interativo
        private string serverPort = "8765";
        private SolidColorBrush fundoAnimado;
        private DispatcherTimer timerCursor;

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

            fundoAnimado = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            MainBorder.Background = fundoAnimado;

            DefinirClickThrough(true);
            Task.Run(() => ConnectWebSocket());

            timerCursor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timerCursor.Tick += TimerCursor_Tick;
            timerCursor.Start();
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

        // Envia a tecla de mídia direto ao SO: qualquer player em foco global responde.
        private void EnviarTeclaMidia(byte tecla)
        {
            keybd_event(tecla, 0, 0, UIntPtr.Zero);
            keybd_event(tecla, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private void BtnAnterior_Click(object sender, RoutedEventArgs e)
        {
            EnviarTeclaMidia(VK_MEDIA_PREV_TRACK);
        }

        private void BtnProxima_Click(object sender, RoutedEventArgs e)
        {
            EnviarTeclaMidia(VK_MEDIA_NEXT_TRACK);
        }

        private void DefinirClickThrough(bool ativar)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ativar ? (extendedStyle | WS_EX_TRANSPARENT)
                                                    : (extendedStyle & ~WS_EX_TRANSPARENT));
        }

        private void TimerCursor_Tick(object sender, EventArgs e)
        {
            if (isGhostMode || !IsLoaded) return;

            GetCursorPos(out POINT p);

            if (!mouseNoCartao)
            {
                // Apenas o texto "acorda" o cartão.
                var topoEsq = StackTexto.PointToScreen(new Point(0, 0));
                var baixoDir = StackTexto.PointToScreen(new Point(StackTexto.ActualWidth, StackTexto.ActualHeight));

                const int margem = 8;
                bool sobreTexto = p.X >= topoEsq.X - margem && p.X <= baixoDir.X + margem
                               && p.Y >= topoEsq.Y - margem && p.Y <= baixoDir.Y + margem;

                if (sobreTexto)
                {
                    mouseNoCartao = true;
                    AplicarFlutuacao(true);
                }
            }
            else
            {
                // Já ativo: permanece enquanto o cursor estiver em QUALQUER parte do cartão,
                // e só desaparece quando o cursor sai completamente da área de fundo.
                var cantoA = MainBorder.PointToScreen(new Point(0, 0));
                var cantoB = MainBorder.PointToScreen(new Point(MainBorder.ActualWidth, MainBorder.ActualHeight));

                bool dentroCartao = p.X >= cantoA.X && p.X <= cantoB.X
                                 && p.Y >= cantoA.Y && p.Y <= cantoB.Y;

                if (!dentroCartao)
                {
                    mouseNoCartao = false;
                    AplicarFlutuacao(false);
                }
            }
        }

        private void AplicarFlutuacao(bool hovered)
        {
            byte alpha = hovered ? (byte)179 : (byte)0;
            var anim = new ColorAnimation(Color.FromArgb(alpha, 0, 0, 0), TimeSpan.FromMilliseconds(180));
            fundoAnimado.BeginAnimation(SolidColorBrush.ColorProperty, anim);

            // Sem hover: só o texto fica na tela e cliques atravessam.
            // Com hover: cartão interativo (arrastar/redimensionar/controlar faixa).
            DefinirClickThrough(!hovered);
            ResizeGrip.Visibility = hovered ? Visibility.Visible : Visibility.Collapsed;

            var fadeControles = new DoubleAnimation(hovered ? 1 : 0, TimeSpan.FromMilliseconds(180));
            PainelControles.BeginAnimation(OpacityProperty, fadeControles);
            PainelControles.IsHitTestVisible = hovered;
        }

        private void SetGhostMode(bool enable)
        {
            isGhostMode = enable;
            if (enable)
            {
                mouseNoCartao = false;
                fundoAnimado.BeginAnimation(SolidColorBrush.ColorProperty, null);
                fundoAnimado.Color = Color.FromArgb(179, 0, 0, 0);
                DefinirClickThrough(true);
                ResizeGrip.Visibility = Visibility.Collapsed;
                PainelControles.BeginAnimation(OpacityProperty, null);
                PainelControles.Opacity = 0;
                PainelControles.IsHitTestVisible = false;
            }
            else
            {
                AplicarFlutuacao(mouseNoCartao);
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
