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
        root.Children.Add(DialogUi.Text("MPRIS / Spotify", 15, true, "#FFFFFF"));
        root.Children.Add(_mpris);
        root.Children.Add(DialogUi.Text(DialogUi.L(_lang,
            "Needed for track information and next/previous controls.",
            "Necessário para ler as músicas e avançar/retroceder faixas.",
            "Necesario para leer canciones y avanzar/retroceder pistas.",
            "Diperlukan untuk informasi lagu dan kontrol trek.")));
        root.Children.Add(DialogUi.Text("Audio recording / Shazam", 15, true, "#FFFFFF"));
        root.Children.Add(_record);
        root.Children.Add(DialogUi.Text(DialogUi.L(_lang,
            "Needed to recognize music from system audio.",
            "Necessário para reconhecer músicas pelo áudio do sistema.",
            "Necesario para reconocer música desde el audio del sistema.",
            "Diperlukan untuk mengenali musik dari audio sistem.")));
        var copy = DialogUi.Button(DialogUi.L(_lang, "Copy connection commands", "Copiar comandos de autorização", "Copiar comandos", "Salin perintah"), true);
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync("sudo snap connect frontline-lyrics:audio-record\n" +
                                             "sudo snap connect frontline-lyrics:mpris spotify:spotify-mpris");
        };
        root.Children.Add(copy);
        root.Children.Add(DialogUi.Text(DialogUi.L(_lang,
            "Paste into a terminal. The Spotify command applies when Spotify is installed as a Snap. For other players, select its MPRIS connection in your system's app permissions.",
            "Cole no terminal. O comando do Spotify vale quando ele está instalado como Snap. Para outros reprodutores, escolha a conexão MPRIS nas permissões do sistema.",
            "Pega en la terminal. El comando de Spotify sirve cuando está instalado como Snap. Para otros reproductores, elige MPRIS en los permisos del sistema.",
            "Tempel di terminal. Perintah Spotify berlaku saat Spotify terpasang sebagai Snap. Untuk pemutar lain, atur izin MPRIS di sistem."), 11));
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
