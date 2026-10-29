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
        // Prefere cantos arredondados nativos no Windows 11, sem usar janela em camadas.
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
                TelIntro = "To know what to improve, FrontLine can send a small anonymous report about how it's set up. It's sent at most once a day.",
                SentLabel = "WHAT IS SENT",
                Sent = new[]
                {
                    "The theme you picked",
                    "The lyric font",
                    "The lyric color",
                    "The app language",
                    "Whether a lock shortcut is set, and which keys",
                    "The app version and your Windows language",
                },
                NeverSent = "NEVER sent: songs, artists, lyrics, searches, your name, e-mail, or any ID that identifies you.",
                Provider = "Data goes to Aptabase, a privacy-focused analytics service. You can turn this off any time in Settings (App tab).",
                Toggle = "Send anonymous usage statistics",
                Start = "Get started",
                Source = "Open source: see exactly what is sent in the code (Telemetry.cs) and the README",
            },
            ["pt"] = new Texts
            {
                Title = "Bem-vindo(a) ao FrontLine Lyrics",
                Thanks = "Obrigado por usar o FrontLine Lyrics! É um projeto gratuito e open-source, e saber que ele é útil para você significa muito.",
                TutorialsLead = "Primeira vez por aqui? ",
                TutorialsLink = "Veja os tutoriais",
                TelHeader = "Estatísticas de uso anônimas",
                TelIntro = "Para saber o que melhorar, o FrontLine pode enviar um pequeno relatório anônimo sobre como ele está configurado. O envio acontece no máximo uma vez por dia.",
                SentLabel = "O QUE É ENVIADO",
                Sent = new[]
                {
                    "O tema escolhido",
                    "A fonte da letra",
                    "A cor da letra",
                    "O idioma do app",
                    "Se há atalho de bloqueio configurado e quais teclas",
                    "A versão do app e o idioma do Windows",
                },
                NeverSent = "NUNCA é enviado: músicas, artistas, letras, buscas, seu nome, e-mail ou qualquer identificador que aponte para você.",
                Provider = "Os dados vão para o Aptabase, um serviço de análise focado em privacidade. Você pode desativar quando quiser em Configurações (aba App).",
                Toggle = "Enviar estatísticas anônimas de uso",
                Start = "Começar",
                Source = "Open-source: veja exatamente o que é enviado no código (Telemetry.cs) e no README",
            },
            ["es"] = new Texts
            {
                Title = "Te damos la bienvenida a FrontLine Lyrics",
                Thanks = "¡Gracias por usar FrontLine Lyrics! Es un proyecto gratuito y de código abierto, y saber que te resulta útil significa mucho.",
                TutorialsLead = "¿Primera vez por aquí? ",
                TutorialsLink = "Mira los tutoriales",
                TelHeader = "Estadísticas de uso anónimas",
                TelIntro = "Para saber qué mejorar, FrontLine puede enviar un pequeño informe anónimo sobre cómo está configurada. Se envía como máximo una vez al día.",
                SentLabel = "QUÉ SE ENVÍA",
                Sent = new[]
                {
                    "El tema elegido",
                    "La fuente de la letra",
                    "El color de la letra",
                    "El idioma de la app",
                    "Si hay un atajo de bloqueo configurado y qué teclas",
                    "La versión de la app y el idioma de Windows",
                },
                NeverSent = "NUNCA se envía: canciones, artistas, letras, búsquedas, tu nombre, correo ni ningún identificador que te señale.",
                Provider = "Los datos van a Aptabase, un servicio de analítica centrado en la privacidad. Puedes desactivarlo cuando quieras en Configuración (pestaña App).",
                Toggle = "Enviar estadísticas de uso anónimas",
                Start = "Empezar",
                Source = "Código abierto: mira exactamente qué se envía en el código (Telemetry.cs) y en el README",
            },
            ["id"] = new Texts
            {
                Title = "Selamat datang di FrontLine Lyrics",
                Thanks = "Terima kasih telah menggunakan FrontLine Lyrics! Ini proyek gratis dan open-source, dan senang sekali mengetahui aplikasinya berguna untukmu.",
                TutorialsLead = "Baru di sini? ",
                TutorialsLink = "Lihat tutorialnya",
                TelHeader = "Statistik penggunaan anonim",
                TelIntro = "Agar tahu apa yang perlu ditingkatkan, FrontLine dapat mengirim laporan anonim singkat tentang pengaturan aplikasi. Dikirim paling banyak sekali sehari.",
                SentLabel = "YANG DIKIRIM",
                Sent = new[]
                {
                    "Tema yang dipilih",
                    "Font lirik",
                    "Warna lirik",
                    "Bahasa aplikasi",
                    "Apakah pintasan kunci diatur, dan tombolnya",
                    "Versi aplikasi dan bahasa Windows",
                },
                NeverSent = "TIDAK PERNAH dikirim: lagu, artis, lirik, pencarian, nama, e-mail, atau ID apa pun yang mengidentifikasi kamu.",
                Provider = "Data dikirim ke Aptabase, layanan analitik yang mengutamakan privasi. Kamu bisa mematikannya kapan saja di Pengaturan (tab Aplikasi).",
                Toggle = "Kirim statistik penggunaan anonim",
                Start = "Mulai",
                Source = "Open-source: lihat persis apa yang dikirim di kode (Telemetry.cs) dan README",
            },
        };

        public static string DetectSystemLanguage()
        {
            string two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            if (two == "in") two = "id"; // código antigo do indonésio
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
            catch { /* sem navegador padrão: ignora */ }
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close(); // mesmo efeito de "Começar" (mantém o toggle como está)
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); } catch { }
        }
    }
}
