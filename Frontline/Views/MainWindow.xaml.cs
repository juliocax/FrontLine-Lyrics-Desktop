using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Linq;
using System.Collections.ObjectModel;
using System.Windows.Data;
using System.Windows.Input;
using System.ComponentModel;

namespace FrontLineOverlay
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hwnd, int index, int newStyle);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr newStyle);
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Win32Point pt);
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private ImageSource? _icoLock;
        private ImageSource? _icoLockOpen;

        private bool isLocked = false;
        private const int LOCK_HOTKEY_ID = 9000;
        private uint lockHotkeyModifiers = 0;
        private uint lockHotKeyVk = 0;
        private bool isCapturingHotkey = false;
        private HwndSource? _hwndSource;
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
        private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

        private static void SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newStyle)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, index, newStyle);
            else SetWindowLong32(hwnd, index, newStyle.ToInt32());
        }

        private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        private const byte VK_MEDIA_PREV_TRACK = 0xB1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const int MaxWsMessageBytes = 512_000;
        private const int CoverDecodeWidth = 260;
        private const int SkipIconDecodeWidth = 64;
        private const int MaxPythonRestarts = 5;

        private const string HelpWebsiteUrl = "https://juliocax.github.io/FrontLine-Lyrics-Desktop/documentation_v1.5.0.html";
        private const string FeedbackFormUrl = "https://forms.gle/bwV1xeKncmGPATDaA";
        // Product ID taken from the Microsoft Store link already present in README.md (9P6LNJCL8ZCC).
        private const string StoreReviewUrl = "ms-windows-store://review/?ProductId=9P6LNJCL8ZCC";

        [StructLayout(LayoutKind.Sequential)] public struct Win32Point { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        private static Mutex? _appMutex;

        private readonly Random _spinnerRandom = new Random();
        private bool _spinnerAnimating = false;

        private bool isGhostMode = false;
        private bool isResizing = false;
        private bool isManualSyncMode = false;
        private string serverPort = "8765";
        private string currentAppStatus = "IDLE";
        private string currentCoverUrl = "";
        private ClientWebSocket? _webSocket;
        private double bgOpacity = 0.8;
        private bool _playerClock;
        private bool _syncingLangPicker;
        private bool keepOriginalWithTranslation = false;
        private string lastCurrentLyricsOriginal = "";
        private bool lastIsTranslatedActive = false;

        private LockButtonWindow? _lockWin;

        private void ShowLockWin(bool show)
        {
            if (!show || WindowState == WindowState.Minimized)
            {
                if (_lockWin != null && _lockWin.IsVisible) _lockWin.Hide();
                return;
            }

            if (_lockWin == null)
            {
                _lockWin = new LockButtonWindow { Owner = this };
                _lockWin.Clicked += () => ToggleLock(false);
                _lockWin.SetTip(uiStrings[currentAppLanguage]["TipUnlock"]);
                ApplySkipGlyph(_lockWin.Icon, _icoLock);
            }

            _lockWin.Left = Left + 6;
            _lockWin.Top = Top + 6;
            if (!_lockWin.IsVisible) _lockWin.Show();
        }

        private readonly DispatcherTimer _mouseTracker = new() { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly DispatcherTimer _ghostTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly DispatcherTimer _opacityPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };

        private Process? pythonServerProcess;
        private readonly CancellationTokenSource _shutdown = new();
        private bool _shuttingDown;
        private int _pythonRestartCount;
        private string? _lastFullLyricsJson;
        private string? _autoHoldKey;
        private DateTime _autoHoldUntilUtc = DateTime.MinValue;

        // Persistence (source / Auto / position): idea by Warith Adetayo,
        // stored in ApplicationData.LocalSettings (MSIX) or local JSON.

        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
        private bool _loadingSettings;
        private DispatcherFrame? _darkDialogFrame;
        private bool _darkDialogResult;
        private bool _wantAuto;
        private bool _autoSyncedWithServer;
        private readonly ObservableCollection<SearchHistoryEntry> _searchHistory = [];
        private ICollectionView? _historyView;
        private string _historySort = "Date";
        private ListSortDirection _historyDir = ListSortDirection.Descending;

        private string currentAppLanguage = "en";
        private string _settingsTab = "lyrics";
        private bool _welcomeWasShownAtLaunch;
        private bool _settingsPreview;
        private readonly Dictionary<string, Dictionary<string, string>> uiStrings = new()
        {
            { "en", new() {
                { "Listen", "LISTEN" }, { "Search", "SEARCH" }, { "ManualSearch", "MANUAL SEARCH" },
                { "Artist", "Artist:" }, { "Song", "Song:" }, { "Find", "FIND" }, { "Cancel", "CANCEL" },
                { "Settings", "SETTINGS" }, { "SettingsTabLyrics", "Lyrics" }, { "SettingsTabTranslate", "Translate" }, { "SettingsTabApp", "General" }, { "ManualSync", "MANUAL SYNC" }, { "Translate", "TRANSLATE LYRICS" },
                { "UILang", "APP LANGUAGE" }, { "Ready", "Ready." },
                { "Listening", "Listening..." }, { "Searching", "Searching lyrics..." },
                { "SideListen", "LISTEN" }, { "SideClear", "CLEAR" }, { "Auto", "AUTO" },
                { "KeepOriginal", "Also show original lyrics" }, { "PreviewLabel", "PREVIEW" },
                { "MockPrevious", "♪ this is a preview line ♪" }, { "MockCurrent", "This is how your lyrics will look" },
                { "MockNext", "♪ next line preview ♪" }, { "MockCurrentOriginal", "(original) this is a preview line" },
                { "DonateTip", "Leave an optional tip" }, { "ReleaseNotesButton", "VIEW RELEASE NOTES" }, { "ReleaseNotesHeading", "WHAT'S NEW" }, { "ReleaseNotesTooltip", "See what's new in this version" },
                { "FeedbackLink", "Share your feedback" },
                { "SupportPrompt", "Enjoying the app?" }, { "SupportDonate", "Support" }, { "SupportRate", "Rate" }, { "SupportFeedback", "Feedback" },
                { "TipSupportDonate", "Leave an optional tip to support development." }, { "TipSupportRate", "Rate FrontLine Lyrics on the Store." }, { "TipSupportFeedback", "Report problems or suggest improvements." },
                { "PrevTrack", "Previous track" }, { "NextTrack", "Next track" },
                { "SearchHistory", "SEARCH HISTORY" }, { "HistoryArtist", "Artist" },
                { "HistorySong", "Song" }, { "HistoryDate", "Date" },
                { "HistoryEmpty", "No searches yet." }, { "HistoryRemove", "Remove" },
                { "FontSizeTitle", "FONT SIZE" },
                { "BgOpacityTitle", "BACKGROUND OPACITY" },
                { "Festival", "FESTIVAL" }, { "FestivalMode", "FESTIVAL MODE" },
                { "FestivalIntro", "At live shows, Shazam often fails. Load a setlist, preload lyrics, then tap the first line when the singer starts." },
                { "FestivalApiKey", "setlist.fm API key" },
                { "FestivalApiKeyHint", "Paste your key to search public setlists by artist, and optionally country, venue, or year." },
                { "FestivalApiKeySaved", "Key saved. Paste a new one to replace it." },
                { "FestivalApiLink", "Get a free API key" },
                { "FestivalSaveKey", "SAVE KEY" }, { "FestivalClose", "CLOSE" },
                { "FestivalPlaylists", "SAVED SETLISTS" },
                { "FestivalNewArtist", "Artist or band" }, { "FestivalCreate", "CREATE" },
                { "FestivalEmpty", "No setlists yet. Enter an artist to create one." },
                { "FestivalFetching", "Searching setlists…" },
                { "FestivalNoSetlist", "No setlist found. Starting empty — add songs below." },
                { "FestivalEditorTitle", "SETLIST" }, { "FestivalBack", "BACK" },
                { "FestivalSongsEmpty", "Empty setlist. Add songs below or search during the show." },
                { "FestivalDragHint", "Drag a song to change the order" },
                { "FestivalAdd", "ADD" }, { "FestivalStart", "START" }, { "FestivalDone", "DONE" },
                { "FestivalEdit", "EDIT SETLIST" }, { "FestivalStop", "STOP FESTIVAL" },
                { "FestivalPrevLine", "Previous lyric line" }, { "FestivalNextLine", "Next lyric line" },
                { "FestivalSongOne", "1 song" }, { "FestivalSongMany", "{0} songs" },
                { "FestivalDeleteConfirm", "Delete this setlist?" },
                { "ConfirmAction", "Delete" }, { "ConfirmCancel", "Cancel" }, { "ConfirmOk", "OK" },
                { "FestivalSearchArtist", "Search artist or band" },
                { "FestivalPlaylistName", "Playlist name" },
                { "FestivalSearch", "SEARCH" },
                { "FestivalEmptySearch", "Search by artist or band. Country, venue, and year are optional." },
                { "FestivalSearchResults", "RECENT SETLISTS" },
                { "FestivalSearchCountry", "Country (optional)" },
                { "FestivalSearchVenue", "Venue (optional)" },
                { "FestivalSearchYear", "Year (optional)" },
                { "FestivalNoResults", "No setlists matched. Spell the artist or band the way it appears on setlist.fm. Country can be a 2-letter code (BR, US) or the full name. Venue and year are optional — if this is too narrow, search with the artist only." },
                { "FestivalCreateFromResult", "CREATE PLAYLIST" },
                { "FestivalApiKeyBad", "API key rejected. Check it at setlist.fm." },
                { "FestivalPrevSong", "Previous song in the setlist" },
                { "FestivalNextSong", "Next song in the setlist" },
                { "FestivalNoLyrics", "No lyrics for this song yet." },
                { "FestivalNextUp", "NEXT UP" },
                { "FestivalContinue", "CONTINUE" },
                { "FestivalSetlistEnd", "End of the setlist." },
                { "TipFestivalContinue", "Start the next song in the setlist" },
                { "TipListen", "Identify what's playing and show synced lyrics" },
                { "TipSearch", "Search for a song by artist and title" },
                { "TipFestival", "Open Festival Mode for live shows" },
                { "TipHelp", "Open the Frontline website" },
                { "TipBack", "Go back" },
                { "TipSettings", "Settings" },
                { "TipMinimize", "Minimize" },
                { "TipRestore", "Restore window" },
                { "TipClose", "Close Frontline" },
                { "TipAuto", "Follow the player automatically" },
                { "TipClear", "Clear the current lyrics" },
                { "TipFind", "Search lyrics for this artist and song" },
                { "TipFestivalSearch", "Search public setlists by artist, with optional country, venue, and year" },
                { "TipFestivalCreate", "Create an empty playlist with this name" },
                { "TipFestivalCreateFromResult", "Create a playlist from the selected setlist" },
                { "TipFestivalSaveKey", "Save your setlist.fm API key on this device" },
                { "TipFestivalStart", "Start this setlist in Festival Mode" },
                { "TipFestivalDone", "Return to the lyrics" },
                { "TipFestivalAdd", "Add this song to the setlist" },
                { "TipFestivalEdit", "Edit the current festival setlist" },
                { "TipFestivalStop", "Stop Festival Mode and save the setlist" },
                { "TipManualSync", "Tap a lyric line to set the timing" },
                { "LockShortcutTitle", "LOCK SHORTCUT" },
                { "LockShortcutHint", "(appears in the left corner on mouse hover)" },
                { "LockFirstTime", "The overlay is locked." },
                { "LockToastMsg", "Locked. Click the padlock to unlock." },
                { "HotkeyFailed", "This combination is already in use by Windows or another app. Try another one." },
                { "LockNone", "None (click to set)" },
                { "LockCapturing", "Press... (Esc cancels, Del clears)" },
                { "TipLock", "Lock overlay: clicks pass through to the app behind it" },
                { "TipUnlock", "Unlock overlay: move and click it again" },
            }},
            { "pt", new() {
                { "Listen", "OUVIR" }, { "Search", "BUSCAR" }, { "ManualSearch", "BUSCA MANUAL" },
                { "Artist", "Artista:" }, { "Song", "Música:" }, { "Find", "BUSCAR" }, { "Cancel", "CANCELAR" },
                { "Settings", "CONFIGURAÇÕES" }, { "SettingsTabLyrics", "Letra" }, { "SettingsTabTranslate", "Traduzir" }, { "SettingsTabApp", "Geral" }, { "ManualSync", "SINC. MANUAL" }, { "Translate", "TRADUZIR LETRAS" },
                { "UILang", "IDIOMA DO APP" }, { "Ready", "Pronto." },
                { "Listening", "Ouvindo..." }, { "Searching", "Buscando letra..." },
                { "SideListen", "OUVIR" }, { "SideClear", "LIMPAR" }, { "Auto", "AUTO" },
                { "KeepOriginal", "Também mostra a letra original" }, { "PreviewLabel", "PRÉVIA" },
                { "MockPrevious", "♪ esta é uma linha de exemplo ♪" }, { "MockCurrent", "É assim que sua letra vai aparecer" },
                { "MockNext", "♪ próxima linha de exemplo ♪" }, { "MockCurrentOriginal", "(original) esta é uma linha de exemplo" },
                { "DonateTip", "Deixe uma gorjeta opcional" }, { "ReleaseNotesButton", "VER NOVIDADES DA VERSÃO" }, { "ReleaseNotesHeading", "NOVIDADES" }, { "ReleaseNotesTooltip", "Veja as novidades desta versão" },
                { "FeedbackLink", "Deixe seu feedback" },
                { "SupportPrompt", "Gostando do app?" }, { "SupportDonate", "Apoie" }, { "SupportRate", "Avalie" }, { "SupportFeedback", "Feedback" },
                { "TipSupportDonate", "Deixe uma gorjeta opcional para apoiar o desenvolvimento." }, { "TipSupportRate", "Avalie o Frontline Lyrics na loja." }, { "TipSupportFeedback", "Relate problemas ou sugira melhorias." },
                { "PrevTrack", "Faixa anterior" }, { "NextTrack", "Próxima faixa" },
                { "SearchHistory", "HISTÓRICO DE BUSCA" }, { "HistoryArtist", "Artista" },
                { "HistorySong", "Música" }, { "HistoryDate", "Data" },
                { "HistoryEmpty", "Nenhuma busca ainda." }, { "HistoryRemove", "Remover" },
                { "FontSizeTitle", "TAMANHO DA FONTE" },
                { "BgOpacityTitle", "OPACIDADE DO FUNDO" },
                { "Festival", "FESTIVAL" }, { "FestivalMode", "MODO FESTIVAL" },
                { "FestivalIntro", "Em shows, o Shazam costuma falhar. Carregue o setlist, pré-carregue as letras e toque a primeira linha quando o cantor começar." },
                { "FestivalApiKey", "Chave da API setlist.fm" },
                { "FestivalApiKeyHint", "Cole a chave para buscar setlists públicos por artista e, se quiser, país, local ou ano." },
                { "FestivalApiKeySaved", "Chave salva. Cole outra para substituir." },
                { "FestivalApiLink", "Obter uma chave grátis" },
                { "FestivalSaveKey", "SALVAR CHAVE" }, { "FestivalClose", "FECHAR" },
                { "FestivalPlaylists", "SETLISTS SALVOS" },
                { "FestivalNewArtist", "Artista ou banda" }, { "FestivalCreate", "CRIAR" },
                { "FestivalEmpty", "Nenhum setlist ainda. Digite um artista para criar." },
                { "FestivalFetching", "Buscando setlists…" },
                { "FestivalNoSetlist", "Nenhum setlist encontrado. Lista vazia — adicione músicas abaixo." },
                { "FestivalEditorTitle", "SETLIST" }, { "FestivalBack", "VOLTAR" },
                { "FestivalSongsEmpty", "Setlist vazio. Adicione músicas abaixo ou busque durante o show." },
                { "FestivalDragHint", "Arraste uma faixa para mudar a ordem" },
                { "FestivalAdd", "ADICIONAR" }, { "FestivalStart", "COMEÇAR" }, { "FestivalDone", "PRONTO" },
                { "FestivalEdit", "EDITAR SETLIST" }, { "FestivalStop", "PARAR FESTIVAL" },
                { "FestivalPrevLine", "Linha anterior" }, { "FestivalNextLine", "Próxima linha" },
                { "FestivalSongOne", "1 música" }, { "FestivalSongMany", "{0} músicas" },
                { "FestivalDeleteConfirm", "Excluir este setlist?" },
                { "ConfirmAction", "Excluir" }, { "ConfirmCancel", "Cancelar" }, { "ConfirmOk", "OK" },
                { "FestivalSearchArtist", "Buscar artista ou banda" },
                { "FestivalPlaylistName", "Nome da playlist" },
                { "FestivalSearch", "BUSCAR" },
                { "FestivalEmptySearch", "Busque por artista ou banda. País, local e ano são opcionais." },
                { "FestivalSearchResults", "SETLISTS RECENTES" },
                { "FestivalSearchCountry", "País (opcional)" },
                { "FestivalSearchVenue", "Local (opcional)" },
                { "FestivalSearchYear", "Ano (opcional)" },
                { "FestivalNoResults", "Nenhum setlist encontrado. Escreva o nome do artista ou da banda como aparece no setlist.fm. O país pode ser o código de 2 letras (BR, US) ou o nome completo. Local e ano são opcionais — se a busca ficou estreita demais, tente só o artista." },
                { "FestivalCreateFromResult", "CRIAR PLAYLIST" },
                { "FestivalApiKeyBad", "Chave da API recusada. Confira no setlist.fm." },
                { "FestivalPrevSong", "Música anterior do setlist" },
                { "FestivalNextSong", "Próxima música do setlist" },
                { "FestivalNoLyrics", "Ainda não há letra desta música." },
                { "FestivalNextUp", "A SEGUIR" },
                { "FestivalContinue", "CONTINUAR" },
                { "FestivalSetlistEnd", "Fim do setlist." },
                { "TipFestivalContinue", "Ir para a próxima música do setlist" },
                { "TipListen", "Identificar o que está tocando e mostrar a letra sincronizada" },
                { "TipSearch", "Buscar uma música por artista e título" },
                { "TipFestival", "Abrir o Modo Festival para shows ao vivo" },
                { "TipHelp", "Abrir o site do Frontline" },
                { "TipBack", "Voltar" },
                { "TipSettings", "Configurações" },
                { "TipMinimize", "Minimizar" },
                { "TipRestore", "Restaurar janela" },
                { "TipClose", "Fechar o Frontline" },
                { "TipAuto", "Acompanhar o player automaticamente" },
                { "TipClear", "Limpar a letra atual" },
                { "TipFind", "Buscar a letra deste artista e música" },
                { "TipFestivalSearch", "Buscar setlists públicos por artista, com país, local e ano opcionais" },
                { "TipFestivalCreate", "Criar uma playlist vazia com este nome" },
                { "TipFestivalCreateFromResult", "Criar uma playlist a partir do setlist selecionado" },
                { "TipFestivalSaveKey", "Salvar a chave da API setlist.fm neste aparelho" },
                { "TipFestivalStart", "Começar este setlist no Modo Festival" },
                { "TipFestivalDone", "Voltar para a letra" },
                { "TipFestivalAdd", "Adicionar esta música ao setlist" },
                { "TipFestivalEdit", "Editar o setlist do festival" },
                { "TipFestivalStop", "Parar o Modo Festival e salvar o setlist" },
                { "TipManualSync", "Toque numa linha da letra para ajustar o tempo" },
                { "LockShortcutTitle", "ATALHO PARA TRAVAR (LOCK)" },
                { "LockShortcutHint", "(aparece no canto esquerdo ao passar o mouse)" },
                { "LockFirstTime", "A sobreposição está travada." },
                { "LockToastMsg", "Travado. Clique no cadeado para destravar." },
                { "HotkeyFailed", "Essa combinação já está em uso pelo Windows ou outro app. Tente outra." },
                { "LockNone", "Nenhum (clique p/ definir)" },
                { "LockCapturing", "Pressione... (Esc cancela, Del limpa)" },
                { "TipLock", "Travar sobreposição: os cliques passam para o app de trás" },
                { "TipUnlock", "Destravar sobreposição: voltar a mover e clicar nela" },
            }},
            { "es", new() {
                { "Listen", "ESCUCHAR" }, { "Search", "BUSCAR" }, { "ManualSearch", "BÚSQUEDA MANUAL" },
                { "Artist", "Artista:" }, { "Song", "Canción:" }, { "Find", "BUSCAR" }, { "Cancel", "CANCELAR" },
                { "Settings", "AJUSTES" }, { "SettingsTabLyrics", "Letra" }, { "SettingsTabTranslate", "Traducir" }, { "SettingsTabApp", "General" }, { "ManualSync", "SINC. MANUAL" }, { "Translate", "TRADUCIR LETRAS" },
                { "UILang", "IDIOMA DE LA APP" }, { "Ready", "Listo." },
                { "Listening", "Escuchando..." }, { "Searching", "Buscando letra..." },
                { "SideListen", "ESCUCHAR" }, { "SideClear", "LIMPIAR" }, { "Auto", "AUTO" },
                { "KeepOriginal", "También muestra la letra original" }, { "PreviewLabel", "VISTA PREVIA" },
                { "MockPrevious", "♪ esta es una línea de ejemplo ♪" }, { "MockCurrent", "Así se verá tu letra" },
                { "MockNext", "♪ próxima línea de ejemplo ♪" }, { "MockCurrentOriginal", "(original) esta es una línea de ejemplo" },
                { "DonateTip", "Deja una propina opcional" }, { "ReleaseNotesButton", "VER NOTAS DE LA VERSIÓN" }, { "ReleaseNotesHeading", "NOVEDADES" }, { "ReleaseNotesTooltip", "Ver las novedades de esta versión" },
                { "FeedbackLink", "Deja tus comentarios" },
                { "SupportPrompt", "¿Te gusta la app?" }, { "SupportDonate", "Apoya" }, { "SupportRate", "Valora" }, { "SupportFeedback", "Feedback" },
                { "TipSupportDonate", "Deja una propina opcional para apoyar el desarrollo." }, { "TipSupportRate", "Valora FrontLine Lyrics en la tienda." }, { "TipSupportFeedback", "Informa de problemas o sugiere mejoras." },
                { "PrevTrack", "Pista anterior" }, { "NextTrack", "Pista siguiente" },
                { "SearchHistory", "HISTORIAL DE BÚSQUEDA" }, { "HistoryArtist", "Artista" },
                { "HistorySong", "Canción" }, { "HistoryDate", "Fecha" },
                { "HistoryEmpty", "Aún no hay búsquedas." },                 { "HistoryRemove", "Quitar" },
                { "FontSizeTitle", "TAMAÑO DE FUENTE" },
                { "BgOpacityTitle", "OPACIDAD DE FONDO" },
                { "Festival", "FESTIVAL" }, { "FestivalMode", "MODO FESTIVAL" },
                { "FestivalIntro", "En conciertos, Shazam suele fallar. Carga el setlist, precarga las letras y toca la primera línea cuando empiece el cantante." },
                { "FestivalApiKey", "Clave API de setlist.fm" },
                { "FestivalApiKeyHint", "Pega la clave para buscar setlists públicos por artista y, si quieres, país, recinto o año." },
                { "FestivalApiKeySaved", "Clave guardada. Pega otra para reemplazarla." },
                { "FestivalApiLink", "Obtener una clave gratis" },
                { "FestivalSaveKey", "GUARDAR CLAVE" }, { "FestivalClose", "CERRAR" },
                { "FestivalPlaylists", "SETLISTS GUARDADOS" },
                { "FestivalNewArtist", "Artista o banda" }, { "FestivalCreate", "CREAR" },
                { "FestivalEmpty", "Aún no hay setlists. Escribe un artista para crear uno." },
                { "FestivalFetching", "Buscando setlists…" },
                { "FestivalNoSetlist", "No se encontró setlist. Lista vacía — añade canciones abajo." },
                { "FestivalEditorTitle", "SETLIST" }, { "FestivalBack", "ATRÁS" },
                { "FestivalSongsEmpty", "Setlist vacío. Añade canciones abajo o busca durante el show." },
                { "FestivalDragHint", "Arrastra una canción para cambiar el orden" },
                { "FestivalAdd", "AÑADIR" }, { "FestivalStart", "EMPEZAR" }, { "FestivalDone", "LISTO" },
                { "FestivalEdit", "EDITAR SETLIST" }, { "FestivalStop", "PARAR FESTIVAL" },
                { "FestivalPrevLine", "Línea anterior" }, { "FestivalNextLine", "Siguiente línea" },
                { "FestivalSongOne", "1 canción" }, { "FestivalSongMany", "{0} canciones" },
                { "FestivalDeleteConfirm", "¿Eliminar este setlist?" },
                { "ConfirmAction", "Eliminar" }, { "ConfirmCancel", "Cancelar" }, { "ConfirmOk", "OK" },
                { "FestivalSearchArtist", "Buscar artista o banda" },
                { "FestivalPlaylistName", "Nombre de la playlist" },
                { "FestivalSearch", "BUSCAR" },
                { "FestivalEmptySearch", "Busca por artista o banda. País, recinto y año son opcionales." },
                { "FestivalSearchResults", "SETLISTS RECIENTES" },
                { "FestivalSearchCountry", "País (opcional)" },
                { "FestivalSearchVenue", "Recinto (opcional)" },
                { "FestivalSearchYear", "Año (opcional)" },
                { "FestivalNoResults", "No hay setlists. Escribe el artista o la banda tal como aparece en setlist.fm. El país puede ser el código de 2 letras (BR, US) o el nombre completo. Recinto y año son opcionales — si la búsqueda es demasiado estrecha, prueba solo con el artista." },
                { "FestivalCreateFromResult", "CREAR PLAYLIST" },
                { "FestivalApiKeyBad", "Clave API rechazada. Revísala en setlist.fm." },
                { "FestivalPrevSong", "Canción anterior del setlist" },
                { "FestivalNextSong", "Siguiente canción del setlist" },
                { "FestivalNoLyrics", "Todavía no hay letra de esta canción." },
                { "FestivalNextUp", "A CONTINUACIÓN" },
                { "FestivalContinue", "CONTINUAR" },
                { "FestivalSetlistEnd", "Fin del setlist." },
                { "TipFestivalContinue", "Pasar a la siguiente canción del setlist" },
                { "TipListen", "Identificar lo que suena y mostrar la letra sincronizada" },
                { "TipSearch", "Buscar una canción por artista y título" },
                { "TipFestival", "Abrir el Modo Festival para conciertos" },
                { "TipHelp", "Abrir el sitio de Frontline" },
                { "TipBack", "Volver" },
                { "TipSettings", "Ajustes" },
                { "TipMinimize", "Minimizar" },
                { "TipRestore", "Restaurar ventana" },
                { "TipClose", "Cerrar Frontline" },
                { "TipAuto", "Seguir el reproductor automáticamente" },
                { "TipClear", "Limpiar la letra actual" },
                { "TipFind", "Buscar la letra de este artista y canción" },
                { "TipFestivalSearch", "Buscar setlists públicos por artista, con país, recinto y año opcionales" },
                { "TipFestivalCreate", "Crear una playlist vacía con este nombre" },
                { "TipFestivalCreateFromResult", "Crear una playlist a partir del setlist seleccionado" },
                { "TipFestivalSaveKey", "Guardar la clave API de setlist.fm en este dispositivo" },
                { "TipFestivalStart", "Empezar este setlist en Modo Festival" },
                { "TipFestivalDone", "Volver a la letra" },
                { "TipFestivalAdd", "Añadir esta canción al setlist" },
                { "TipFestivalEdit", "Editar el setlist del festival" },
                { "TipFestivalStop", "Parar el Modo Festival y guardar el setlist" },
                { "TipManualSync", "Toca una línea de la letra para ajustar el tiempo" },
                { "LockShortcutTitle", "ATAJO DE BLOQUEO (LOCK)" },
                { "LockShortcutHint", "(aparece en la esquina izquierda al pasar el ratón)" },
                { "LockFirstTime", "La superposición está bloqueada." },
                { "LockToastMsg", "Bloqueado. Haz clic en el candado para desbloquear." },
                { "HotkeyFailed", "Esta combinación ya está en uso por Windows u otra aplicación. Intenta con otra." },
                { "LockNone", "Ninguno (clic para establecer)" },
                { "LockCapturing", "Presiona... (Esc cancela, Del borra)" },
                { "TipLock", "Bloquear superposición: los clics pasan a la app de atrás" },
                { "TipUnlock", "Desbloquear superposición: volver a moverla y hacer clic" },
            }},
            { "id", new() {
                { "Listen", "DENGAR" }, { "Search", "CARI" }, { "ManualSearch", "PENCARIAN MANUAL" },
                { "Artist", "Artis:" }, { "Song", "Lagu:" }, { "Find", "CARI" }, { "Cancel", "BATAL" },
                { "Settings", "PENGATURAN" }, { "SettingsTabLyrics", "Lirik" }, { "SettingsTabTranslate", "Terjemah" }, { "SettingsTabApp", "Umum" }, { "ManualSync", "SINKRON MANUAL" }, { "Translate", "TERJEMAHKAN LIRIK" },
                { "UILang", "BAHASA APLIKASI" }, { "Ready", "Siap." },
                { "Listening", "Mendengarkan..." }, { "Searching", "Mencari lirik..." },
                { "SideListen", "DENGAR" }, { "SideClear", "HAPUS" }, { "Auto", "AUTO" },
                { "KeepOriginal", "Tampilkan juga lirik asli" }, { "PreviewLabel", "PRATINJAU" },
                { "MockPrevious", "♪ ini baris contoh ♪" }, { "MockCurrent", "Lirik Anda akan tampil seperti ini" },
                { "MockNext", "♪ baris berikutnya ♪" }, { "MockCurrentOriginal", "(asli) ini baris contoh" },
                { "DonateTip", "Tinggalkan tip opsional" }, { "ReleaseNotesButton", "LIHAT CATATAN RILIS" }, { "ReleaseNotesHeading", "YANG BARU" }, { "ReleaseNotesTooltip", "Lihat yang baru di versi ini" },
                { "FeedbackLink", "Berikan tanggapan Anda" },
                { "SupportPrompt", "Suka aplikasinya?" }, { "SupportDonate", "Dukung" }, { "SupportRate", "Nilai" }, { "SupportFeedback", "Masukan" },
                { "TipSupportDonate", "Tinggalkan tip opsional untuk mendukung pengembangan." }, { "TipSupportRate", "Beri nilai FrontLine Lyrics di toko." }, { "TipSupportFeedback", "Laporkan masalah atau sarankan perbaikan." },
                { "PrevTrack", "Lagu sebelumnya" }, { "NextTrack", "Lagu berikutnya" },
                { "SearchHistory", "RIWAYAT PENCARIAN" }, { "HistoryArtist", "Artis" },
                { "HistorySong", "Lagu" }, { "HistoryDate", "Tanggal" },
                { "HistoryEmpty", "Belum ada pencarian." }, { "HistoryRemove", "Hapus" },
                { "FontSizeTitle", "UKURAN FONT" },
                { "BgOpacityTitle", "OPASITAS LATAR" },
                { "Festival", "FESTIVAL" }, { "FestivalMode", "MODE FESTIVAL" },
                { "FestivalIntro", "Di konser, Shazam sering gagal. Muat setlist, siapkan liriknya lebih dulu, lalu ketuk baris pertama saat penyanyi mulai." },
                { "FestivalApiKey", "Kunci API setlist.fm" },
                { "FestivalApiKeyHint", "Tempel kunci untuk mencari setlist publik berdasarkan artis, plus negara, tempat, atau tahun jika perlu." },
                { "FestivalApiKeySaved", "Kunci tersimpan. Tempel yang baru untuk mengganti." },
                { "FestivalApiLink", "Dapatkan kunci gratis" },
                { "FestivalSaveKey", "SIMPAN KUNCI" }, { "FestivalClose", "TUTUP" },
                { "FestivalPlaylists", "SETLIST TERSIMPAN" },
                { "FestivalNewArtist", "Artis atau band" }, { "FestivalCreate", "BUAT" },
                { "FestivalEmpty", "Belum ada setlist. Masukkan nama playlist untuk membuat." },
                { "FestivalFetching", "Mencari setlist…" },
                { "FestivalNoSetlist", "Setlist tidak ditemukan." },
                { "FestivalEditorTitle", "SETLIST" }, { "FestivalBack", "KEMBALI" },
                { "FestivalSongsEmpty", "Setlist kosong. Tambah lagu di bawah atau cari selama acara." },
                { "FestivalDragHint", "Seret lagu untuk mengubah urutan" },
                { "FestivalAdd", "TAMBAH" }, { "FestivalStart", "MULAI" }, { "FestivalDone", "SELESAI" },
                { "FestivalEdit", "EDIT SETLIST" }, { "FestivalStop", "HENTIKAN FESTIVAL" },
                { "FestivalPrevLine", "Baris lirik sebelumnya" }, { "FestivalNextLine", "Baris lirik berikutnya" },
                { "FestivalSongOne", "1 lagu" }, { "FestivalSongMany", "{0} lagu" },
                { "FestivalDeleteConfirm", "Hapus setlist ini?" },
                { "ConfirmAction", "Hapus" }, { "ConfirmCancel", "Batal" }, { "ConfirmOk", "OK" },
                { "FestivalSearchArtist", "Cari artis atau band" },
                { "FestivalPlaylistName", "Nama playlist" },
                { "FestivalSearch", "CARI" },
                { "FestivalEmptySearch", "Cari artis atau band. Negara, tempat, dan tahun bersifat opsional." },
                { "FestivalSearchResults", "SETLIST TERBARU" },
                { "FestivalSearchCountry", "Negara (opsional)" },
                { "FestivalSearchVenue", "Tempat (opsional)" },
                { "FestivalSearchYear", "Tahun (opsional)" },
                { "FestivalNoResults", "Tidak ada setlist. Tulis nama artis atau band persis seperti di setlist.fm. Negara bisa kode 2 huruf (BR, US) atau nama lengkap. Tempat dan tahun opsional — jika terlalu sempit, cari dengan artis saja." },
                { "FestivalCreateFromResult", "BUAT PLAYLIST" },
                { "FestivalApiKeyBad", "Kunci API ditolak. Periksa di setlist.fm." },
                { "FestivalPrevSong", "Lagu sebelumnya di setlist" },
                { "FestivalNextSong", "Lagu berikutnya di setlist" },
                { "FestivalNoLyrics", "Lirik lagu ini belum tersedia." },
                { "FestivalNextUp", "SELANJUTNYA" },
                { "FestivalContinue", "LANJUT" },
                { "FestivalSetlistEnd", "Setlist selesai." },
                { "TipFestivalContinue", "Lanjut ke lagu berikutnya di setlist" },
                { "TipListen", "Kenali lagu yang diputar dan tampilkan lirik tersinkron" },
                { "TipSearch", "Cari lagu berdasarkan artis dan judul" },
                { "TipFestival", "Buka Mode Festival untuk konser langsung" },
                { "TipHelp", "Buka situs Frontline" },
                { "TipBack", "Kembali" },
                { "TipSettings", "Pengaturan" },
                { "TipMinimize", "Minimalkan" },
                { "TipRestore", "Pulihkan jendela" },
                { "TipClose", "Tutup Frontline" },
                { "TipAuto", "Ikuti pemutar secara otomatis" },
                { "TipClear", "Hapus lirik saat ini" },
                { "TipFind", "Cari lirik artis dan lagu ini" },
                { "TipFestivalSearch", "Cari setlist publik berdasarkan artis, dengan negara, tempat, dan tahun opsional" },
                { "TipFestivalCreate", "Buat playlist kosong dengan nama ini" },
                { "TipFestivalCreateFromResult", "Buat playlist dari setlist yang dipilih" },
                { "TipFestivalSaveKey", "Simpan kunci API setlist.fm di perangkat ini" },
                { "TipFestivalStart", "Mulai setlist ini di Mode Festival" },
                { "TipFestivalDone", "Kembali ke lirik" },
                { "TipFestivalAdd", "Tambahkan lagu ini ke setlist" },
                { "TipFestivalEdit", "Edit setlist festival" },
                { "TipFestivalStop", "Hentikan Mode Festival dan simpan setlist" },
                { "TipManualSync", "Ketuk baris lirik untuk mengatur waktu" },
                { "LockShortcutTitle", "PINTASAN KUNCI (LOCK)" },
                { "LockShortcutHint", "(muncul di sudut kiri saat kursor diarahkan)" },
                { "LockFirstTime", "Overlay telah dikunci." },
                { "LockToastMsg", "Terkunci. Klik gembok untuk membuka kunci." },
                { "HotkeyFailed", "Kombinasi ini sudah digunakan oleh Windows atau aplikasi lain. Coba yang lain." },
                { "LockNone", "Tidak ada (klik untuk mengatur)" },
                { "LockCapturing", "Tekan... (Esc batal, Del hapus)" },
                { "TipLock", "Kunci overlay: klik diteruskan ke aplikasi di belakangnya" },
                { "TipUnlock", "Buka kunci overlay: bisa digerakkan dan diklik lagi" },
            }}
        };

        private void SldBgOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            bgOpacity = e.NewValue;
            if (_loadingSettings) return;
            UpdateOpacityPreview();
        }

        private void UpdateOpacityPreview()
        {
            if (OpacityPreviewOverlay == null) return;

            byte alpha = (byte)(255 * bgOpacity);
            OpacityPreviewOverlay.Background = new SolidColorBrush(Color.FromArgb(alpha, 5, 5, 5));

            var t = uiStrings[currentAppLanguage];

            if (currentAppStatus == "IDLE")
            {
                LblPreviewPrevious.Text = t["MockPrevious"];
                LblPreviewCurrent.Text = t["MockCurrent"];
                LblPreviewNext.Text = t["MockNext"];
                LblPreviewCurrentOriginal.Text = t["MockCurrentOriginal"];
                LblPreviewCurrentOriginal.Visibility = keepOriginalWithTranslation ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                LblPreviewPrevious.Text = LblPrevious.Text;
                LblPreviewCurrent.Text = LblCurrent.Text;
                LblPreviewNext.Text = LblNext.Text;
                LblPreviewCurrentOriginal.Text = LblCurrentOriginal.Text;
                LblPreviewCurrentOriginal.Visibility = LblCurrentOriginal.Visibility;
            }

            SetSettingsPreviewMode(true);
            OpacityPreviewOverlay.Visibility = Visibility.Visible;
            _opacityPreviewTimer.Stop();
            _opacityPreviewTimer.Start();
        }

        private void SettingsTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton tab)
                ShowSettingsTab(tab.Tag as string ?? "lyrics");
        }

        private void ShowSettingsTab(string id)
        {
            if (id != "translate" && id != "app") id = "lyrics";
            _settingsTab = id;
            if (TabSettingsLyrics != null) TabSettingsLyrics.IsChecked = id == "lyrics";
            if (TabSettingsTranslate != null) TabSettingsTranslate.IsChecked = id == "translate";
            if (TabSettingsApp != null) TabSettingsApp.IsChecked = id == "app";
            if (SettingsPanelLyrics != null) SettingsPanelLyrics.Visibility = id == "lyrics" ? Visibility.Visible : Visibility.Collapsed;
            if (SettingsPanelTranslate != null) SettingsPanelTranslate.Visibility = id == "translate" ? Visibility.Visible : Visibility.Collapsed;
            if (SettingsPanelApp != null) SettingsPanelApp.Visibility = id == "app" ? Visibility.Visible : Visibility.Collapsed;
            if (SettingsOpacityBlock != null) SettingsOpacityBlock.Visibility = id == "lyrics" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetSettingsPreviewMode(bool previewing)
        {
            _settingsPreview = previewing;
            MainBorder.Visibility = previewing ? Visibility.Hidden : Visibility.Visible;
            if (LblSettingsTitle != null) LblSettingsTitle.Visibility = previewing ? Visibility.Collapsed : Visibility.Visible;
            if (SettingsTabs != null) SettingsTabs.Visibility = previewing ? Visibility.Collapsed : Visibility.Visible;
            if (previewing)
            {
                if (SettingsPanelLyrics != null) SettingsPanelLyrics.Visibility = Visibility.Collapsed;
                if (SettingsPanelTranslate != null) SettingsPanelTranslate.Visibility = Visibility.Collapsed;
                if (SettingsPanelApp != null) SettingsPanelApp.Visibility = Visibility.Collapsed;
                if (SettingsOpacityBlock != null) SettingsOpacityBlock.Visibility = Visibility.Visible;
                if (BtnManualSyncToggle != null) BtnManualSyncToggle.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowSettingsTab(_settingsTab);
                SyncTimingTools();
            }
            if (SearchInputPanel != null)
            {
                bool searchOpen = SearchInputPanel.Visibility == Visibility.Visible;
                SearchInputPanel.Opacity = (previewing && searchOpen) ? 0 : 1;
                SearchInputPanel.IsHitTestVisible = !(previewing && searchOpen);
            }
            if (FestivalHubPanel != null)
            {
                bool festivalOpen = FestivalHubPanel.Visibility == Visibility.Visible;
                FestivalHubPanel.Opacity = (previewing && festivalOpen) ? 0 : 1;
                FestivalHubPanel.IsHitTestVisible = !(previewing && festivalOpen);
            }
        }

        private void OpacityPreviewTimer_Tick(object? sender, EventArgs e)
        {
            _opacityPreviewTimer.Stop();
            OpacityPreviewOverlay.Visibility = Visibility.Collapsed;
            SetSettingsPreviewMode(false);
        }

        private void SettingsPopup_Closed(object? sender, EventArgs e)
        {
            _opacityPreviewTimer.Stop();
            OpacityPreviewOverlay.Visibility = Visibility.Collapsed;
            SetSettingsPreviewMode(false);
        }

        private void SldFontSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            ApplyFontScale(e.NewValue);
            if (!_loadingSettings) ScheduleSave();
        }

        private void ApplyFontScale(double fontSize)
        {
            double scale = fontSize / 26.0;
            if (MainBorder != null)
                MainBorder.LayoutTransform = new ScaleTransform(scale, scale);
            if (SearchContentBorder != null)
                SearchContentBorder.LayoutTransform = new ScaleTransform(scale, scale);
            if (FestivalContentBorder != null)
                FestivalContentBorder.LayoutTransform = new ScaleTransform(scale, scale);

            if (SupportBar != null)
            {
                SupportBar.LayoutTransform = Math.Abs(scale - 1.0) < 0.001
                    ? Transform.Identity
                    : new ScaleTransform(1.0 / scale, 1.0 / scale);
            }
            UpdateSupportBarLayout();
        }

        // Responsive behaviour of the bottom-right "Enjoying the app?" bar.
        // Wide window: prompt + divider + icon/text buttons. Medium: buttons only. Narrow: icons only.
        private void UpdateSupportBarLayout()
        {
            if (SupportBar == null || TxtSupportPrompt == null || BtnDonate == null || BtnRate == null || BtnFeedback == null) return;
            if (!uiStrings.TryGetValue(currentAppLanguage, out var t)) return;

            double scale = SldFontSize != null ? SldFontSize.Value / 26.0 : 1.0;
            double w = ActualWidth > 0 ? ActualWidth / Math.Max(1.0, scale) : Width;

            bool compact = w < 500;
            bool showPrompt = w >= 640;

            TxtSupportPrompt.Text = t["SupportPrompt"];
            TxtSupportPrompt.Visibility = showPrompt ? Visibility.Visible : Visibility.Collapsed;
            SupportDivider.Visibility = showPrompt ? Visibility.Visible : Visibility.Collapsed;

            BtnDonate.Content = compact ? "" : t["SupportDonate"];
            BtnRate.Content = compact ? "" : t["SupportRate"];
            BtnFeedback.Content = compact ? "" : t["SupportFeedback"];
            var pad = compact ? new Thickness(10, 0, 10, 0) : new Thickness(12, 0, 12, 0);
            BtnDonate.Padding = BtnRate.Padding = BtnFeedback.Padding = pad;
        }

        private void BtnResetFont_Click(object sender, RoutedEventArgs e)
        {
            if (SldFontSize != null)
                SldFontSize.Value = 26;
        }
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            _hwndSource?.AddHook(HwndHook);
            RegisterCurrentHotkey();
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == LOCK_HOTKEY_ID)
            {
                if (currentAppStatus != "IDLE") ToggleLock(!isLocked);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private bool RegisterCurrentHotkey()
        {
            if (_hwndSource == null || lockHotKeyVk == 0) return false;
            return RegisterHotKey(_hwndSource.Handle, LOCK_HOTKEY_ID, lockHotkeyModifiers, lockHotKeyVk);
        }

        private void UnregisterCurrentHotkey()
        {
            if (_hwndSource != null) UnregisterHotKey(_hwndSource.Handle, LOCK_HOTKEY_ID);
        }

        private void BtnLockOverlay_Click(object sender, RoutedEventArgs e) => ToggleLock(BtnLockOverlay.IsChecked == true);

        private void ToggleLock(bool lockState)
        {
            if (currentAppStatus == "IDLE") return;
            isLocked = lockState;
            BtnLockOverlay.IsChecked = lockState;

            ApplySkipGlyph(ImgLockIcon, lockState ? _icoLock : _icoLockOpen);

            if (isLocked)
            {
                if (_lockWin != null) ApplySkipGlyph(_lockWin.Icon, _icoLock);
                UpdateVisualState(true);
            }
            else
            {
                _ghostTimer.Stop();
                UpdateVisualState(false);
            }
            ScheduleSave();
        }

        private void BtnSetLockShortcut_Click(object sender, RoutedEventArgs e)
        {
            isCapturingHotkey = true;
            BtnSetLockShortcut.Content = uiStrings[currentAppLanguage]["LockCapturing"];
        }



        private void BtnSetLockShortcut_KeyDown(object sender, KeyEventArgs e)
        {
            if (!isCapturingHotkey) return;
            e.Handled = true;

            if (e.Key == Key.Escape)
            {
                isCapturingHotkey = false;
                UpdateShortcutButtonText();
                return;
            }

            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                isCapturingHotkey = false;
                UnregisterCurrentHotkey();
                lockHotKeyVk = 0;
                lockHotkeyModifiers = 0;
                UpdateShortcutButtonText();
                ScheduleSave();
                return;
            }

            Key key = (e.Key == Key.System ? e.SystemKey : e.Key);

            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt || key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin)
                return;

            uint mods = 0;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= 0x0002;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= 0x0001;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= 0x0004;

            if (mods == 0) return;

            isCapturingHotkey = false;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

            UnregisterCurrentHotkey();
            lockHotKeyVk = vk;
            lockHotkeyModifiers = mods;

            if (!RegisterCurrentHotkey())
            {
                AlertDark(uiStrings[currentAppLanguage]["HotkeyFailed"]);
                lockHotKeyVk = 0; lockHotkeyModifiers = 0;
            }

            UpdateShortcutButtonText();
            ScheduleSave();
        }

        private void UpdateShortcutButtonText()
        {
            if (lockHotKeyVk == 0)
            {
                BtnSetLockShortcut.Content = uiStrings[currentAppLanguage]["LockNone"];
                return;
            }

            var parts = new List<string>();
            if ((lockHotkeyModifiers & 0x0002) != 0) parts.Add("Ctrl");
            if ((lockHotkeyModifiers & 0x0001) != 0) parts.Add("Alt");
            if ((lockHotkeyModifiers & 0x0004) != 0) parts.Add("Shift");

            Key key = KeyInterop.KeyFromVirtualKey((int)lockHotKeyVk);
            parts.Add(key.ToString());

            BtnSetLockShortcut.Content = string.Join(" + ", parts);
        }

        public MainWindow()
        {

            const string appName = "FrontLineLyrics_UniqueMutex";
            _appMutex = new Mutex(true, appName, out bool createdNew);
            if (!createdNew)
            {
                Application.Current.Shutdown();
                return;
            }

            CrashReporter.Install();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownEngine();
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    CrashReporter.Log(ex, "AppDomain.UnhandledException", terminating: e.IsTerminating);
                ShutdownEngine();
            };

            _welcomeWasShownAtLaunch = Telemetry.WelcomeShown;
            InitializeComponent();
            this.StateChanged += MainWindow_StateChanged;
            Loaded += MainWindow_Loaded;
            LocationChanged += (_, _) => ScheduleSave();
            SizeChanged += (_, _) => { ScheduleSave(); UpdateSupportBarLayout(); };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); PersistSettings(); };

            RestoreFontAndAuto();
            InitAppearance();
            InitSearchHistory();
            InitFestivalMode();

            _mouseTracker.Tick += MouseTracker_Tick;
            BtnLockOverlay.Visibility = Visibility.Collapsed;
            ShowLockWin(false);
            _mouseTracker.Start();
            _ghostTimer.Tick += GhostTimer_Tick;
            _opacityPreviewTimer.Tick += OpacityPreviewTimer_Tick;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && int.TryParse(args[1], out _))
                serverPort = args[1];

            RestoreWindowPlacement();

            LoadAppIcon();

            LoadSkipIcons();
            LoadCoverArt("");
            UpdateVisualState(false);
            ApplyUILanguage(currentAppLanguage);

            StartPythonServer();
            _ = Task.Run(() => ConnectWebSocket(), _shutdown.Token);
            InitTelemetry();
            if (!_welcomeWasShownAtLaunch)
                AppSettings.SetString("LastSeenReleaseNotesVersion", ReleaseNotesWindow.CurrentVersion);
            else
                Dispatcher.BeginInvoke(new Action(ShowNewVersionNotesIfNeeded), DispatcherPriority.ApplicationIdle);
        }

        private void StartPythonServer()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string serverExePath = System.IO.Path.Combine(baseDir, "FrontlineServer", "FrontlineServer.exe");

                if (!File.Exists(serverExePath))
                {
                    string solutionDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, @"..\..\..\.."));
                    serverExePath = System.IO.Path.Combine(solutionDir, "FrontlineServer", "dist", "FrontlineServer", "FrontlineServer.exe");
                }

                if (!File.Exists(serverExePath))
                {
                    CrashReporter.Info($"Motor de áudio ausente: {serverExePath}");
                    MessageBox.Show($"O motor de áudio não foi encontrado em:\n{serverExePath}", "Erro de Inicialização", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                KillOrphanServers(serverExePath);

                ProcessStartInfo psi = new()
                {
                    FileName = serverExePath,
                    Arguments = $"{serverPort} {Environment.ProcessId}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = false,
                    RedirectStandardOutput = false
                };

                pythonServerProcess = new() { StartInfo = psi, EnableRaisingEvents = true };
                pythonServerProcess.Exited += PythonServer_Exited;
                pythonServerProcess.Start();
                ChildProcessJob.Attach(pythonServerProcess);
                CrashReporter.LogBreadcrumb("FrontlineServer.start", $"pid={pythonServerProcess.Id}");
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "StartPythonServer");
                MessageBox.Show($"Falha ao iniciar o processo do servidor: {ex.Message}", "Erro Crítico", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void KillOrphanServers(string expectedExePath)
        {
            string expected = System.IO.Path.GetFullPath(expectedExePath);
            foreach (var p in Process.GetProcessesByName("FrontlineServer"))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path != null &&
                        string.Equals(System.IO.Path.GetFullPath(path), expected,
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        CrashReporter.Info($"Matando FrontlineServer órfão (PID {p.Id})");
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(3000);
                    }
                }
                catch (Exception ex) { CrashReporter.Log(ex, "KillOrphanServers"); }
                finally { p.Dispose(); }
            }
        }

        private void PythonServer_Exited(object? sender, EventArgs e)
        {
            if (_shuttingDown) return;
            int code = -1;
            try { code = pythonServerProcess?.ExitCode ?? -1; } catch { }
            CrashReporter.LogPythonExit(code);

            if (_pythonRestartCount >= MaxPythonRestarts)
            {
                CrashReporter.Info("FrontlineServer: limite de reinícios atingido.");
                return;
            }

            _pythonRestartCount++;
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_shuttingDown) return;
                    CrashReporter.Info($"Reiniciando FrontlineServer (tentativa {_pythonRestartCount}).");
                    StartPythonServer();
                });
            }
            catch (Exception ex) { CrashReporter.Log(ex, "PythonServer_Exited"); }
        }

        private void ShutdownEngine()
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            try { _shutdown.Cancel(); } catch { }
            KillPythonServer();
            try { _webSocket?.Abort(); } catch { }
            try { _appMutex?.ReleaseMutex(); _appMutex?.Dispose(); } catch { }
            _appMutex = null;
        }

        private void KillPythonServer()
        {
            var proc = pythonServerProcess;
            pythonServerProcess = null;
            if (proc == null) return;
            try { proc.Exited -= PythonServer_Exited; } catch { }
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(true);
                    proc.WaitForExit(2000);
                }
                proc.Dispose();
            }
            catch (Exception ex) { CrashReporter.Log(ex, "KillPythonServer"); }
        }

        protected override void OnClosed(EventArgs e)
        {
            _saveTimer.Stop();
            PersistFestivalOnExit();
            PersistSettings();
            _mouseTracker.Stop();
            _ghostTimer.Stop();
            _opacityPreviewTimer.Stop();
            ShutdownEngine();
            base.OnClosed(e);
        }

        private void ApplyUILanguage(string lang)
        {
            if (!uiStrings.TryGetValue(lang, out var t)) return;
            currentAppLanguage = lang;

            TxtListenBig.Text = t["Listen"];
            TxtSearchBig.Text = t["Search"];
            TxtListenSide.Text = t["SideListen"];
            TxtResetSide.Text = t["SideClear"];
            BtnAutoSide.Content = t["Auto"];
            LblManualSearchTitle.Text = t["ManualSearch"];
            LblArtistSearch.Text = t["Artist"];
            LblSongSearch.Text = t["Song"];
            BtnFindSearch.Content = t["Find"];
            LblSettingsTitle.Text = t["Settings"];
            if (TabSettingsLyrics != null) TabSettingsLyrics.Content = t["SettingsTabLyrics"];
            if (TabSettingsTranslate != null) TabSettingsTranslate.Content = t["SettingsTabTranslate"];
            if (TabSettingsApp != null) TabSettingsApp.Content = t["SettingsTabApp"];
            BtnManualSyncToggle.Content = t["ManualSync"];
            LblTranslateTitle.Text = t["Translate"];
            if (LblAppLangTitle != null) LblAppLangTitle.Text = t["UILang"];
            LblKeepOriginal.Text = t["KeepOriginal"];
            LblPreviewTag.Text = t["PreviewLabel"];
            UpdateSupportBarLayout();
            LblSearchHistoryTitle.Text = t["SearchHistory"];
            LblSearchHistoryEmpty.Text = t["HistoryEmpty"];
            LblFontSizeTitle.Text = t["FontSizeTitle"];
            LblBgOpacityTitle.Text = t["BgOpacityTitle"];
            LblLockShortcutTitle.Text = t["LockShortcutTitle"];
            LblLockShortcutHint.Text = t["LockShortcutHint"];
            UpdateShortcutButtonText();
            ApplyAppearanceLanguage();
            ApplyTelemetryLanguage();
            SyncLanguagePicker(lang);
            ApplyButtonTooltips(t);
            ApplyFestivalUiLanguage();
            RefreshHistoryHeaders();
            RefreshHistoryDateLabels();

            if (currentAppStatus == "IDLE") LblArtistName.Text = t["Ready"];
            else if (currentAppStatus == "LISTENING") LblLoadingText.Text = t["Listening"];
            else if (currentAppStatus == "SEARCHING") LblLoadingText.Text = t["Searching"];
            if (!_loadingSettings)
                AppSettings.SetString("AppLanguage", lang);
        }

        private void SyncLanguagePicker(string lang)
        {
            if (CmbAppLang == null) return;
            _syncingLangPicker = true;
            try
            {
                foreach (var obj in CmbAppLang.Items)
                {
                    if (obj is ComboBoxItem item && item.Tag as string == lang)
                    {
                        CmbAppLang.SelectedItem = item;
                        break;
                    }
                }
            }
            finally { _syncingLangPicker = false; }
        }

        private void CmbAppLang_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _loadingSettings || _syncingLangPicker) return;
            if (CmbAppLang?.SelectedItem is ComboBoxItem item && item.Tag is string lang)
                ApplyUILanguage(lang);
        }

        private void CmbAppLang_DropDownOpened(object sender, EventArgs e)
        {
            if (SettingsPopup != null) SettingsPopup.StaysOpen = true;
        }

        private void CmbAppLang_DropDownClosed(object sender, EventArgs e)
        {
            if (SettingsPopup != null) SettingsPopup.StaysOpen = false;
        }

        private void ApplyButtonTooltips(Dictionary<string, string> t)
        {
            BtnListenBig.ToolTip = t["TipListen"];
            BtnSearchBig.ToolTip = t["TipSearch"];
            BtnFestivalBig.ToolTip = t["TipFestival"];
            BtnHelpHome.ToolTip = t["TipHelp"];
            BtnReleaseNotes.ToolTip = t["ReleaseNotesTooltip"];
            if (BtnSettingsReleaseNotes != null)
                BtnSettingsReleaseNotes.Content = t["ReleaseNotesButton"];
            if (LblReleaseNotesTitle != null)
                LblReleaseNotesTitle.Text = t["ReleaseNotesHeading"];
            BtnOverlayBack.ToolTip = t["TipBack"];
            BtnMenu.ToolTip = t["TipSettings"];
            BtnMinimize.ToolTip = t["TipMinimize"];
            BtnRestore.ToolTip = t["TipRestore"];
            BtnCloseTop.ToolTip = t["TipClose"];
            BtnPrevTrack.ToolTip = t["PrevTrack"];
            BtnNextTrack.ToolTip = t["NextTrack"];
            BtnListenSide.ToolTip = t["TipListen"];
            BtnAutoSide.ToolTip = t["TipAuto"];
            BtnResetSide.ToolTip = t["TipClear"];
            BtnSearchSide.ToolTip = t["TipSearch"];
            BtnFestivalPrevTrack.ToolTip = t["FestivalPrevSong"];
            BtnFestivalNextTrack.ToolTip = t["FestivalNextSong"];
            BtnFestivalPrevLine.ToolTip = t["FestivalPrevLine"];
            BtnFestivalNextLine.ToolTip = t["FestivalNextLine"];
            BtnFestivalEdit.ToolTip = t["TipFestivalEdit"];
            BtnFestivalStop.ToolTip = t["TipFestivalStop"];
            if (BtnFestivalAdvance != null) BtnFestivalAdvance.ToolTip = t["TipFestivalContinue"];
            BtnFestivalSearch.ToolTip = t["TipSearch"];
            BtnFindSearch.ToolTip = t["TipFind"];
            BtnFestivalCreate.ToolTip = HasSetlistApiKey ? t["TipFestivalSearch"] : t["TipFestivalCreate"];
            BtnFestivalCreateFromResult.ToolTip = t["TipFestivalCreateFromResult"];
            BtnFestivalSaveKey.ToolTip = t["TipFestivalSaveKey"];
            BtnFestivalStart.ToolTip = _festivalModeActive ? t["TipFestivalDone"] : t["TipFestivalStart"];
            BtnFestivalAddSong.ToolTip = t["TipFestivalAdd"];
            BtnManualSyncToggle.ToolTip = t["TipManualSync"];
            BtnDonate.ToolTip = t["TipSupportDonate"];
            BtnRate.ToolTip = t["TipSupportRate"];
            BtnFeedback.ToolTip = t["TipSupportFeedback"];
            BtnLockOverlay.ToolTip = t["TipLock"];
            _lockWin?.SetTip(t["TipUnlock"]);
        }


        private void GhostTimer_Tick(object? sender, EventArgs e) { _ghostTimer.Stop(); if (currentAppStatus != "IDLE") SetGhostMode(true); }

        private void SetGhostMode(bool enable)
        {
            if (isGhostMode == enable || isResizing) return;
            UpdateVisualState(enable);
        }

        private void SetWindowStyle(IntPtr hwnd, bool makeTransparent, bool noActivate)
        {
            int GWL_EXSTYLE = -20;
            long currentStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            long newStyle = currentStyle;
            if (makeTransparent) newStyle |= 0x20L; else newStyle &= ~0x20L;
            if (noActivate) newStyle |= 0x08000000L; else newStyle &= ~0x08000000L;
            if (newStyle != currentStyle) SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(newStyle));
        }

        private void MouseTracker_Tick(object? sender, EventArgs e)
        {
            if (currentAppStatus == "IDLE" || BtnMenu.IsChecked == true || SearchInputPanel.Visibility == Visibility.Visible || FestivalHubPanel.Visibility == Visibility.Visible || isResizing)
            {
                BtnLockOverlay.Visibility = Visibility.Collapsed;
                _ghostTimer.Stop();
                if (isGhostMode && !isLocked) SetGhostMode(false);
                return;
            }

            GetCursorPos(out Win32Point p);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (GetWindowRect(hwnd, out RECT rect))
            {
                bool isMouseOverApp = (p.X >= rect.Left && p.X <= rect.Right && p.Y >= rect.Top && p.Y <= rect.Bottom);

                if (isLocked)
                {
                    BtnLockOverlay.Visibility = Visibility.Collapsed;
                    SetWindowStyle(hwnd, true, true);
                    ShowLockWin(isMouseOverApp);
                    return;
                }

                ShowLockWin(false);

                BtnLockOverlay.Visibility = isMouseOverApp ? Visibility.Visible : Visibility.Collapsed;

                if (isMouseOverApp)
                {
                    _ghostTimer.Stop();
                    if (isGhostMode) SetGhostMode(false);
                }
                else
                {
                    if (!isGhostMode && !_ghostTimer.IsEnabled) _ghostTimer.Start();
                }
            }
        }

        private void UpdateVisualState(bool enableGhost)
        {
            if (isResizing) return;
            isGhostMode = enableGhost;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (currentAppStatus == "IDLE" || SearchInputPanel.Visibility == Visibility.Visible || FestivalHubPanel.Visibility == Visibility.Visible)
            {
                SetWindowStyle(hwnd, false, false);
                MainBorder.Background = new SolidColorBrush(Color.FromArgb(255, 10, 10, 10));
                MainBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));
                SidePanelCol.Width = new GridLength(35, GridUnitType.Star);
                ContentPanelCol.Width = new GridLength(65, GridUnitType.Star);
                SidePanel.Visibility = Visibility.Visible;
                ResizeGrip.Visibility = Visibility.Visible;
                TopRightControls.Visibility = Visibility.Visible;
                PlayingControls.Visibility = Visibility.Collapsed;
            }
            else if (isGhostMode || isLocked)
            {
                SetWindowStyle(hwnd, true, isLocked);

                byte alpha = (byte)(255 * bgOpacity);
                MainBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 5, 5, 5));
                MainBorder.BorderBrush = Brushes.Transparent;
                SidePanelCol.Width = new GridLength(0);
                ContentPanelCol.Width = new GridLength(100, GridUnitType.Star);
                SidePanel.Visibility = Visibility.Collapsed;
                ResizeGrip.Visibility = Visibility.Collapsed;
                TopRightControls.Visibility = Visibility.Collapsed;
                PlayingControls.Visibility = Visibility.Collapsed;
            }
            else
            {
                SetWindowStyle(hwnd, false, false);
                MainBorder.Background = new SolidColorBrush(Color.FromArgb(255, 10, 10, 10));
                MainBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));
                SidePanelCol.Width = new GridLength(35, GridUnitType.Star);
                ContentPanelCol.Width = new GridLength(65, GridUnitType.Star);
                SidePanel.Visibility = Visibility.Visible;
                ResizeGrip.Visibility = Visibility.Visible;
                TopRightControls.Visibility = Visibility.Visible;
                PlayingControls.Visibility = Visibility.Visible;
            }

            SyncTimingTools();
            RefreshSeasonalChrome();
        }

        private bool TimingToolsAllowed()
        {
            if (isGhostMode || isResizing) return false;
            if (currentAppStatus != "SYNCED") return false;
            if (SearchInputPanel.Visibility == Visibility.Visible) return false;
            if (FestivalHubPanel.Visibility == Visibility.Visible) return false;
            if (FestivalAdvanceView?.Visibility == Visibility.Visible) return false;
            if (_festivalModeActive) return true;
            return !_playerClock;
        }

        private void SyncTimingTools()
        {
            if (_settingsPreview) return;
            bool show = TimingToolsAllowed();
            if (FestivalLineSkip != null)
                FestivalLineSkip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (BtnManualSyncToggle != null)
                BtnManualSyncToggle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show && isManualSyncMode && !_festivalAwaitingFirstTap)
            {
                isManualSyncMode = false;
                if (FullLyricsList != null) FullLyricsList.Visibility = Visibility.Collapsed;
                if (LyricsNormalView != null) LyricsNormalView.Visibility = Visibility.Visible;
            }
        }

        private void SyncFestivalLineSkip() => SyncTimingTools();

        private void ResizeGrip_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (currentAppStatus != "IDLE" && SearchInputPanel.Visibility != Visibility.Visible && FestivalHubPanel.Visibility != Visibility.Visible)
            {
                isResizing = true;
                SidePanelCol.Width = new GridLength(0);
                ContentPanelCol.Width = new GridLength(100, GridUnitType.Star);
                SidePanel.Visibility = Visibility.Collapsed;
                PlayingControls.Visibility = Visibility.Collapsed;
                TopRightControls.Visibility = Visibility.Collapsed;
                if (FestivalLineSkip != null) FestivalLineSkip.Visibility = Visibility.Collapsed;
                MainBorder.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * bgOpacity), 5, 5, 5));
                MainBorder.BorderBrush = Brushes.Transparent;
                RefreshSeasonalChrome();
            }
        }

        private void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            isResizing = false;
            if (currentAppStatus != "IDLE" && SearchInputPanel.Visibility != Visibility.Visible && FestivalHubPanel.Visibility != Visibility.Visible)
                UpdateVisualState(isGhostMode);
        }

        private async Task ConnectWebSocket()
        {
            var buffer = new byte[8192];
            using var message = new MemoryStream();

            while (!_shutdown.IsCancellationRequested)
            {
                _webSocket = new ClientWebSocket();
                try
                {
                    await _webSocket.ConnectAsync(new Uri($"ws://127.0.0.1:{serverPort}"), _shutdown.Token);
                    CrashReporter.LogBreadcrumb("websocket.connected", serverPort);
                    message.SetLength(0);
                    while (_webSocket.State == WebSocketState.Open && !_shutdown.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _shutdown.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                            break;

                        message.Write(buffer, 0, result.Count);
                        if (message.Length > MaxWsMessageBytes)
                        {
                            CrashReporter.Info($"Mensagem WS descartada ({message.Length} bytes).");
                            message.SetLength(0);
                            continue;
                        }
                        if (!result.EndOfMessage)
                            continue;

                        string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                        message.SetLength(0);
                        UpdateUI(json);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (!_shuttingDown) CrashReporter.Log(ex, "ConnectWebSocket");
                }
                if (_shutdown.IsCancellationRequested) break;
                try { await Task.Delay(2000, _shutdown.Token); }
                catch (OperationCanceledException) { break; }
            }
        }

        private void HoldAutoForCurrentTrack()
        {
            string artist = (LblArtistName.Text ?? "").Trim();
            string song = (LblSongTitle.Text ?? "").Trim();
            _autoHoldKey = $"{artist}|{song}".ToLowerInvariant();
            _autoHoldUntilUtc = DateTime.UtcNow.AddSeconds(2);
            CrashReporter.LogBreadcrumb("auto.hold", _autoHoldKey);
        }

        private void ReleaseAutoHold()
        {
            _autoHoldKey = null;
            _autoHoldUntilUtc = DateTime.MinValue;
        }

        private bool ShouldSuppressAutoStatus(string status, string artist, string song)
        {
            bool cooldown = DateTime.UtcNow < _autoHoldUntilUtc;
            bool held = !string.IsNullOrEmpty(_autoHoldKey);
            if (!cooldown && !held) return false;
            if (status == "IDLE") return false;

            if (status is "LISTENING" or "SEARCHING")
                return cooldown || held;

            string key = $"{artist.Trim()}|{song.Trim()}".ToLowerInvariant();
            if (held && key == _autoHoldKey)
                return true;

            ReleaseAutoHold();
            return false;
        }

        private void UpdateUI(string jsonMessage)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonMessage);
                JsonElement root = doc.RootElement;
                string status = root.GetProperty("status").GetString() ?? "IDLE";
                string currentLyrics = root.TryGetProperty("current_lyrics", out var clp) ? clp.GetString() ?? "" : "";
                bool isTranslating = root.TryGetProperty("is_translating", out var tp) && tp.GetBoolean();
                bool autoMode = root.TryGetProperty("auto_mode", out var amp) && amp.GetBoolean();
                string currentLyricsOriginal = root.TryGetProperty("current_lyrics_original", out var clo) ? clo.GetString() ?? "" : "";
                bool isTranslatedActive = root.TryGetProperty("is_translated_active", out var ita) && ita.GetBoolean();
                string currentLanguage = root.TryGetProperty("current_language", out var cl) ? cl.GetString() ?? "original" : "original";
                string song = root.TryGetProperty("song", out var songEl) ? songEl.GetString() ?? "" : "";
                string artist = root.TryGetProperty("artist", out var artEl) ? artEl.GetString() ?? "" : "";
                string previousLyrics = root.TryGetProperty("previous_lyrics", out var prevEl) ? prevEl.GetString() ?? "" : "";
                string nextLyrics = root.TryGetProperty("next_lyrics", out var nextEl) ? nextEl.GetString() ?? "" : "";
                string? coverUrl = root.TryGetProperty("cover_art", out var c) ? c.GetString() : null;
                bool festivalMode = root.TryGetProperty("festival_mode", out var fme) && fme.ValueKind == JsonValueKind.True;
                bool playerClock = root.TryGetProperty("player_clock", out var pcl) && pcl.ValueKind == JsonValueKind.True;
                string? festivalAdvanceSong = null;
                string? festivalAdvanceArtist = null;
                if (root.TryGetProperty("festival_advance", out var fa) && fa.ValueKind == JsonValueKind.Object)
                {
                    festivalAdvanceSong = fa.TryGetProperty("song", out var fas) ? fas.GetString() : "";
                    festivalAdvanceArtist = fa.TryGetProperty("artist", out var faa) ? faa.GetString() : "";
                }
                string? fullLyricsRaw = null;
                if (root.TryGetProperty("full_lyrics", out var full) && full.ValueKind is JsonValueKind.Array or JsonValueKind.String)
                    fullLyricsRaw = full.GetRawText();

                if (_shuttingDown || Dispatcher.HasShutdownStarted) return;

                Dispatcher.Invoke(() => {
                    if (ShouldSuppressAutoStatus(status, artist, song))
                    {
                        CrashReporter.LogBreadcrumb("auto.suppress", status);
                        status = "IDLE";
                        currentLyrics = previousLyrics = nextLyrics = "";
                        song = "";
                        coverUrl = "";
                        fullLyricsRaw = null;
                    }

                    currentAppStatus = status;
                    _playerClock = playerClock;
                    LblTranslating.Visibility = isTranslating ? Visibility.Visible : Visibility.Collapsed;
                    ApplyAutoModeFromServer(autoMode);

                    TransOriginal.IsChecked = currentLanguage == "original";
                    TransRomanized.IsChecked = currentLanguage == "romanized";
                    TransEn.IsChecked = currentLanguage == "en";
                    TransEs.IsChecked = currentLanguage == "es";
                    TransFr.IsChecked = currentLanguage == "fr";
                    TransPt.IsChecked = currentLanguage == "pt";

                    if (status == "IDLE")
                    {
                        LblSongTitle.Visibility = Visibility.Collapsed;
                        LblArtistName.Text = uiStrings[currentAppLanguage]["Ready"];
                        currentCoverUrl = ""; LoadCoverArt("");
                        HomeControls.Visibility = (festivalMode || IsFestivalHubOpen) ? Visibility.Collapsed : Visibility.Visible;
                        isGhostMode = false;
                        LblCurrent.Text = ""; LblPrevious.Text = ""; LblNext.Text = "";
                        LblCurrentOriginal.Text = ""; LblCurrentOriginal.Visibility = Visibility.Collapsed;
                        lastCurrentLyricsOriginal = ""; lastIsTranslatedActive = false;
                        if (_lastFullLyricsJson != null)
                        {
                            _lastFullLyricsJson = null;
                            FullLyricsList.ItemsSource = null;
                        }
                        LoadingSpinner.Visibility = Visibility.Collapsed;
                        StopLoadingSpinnerAnimation();
                    }
                    else
                    {
                        LblSongTitle.Visibility = Visibility.Visible;
                        HomeControls.Visibility = Visibility.Collapsed;
                        LblSongTitle.Text = song;
                        LblArtistName.Text = string.IsNullOrEmpty(artist) ? "..." : artist;
                        string incomingCover = coverUrl ?? "";
                        if (incomingCover != currentCoverUrl)
                        {
                            currentCoverUrl = incomingCover;
                            LoadCoverArt(incomingCover);
                        }
                    }

                    if (status == "NOT_FOUND" && festivalMode)
                    {
                        currentLyrics = uiStrings[currentAppLanguage]["FestivalNoLyrics"];
                        previousLyrics = "";
                        nextLyrics = "";
                    }

                    if (status == "LISTENING" || status == "SEARCHING")
                    {
                        LoadingSpinner.Visibility = Visibility.Visible;
                        StartLoadingSpinnerAnimation();
                        LblLoadingText.Text = (status == "LISTENING") ? uiStrings[currentAppLanguage]["Listening"] : uiStrings[currentAppLanguage]["Searching"];
                        LyricsNormalView.Visibility = Visibility.Collapsed;
                    }
                    else if (status != "IDLE")
                    {
                        LoadingSpinner.Visibility = Visibility.Collapsed;
                        StopLoadingSpinnerAnimation();
                        if (!isManualSyncMode) LyricsNormalView.Visibility = Visibility.Visible;
                        LblCurrent.Text = currentLyrics;
                        LblPrevious.Text = previousLyrics;
                        LblNext.Text = nextLyrics;
                        lastCurrentLyricsOriginal = currentLyricsOriginal;
                        lastIsTranslatedActive = isTranslatedActive;
                        RefreshOriginalLyricsDisplay();
                    }
                    if (fullLyricsRaw != null && fullLyricsRaw != _lastFullLyricsJson)
                    {
                        _lastFullLyricsJson = fullLyricsRaw;
                        FullLyricsList.ItemsSource = JsonSerializer.Deserialize<List<LyricLine>>(fullLyricsRaw);
                    }

                    ApplyFestivalFromServer(festivalMode, artist, song, status, festivalAdvanceSong, festivalAdvanceArtist);
                    SyncTimingTools();

                    if (!isResizing) UpdateVisualState(isGhostMode);
                });
            }
            catch (OutOfMemoryException ex)
            {
                CrashReporter.Log(ex, "UpdateUI.OOM");
                CrashReporter.TryRecoverMemory();
                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        AlbumCoverImg.Source = null;
                        FullLyricsList.ItemsSource = null;
                        _lastFullLyricsJson = null;
                    });
                }
                catch { }
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "UpdateUI");
            }
        }

        private void LoadSkipIcons()
        {
            try
            {
                var prev = LoadSkipIcon("double_arrow_left");
                var next = LoadSkipIcon("double_arrow_right");
                ApplySkipGlyph(ImgPrevTrack, prev);
                ApplySkipGlyph(ImgNextTrack, next);
                ApplySkipGlyph(ImgFestivalPrevTrack, prev);
                ApplySkipGlyph(ImgFestivalNextTrack, next);
                ApplySkipGlyph(ImgOverlayBack, LoadSkipIcon("arrow_back"));
                ApplySkipGlyph(ImgFestivalLineUp, LoadSkipIcon("text_up"));
                ApplySkipGlyph(ImgFestivalLineDown, LoadSkipIcon("text_down"));
                ApplySkipGlyph(IcoListenBig, LoadSkipIcon("icon_listen"));
                ApplySkipGlyph(IcoSearchBig, LoadSkipIcon("icon_search"));
                ApplySkipGlyph(IcoFestivalBig, LoadSkipIcon("icon_festival"));
                ApplySkipGlyph(IcoHelpHome, LoadSkipIcon("icon_help"));
                ApplySkipGlyph(IcoListenSide, LoadSkipIcon("icon_listen"));
                ApplySkipGlyph(IcoSearchSide, LoadSkipIcon("icon_search"));
                ApplySkipGlyph(IcoFestivalSearch, LoadSkipIcon("icon_search"));

                // Support bar icons (assets/bmc-logo.svg, rate.svg, feedback.svg) — used as alpha masks by SupportPillButton.
                BtnDonate.Tag = LoadSkipIcon("bmc-logo");
                BtnRate.Tag = LoadSkipIcon("rate");
                BtnFeedback.Tag = LoadSkipIcon("feedback");

                _icoLock = LoadSkipIcon("lock");
                _icoLockOpen = LoadSkipIcon("lock_open");
                ApplySkipGlyph(ImgLockIcon, isLocked ? _icoLock : _icoLockOpen);
            }
            catch (Exception ex) { CrashReporter.Log(ex, "LoadSkipIcons"); }
        }

        private static void ApplySkipGlyph(System.Windows.Shapes.Rectangle target, ImageSource? source)
        {
            if (source == null)
            {
                target.OpacityMask = null;
                target.Fill = Brushes.Transparent;
                return;
            }
            var mask = new ImageBrush(source) { Stretch = Stretch.Uniform };
            if (mask.CanFreeze) mask.Freeze();
            target.OpacityMask = mask;
            target.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        }

        private static ImageSource? LoadSkipIcon(string stem)
        {
            foreach (string name in new[] { stem + ".png", stem + ".svg" })
            {
                string? disk = FindAssetFile(name);
                if (disk == null) continue;
                ImageSource? src = LoadSkipFile(disk);
                if (src != null) return src;
            }

            foreach (string name in new[] { stem + ".png", stem + ".svg" })
            {
                ImageSource? packed = TryLoadPackedSkip(name);
                if (packed != null) return packed;
            }

            CrashReporter.Info($"Ícone de skip ausente: {stem}.png / {stem}.svg");
            return null;
        }

        private static ImageSource? LoadSkipFile(string path)
        {
            try
            {
                if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                    return DecodeBitmap(new Uri(path, UriKind.Absolute), SkipIconDecodeWidth);

                Color fill = Color.FromRgb(0xE0, 0xE0, 0xE0);
                return SvgGlyph.TryLoadFile(path, fill);
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "LoadSkipFile");
                return null;
            }
        }

        private static ImageSource? TryLoadPackedSkip(string fileName)
        {
            foreach (string uri in new[]
            {
                $"pack://application:,,,/assets/{fileName}",
                $"pack://siteoforigin:,,,/assets/{fileName}",
            })
            {
                try
                {
                    if (fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    {
                        var info = Application.GetResourceStream(new Uri(uri, UriKind.Absolute));
                        if (info?.Stream == null) continue;
                        using var reader = new StreamReader(info.Stream);
                        return SvgGlyph.TryParse(reader.ReadToEnd(), Color.FromRgb(0xE0, 0xE0, 0xE0));
                    }
                    return DecodeBitmap(new Uri(uri, UriKind.Absolute), SkipIconDecodeWidth);
                }
                catch { }
            }
            return null;
        }

        private static string? FindAssetFile(string fileName)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in AssetSearchDirs())
            {
                string path = System.IO.Path.Combine(dir, fileName);
                if (seen.Add(path) && File.Exists(path))
                    return path;
            }
            return null;
        }

        private static IEnumerable<string> AssetSearchDirs()
        {
            string? dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 7 && !string.IsNullOrEmpty(dir); i++)
            {
                yield return System.IO.Path.Combine(dir, "assets");
                yield return System.IO.Path.Combine(dir, "Frontline", "assets");
                dir = Directory.GetParent(dir)?.FullName;
            }
        }

        private void StartLoadingSpinnerAnimation()
        {
            if (_spinnerAnimating) return;
            _spinnerAnimating = true;
            AnimateSpinnerRotationStep();
            AnimateSpinnerArcStep();
        }

        private void StopLoadingSpinnerAnimation()
        {
            _spinnerAnimating = false;
            ListenArcSpin.BeginAnimation(RotateTransform.AngleProperty, null);
            ListenArc.BeginAnimation(Ellipse.StrokeDashOffsetProperty, null);
            ListenArc.BeginAnimation(UIElement.OpacityProperty, null);
            ListenArcSpin.Angle = 0;
            ListenArc.StrokeDashOffset = 39;
            ListenArc.Opacity = 1.0;
        }

        private void AnimateSpinnerRotationStep()
        {
            if (!_spinnerAnimating) return;

            double durationSeconds = 0.5 + _spinnerRandom.NextDouble() * 1.7;
            double fromAngle = ListenArcSpin.Angle;
            double toAngle = fromAngle + 360.0;

            var rotationAnim = new DoubleAnimation
            {
                From = fromAngle,
                To = toAngle,
                Duration = TimeSpan.FromSeconds(durationSeconds),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            rotationAnim.Completed += (s, e) =>
            {
                if (!_spinnerAnimating) return;
                ListenArcSpin.BeginAnimation(RotateTransform.AngleProperty, null);
                ListenArcSpin.Angle = toAngle % 360.0;
                AnimateSpinnerRotationStep();
            };

            ListenArcSpin.BeginAnimation(RotateTransform.AngleProperty, rotationAnim);
        }

        private void AnimateSpinnerArcStep()
        {
            if (!_spinnerAnimating) return;

            double targetOffset = _spinnerRandom.NextDouble() * 39.0;
            double durationSeconds = 0.35 + _spinnerRandom.NextDouble() * 0.9;

            var arcAnim = new DoubleAnimation
            {
                To = targetOffset,
                Duration = TimeSpan.FromSeconds(durationSeconds),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            double targetOpacity = 0.35 + _spinnerRandom.NextDouble() * 0.65;
            double opacityDuration = 0.3 + _spinnerRandom.NextDouble() * 0.8;
            var opacityAnim = new DoubleAnimation
            {
                To = targetOpacity,
                Duration = TimeSpan.FromSeconds(opacityDuration),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            arcAnim.Completed += (s, e) =>
            {
                if (!_spinnerAnimating) return;
                AnimateSpinnerArcStep();
            };

            ListenArc.BeginAnimation(Ellipse.StrokeDashOffsetProperty, arcAnim);
            ListenArc.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        }

        private void LoadCoverArt(string? url)
        {
            try
            {
                if (string.IsNullOrEmpty(url))
                {
                    ApplyMenuLogo(force: true);
                    return;
                }

                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && url.Length > 400_000)
                {
                    CrashReporter.Info("Capa data-URI ignorada (grande demais).");
                    return;
                }

                AlbumCoverImg.Source = DecodeBitmap(new Uri(url, UriKind.Absolute));
                SetLogoAdornShown(false);
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "LoadCoverArt");
                currentCoverUrl = "";
            }
        }

        private static BitmapImage DecodeBitmap(Uri uri, int? decodePixelWidth = CoverDecodeWidth)
        {
            BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
            if (decodePixelWidth is > 0)
                bitmap.DecodePixelWidth = decodePixelWidth.Value;
            bitmap.EndInit();
            if (bitmap.CanFreeze) bitmap.Freeze();
            return bitmap;
        }

        private async void SendCommand(string action, string? lang = null, string? artist = null, string? song = null, double? time = null, bool? autoOn = null, Dictionary<string, object?>? extra = null)
        {
            try
            {
                if (_webSocket != null && _webSocket.State == WebSocketState.Open)
                {
                    var p = new Dictionary<string, object?> { ["action"] = action };
                    if (lang != null) p["lang"] = lang;
                    if (artist != null) p["artist"] = artist;
                    if (song != null) p["song"] = song;
                    if (time != null) p["time"] = time;
                    if (autoOn != null) p["on"] = autoOn.Value;
                    if (action == "RESET") p["hold_auto"] = true;
                    if (extra != null)
                    {
                        foreach (var kv in extra)
                            p[kv.Key] = kv.Value;
                    }
                    await _webSocket.SendAsync(
                        new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p))),
                        WebSocketMessageType.Text, true, _shutdown.Token);
                }
            }
            catch (Exception ex) { CrashReporter.Log(ex, "SendCommand"); }
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Maximized) BtnRestore.Visibility = Visibility.Visible;
            else if (this.WindowState == WindowState.Normal) BtnRestore.Visibility = Visibility.Collapsed;
        }

        private void BtnRestore_Click(object sender, RoutedEventArgs e) { this.WindowState = WindowState.Normal; }
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (!isGhostMode) DragMove(); }
        private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e) { Width = Math.Max(MinWidth, Width + e.HorizontalChange); Height = Math.Max(MinHeight, Height + e.VerticalChange); }
        private void BtnMinimize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            SendCommand("QUIT");
            ShutdownEngine();
            Application.Current.Shutdown();
        }

        private void BtnSearchShow_Click(object sender, RoutedEventArgs e)
        {
            OpenManualSearchScreen();
        }

        private void OpenManualSearchScreen()
        {
            try
            {
                BtnMenu.IsChecked = false;
                if (!_festivalModeActive)
                    FestivalHubPanel.Visibility = Visibility.Collapsed;
                SearchInputPanel.Visibility = Visibility.Visible;
                SetOverlayBackVisible(true);
                ResizeGrip.Visibility = Visibility.Visible;
                TopRightControls.Visibility = Visibility.Visible;
                RefreshHistoryEmptyState();
                if (!isResizing) UpdateVisualState(false);
                Dispatcher.BeginInvoke(() =>
                {
                    try { TxtArtist.Focus(); } catch { }
                });
            }
            catch (Exception ex) { CrashReporter.Log(ex, "OpenManualSearchScreen"); }
        }

        private void TurnAutoOff()
        {
            _wantAuto = false;
            BtnAutoSide.IsChecked = false;
            AppSettings.SetBool("AutoMode", false);
            if (_autoSyncedWithServer)
                SendCommand("AUTO_SET", autoOn: false);
        }

        private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                BtnManualSearch_Execute(sender, e);
        }

        private void BtnManualSearch_Execute(object sender, RoutedEventArgs e)
        {
            string artist = SearchHistoryStore.Clamp(TxtArtist.Text);
            string song = SearchHistoryStore.Clamp(TxtSong.Text);
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(song))
                return;
            TxtArtist.Text = artist;
            TxtSong.Text = song;
            RememberSearch(artist, song);
            RememberFestivalSearch(artist, song);
            if (!_festivalModeActive)
            {
                TurnAutoOff();
                ReleaseAutoHold();
            }
            SendCommand("MANUAL_SEARCH", null, artist, song);
            SearchInputPanel.Visibility = Visibility.Collapsed;
            SetOverlayBackVisible(IsFestivalHubOpen);
        }

        private void BtnSearchCancel_Click(object sender, RoutedEventArgs e)
        {
            SearchInputPanel.Visibility = Visibility.Collapsed;
            SearchHistoryList.SelectedItem = null;
            SetOverlayBackVisible(IsFestivalHubOpen);
            if (!isResizing) UpdateVisualState(isGhostMode);
        }
        private void BtnManualSync_Toggle(object sender, RoutedEventArgs e) { isManualSyncMode = !isManualSyncMode; FullLyricsList.Visibility = isManualSyncMode ? Visibility.Visible : Visibility.Collapsed; LyricsNormalView.Visibility = isManualSyncMode ? Visibility.Collapsed : Visibility.Visible; BtnMenu.IsChecked = false; }
        private void FullLyricsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FullLyricsList.SelectedItem is not LyricLine s) return;
            SendCommand("SET_SYNC_TIME", null, null, null, s.Timestamp);
            isManualSyncMode = false;
            OnFestivalLinePicked();
            FullLyricsList.Visibility = Visibility.Collapsed;
            LyricsNormalView.Visibility = Visibility.Visible;
            FullLyricsList.SelectedItem = null;
        }
        private void BtnListen_Click(object sender, RoutedEventArgs e)
        {
            ReleaseAutoHold();
            SendCommand("LISTEN");
        }
        private void SendMediaKey(byte virtualKey)
        {
            keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
            keybd_event(virtualKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        private void BtnPrevTrack_Click(object sender, RoutedEventArgs e) { SendMediaKey(VK_MEDIA_PREV_TRACK); }
        private void BtnNextTrack_Click(object sender, RoutedEventArgs e) { SendMediaKey(VK_MEDIA_NEXT_TRACK); }
        private void BtnAuto_Click(object sender, RoutedEventArgs e)
        {
            ReleaseAutoHold();
            _wantAuto = BtnAutoSide.IsChecked == true;
            AppSettings.SetBool("AutoMode", _wantAuto);
            if (_autoSyncedWithServer)
                SendCommand("AUTO_TOGGLE");
            SyncTimingTools();
        }
        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            HoldAutoForCurrentTrack();
            SendCommand("RESET");
            BtnMenu.IsChecked = false;
        }
        private void BtnTrans_Click(object sender, RoutedEventArgs e) { if (sender is ToggleButton b && b.Tag is string l) SendCommand("TRANSLATE", l); BtnMenu.IsChecked = false; }
        private void ChkKeepOriginal_Click(object sender, RoutedEventArgs e)
        {
            keepOriginalWithTranslation = ChkKeepOriginal.IsChecked == true;
            RefreshOriginalLyricsDisplay();
        }

        private void RefreshOriginalLyricsDisplay()
        {
            bool show = keepOriginalWithTranslation && lastIsTranslatedActive && !string.IsNullOrWhiteSpace(lastCurrentLyricsOriginal);
            LblCurrentOriginal.Text = lastCurrentLyricsOriginal;
            LblCurrentOriginal.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
        private void BtnStartHelp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(HelpWebsiteUrl) { UseShellExecute = true });
                WindowState = WindowState.Minimized;
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "HelpWebsite");
                AlertDark("Não foi possível abrir o link: " + ex.Message);
            }
        }
        private void BtnDonate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://www.buymeacoffee.com/juliocax") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "Donate");
                AlertDark("Não foi possível abrir o link: " + ex.Message);
            }
        }

        private void BtnRate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(StoreReviewUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "RateApp");
                AlertDark("Não foi possível abrir o link: " + ex.Message);
            }
        }

        private void BtnFeedback_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(FeedbackFormUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "Feedback");
                AlertDark("Não foi possível abrir o link: " + ex.Message);
            }
        }

        private void ShowNewVersionNotesIfNeeded()
        {
            string version = ReleaseNotesWindow.CurrentVersion;
            if (AppSettings.GetString("LastSeenReleaseNotesVersion", "") == version) return;
            ShowReleaseNotes();
        }

        private void ShowReleaseNotes()
        {
            try
            {
                if (SettingsPopup != null) SettingsPopup.IsOpen = false;
                var notes = new ReleaseNotesWindow(currentAppLanguage) { Owner = this };
                notes.ShowDialog();
                AppSettings.SetString("LastSeenReleaseNotesVersion", ReleaseNotesWindow.CurrentVersion);
            }
            catch (Exception ex) { CrashReporter.Log(ex, "ReleaseNotes"); }
        }

        private void BtnReleaseNotes_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://buymeacoffee.com/juliocax/frontline-lyrics-1-5-0") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "ReleaseNotes");
                AlertDark("Não foi possível abrir o link: " + ex.Message);
            }
        }

        private void InitSearchHistory()
        {
            try
            {
                _searchHistory.Clear();
                foreach (var row in SearchHistoryStore.Load())
                    _searchHistory.Add(row);
                SearchHistoryList.ItemsSource = _searchHistory;
                _historyView = CollectionViewSource.GetDefaultView(_searchHistory);
                StyleSearchHistoryRows();
                ApplyHistorySort();
                RefreshHistoryDateLabels();
                RefreshHistoryEmptyState();
            }
            catch (Exception ex) { CrashReporter.Log(ex, "InitSearchHistory"); }
        }

        private void StyleSearchHistoryRows()
        {
            if (SearchHistoryList == null) return;
            var text = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4));
            text.Freeze();
            SearchHistoryList.Resources[SystemColors.HighlightBrushKey] = Brushes.Transparent;
            SearchHistoryList.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = Brushes.Transparent;
            SearchHistoryList.Resources[SystemColors.ControlBrushKey] = Brushes.Transparent;
            SearchHistoryList.Resources[SystemColors.HighlightTextBrushKey] = text;
            SearchHistoryList.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = text;
        }

        private void RememberSearch(string artist, string song)
        {
            try
            {
                string key = $"{artist}|{song}".ToLowerInvariant();
                var existing = _searchHistory.FirstOrDefault(r => r.Key == key);
                if (existing != null)
                    _searchHistory.Remove(existing);
                var row = new SearchHistoryEntry
                {
                    Artist = artist,
                    Song = song,
                    SearchedAtUtc = DateTime.UtcNow,
                };
                row.Normalize();
                row.RefreshDateLabel(currentAppLanguage);
                _searchHistory.Insert(0, row);
                while (_searchHistory.Count > SearchHistoryStore.MaxEntries)
                    _searchHistory.RemoveAt(_searchHistory.Count - 1);
                PersistSearchHistory();
                ApplyHistorySort();
                RefreshHistoryEmptyState();
            }
            catch (Exception ex) { CrashReporter.Log(ex, "RememberSearch"); }
        }

        private void PersistSearchHistory() => SearchHistoryStore.Save(_searchHistory);

        private void RefreshHistoryEmptyState()
        {
            if (LblSearchHistoryEmpty == null) return;
            LblSearchHistoryEmpty.Visibility = _searchHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshHistoryDateLabels()
        {
            foreach (var row in _searchHistory)
                row.RefreshDateLabel(currentAppLanguage);
            _historyView?.Refresh();
        }

        private void RefreshHistoryHeaders()
        {
            if (!uiStrings.TryGetValue(currentAppLanguage, out var t)) return;
            if (ColHistoryArtist != null)
                ColHistoryArtist.Header = HeaderWithArrow(t["HistoryArtist"], "Artist");
            if (ColHistorySong != null)
                ColHistorySong.Header = HeaderWithArrow(t["HistorySong"], "Song");
            if (ColHistoryDate != null)
                ColHistoryDate.Header = HeaderWithArrow(t["HistoryDate"], "Date");
        }

        private string HeaderWithArrow(string title, string column)
        {
            if (_historySort != column) return title;
            return _historyDir == ListSortDirection.Ascending ? title + " ▲" : title + " ▼";
        }

        private void ApplyHistorySort()
        {
            if (_historyView == null) return;
            string prop = _historySort switch
            {
                "Artist" => nameof(SearchHistoryEntry.Artist),
                "Song" => nameof(SearchHistoryEntry.Song),
                _ => nameof(SearchHistoryEntry.SearchedAtUtc),
            };
            _historyView.SortDescriptions.Clear();
            _historyView.SortDescriptions.Add(new SortDescription(prop, _historyDir));
            RefreshHistoryHeaders();
        }

        private void HistoryHeader_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader header) return;
            if (header.Role == GridViewColumnHeaderRole.Padding) return;
            if (header.Column == null || header.Column == ColHistoryDelete) return;
            string column = header.Column == ColHistoryArtist ? "Artist"
                          : header.Column == ColHistorySong ? "Song"
                          : "Date";
            if (_historySort == column)
                _historyDir = _historyDir == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            else
            {
                _historySort = column;
                _historyDir = column == "Date" ? ListSortDirection.Descending : ListSortDirection.Ascending;
            }
            ApplyHistorySort();
        }

        private void HistoryList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (FindParentButton(e.OriginalSource as DependencyObject) != null)
                return;
            var item = FindParentListViewItem(e.OriginalSource as DependencyObject);
            if (item?.DataContext is SearchHistoryEntry row)
                RunHistorySearch(row);
        }

        private static ListViewItem? FindParentListViewItem(DependencyObject? node)
        {
            while (node != null)
            {
                if (node is ListViewItem item) return item;
                node = VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        private void RunHistorySearch(SearchHistoryEntry row)
        {
            TxtArtist.Text = row.Artist;
            TxtSong.Text = row.Song;
            BtnManualSearch_Execute(this, new RoutedEventArgs());
        }

        private void BtnHistoryDelete_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement fe || fe.DataContext is not SearchHistoryEntry row)
                return;
            _searchHistory.Remove(row);
            PersistSearchHistory();
            RefreshHistoryEmptyState();
        }

        private static Button? FindParentButton(DependencyObject? node)
        {
            while (node != null)
            {
                if (node is Button b) return b;
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        private void ApplyAutoModeFromServer(bool autoMode)
        {
            if (!_autoSyncedWithServer)
            {
                _autoSyncedWithServer = true;
                BtnAutoSide.IsChecked = _wantAuto;
                if (_wantAuto != autoMode)
                    SendCommand("AUTO_TOGGLE");
                return;
            }

            BtnAutoSide.IsChecked = autoMode;
            if (_wantAuto != autoMode)
            {
                _wantAuto = autoMode;
                AppSettings.SetBool("AutoMode", _wantAuto);
            }
        }

        private void RestoreFontAndAuto()
        {
            _loadingSettings = true;
            try
            {
                double font = AppSettings.GetDouble("FontSize", 26);
                if (SldFontSize != null)
                    SldFontSize.Value = Math.Clamp(font, SldFontSize.Minimum, SldFontSize.Maximum);
                _wantAuto = AppSettings.GetBool("AutoMode", false);
                if (BtnAutoSide != null)
                    BtnAutoSide.IsChecked = _wantAuto;
                string lang = AppSettings.GetString("AppLanguage", "en");
                if (!uiStrings.ContainsKey(lang)) lang = "en";
                currentAppLanguage = lang;
                double opacity = AppSettings.GetDouble("BgOpacity", 0.8);
                if (SldBgOpacity != null)
                    SldBgOpacity.Value = Math.Clamp(opacity, SldBgOpacity.Minimum, SldBgOpacity.Maximum);
                else
                    bgOpacity = Math.Clamp(opacity, 0.0, 1.0);

                isLocked = AppSettings.GetBool("OverlayLocked", false);
                BtnLockOverlay.IsChecked = isLocked;
                lockHotKeyVk = (uint)AppSettings.GetDouble("LockHotkeyVk", 0);
                lockHotkeyModifiers = (uint)AppSettings.GetDouble("LockHotkeyMods", 0);
                UpdateShortcutButtonText();

            }
            catch (Exception ex) { CrashReporter.Log(ex, "RestoreFontAndAuto"); }
            finally { _loadingSettings = false; }
        }

        private void RestoreWindowPlacement()
        {
            // Restores only if the rectangle still fits inside the virtual area
            // (the monitor may have been disconnected). Contribution by Warith Adetayo.
            try
            {
                double w = AppSettings.GetDouble("WindowWidth", double.NaN);
                double h = AppSettings.GetDouble("WindowHeight", double.NaN);
                double x = AppSettings.GetDouble("WindowLeft", double.NaN);
                double y = AppSettings.GetDouble("WindowTop", double.NaN);

                if (!double.IsNaN(w) && w >= MinWidth && w <= SystemParameters.VirtualScreenWidth)
                    Width = w;
                if (!double.IsNaN(h) && h >= MinHeight && h <= SystemParameters.VirtualScreenHeight)
                    Height = h;

                if (double.IsNaN(x) || double.IsNaN(y)) return;

                bool visivel = x + Width >= SystemParameters.VirtualScreenLeft + 60
                            && x <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60
                            && y + Height >= SystemParameters.VirtualScreenTop + 20
                            && y <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 20;
                if (!visivel) return;

                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = x;
                Top = y;
            }
            catch (Exception ex) { CrashReporter.Log(ex, "RestoreWindowPlacement"); }
        }

        private void ScheduleSave()
        {
            if (_loadingSettings || !IsLoaded) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        internal bool ConfirmDark(string title, string message)
        {
            var t = uiStrings[currentAppLanguage];
            return ShowDarkDialog(title, message, t["ConfirmAction"], t["ConfirmCancel"], showCancel: true);
        }

        internal void AlertDark(string message)
        {
            var t = uiStrings.TryGetValue(currentAppLanguage, out var dict) ? dict : uiStrings["en"];
            ShowDarkDialog("", message, t["ConfirmOk"], "", showCancel: false);
        }

        private bool ShowDarkDialog(string title, string message, string yesText, string noText, bool showCancel)
        {
            if (DarkDialogOverlay == null)
                return MessageBox.Show(message, title, showCancel ? MessageBoxButton.YesNo : MessageBoxButton.OK) == MessageBoxResult.Yes;

            LblDarkDialogTitle.Text = title ?? "";
            LblDarkDialogTitle.Visibility = string.IsNullOrWhiteSpace(title) ? Visibility.Collapsed : Visibility.Visible;
            LblDarkDialogBody.Text = message ?? "";
            BtnDarkDialogYes.Content = yesText;
            BtnDarkDialogNo.Content = noText;
            BtnDarkDialogNo.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
            _darkDialogResult = false;
            DarkDialogOverlay.Visibility = Visibility.Visible;
            DarkDialogOverlay.Focus();
            _darkDialogFrame = new DispatcherFrame();
            Dispatcher.PushFrame(_darkDialogFrame);
            _darkDialogFrame = null;
            DarkDialogOverlay.Visibility = Visibility.Collapsed;
            return _darkDialogResult;
        }

        private void CloseDarkDialog(bool result)
        {
            _darkDialogResult = result;
            if (_darkDialogFrame != null)
                _darkDialogFrame.Continue = false;
        }

        private void BtnDarkDialogYes_Click(object sender, RoutedEventArgs e) => CloseDarkDialog(true);
        private void BtnDarkDialogNo_Click(object sender, RoutedEventArgs e) => CloseDarkDialog(false);

        private void DarkDialogOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseDarkDialog(false);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                CloseDarkDialog(true);
                e.Handled = true;
            }
        }

        private void PersistSettings()
        {
            if (_loadingSettings) return;
            try
            {
                if (SldFontSize != null)
                    AppSettings.SetDouble("FontSize", SldFontSize.Value);
                AppSettings.SetDouble("BgOpacity", SldBgOpacity != null ? SldBgOpacity.Value : bgOpacity);
                AppSettings.SetBool("AutoMode", _wantAuto);
                AppSettings.SetString("AppLanguage", currentAppLanguage);
                PersistAppearance();

                if (WindowState != WindowState.Normal) return;

                AppSettings.SetDouble("WindowLeft", Left);
                AppSettings.SetDouble("WindowTop", Top);
                AppSettings.SetDouble("WindowWidth", Width);
                AppSettings.SetDouble("WindowHeight", Height);
                AppSettings.SetBool("OverlayLocked", isLocked);
                AppSettings.SetDouble("LockHotkeyVk", lockHotKeyVk);
                AppSettings.SetDouble("LockHotkeyMods", lockHotkeyModifiers);
            }
            catch (Exception ex) { CrashReporter.Log(ex, "PersistSettings"); }
        }
    }

    public class LyricLine
    {
        [JsonPropertyName("timestamp")] public double Timestamp { get; set; }
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// Amarra processos filhos ao ciclo de vida do overlay: se o overlay morrer
    /// (Stop do Visual Studio, crash, Gerenciador de Tarefas), o Windows mata o filho.
    /// </summary>
    internal static class ChildProcessJob
    {
        private static readonly IntPtr _job = Create();

        private static IntPtr Create()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE

            int size = Marshal.SizeOf(info);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, ptr, (uint)size);
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return job;
        }

        /// Chame logo depois de Process.Start() do servidor Python.
        public static void Attach(Process p)
        {
            try { AssignProcessToJobObject(_job, p.Handle); }
            catch (Exception ex) { CrashReporter.Log(ex, "ChildProcessJob.Attach"); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
    }

}