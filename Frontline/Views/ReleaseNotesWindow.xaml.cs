using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FrontLineOverlay
{
    public partial class ReleaseNotesWindow : Window
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

        // Usa a mesma versão enviada pela telemetria. Altere o número de versão
        // no projeto a cada release; a chave salva permite exibir uma vez por versão.
        public static string CurrentVersion =>
            typeof(ReleaseNotesWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        private string _language;

        // Edite este catálogo ao preparar cada versão. O fallback nunca inventa novidades.
        // Exemplo para a PRÓXIMA versão: substitua pela versão real ao publicar.
        private static readonly Dictionary<string, Dictionary<string, string[]>> Notes = new()
        {
            ["1.5.0"] = new()
            {
                ["en"] = new[] {
                    "Refreshed Christmas and New Year themes, plus cleaner Support, Rate and Feedback buttons.",
                    "LISTEN now shows a loading animation until the audio service is ready.",
                    "Improved lyric font resizing and updated seasonal theme dates.",
                    "Optional anonymous settings telemetry, controlled under Settings → General.",
                    "Coming to Supporter Early Access: 1, 3 or 5 lines, lyric alignment and more themes."
                },
                ["pt"] = new[] {
                    "Temas de Natal e Ano-Novo renovados; botões Apoie, Avalie e Feedback redesenhados.",
                    "O botão OUVIR agora mostra uma animação até o serviço de áudio ficar pronto.",
                    "Melhorias no tamanho das letras e novas datas dos temas sazonais.",
                    "Telemetria anônima opcional, controlada em Configurações → Geral.",
                    "Em breve no Acesso Antecipado: 1, 3 ou 5 linhas, alinhamento e mais temas."
                },
                ["es"] = new[] {
                    "Temas de Navidad y Año Nuevo renovados; botones de apoyo, valoración y comentarios rediseñados.",
                    "ESCUCHAR muestra una animación mientras se inicia el servicio de audio.",
                    "Mejor ajuste del tamaño de las letras y nuevas fechas de temas de temporada.",
                    "Telemetría anónima opcional desde Ajustes → General.",
                    "Próximamente en acceso anticipado: 1, 3 o 5 líneas, alineación y más temas."
                },
                ["id"] = new[] {
                    "Tema Natal dan Tahun Baru diperbarui, begitu juga tombol Dukungan, Nilai, dan Masukan.",
                    "DENGAR kini menampilkan animasi saat layanan audio dimulai.",
                    "Ukuran font lirik lebih baik dan jadwal tema musiman diperbarui.",
                    "Telemetri pengaturan anonim opsional di Pengaturan → Umum.",
                    "Segera di Akses Awal pendukung: 1, 3, atau 5 baris, perataan lirik, dan tema lainnya."
                }
            },
            ["1.4.3"] = new()
            {
                ["en"] = new[] { "Lyric color is now included in optional anonymous configuration statistics.", "New what's-new window on the first launch after an update.", "Release notes can also be opened from General settings." },
                ["pt"] = new[] { "A cor da letra agora faz parte das estatísticas opcionais e anônimas de configuração.", "Nova tela de novidades no primeiro uso após uma atualização.", "As notas da versão também podem ser abertas nas configurações gerais." },
                ["es"] = new[] { "El color de la letra ahora se incluye en las estadísticas anónimas opcionales.", "Nueva pantalla de novedades tras actualizar la aplicación.", "Las notas también se pueden abrir desde los ajustes generales." },
                ["id"] = new[] { "Warna lirik kini disertakan dalam statistik konfigurasi anonim opsional.", "Layar catatan rilis baru saat pertama kali membuka versi terbaru.", "Catatan rilis juga dapat dibuka dari pengaturan umum." }
            }
        };

        private static readonly Dictionary<string, string[]> Ui = new()
        {
            ["en"] = new[] { "What's new in FrontLine Lyrics", "Here's what changed in this version.", "IN THIS RELEASE", "Got it", "Release notes have not been added for this version yet." },
            ["pt"] = new[] { "Novidades do FrontLine Lyrics", "Veja o que mudou nesta versão.", "NESTA VERSÃO", "ENTENDI", "As notas desta versão ainda não foram cadastradas." },
            ["es"] = new[] { "Novedades de FrontLine Lyrics", "Esto es lo que ha cambiado en esta versión.", "EN ESTA VERSIÓN", "ENTENDIDO", "Todavía no se han añadido notas para esta versión." },
            ["id"] = new[] { "Yang baru di FrontLine Lyrics", "Berikut perubahan dalam versi ini.", "DALAM RILIS INI", "MENGERTI", "Catatan untuk versi ini belum tersedia." }
        };

        private const string FullReleaseUrl = "https://buymeacoffee.com/juliocax/frontline-lyrics-1-5-0";

        private static readonly Dictionary<string, string> FullReleaseLabels = new()
        {
            ["en"] = "Read the full announcement on Buy Me a Coffee",
            ["pt"] = "Leia o anúncio completo no Buy Me a Coffee",
            ["es"] = "Lee el anuncio completo en Buy Me a Coffee",
            ["id"] = "Baca pengumuman lengkap di Buy Me a Coffee"
        };

        public ReleaseNotesWindow(string language)
        {
            InitializeComponent();
            SourceInitialized += (_, _) => EnableNativeRoundedCorners();
            MaxHeight = SystemParameters.WorkArea.Height - 20;
            _language = Ui.ContainsKey(language) ? language : "en";
            ApplyLanguage(_language);
        }

        private void ApplyLanguage(string language)
        {
            _language = language;
            var labels = Ui[language];
            TxtTitle.Text = labels[0];
            TxtVersion.Text = "v" + CurrentVersion;
            TxtDescription.Text = labels[1];
            TxtChangesTitle.Text = labels[2];
            BtnDone.Content = labels[3];
            TxtFullRelease.Visibility = CurrentVersion == "1.5.0"
                ? Visibility.Visible : Visibility.Collapsed;
            LinkFullRelease.Inlines.Clear();
            if (CurrentVersion == "1.5.0")
                LinkFullRelease.Inlines.Add(FullReleaseLabels[language]);
            PanelChanges.Children.Clear();
            var changes = Notes.TryGetValue(CurrentVersion, out var translations)
                && translations.TryGetValue(language, out var items)
                ? items : new[] { labels[4] };
            foreach (var item in changes)
                PanelChanges.Children.Add(new TextBlock
                {
                    Text = "•  " + item, FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255)),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 0, 5)
                });
            foreach (var child in LangPanel.Children)
                if (child is ToggleButton b) b.IsChecked = (b.Tag as string) == language;
        }

        private void Lang_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton b && b.Tag is string lang && Ui.ContainsKey(lang))
                ApplyLanguage(lang);
        }
        private void LinkFullRelease_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(FullReleaseUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Could not open release URL: " + ex.Message);
            }
        }

        private void BtnDone_Click(object sender, RoutedEventArgs e) => Close();
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
