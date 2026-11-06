using System.Runtime.InteropServices;
using System.Windows.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FrontLineOverlay
{
    public partial class WelcomeWindow : Window
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private void EnableNativeRoundedCorners()
        {
            try
            {
                const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
                const int DWMWCP_ROUND = 2;
                const int DWMWA_BORDER_COLOR = 34;
                const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
                int preference = DWMWCP_ROUND;
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE,
                        ref preference, sizeof(int));
                if (hwnd != IntPtr.Zero)
                {
                    int borderColor = DWMWA_COLOR_NONE;
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR,
                        ref borderColor, sizeof(int));
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private const string SourceUrl = "https://github.com/juliocax/FrontLine-Lyrics-Desktop";
        private static readonly string[] Supported = { "en", "pt", "es", "id" };

        private readonly string _tutorialsUrl;
        private string _lang = "en";

        public string SelectedLanguage => _lang;
        public bool TelemetryEnabled => ToggleTelemetry.IsChecked == true;

        private sealed class Texts
        {
            public string Title = "", Thanks = "", TutorialsLead = "", TutorialsLink = "";
            public string TelHeader = "", TelIntro = "", SentLabel = "";
            public string[] Sent = Array.Empty<string>();
            public string NeverSent = "", Provider = "", Toggle = "", Start = "", Source = "";
        }

        private static readonly Dictionary<string, Texts> T = new()
        {
            ["en"] = new Texts
            {
                Title = "Welcome to FrontLine Lyrics",
                Thanks = "Thank you for using FrontLine Lyrics! It's a free, open-source project, and knowing it's useful to you means a lot.",
                TutorialsLead = "New here? ",
                TutorialsLink = "See the tutorials",
                TelHeader = "Anonymous usage statistics",
                TelIntro = "This is optional. With this option enabled, FrontLine sends a small report about your app settings, at most once every 24 hours. It helps decide which features to improve; it does not track what you listen to.",
                SentLabel = "WHAT IS SENT",
                Sent = new[]
                {
                    "Appearance and lyrics: theme, background opacity and scenery, font, color, size, style, lines and alignment.",
                    "Languages and controls: translation, original lyrics, operating modes, window lock and shortcut keys.",
                    "Window and technical details: size, position, state, app version and Windows language.",
                },
                NeverSent = "These statistics do NOT include songs, artists, lyrics, searches, listening history, audio recordings, API keys, your name or email. They do not include a permanent personal identifier.",
                Provider = "The report goes to Aptabase. You can turn it off at any time in Settings → General; disabling it stops future statistics reports. This choice controls statistics only, not the audio recognition needed by Listen.",
                Toggle = "Send anonymous usage statistics",
                Start = "Get started",
                Source = "Open source: view the project code and documentation",
            },
            ["pt"] = new Texts
            {
                Title = "Bem-vindo(a) ao FrontLine Lyrics",
                Thanks = "Obrigado por usar o FrontLine Lyrics! É um projeto gratuito e open-source, e saber que ele é útil para você significa muito.",
                TutorialsLead = "Primeira vez por aqui? ",
                TutorialsLink = "Veja os tutoriais",
                TelHeader = "Estatísticas de uso anônimas",
                TelIntro = "A participação é opcional. Com esta opção ativada, o FrontLine envia um pequeno relatório das configurações do aplicativo, no máximo uma vez a cada 24 horas. Isso ajuda a decidir o que melhorar, sem acompanhar o que você ouve.",
                SentLabel = "O QUE É ENVIADO",
                Sent = new[]
                {
                    "Aparência e letras: tema, opacidade e cenário, fonte, cor, tamanho, estilo, linhas e alinhamento.",
                    "Idiomas e controles: tradução, letra original, modos de uso, bloqueio e teclas do atalho.",
                    "Janela e dados técnicos: tamanho, posição, estado, versão do app e idioma do Windows.",
                },
                NeverSent = "Essas estatísticas NÃO incluem músicas, artistas, letras, buscas, histórico do que você ouve, gravações de áudio, chaves de API, seu nome ou e-mail. Também não incluem um identificador pessoal permanente.",
                Provider = "O relatório é enviado ao Aptabase. Você pode desativar a opção quando quiser em Configurações → Geral; isso interrompe os próximos envios de estatísticas. A opção controla apenas as estatísticas, não o reconhecimento de áudio necessário ao modo Ouvir.",
                Toggle = "Enviar estatísticas anônimas de uso",
                Start = "Começar",
                Source = "Código aberto: consulte o código e a documentação do projeto",
            },
            ["es"] = new Texts
            {
                Title = "Te damos la bienvenida a FrontLine Lyrics",
                Thanks = "¡Gracias por usar FrontLine Lyrics! Es un proyecto gratuito y de código abierto, y saber que te resulta útil significa mucho.",
                TutorialsLead = "¿Primera vez por aquí? ",
                TutorialsLink = "Mira los tutoriales",
                TelHeader = "Estadísticas de uso anónimas",
                TelIntro = "La participación es opcional. Al activar esta opción, FrontLine envía un pequeño informe de los ajustes de la aplicación, como máximo una vez cada 24 horas. Ayuda a decidir qué mejorar, sin registrar lo que escuchas.",
                SentLabel = "QUÉ SE ENVÍA",
                Sent = new[]
                {
                    "Apariencia y letras: tema, opacidad y fondo, fuente, color, tamaño, estilo, líneas y alineación.",
                    "Idiomas y controles: traducción, letra original, modos de uso, bloqueo y teclas del atajo.",
                    "Ventana y datos técnicos: tamaño, posición, estado, versión de la app e idioma de Windows.",
                },
                NeverSent = "Estas estadísticas NO incluyen canciones, artistas, letras, búsquedas, historial de escucha, grabaciones de audio, claves API, tu nombre o correo. Tampoco incluyen un identificador personal permanente.",
                Provider = "El informe se envía a Aptabase. Puedes desactivarlo en cualquier momento en Ajustes → General; esto detiene los próximos envíos de estadísticas. La opción solo controla las estadísticas, no el reconocimiento de audio necesario para Escuchar.",
                Toggle = "Enviar estadísticas de uso anónimas",
                Start = "Empezar",
                Source = "Código abierto: consulta el código y la documentación del proyecto",
            },
            ["id"] = new Texts
            {
                Title = "Selamat datang di FrontLine Lyrics",
                Thanks = "Terima kasih telah menggunakan FrontLine Lyrics! Ini proyek gratis dan open-source, dan senang sekali mengetahui aplikasinya berguna untukmu.",
                TutorialsLead = "Baru di sini? ",
                TutorialsLink = "Lihat tutorialnya",
                TelHeader = "Statistik penggunaan anonim",
                TelIntro = "Partisipasi bersifat opsional. Jika diaktifkan, FrontLine mengirim laporan singkat tentang pengaturan aplikasi paling banyak sekali setiap 24 jam. Ini membantu menentukan perbaikan tanpa melacak apa yang kamu dengarkan.",
                SentLabel = "YANG DIKIRIM",
                Sent = new[]
                {
                    "Tampilan dan lirik: tema, opasitas dan latar, font, warna, ukuran, gaya, baris dan perataan.",
                    "Bahasa dan kontrol: terjemahan, lirik asli, mode penggunaan, kunci jendela dan tombol pintasan.",
                    "Jendela dan detail teknis: ukuran, posisi, status, versi aplikasi dan bahasa Windows.",
                },
                NeverSent = "Statistik ini TIDAK menyertakan lagu, artis, lirik, pencarian, riwayat mendengarkan, rekaman audio, kunci API, nama atau email. Tidak ada pengenal pribadi permanen yang disertakan.",
                Provider = "Laporan dikirim ke Aptabase. Kamu dapat mematikannya kapan saja di Pengaturan → Umum untuk menghentikan pengiriman statistik berikutnya. Opsi ini hanya mengontrol statistik, bukan pengenalan audio yang diperlukan oleh mode Dengar.",
                Toggle = "Kirim statistik penggunaan anonim",
                Start = "Mulai",
                Source = "Open source: lihat kode dan dokumentasi proyek",
            },
        };

        public static string DetectSystemLanguage()
        {
            string two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            if (two == "in") two = "id";
            return Array.IndexOf(Supported, two) >= 0 ? two : "en";
        }

        public WelcomeWindow(string language, bool telemetryEnabled, string tutorialsUrl)
        {
            InitializeComponent();
            SourceInitialized += (_, _) => EnableNativeRoundedCorners();
            _tutorialsUrl = tutorialsUrl;
            ToggleTelemetry.IsChecked = telemetryEnabled;
            MaxHeight = SystemParameters.WorkArea.Height - 20;
            ApplyLanguage(Array.IndexOf(Supported, language) >= 0 ? language : "en");
        }

        private void ApplyLanguage(string lang)
        {
            _lang = lang;
            var t = T[lang];

            TxtTitle.Text = t.Title;
            TxtThanks.Text = t.Thanks;
            RunTutorialsLead.Text = t.TutorialsLead;
            RunTutorialsLink.Text = t.TutorialsLink;
            TxtTutorials.Visibility = string.IsNullOrWhiteSpace(_tutorialsUrl) ? Visibility.Collapsed : Visibility.Visible;
            TxtTelHeader.Text = t.TelHeader;
            TxtTelIntro.Text = t.TelIntro;
            TxtSentLabel.Text = t.SentLabel;
            TxtNeverSent.Text = t.NeverSent;
            TxtProvider.Text = t.Provider;
            TxtToggle.Text = t.Toggle;
            BtnStart.Content = t.Start;
            RunSourceLink.Text = t.Source;

            PanelSent.Children.Clear();
            foreach (var s in t.Sent)
            {
                PanelSent.Children.Add(new TextBlock
                {
                    Text = "•  " + s,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(6, 2, 0, 0),
                });
            }

            foreach (var child in LangPanel.Children)
                if (child is ToggleButton b) b.IsChecked = (b.Tag as string) == lang;
        }

        private void Lang_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton b && b.Tag is string lang) ApplyLanguage(lang);
        }

        private void LinkTutorials_Click(object sender, RoutedEventArgs e) => Open(_tutorialsUrl);
        private void LinkSource_Click(object sender, RoutedEventArgs e) => Open(SourceUrl);

        private static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /*  */ }
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close(); 
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); } catch { }
        }
    }
}
