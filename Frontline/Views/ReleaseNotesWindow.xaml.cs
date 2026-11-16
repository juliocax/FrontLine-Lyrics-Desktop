using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace FrontLineOverlay
{
    public partial class ReleaseNotesWindow : Window
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

        public static string CurrentVersion =>
            typeof(ReleaseNotesWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        private string _language;

        private static readonly Dictionary<string, Dictionary<string, string[]>> Notes = new()
        {
            ["1.7.0"] = new()
            {
                ["en"] = new[]
                {
                    "On-device translation models\nInstall models for the language pairs you use most for more natural translations. Choose a source and a target in Manage models. The app finds a route and downloads only the missing models. For example, Japanese → Brazilian Portuguese can automatically use Japanese → English and English → Brazilian Portuguese.",
                    "Improved romanization\nMandarin, Korean and Thai now use readings tailored to each language. For Japanese, an optional dictionary improves the readings of kana and kanji. The dictionary would add substantial size to the installation, so it is available as an add-on. Romanization writes the lyrics in Latin letters to help you follow the pronunciation.",
                    "New add-ons screen\nFind translation models and the Japanese dictionary in Settings → Translation → Manage add-ons. The separate downloads keep the app installation small and let you install only what you need for the languages you listen to.",
                    "New translation and romanization controls\nOriginal and Romanization are two choices for the same setting. Switching on one turns off the other. Translate has its own switch. When it is off, the selected mode shows the main lyrics. When it is on, the translation becomes the main lyrics. If you also enable Show lyrics below the translation, the Original or Romanization setting determines what appears underneath.",
                },
                ["pt"] = new[]
                {
                    "Modelos de tradução no dispositivo\nAgora você pode instalar modelos para os pares de idiomas que mais usa e obter traduções mais naturais. Basta escolher o idioma de origem e o de destino em Gerenciar modelos. Na hora da instalação, o app encontra a rota e baixa só os modelos que faltam. Se você escolher Japonês → Português (Brasil), por exemplo, o app pode combinar Japonês → Inglês e Inglês → Português (Brasil) automaticamente.",
                    "Romanização aprimorada\nA romanização foi aprimorada em mandarim, coreano e tailandês, com leituras próprias para cada idioma. Para o japonês, você pode instalar um dicionário que melhora a leitura de kana e kanji. Como esse dicionário aumentaria significativamente o tamanho da instalação, ele fica disponível como complemento opcional. A romanização escreve a letra com caracteres latinos para ajudar você a acompanhar a pronúncia.",
                    "Nova tela de complementos\nEm Configurações → Tradução → Gerenciar complementos, você encontra os modelos de tradução e o dicionário de romanização japonesa. Esses arquivos ficam disponíveis para download separado para manter a instalação do app leve. Assim, você baixa apenas os complementos úteis para os idiomas que costuma ouvir.",
                    "Novos controles de tradução e romanização\nOriginal e Romanização são opções da mesma escolha. Ao ligar uma, a outra desliga. Traduzir tem um interruptor independente. Com ele desligado, a letra principal aparece no modo escolhido. Com ele ligado, a tradução vira a letra principal. Se você também ativar Mostrar letra abaixo da tradução, a escolha entre Original e Romanização define o que aparece abaixo.",
                },
                ["es"] = new[]
                {
                    "Modelos de traducción en tu dispositivo\nInstala modelos para los pares de idiomas que más usas y obtén traducciones más naturales. Elige el idioma de origen y el de destino en Gestionar modelos. La aplicación calcula la ruta y descarga solo los modelos que faltan. Por ejemplo, Japonés → Portugués (Brasil) puede combinar automáticamente Japonés → Inglés e Inglés → Portugués (Brasil).",
                    "Romanización mejorada\nMejoramos la romanización del mandarín, el coreano y el tailandés con lecturas adaptadas a cada idioma. Para el japonés, puedes instalar un diccionario que mejora la lectura de kana y kanji. Como el diccionario aumentaría considerablemente el tamaño de la instalación, está disponible como complemento opcional. La romanización usa caracteres latinos para facilitar el seguimiento de la pronunciación.",
                    "Nueva pantalla de complementos\nEn Ajustes → Traducción → Gestionar complementos encontrarás los modelos de traducción y el diccionario japonés. Los archivos se descargan por separado para mantener la instalación ligera y permitirte instalar solo lo que necesites.",
                    "Nuevos controles de traducción y romanización\nOriginal y Romanización son opciones del mismo ajuste. Al activar una, se desactiva la otra. Traducir tiene su propio interruptor. Si lo desactivas, la letra principal sigue el modo elegido. Si lo activas, la traducción ocupa ese lugar. Si también activas Mostrar letra debajo de la traducción, la opción Original o Romanización determina lo que aparece debajo.",
                },
                ["id"] = new[]
                {
                    "Model terjemahan di perangkat\nPasang model untuk pasangan bahasa yang paling sering kamu gunakan agar terjemahan terdengar lebih alami. Pilih bahasa sumber dan tujuan di Kelola model. Aplikasi menentukan jalurnya dan hanya mengunduh model yang belum terpasang. Misalnya, Jepang → Portugis (Brasil) dapat otomatis memakai Jepang → Inggris dan Inggris → Portugis (Brasil).",
                    "Romanisasi yang lebih baik\nRomanisasi bahasa Mandarin, Korea, dan Thai kini memakai pembacaan yang disesuaikan dengan tiap bahasa. Untuk bahasa Jepang, kamus opsional memperbaiki pembacaan kana dan kanji. Kamus tersedia sebagai add-on karena ukurannya akan menambah ukuran instalasi secara signifikan. Romanisasi menampilkan lirik dengan huruf Latin agar pelafalannya lebih mudah diikuti.",
                    "Layar add-on baru\nModel terjemahan dan kamus Jepang tersedia di Pengaturan → Terjemahan → Kelola add-on. Unduhan terpisah menjaga ukuran instalasi tetap kecil sehingga kamu cukup memasang add-on yang diperlukan untuk bahasa yang biasa kamu dengarkan.",
                    "Kontrol terjemahan dan romanisasi baru\nAsli dan Romanisasi adalah dua pilihan untuk pengaturan yang sama. Mengaktifkan salah satunya akan menonaktifkan yang lain. Terjemahkan mempunyai sakelar tersendiri. Saat dimatikan, lirik utama mengikuti mode yang dipilih. Saat dinyalakan, terjemahan menjadi lirik utama. Jika Tampilkan lirik di bawah terjemahan juga aktif, pilihan Asli atau Romanisasi menentukan tampilan di bawahnya.",
                },
            },
            ["1.6.2"] = new()
            {
                ["en"] = new[]
                {
                    "Addresses an issue that affected saved preferences after updating to 1.6.1.",
                    "Fixed synced lyrics lookup for tracks with titles written partly or entirely in non-Latin scripts.",
                    "The update button appears below the logo when a new version is available.",
                },
                ["pt"] = new[]
                {
                    "Corrigimos um problema que afetava as preferências salvas após a atualização para a 1.6.1.",
                    "Corrigimos a busca de letras sincronizadas para títulos escritos total ou parcialmente em sistemas de escrita não latinos.",
                    "Quando há uma nova versão, o botão de atualização aparece abaixo da logo.",
                },
                ["es"] = new[]
                {
                    "Corregimos un problema que afectaba a las preferencias guardadas tras actualizar a la versión 1.6.1.",
                    "Corregimos la búsqueda de letras sincronizadas para títulos escritos total o parcialmente en sistemas de escritura no latinos.",
                    "Cuando hay una nueva versión, el botón de actualización aparece debajo del logotipo.",
                },
                ["id"] = new[]
                {
                    "Memperbaiki masalah pada preferensi tersimpan setelah pembaruan ke versi 1.6.1.",
                    "Memperbaiki pencarian lirik tersinkronisasi untuk judul yang ditulis sebagian atau seluruhnya dengan aksara non-Latin.",
                    "Tombol pembaruan muncul di bawah logo saat versi baru tersedia.",
                }
            },
            ["1.6.1"] = new()
            {
                ["en"] = new[] { "Fixed lyric matching for tracks with titles written partly or entirely in non-Latin scripts.", "The update button now appears below the logo when a new version is available." },
                ["pt"] = new[] { "Corrigimos a busca de letras para músicas com títulos escritos total ou parcialmente em alfabetos e sistemas de escrita não latinos.", "Quando há uma nova versão, o botão de atualização aparece abaixo da logo." },
                ["es"] = new[] { "Corregimos la búsqueda de letras para títulos escritos total o parcialmente en sistemas de escritura no latinos.", "Cuando hay una nueva versión, el botón de actualización aparece debajo del logotipo." },
                ["id"] = new[] { "Pencarian lirik untuk judul yang ditulis sebagian atau seluruhnya dengan aksara non-Latin telah diperbaiki.", "Tombol pembaruan muncul di bawah logo saat versi baru tersedia." }
            },
            ["1.6.0"] = new()
            {
                ["en"] = new[]
                {
                    "A fresh look: new logo, refreshed settings and a more cohesive visual identity.",
                    "New themes: Neon Nights, City Lights and Cat Lovers, alongside the existing and seasonal themes.",
                    "Use the active theme behind lyrics and adjust its background opacity in Lyrics settings.",
                    "Choose 1, 3 or 5 lines, or the full lyrics, with left, center or right alignment.",

                },
                ["pt"] = new[]
                {
                    "Visual renovado: nova logo, configurações redesenhadas e uma identidade visual mais integrada.",
                    "Novos temas: Noites Neon, Luzes da Cidade e Amantes de Gatos, junto dos temas existentes e sazonais.",
                    "Use o tema ativo atrás das letras e ajuste a opacidade do fundo nas configurações de Letras.",
                    "Escolha 1, 3 ou 5 linhas, ou a letra completa, com alinhamento à esquerda, ao centro ou à direita.",

                },
                ["es"] = new[]
                {
                    "Nueva imagen: nuevo logo, ajustes rediseñados y una identidad visual más integrada.",
                    "Nuevos temas: Noches Neón, Luces de la Ciudad y Amantes de los Gatos, además de los temas existentes y estacionales.",
                    "Usa el tema activo detrás de las letras y ajusta la opacidad del fondo en los ajustes de Letras.",
                    "Elige 1, 3 o 5 líneas, o la letra completa, con alineación izquierda, centrada o derecha.",

                },
                ["id"] = new[]
                {
                    "Tampilan baru: logo baru, pengaturan yang didesain ulang dan identitas visual yang lebih selaras.",
                    "Tema baru: Malam Neon, Cahaya Kota dan Pencinta Kucing, selain tema lama dan musiman.",
                    "Gunakan tema aktif di belakang lirik dan atur opasitas latar di pengaturan Lirik.",
                    "Pilih 1, 3 atau 5 baris, atau seluruh lirik, dengan perataan kiri, tengah atau kanan.",

                },
            },
            ["1.5.3"] = new()
            {
                ["en"] = new[] { "Improved handling of missing lyrics and temporary service errors." },
                ["pt"] = new[] { "Melhorias nos avisos de letras não encontradas e falhas temporárias do serviço." },
                ["es"] = new[] { "Mejoras en los avisos de letras no encontradas y errores temporales del servicio." },
                ["id"] = new[] { "Peningkatan penanganan lirik yang tidak ditemukan dan gangguan layanan sementara." }
            },
            ["1.5.2"] = new()
            {
                ["en"] = new[] { "Minor adjustments." },
                ["pt"] = new[] { "Pequenos ajustes." },
                ["es"] = new[] { "Pequeños ajustes." },
                ["id"] = new[] { "Penyesuaian kecil." }
            },
            ["1.5.1"] = new()
            {
                ["en"] = new[] {
                    "Added clear notices when the lyrics service is unavailable or an internet connection fails.",
                    "Improved Spotify track-change detection and playback synchronization during slow lyrics requests.",
                    "Fixed incorrect lyrics matches and improved searches for covers, remasters and featured artists.",
                    "Improved recovery from failed recognition and lyrics lookups."
                },
                ["pt"] = new[] {
                    "Novos avisos quando a fonte de letras estiver indisponível ou houver problemas de conexão.",
                    "Melhorias na detecção de troca de faixa do Spotify e na sincronização durante buscas lentas.",
                    "Correções de letras incorretas e buscas aprimoradas para covers, remasters e participações.",
                    "Melhor recuperação de falhas no reconhecimento e na busca de letras."
                },
                ["es"] = new[] {
                    "Nuevos avisos cuando la fuente de letras no esté disponible o haya problemas de conexión.",
                    "Mejor detección de cambios de pista en Spotify y sincronización durante búsquedas lentas.",
                    "Correcciones de letras incorrectas y búsquedas mejoradas para versiones, remasterizaciones y colaboraciones.",
                    "Mejor recuperación ante fallos de reconocimiento y búsqueda de letras."
                },
                ["id"] = new[] {
                    "Notifikasi baru saat sumber lirik tidak tersedia atau koneksi internet bermasalah.",
                    "Peningkatan deteksi pergantian lagu Spotify dan sinkronisasi saat pencarian lirik lambat.",
                    "Perbaikan hasil lirik yang keliru serta pencarian versi cover, remaster, dan kolaborasi.",
                    "Pemulihan yang lebih baik saat pengenalan lagu atau pencarian lirik gagal."
                }
            },
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

        internal const string Version16AnnouncementUrl = "https://buymeacoffee.com/juliocax/frontline-lyrics-1-6";

        private static string? FullReleaseUrl => CurrentVersion switch
        {
            "1.7.0" => "https://buymeacoffee.com/juliocax/frontline-lyrics-1-7",
            "1.6.0" or "1.6.1" or "1.6.2" => Version16AnnouncementUrl,
            "1.5.0" or "1.5.1" or "1.5.2" or "1.5.3" => "https://buymeacoffee.com/juliocax/frontline-lyrics-1-5-0",
            _ => null
        };

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
            TxtFullRelease.Visibility = (FullReleaseUrl != null)
                ? Visibility.Visible : Visibility.Collapsed;
            LinkFullRelease.Inlines.Clear();
            if (FullReleaseUrl != null)
                LinkFullRelease.Inlines.Add(FullReleaseLabels[language]);
            PanelChanges.Children.Clear();
            var changes = Notes.TryGetValue(CurrentVersion, out var translations)
                && translations.TryGetValue(language, out var items)
                ? items : new[] { labels[4] };
            foreach (var item in changes)
            {
                int headingEnd = item.IndexOf('\n');
                var entry = new TextBlock
                {
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255)),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 0, 5)
                };
                if (headingEnd < 0)
                    entry.Text = "•  " + item;
                else
                {
                    entry.Margin = new Thickness(0, 6, 0, 12);
                    entry.Inlines.Add(new Run(item[..headingEnd]) { FontWeight = FontWeights.SemiBold });
                    entry.Inlines.Add(new LineBreak());
                    entry.Inlines.Add(new Run(item[(headingEnd + 1)..]));
                }
                PanelChanges.Children.Add(entry);
            }
            ChangesScroll.ScrollToTop();
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
                var url = FullReleaseUrl;
                if (url != null)
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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
