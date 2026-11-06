using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace FrontLine_Linux;

internal sealed class SnapPermissionsDialog : Window
{
    private readonly string _lang;
    private readonly TextBlock _recordStatus = DialogUi.Text("");
    private readonly TextBlock _mprisStatus = DialogUi.Text("");
    private readonly StackPanel _audioHelp = new() { Spacing = 7 };
    private readonly StackPanel _playerHelp = new() { Spacing = 7 };
    private readonly TextBlock _feedback = DialogUi.Text("");
    private readonly Button _refresh;

    internal SnapPermissionsDialog(string language)
    {
        _lang = language;
        Title = L("Permissions", "Permissões", "Permisos", "Izin");
        Width = 670;
        Height = 650;
        MinWidth = 440;
        MinHeight = 440;
        Background = Brush("#101010");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(24), Spacing = 13 };
        root.Children.Add(DialogUi.Text(L("Connect your music players", "Conecte seus reprodutores", "Conecta tus reproductores", "Hubungkan pemutar musik"), 20, true, "#FFFFFF"));
        root.Children.Add(DialogUi.Text(L(
            "FrontLine checks the Snap permissions already granted. Only missing connections need attention.",
            "O FrontLine verifica as permissões já concedidas ao Snap. Só é preciso cuidar das conexões pendentes.",
            "FrontLine comprueba los permisos ya concedidos a Snap. Solo debes revisar las conexiones pendientes.",
            "FrontLine memeriksa izin Snap yang sudah diberikan. Hanya koneksi yang belum aktif yang perlu diperhatikan."), 12));

        root.Children.Add(DialogUi.Text(L("Music players · MPRIS", "Reprodutores · MPRIS", "Reproductores · MPRIS", "Pemutar · MPRIS"), 15, true, "#FFFFFF"));
        root.Children.Add(_mprisStatus);
        root.Children.Add(_playerHelp);

        root.Children.Add(DialogUi.Text(L("Audio recognition · Shazam", "Reconhecimento de áudio · Shazam", "Reconocimiento de audio · Shazam", "Pengenalan audio · Shazam"), 15, true, "#FFFFFF"));
        root.Children.Add(_recordStatus);
        root.Children.Add(_audioHelp);

        root.Children.Add(DialogUi.Text(L(
            "You stay in control: commands are displayed for review and copying only. They are never executed automatically.",
            "Você mantém o controle: os comandos são exibidos apenas para leitura e cópia. Nunca são executados automaticamente.",
            "Tú tienes el control: los comandos solo se muestran para leerlos y copiarlos. Nunca se ejecutan automáticamente.",
            "Anda tetap memegang kendali: perintah hanya ditampilkan untuk dibaca dan disalin, tidak dijalankan otomatis."), 11));
        _feedback.Foreground = Brush("#8DDDA6");
        root.Children.Add(_feedback);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        _refresh = DialogUi.Button(L("Check again", "Verificar novamente", "Comprobar de nuevo", "Periksa lagi"));
        _refresh.Click += async (_, _) => await RefreshAsync();
        var close = DialogUi.Button(L("Continue", "Continuar", "Continuar", "Lanjut"));
        close.Click += (_, _) => Close();
        buttons.Children.Add(_refresh);
        buttons.Children.Add(close);
        root.Children.Add(buttons);

        Content = new ScrollViewer { Content = root };
        Opened += async (_, _) => await RefreshAsync();
    }

    private string L(string en, string pt, string es, string id) => DialogUi.L(_lang, en, pt, es, id);
    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private void CommandRow(StackPanel parent, string description, string command)
    {
        parent.Children.Add(DialogUi.Text(description, 12));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        var code = new TextBox
        {
            Text = command, IsReadOnly = true, MinHeight = 38,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 11, FontFamily = new FontFamily("monospace"),
            Background = Brush("#191919"), Foreground = Brushes.White,
            BorderBrush = Brush("#454545"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        row.Children.Add(code);
        var copy = DialogUi.Button(L("Copy", "Copiar", "Copiar", "Salin"));
        copy.MinWidth = 76;
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            try
            {
                await clipboard.SetTextAsync(command);
                _feedback.Text = L("Command copied.", "Comando copiado.", "Comando copiado.", "Perintah disalin.");
            }
            catch
            {
                _feedback.Text = L("Could not copy. Select the command manually.", "Não foi possível copiar. Selecione o comando manualmente.", "No se pudo copiar. Selecciona el comando manualmente.", "Tidak dapat menyalin. Pilih perintah secara manual.");
            }
        };
        Grid.SetColumn(copy, 1);
        row.Children.Add(copy);
        parent.Children.Add(row);
    }

    private async Task RefreshAsync()
    {
        _refresh.IsEnabled = false;
        try
        {
            bool mpris = await IsConnectedAsync("mpris");
            bool audio = await IsConnectedAsync("audio-record");
            _mprisStatus.Text = mpris
                ? L("Connected. Player access depends on the player you selected.", "Conectado. O acesso depende do reprodutor selecionado.", "Conectado. El acceso depende del reproductor seleccionado.", "Terhubung. Akses bergantung pada pemutar yang dipilih.")
                : L("Not connected. Choose the music player you use.", "Não conectado. Escolha o reprodutor que você usa.", "No conectado. Elige el reproductor que usas.", "Belum terhubung. Pilih pemutar yang digunakan.");
            _recordStatus.Text = audio
                ? L("Allowed. Audio recognition is available.", "Permitido. O reconhecimento de áudio está autorizado.", "Permitido. El reconocimiento de audio está autorizado.", "Diizinkan. Pengenalan audio tersedia.")
                : L("Not allowed. Needed to recognize songs by sound.", "Não autorizado. Necessário para reconhecer músicas pelo áudio.", "No permitido. Necesario para reconocer canciones por audio.", "Belum diizinkan. Diperlukan untuk mengenali lagu dari audio.");
            _mprisStatus.Foreground = Brush(mpris ? "#8DDDA6" : "#F5C779");
            _recordStatus.Foreground = Brush(audio ? "#8DDDA6" : "#F5C779");
            _playerHelp.Children.Clear();
            _audioHelp.Children.Clear();

            if (!mpris)
            {
                _playerHelp.Children.Add(DialogUi.Text(L(
                    "Open Ubuntu's application permissions for FrontLine Lyrics and select the music player under 'Control music and video players'. The choices depend on your installed apps.",
                    "Abra as permissões do FrontLine Lyrics no Ubuntu e selecione o player em 'Controlar reprodutores de música e vídeo'. As opções dependem dos apps instalados.",
                    "Abre los permisos de FrontLine Lyrics en Ubuntu y elige el reproductor en 'Controlar reproductores de música y vídeo'.",
                    "Buka izin FrontLine Lyrics di Ubuntu dan pilih pemutar musik/video."), 12));
                CommandRow(_playerHelp, L(
                    "If Spotify is installed as a Snap:", "Se o Spotify estiver instalado como Snap:",
                    "Si Spotify está instalado como Snap:", "Jika Spotify terpasang sebagai Snap:"),
                    "sudo snap connect frontline-lyrics:mpris spotify:spotify-mpris");
                CommandRow(_playerHelp, L(
                    "To inspect available player connections:", "Para consultar as conexões disponíveis:",
                    "Para consultar las conexiones disponibles:", "Untuk memeriksa koneksi yang tersedia:"),
                    "snap connections --all | grep mpris");
            }
            else
            {
                _playerHelp.Children.Add(DialogUi.Text(L(
                    "A connected MPRIS interface does not guarantee access to every player. If another player is missing, change the selected player in Ubuntu permissions or inspect its Snap slot.",
                    "Uma conexão MPRIS ativa não libera todos os players. Se outro player não aparecer, altere a seleção nas permissões do Ubuntu ou verifique o slot Snap dele.",
                    "Una conexión MPRIS activa no autoriza todos los reproductores. Cambia el reproductor seleccionado o comprueba su slot Snap.",
                    "Koneksi MPRIS tidak otomatis memberi akses ke semua pemutar. Ubah pilihan izin atau periksa slot Snap."), 11));
            }

            if (!audio)
                CommandRow(_audioHelp, L(
                    "Enable audio capture for recognition:", "Autorizar captura de áudio para reconhecimento:",
                    "Permitir captura de audio para reconocimiento:", "Izinkan perekaman audio untuk pengenalan:"),
                    "sudo snap connect frontline-lyrics:audio-record");
            _feedback.Text = "";
        }
        finally { _refresh.IsEnabled = true; }
    }

    internal static async Task<bool> AnyRequiredMissingAsync() =>
        !await IsConnectedAsync("mpris") || !await IsConnectedAsync("audio-record");

    internal static async Task<bool> IsConnectedAsync(string plug)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNAP"))) return true;
        try
        {
            var psi = new ProcessStartInfo("snapctl")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            };
            psi.ArgumentList.Add("is-connected");
            psi.ArgumentList.Add(plug);
            using var process = Process.Start(psi);
            if (process == null) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }
}
