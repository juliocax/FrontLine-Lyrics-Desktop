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
    private readonly TextBlock _record = DialogUi.Text("");
    private readonly TextBlock _mpris = DialogUi.Text("");

    internal SnapPermissionsDialog(string language)
    {
        _lang = language;
        Title = DialogUi.L(_lang, "Permissions", "Permissões", "Permisos", "Izin");
        Width = 545; Height = 470; MinWidth = 420; MinHeight = 390;
        Background = new SolidColorBrush(Color.Parse("#101010"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        root.Children.Add(DialogUi.Text(DialogUi.L(_lang,
            "Allow FrontLine Lyrics to access music players",
            "Permita que o FrontLine Lyrics acesse os reprodutores",
            "Permite que FrontLine Lyrics acceda a reproductores",
            "Izinkan FrontLine Lyrics mengakses pemutar musik"), 19, true, "#FFFFFF"));
        root.Children.Add(DialogUi.Text(DialogUi.L(_lang,
            "Snap isolates apps. You control access, and can revoke it later in system settings.",
            "O Snap isola aplicativos. Você controla o acesso e pode revogá-lo nas configurações do sistema.",
            "Snap aísla las aplicaciones. Puedes revocar estos permisos en la configuración del sistema.",
            "Snap mengisolasi aplikasi. Anda dapat mencabut izin nanti di pengaturan sistem.")));
        root.Children.Add(DialogUi.Text(DialogUi.L(_lang,
            "Music player connection (MPRIS)", "Conexão com reprodutores (MPRIS)",
            "Conexión con reproductores (MPRIS)", "Koneksi pemutar musik (MPRIS)"), 15, true, "#FFFFFF"));
        root.Children.Add(_mpris);
        root.Children.Add(DialogUi.Text(DialogUi.L(_lang,
            "Lets FrontLine read track information and send next/previous commands to an authorized player. Choose the player you use in your system's app permissions. Connecting Spotify does not automatically authorize Firefox or another player.",
            "Permite identificar a faixa e avançar ou voltar músicas no reprodutor autorizado. Nas permissões do sistema, escolha o app que você usa. Autorizar o Spotify não libera automaticamente o Firefox ou outro reprodutor.",
            "Permite leer las canciones y controlarlas en el reproductor autorizado. Selecciona tu reproductor en los permisos del sistema. Autorizar Spotify no autoriza automáticamente Firefox u otros reproductores.",
            "Memungkinkan FrontLine membaca lagu dan mengontrol pemutar yang diizinkan. Pilih pemutar di izin sistem. Izin Spotify tidak otomatis berlaku untuk Firefox atau pemutar lain."), 12));
        root.Children.Add(DialogUi.Text(DialogUi.L(_lang,
            "Audio recognition (Shazam)", "Reconhecimento de áudio (Shazam)",
            "Reconocimiento de audio (Shazam)", "Pengenalan audio (Shazam)"), 15, true, "#FFFFFF"));
        root.Children.Add(_record);
        root.Children.Add(DialogUi.Text(DialogUi.L(_lang,
            "Allows music recognition from computer audio even when direct player control is unavailable.",
            "Permite reconhecer a música pelo som do computador, mesmo quando não há controle direto do reprodutor.",
            "Permite identificar música por el audio del equipo, incluso sin controlar directamente el reproductor.",
            "Memungkinkan pengenalan musik dari audio komputer tanpa perlu mengontrol pemutar."), 12));

        var copyAudio = DialogUi.Button(DialogUi.L(_lang,
            "Copy audio permission command", "Copiar comando do áudio",
            "Copiar comando de audio", "Salin perintah audio"));
        copyAudio.Click += async (_, _) => await CopyAsync("sudo snap connect frontline-lyrics:audio-record");
        root.Children.Add(copyAudio);

        var copySpotify = DialogUi.Button(DialogUi.L(_lang,
            "Copy Spotify command", "Copiar comando do Spotify",
            "Copiar comando de Spotify", "Salin perintah Spotify"));
        copySpotify.Click += async (_, _) => await CopyAsync("sudo snap connect frontline-lyrics:mpris spotify:spotify-mpris");
        root.Children.Add(copySpotify);

        var copyPlayers = DialogUi.Button(DialogUi.L(_lang,
            "Copy command to list available players", "Copiar comando para listar reprodutores",
            "Copiar comando para ver reproductores", "Salin perintah daftar pemutar"));
        copyPlayers.Click += async (_, _) => await CopyAsync("snap connections --all | grep mpris");
        root.Children.Add(copyPlayers);

        root.Children.Add(DialogUi.Text(DialogUi.L(_lang,
            "In Ubuntu's app permissions, open FrontLine Lyrics and choose your music player under 'Control music and video players'. For a Snap player, its MPRIS slot may also be connected manually. The available players depend on what is installed, and changing the selected player may replace the previous connection. Paste only the command appropriate for your device into a terminal and confirm it yourself.",
            "Nas permissões do Ubuntu, abra FrontLine Lyrics e escolha seu aplicativo em 'Controle reprodutores de música e vídeo'. Outros apps Snap podem oferecer sua própria conexão MPRIS. As opções dependem dos programas instalados, e trocar o reprodutor selecionado pode substituir a conexão anterior. Cole no terminal somente o comando adequado e confirme você mesmo.",
            "En los permisos de Ubuntu, abre FrontLine Lyrics y elige el reproductor en 'Controlar reproductores de música y vídeo'. Otros Snap pueden ofrecer conexiones MPRIS diferentes. Cambiar el reproductor puede sustituir la conexión anterior. Ejecuta solo el comando que corresponda.",
            "Buka izin FrontLine Lyrics di Ubuntu dan pilih pemutar pada kontrol musik dan video. Pemutar Snap lain dapat memiliki koneksi MPRIS sendiri. Memilih pemutar lain dapat mengganti koneksi sebelumnya. Jalankan hanya perintah yang sesuai."), 11));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var refresh = DialogUi.Button(DialogUi.L(_lang, "Check again", "Verificar novamente", "Comprobar de nuevo", "Periksa lagi"));
        refresh.Click += async (_, _) => await RefreshAsync();
        var close = DialogUi.Button(DialogUi.L(_lang, "Continue", "Continuar", "Continuar", "Lanjut"));
        close.Click += (_, _) => Close();
        actions.Children.Add(refresh); actions.Children.Add(close);
        root.Children.Add(actions);
        Content = new ScrollViewer { Content = root };
        Opened += async (_, _) => await RefreshAsync();
    }

    private async Task CopyAsync(string command)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(command);
    }

    private async Task RefreshAsync()
    {
        bool mpris = await IsConnectedAsync("mpris");
        bool record = await IsConnectedAsync("audio-record");
        _mpris.Text = Status(mpris);
        _record.Text = Status(record);
        _mpris.Foreground = new SolidColorBrush(Color.Parse(mpris ? "#8DDDA6" : "#F5C779"));
        _record.Foreground = new SolidColorBrush(Color.Parse(record ? "#8DDDA6" : "#F5C779"));
    }
    private string Status(bool ok) => ok
        ? DialogUi.L(_lang, "Connected", "Conectada", "Conectada", "Terhubung")
        : DialogUi.L(_lang, "Not connected", "Não conectada", "No conectada", "Belum terhubung");

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
            psi.ArgumentList.Add("is-connected"); psi.ArgumentList.Add(plug);
            using var p = Process.Start(psi);
            if (p == null) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await p.WaitForExitAsync(timeout.Token);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
