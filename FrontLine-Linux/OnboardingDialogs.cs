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

    internal static Button Link(string title) => new()
    {
        Content = new TextBlock
        {
            Text = title, TextDecorations = TextDecorations.Underline,
            Foreground = new SolidColorBrush(Color.Parse("#BCA7FF")), FontSize = 13
        },
        Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        Padding = new Thickness(0), MinHeight = 0,
        HorizontalAlignment = HorizontalAlignment.Left,
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
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
        var shell = DialogUi.Window(610, 610);
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
        var root = new DockPanel { Margin = new Thickness(26, 22, 26, 22), LastChildFill = true };
        var header = new StackPanel { Spacing = 12 };
        var lang = new ComboBox { HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 154 };
        foreach (var (id, title) in new[] { ("en", "English"), ("pt", "Português"), ("es", "Español"), ("id", "Bahasa Indonesia") })
            lang.Items.Add(new ComboBoxItem { Content = title, Tag = id });
        foreach (var item in lang.Items)
            if (item is ComboBoxItem option && option.Tag?.ToString() == _language) lang.SelectedItem = option;
        lang.SelectionChanged += (_, _) =>
        {
            var next = (lang.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (next is not null && next != _language) { _language = next; Render(); }
        };
        header.Children.Add(lang);
        header.Children.Add(DialogUi.Text(DialogUi.L(_language,
            "Welcome to FrontLine Lyrics", "Bem-vindo(a) ao FrontLine Lyrics",
            "Bienvenido(a) a FrontLine Lyrics", "Selamat datang di FrontLine Lyrics"), 23, true, "#FFFFFF"));
        header.Children.Add(DialogUi.Text(DialogUi.L(_language,
            "Thanks for using FrontLine Lyrics! It's a free, open-source project, and knowing it's useful to you means a lot.",
            "Obrigado por usar o FrontLine Lyrics! É um projeto gratuito e open-source, e saber que ele é útil para você significa muito.",
            "¡Gracias por usar FrontLine Lyrics! Es un proyecto gratuito y de código abierto; saber que te resulta útil significa mucho.",
            "Terima kasih telah menggunakan FrontLine Lyrics! Ini proyek gratis dan open-source; mengetahui bahwa aplikasi ini bermanfaat sangat berarti.")));
        var tutorial = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        tutorial.Children.Add(DialogUi.Text(DialogUi.L(_language,
            "First time here?", "Primeira vez por aqui?", "¿Es tu primera vez aquí?", "Baru pertama kali di sini?")));
        var tutorialLink = DialogUi.Link(DialogUi.L(_language,
            "See the tutorials", "Veja os tutoriais", "Ver los tutoriales", "Lihat tutorial"));
        tutorialLink.Click += (_, _) => Open("https://juliocax.github.io/FrontLine-Lyrics-Desktop/documentation_linux_v1.0.0.html");
        tutorial.Children.Add(tutorialLink);
        header.Children.Add(tutorial);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var footer = new StackPanel { Spacing = 12, Margin = new Thickness(0, 13, 0, 0) };
        var choice = new CheckBox
        {
            IsChecked = _enabled,
            Content = DialogUi.L(_language, "Send anonymous usage statistics",
                "Enviar estatísticas anônimas de uso", "Enviar estadísticas de uso anónimas",
                "Kirim statistik penggunaan anonim"),
            Foreground = Brushes.White
        };
        choice.Click += (_, _) => _enabled = choice.IsChecked == true;
        footer.Children.Add(choice);
        var start = DialogUi.Button(DialogUi.L(_language, "Get started", "Começar", "Empezar", "Mulai"), true);
        start.HorizontalAlignment = HorizontalAlignment.Right;
        start.Click += (_, _) => { Completed = true; Close(); };
        footer.Children.Add(start);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var content = new StackPanel { Spacing = 10, Margin = new Thickness(0, 17, 8, 0) };
        content.Children.Add(DialogUi.Text(DialogUi.L(_language,
            "ANONYMOUS USAGE STATISTICS", "ESTATÍSTICAS DE USO ANÔNIMAS",
            "ESTADÍSTICAS DE USO ANÓNIMAS", "STATISTIK PENGGUNAAN ANONIM"), 12, true, "#BBBBBB"));
        content.Children.Add(DialogUi.Text(DialogUi.L(_language,
            "To understand what to improve, FrontLine can send a small anonymous report about its settings, at most once per day.",
            "Para saber o que melhorar, o FrontLine pode enviar um pequeno relatório anônimo sobre como ele está configurado. O envio acontece no máximo uma vez por dia.",
            "Para saber qué mejorar, FrontLine puede enviar un breve informe anónimo sobre su configuración, como máximo una vez al día.",
            "Untuk mengetahui apa yang perlu diperbaiki, FrontLine dapat mengirim ringkasan pengaturan anonim paling banyak sekali sehari.")));
        content.Children.Add(DialogUi.Text(DialogUi.L(_language,
            "WHAT IS SENT", "O QUE É ENVIADO", "QUÉ SE ENVÍA", "DATA YANG DIKIRIM"), 11, true, "#BBBBBB"));
        var sent = _language switch
        {
            "pt" => new[] { "A fonte da letra, a cor e a quantidade de linhas visíveis", "O idioma do aplicativo", "O tema padrão (atual) e a plataforma Linux", "A versão do aplicativo e o idioma do sistema" },
            "es" => new[] { "Fuente, color y cantidad de líneas visibles", "Idioma de la aplicación", "Tema predeterminado (actual) y plataforma Linux", "Versión de la aplicación e idioma del sistema" },
            "id" => new[] { "Font, warna, dan jumlah baris lirik", "Bahasa aplikasi", "Tema bawaan (saat ini) dan platform Linux", "Versi aplikasi dan bahasa sistem" },
            _ => new[] { "Lyric font, color, and visible line count", "App language", "Default theme (currently) and Linux platform", "App version and system language" }
        };
        foreach (var item in sent)
            content.Children.Add(DialogUi.Text("•  " + item, 12));
        content.Children.Add(DialogUi.Text(DialogUi.L(_language,
            "NEVER SENT: songs, artists, lyrics, searches, your name or email. No persistent user identifier is included in the event.",
            "NUNCA é enviado: músicas, artistas, letras, buscas, seu nome ou e-mail. O evento não inclui um identificador persistente de usuário.",
            "NUNCA se envían canciones, artistas, letras, búsquedas, nombre o correo electrónico. El evento no incluye un identificador persistente del usuario.",
            "TIDAK PERNAH dikirim: lagu, artis, lirik, pencarian, nama atau email. Tidak ada pengenal pengguna permanen dalam peristiwa ini."), 12));
        content.Children.Add(DialogUi.Text(DialogUi.L(_language,
            "Data goes to Aptabase, a privacy-focused analytics service. You can turn this off any time in Settings → General. No report is sent without a configured Aptabase app key.",
            "Os dados vão para o Aptabase, um serviço de análise focado em privacidade. Você pode desativar quando quiser em Configurações → Geral. Sem chave Aptabase configurada, nenhum relatório é enviado.",
            "Los datos van a Aptabase, un servicio de análisis centrado en la privacidad. Puedes desactivarlo en Ajustes → General. Sin una clave Aptabase configurada, no se envía nada.",
            "Data dikirim ke Aptabase, layanan analitik berfokus privasi. Anda bisa menonaktifkan di Pengaturan → Umum. Tanpa kunci Aptabase, laporan tidak dikirim."), 12, false, "#BBBBBB"));
        var source = DialogUi.Link(DialogUi.L(_language,
            "Open-source: view the project repository",
            "Open-source: veja o repositório do projeto",
            "Código abierto: consulta el repositorio del proyecto",
            "Open-source: lihat repositori proyek"));
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
