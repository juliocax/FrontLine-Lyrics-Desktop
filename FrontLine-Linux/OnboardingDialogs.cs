using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;

namespace FrontLine_Linux;

internal static class DialogUi
{
    internal static string L(string lang, string en, string pt, string es, string id) => lang switch
    {
        "pt" => pt, "es" => es, "id" => id, _ => en
    };

    internal static TextBlock Text(string value, int size = 13, bool bold = false, string color = "#DDDDDD") => new()
    {
        Text = value, TextWrapping = TextWrapping.Wrap, FontSize = size,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        Foreground = new SolidColorBrush(Color.Parse(color))
    };

    internal static Button Button(string title, bool primary = false) => new()
    {
        Content = title, MinHeight = 36, Padding = new Thickness(17, 8),
        Background = new SolidColorBrush(Color.Parse(primary ? "#393939" : "#202020")),
        Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.Parse("#464646")),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9)
    };

    internal static Window Window(int width = 565, int height = 525) => new()
    {
        Title = "FrontLine Lyrics", Width = width, Height = height,
        MinWidth = 395, MinHeight = 300,
        Background = new SolidColorBrush(Color.Parse("#090909")),
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        WindowDecorations = WindowDecorations.Full,
        ShowInTaskbar = false, CanResize = true
    };
}

internal sealed class WelcomeDialog : Window
{
    private string _language;
    private bool _enabled;
    internal string SelectedLanguage => _language;
    internal bool TelemetryEnabled => _enabled;
    internal bool Completed { get; private set; }

    internal WelcomeDialog(string language, bool enabled)
    {
        var shell = DialogUi.Window(570, 570);
        Title = shell.Title; Width = shell.Width; Height = shell.Height;
        MinWidth = shell.MinWidth; MinHeight = shell.MinHeight;
        Background = shell.Background; WindowStartupLocation = shell.WindowStartupLocation;
        WindowDecorations = shell.WindowDecorations; ShowInTaskbar = false;
        CanResize = true;
        _language = language; _enabled = enabled;
        Render();
    }

    private void Render()
    {
        var root = new DockPanel { Margin = new Thickness(23, 18, 23, 20), LastChildFill = true };
        var header = new StackPanel { Spacing = 11 };
        var lang = new ComboBox { HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 144 };
        foreach (var (id, title) in new[] { ("en", "English"), ("pt", "Português"), ("es", "Español"), ("id", "Bahasa Indonesia") })
            lang.Items.Add(new ComboBoxItem { Content = title, Tag = id });
        foreach (var item in lang.Items)
            if (item is ComboBoxItem languageOption && languageOption.Tag?.ToString() == _language) lang.SelectedItem = languageOption;
        lang.SelectionChanged += (_, _) =>
        {
            var next = (lang.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (next is not null && next != _language) { _language = next; Render(); }
        };
        header.Children.Add(lang);
        header.Children.Add(DialogUi.Text(DialogUi.L(_language,"Welcome to FrontLine Lyrics","Bem-vindo(a) ao FrontLine Lyrics","Bienvenido a FrontLine Lyrics","Selamat datang di FrontLine Lyrics"), 23, true, "#FFFFFF"));
        header.Children.Add(DialogUi.Text(DialogUi.L(_language,
            "Thanks for using this free, open-source lyrics companion.",
            "Obrigado por usar este aplicativo gratuito e de código aberto.",
            "Gracias por usar esta aplicación gratuita y de código abierto.",
            "Terima kasih telah menggunakan aplikasi gratis dan open-source ini.")));
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var footer = new StackPanel { Spacing = 12, Margin = new Thickness(0, 14, 0, 0) };
        var choice = new CheckBox { IsChecked = _enabled, Content = DialogUi.L(_language,
            "Send optional anonymous settings statistics", "Enviar estatísticas anônimas opcionais",
            "Enviar estadísticas anónimas opcionales", "Kirim statistik pengaturan anonim (opsional)"),
            Foreground = Brushes.White };
        choice.Click += (_, _) => _enabled = choice.IsChecked == true;
        footer.Children.Add(choice);
        var start = DialogUi.Button(DialogUi.L(_language,"Get started","Começar","Empezar","Mulai"), true);
        start.HorizontalAlignment = HorizontalAlignment.Right;
        start.Click += (_, _) => { Completed = true; Close(); };
        footer.Children.Add(start);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var content = new StackPanel { Spacing = 13, Margin = new Thickness(0, 17, 8, 0) };
        content.Children.Add(DialogUi.Text(DialogUi.L(_language,"GETTING STARTED","PRIMEIROS PASSOS","PRIMEROS PASOS","MEMULAI"), 12, true, "#BBBBBB"));
        content.Children.Add(DialogUi.Text(DialogUi.L(_language,
            "LISTEN identifies songs from audio. SEARCH finds lyrics by artist and title. Use the settings icon to adjust appearance, translation and interface language.",
            "OUVIR identifica músicas pelo áudio. BUSCAR encontra letras por artista e título. Use o ícone de configurações para mudar a aparência, tradução e idioma.",
            "ESCUCHAR identifica canciones por audio. BUSCAR encuentra letras por artista y título. Ajusta apariencia, traducción e idioma en configuración.",
            "DENGARKAN mengenali lagu dari audio. CARI menemukan lirik melalui artis dan judul. Atur tampilan, terjemahan dan bahasa di pengaturan.")));
        var guide = DialogUi.Button(DialogUi.L(_language,"Open tutorial","Abrir tutorial","Abrir tutorial","Buka tutorial"));
        guide.HorizontalAlignment = HorizontalAlignment.Left;
        guide.Click += (_, _) => Open("https://juliocax.github.io/FrontLine-Lyrics-Desktop/documentation_v0.1.0.html");
        content.Children.Add(guide);
        content.Children.Add(DialogUi.Text(DialogUi.L(_language,"ANONYMOUS STATISTICS","ESTATÍSTICAS ANÔNIMAS","ESTADÍSTICAS ANÓNIMAS","STATISTIK ANONIM"), 12, true, "#BBBBBB"));
        content.Children.Add(DialogUi.Text(DialogUi.L(_language,
            "If enabled, a small settings snapshot may be sent to Aptabase at most once per day: lyric font, color, visible lines, interface language, app version and system locale. This does not contain songs, artists, lyrics, searches, names, emails or a persistent user ID.",
            "Se ativado, o Aptabase poderá receber no máximo uma vez por dia um resumo: fonte, cor e linhas da letra, idioma da interface, versão e idioma do sistema. Não inclui músicas, artistas, letras, buscas, nomes, e-mails nem identificador persistente.",
            "Si se activa, Aptabase podrá recibir como máximo una vez al día un resumen: fuente, color, líneas visibles, idioma, versión e idioma del sistema. No incluye canciones, artistas, letras, búsquedas ni identificador persistente.",
            "Jika diaktifkan, ringkasan font, warna, baris lirik, bahasa aplikasi, versi dan lokal sistem dapat dikirim ke Aptabase maksimal sekali sehari. Tidak menyertakan lagu, artis, lirik, pencarian atau ID pengguna permanen.")));
        content.Children.Add(DialogUi.Text(DialogUi.L(_language,
            "You can change your choice any time under Settings → General. Without a configured Aptabase app key, no data is sent.",
            "Você pode mudar sua escolha em Configurações → Geral. Sem uma chave Aptabase configurada, nenhum dado é enviado.",
            "Puedes cambiarlo en Ajustes → General. Sin clave Aptabase configurada, no se envían datos.",
            "Anda dapat mengubah pilihan di Pengaturan → Umum. Tanpa kunci aplikasi Aptabase, tidak ada data terkirim."), 12, false, "#AAAAAA"));
        var source = DialogUi.Button(DialogUi.L(_language,"View source code","Ver código-fonte","Ver código fuente","Lihat kode sumber"));
        source.HorizontalAlignment = HorizontalAlignment.Left;
        source.Click += (_, _) => Open("https://github.com/juliocax/FrontLine-Lyrics-Desktop");
        content.Children.Add(source);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Content = root;
    }

    internal static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Open URL: " + ex.Message); }
    }
}

internal sealed class ReleaseNotesDialog : Window
{
    internal ReleaseNotesDialog(string lang)
    {
        var shell = DialogUi.Window(560, 450);
        Title = shell.Title; Width = shell.Width; Height = shell.Height;
        MinWidth = shell.MinWidth; MinHeight = shell.MinHeight;
        Background = shell.Background; WindowStartupLocation = shell.WindowStartupLocation;
        WindowDecorations = shell.WindowDecorations; ShowInTaskbar = false; CanResize = true;
        var root = new DockPanel { Margin = new Thickness(25, 22) };
        var done = DialogUi.Button(DialogUi.L(lang,"Got it","Entendi","Entendido","Mengerti"), true);
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Margin = new Thickness(0, 14, 0, 0);
        done.Click += (_, _) => Close();
        DockPanel.SetDock(done, Dock.Bottom); root.Children.Add(done);
        var body = new StackPanel { Spacing = 15 };
        body.Children.Add(DialogUi.Text(DialogUi.L(lang,"What's new in FrontLine Lyrics","Novidades do FrontLine Lyrics","Novedades de FrontLine Lyrics","Yang baru di FrontLine Lyrics"), 23, true, "#FFFFFF"));
        body.Children.Add(DialogUi.Text("v1.0.0", 13, true, "#BBBBBB"));
        string[] details = lang switch
        {
            "pt" => ["FrontLine Lyrics 1.0.0 para Linux, com Avalonia e servidor Python compartilhado.", "Identificação de músicas por áudio e acompanhamento de players MPRIS.", "Letras sincronizadas, busca manual e histórico persistente.", "Tradução, romanização e personalização de fontes, cores e linhas.", "Configurações persistentes e telemetria anônima opcional."],
            "es" => ["FrontLine Lyrics 1.0.0 para Linux, con Avalonia y servidor Python compartido.", "Identificación por audio y seguimiento MPRIS.", "Letras sincronizadas, búsqueda manual e historial persistente.", "Traducción, romanización y personalización de fuentes, colores y líneas.", "Configuración persistente y telemetría anónima opcional."],
            "id" => ["FrontLine Lyrics 1.0.0 untuk Linux dengan Avalonia dan backend Python bersama.", "Pengenalan lagu dan pemantauan pemutar MPRIS.", "Lirik tersinkronisasi, pencarian manual dan riwayat tersimpan.", "Terjemahan, romanisasi, penyesuaian font, warna dan jumlah baris.", "Pengaturan tersimpan dan telemetri anonim opsional."],
            _ => ["FrontLine Lyrics 1.0.0 for Linux with Avalonia UI and shared Python backend.", "Audio recognition and MPRIS player tracking.", "Synchronized lyrics, manual search and saved history.", "Translation, romanization and customizable fonts, colors and lines.", "Persistent settings and optional anonymous telemetry."]
        };
        foreach (var item in details) body.Children.Add(DialogUi.Text("•  " + item, 13));

        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Content = root;
    }
}
