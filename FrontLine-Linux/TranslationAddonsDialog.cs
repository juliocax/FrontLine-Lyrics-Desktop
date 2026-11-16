using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FrontLine_Linux;

internal sealed class TranslationAddonsDialog : Window
{
    internal sealed record Model(string Source, string Target, string SourceName,
                                 string TargetName, bool Installed);
    internal sealed record State(Model[] Models, bool Dictionary, string? Source, Model[]? Catalog);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _language;
    private readonly Grid _options = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
    private readonly StackPanel _modelCard = new() { Spacing = 9 };
    private readonly StackPanel _dictionaryCard = new() { Spacing = 9 };
    private readonly TextBlock _message = DialogUi.Text("");
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, IsVisible = false };
    private readonly Button _cancel;
    private readonly Button _from = DialogUi.Button("");
    private readonly Button _to = DialogUi.Button("");
    private string? _fromCode;
    private string? _toCode;
    private CancellationTokenSource? _operation;
    private State? _state;
    private Model[] _catalog = Array.Empty<Model>();
    private bool _closed;
    internal bool Changed { get; private set; }

    internal TranslationAddonsDialog(string language)
    {
        _language = language;
        var shell = DialogUi.Window(680, 580);
        Title = L("Manage add-ons", "Gerenciar complementos", "Gestionar complementos", "Kelola add-on");
        Width = shell.Width; Height = shell.Height;
        MinWidth = 560; MinHeight = shell.MinHeight;
        Background = shell.Background; WindowStartupLocation = shell.WindowStartupLocation;
        WindowDecorations = shell.WindowDecorations; ShowInTaskbar = false;
        var page = new StackPanel { Spacing = 12, Margin = new Thickness(22) };
        page.Children.Add(DialogUi.Text(Title!, 21, true));
        var dictionary = AddonCard(_dictionaryCard);
        Grid.SetColumn(dictionary, 1);
        _options.Children.Add(AddonCard(_modelCard));
        _options.Children.Add(dictionary);
        page.Children.Add(_options);
        page.Children.Add(_progress);
        page.Children.Add(_message);
        _cancel = DialogUi.Button(L("Cancel operation", "Cancelar operação", "Cancelar operación", "Batalkan operasi"));
        _cancel.IsVisible = false;
        _cancel.Click += (_, _) => _operation?.Cancel();
        page.Children.Add(_cancel);
        var close = DialogUi.Button(L("Close", "Fechar", "Cerrar", "Tutup"));
        close.Click += (_, _) => Close();
        page.Children.Add(close);
        Content = new ScrollViewer { Content = page };
        Opened += async (_, _) => await RunAsync("status");
        Closed += (_, _) => { _closed = true; _operation?.Cancel(); };
        Render();
    }

    private string L(string en, string pt, string es, string id) => DialogUi.L(_language, en, pt, es, id);

    internal static async Task<State> RequestAsync(string operation, CancellationToken token, params string[] codes)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri("ws://127.0.0.1:8765/addons"), token);
        var request = JsonSerializer.SerializeToUtf8Bytes(new { operation, codes });
        await socket.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, token);
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (frame.MessageType != WebSocketMessageType.Text || stream.Length + frame.Count > 256_000)
                throw new InvalidDataException("Invalid response from the add-on service.");
            stream.Write(buffer, 0, frame.Count);
        } while (!frame.EndOfMessage);
        using var json = JsonDocument.Parse(stream.ToArray());
        if (json.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException(error.GetString());
        return json.RootElement.Deserialize<State>(JsonOptions)
            ?? throw new InvalidDataException("Empty response from the add-on service.");
    }

    private async Task RunAsync(string operation, params string[] codes)
    {
        if (_operation is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        _operation = cancellation;
        _message.Text = "";
        _options.IsEnabled = false;
        _progress.IsVisible = _cancel.IsVisible = true;
        bool mutates = operation is not ("list" or "status");
        try
        {
            _state = await RequestAsync(operation, cancellation.Token, codes);
            if (_state.Catalog is not null) _catalog = _state.Catalog;
            Changed |= mutates;
        }
        catch (OperationCanceledException)
        {
            _message.Text = L("Operation canceled.", "Operação cancelada.", "Operación cancelada.", "Operasi dibatalkan.");
            Changed |= mutates;
            if (!_closed && mutates) await RefreshStatusAsync();
        }
        catch (Exception error)
        {
            _message.Text = L("The operation failed.", "A operação falhou.", "La operación falló.", "Operasi gagal.") + " " + error.Message;
            Changed |= mutates;
            if (!_closed && mutates) await RefreshStatusAsync();
        }
        finally
        {
            _operation = null;
            if (!_closed)
            {
                _options.IsEnabled = true;
                _progress.IsVisible = _cancel.IsVisible = false;
                Render();
            }
        }
    }

    private async Task RefreshStatusAsync()
    {
        using var refresh = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { _state = await RequestAsync("status", refresh.Token); }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
    }

    private Button Action(string label, Func<Task> action)
    {
        var button = DialogUi.Button(label);
        button.Content = DialogUi.Text(label);
        button.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        button.Click += async (_, _) => await action();
        return button;
    }

    private static Border AddonCard(Control content) => new()
    {
        Background = new SolidColorBrush(Color.Parse("#151318")),
        BorderBrush = new SolidColorBrush(Color.Parse("#35303D")),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(14),
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        Child = content
    };

    private void Render()
    {
        _modelCard.Children.Clear();
        _dictionaryCard.Children.Clear();
        _modelCard.Children.Add(DialogUi.Text(L("Manage models", "Gerenciar modelos", "Gestionar modelos", "Kelola model"), 17, true));
        _modelCard.Children.Add(Action(L("Load model catalog", "Carregar catálogo de modelos", "Cargar catálogo de modelos", "Muat katalog model"),
                                    () => RunAsync("list")));
        if (_catalog.Length > 0)
        {
            var sources = Choices(_catalog.Select(m => new LanguageOption(m.Source, LanguageName(m.Source, m.SourceName))));
            _fromCode = (sources.FirstOrDefault(item => item.Code == _fromCode) ?? sources.FirstOrDefault())?.Code;
            LanguagePicker.Bind(_language, _from, sources, _fromCode, code =>
            {
                _fromCode = code;
                UpdateDestinations();
            });
            UpdateDestinations();
            _modelCard.Children.Add(DialogUi.Text(L("Source language", "Idioma de origem", "Idioma de origen", "Bahasa sumber")));
            _modelCard.Children.Add(_from);
            _modelCard.Children.Add(DialogUi.Text(L("Target language", "Idioma de destino", "Idioma de destino", "Bahasa tujuan")));
            _modelCard.Children.Add(_to);
            _modelCard.Children.Add(Action(L("Install missing models", "Instalar modelos que faltam", "Instalar modelos que faltan", "Instal model yang kurang"),
                () => _fromCode is { } source && _toCode is { } target
                    ? RunAsync("install", source, target) : Task.CompletedTask));
        }
        var models = _state?.Models ?? Array.Empty<Model>();
        if (_state is not null)
            _modelCard.Children.Add(DialogUi.Text(L("Installed models", "Modelos instalados", "Modelos instalados", "Model terpasang"), 15, true));
        if (_state is not null && models.Length == 0)
            _modelCard.Children.Add(DialogUi.Text(L("No models installed.", "Nenhum modelo instalado.", "No hay modelos instalados.", "Belum ada model terpasang.")));
        foreach (var model in models.OrderBy(m => m.SourceName).ThenBy(m => m.TargetName))
        {
            _modelCard.Children.Add(Action(
                L("Remove", "Remover", "Quitar", "Hapus") + $" · {LanguageName(model.Source, model.SourceName)} → {LanguageName(model.Target, model.TargetName)}",
                () => RunAsync("remove", model.Source, model.Target)));
        }
        _dictionaryCard.Children.Add(DialogUi.Text("UniDic Lite", 17, true));
        _dictionaryCard.Children.Add(DialogUi.Text(L("Optional dictionary for Japanese romanization.",
            "Dicionário opcional para a romanização do japonês.", "Diccionario opcional para romanizar japonés.", "Kamus opsional untuk romanisasi bahasa Jepang.")));
        if (_state is null) return;
        _dictionaryCard.Children.Add(_state.Dictionary
            ? Action(L("Remove dictionary", "Remover dicionário", "Quitar diccionario", "Hapus kamus"), () => RunAsync("dictionary_remove"))
            : Action(L("Download dictionary", "Baixar dicionário", "Descargar diccionario", "Unduh kamus"), () => RunAsync("dictionary_install")));
    }

    private void UpdateDestinations()
    {
        var code = _fromCode;
        var viaEnglish = _catalog.Any(m => m.Source == code && m.Target == "en");
        var targets = Choices(_catalog.Where(m => m.Target != code && (m.Source == code || viaEnglish && m.Source == "en"))
            .Select(m => new LanguageOption(m.Target, LanguageName(m.Target, m.TargetName))));
        _toCode = (targets.FirstOrDefault(item => item.Code == _toCode) ?? targets.FirstOrDefault())?.Code;
        LanguagePicker.Bind(_language, _to, targets, _toCode, target => _toCode = target);
    }

    private static LanguageOption[] Choices(IEnumerable<LanguageOption> choices) =>
        choices.GroupBy(c => c.Code).Select(group => group.First()).OrderBy(c => c.Label).ToArray();

    private string LanguageName(string code, string fallback) =>
        LanguageLabel(_language, code == "pt" ? "pt-pt" : code, fallback);

    internal static IEnumerable<string> LanguageCodes => LanguageNames.Keys;

    internal static string LanguageLabel(string language, string code, string fallback) =>
        LanguageNames.TryGetValue(code, out var names)
            ? DialogUi.L(language, names.En, names.Pt, names.Es, names.Id) : fallback;

    private static readonly Dictionary<string, (string En, string Pt, string Es, string Id)> LanguageNames = new()
    {
        ["ar"] = ("Arabic", "Árabe", "Árabe", "Arab"),
        ["az"] = ("Azerbaijani", "Azerbaijano", "Azerbaiyano", "Azerbaijani"),
        ["bg"] = ("Bulgarian", "Búlgaro", "Búlgaro", "Bulgaria"),
        ["bn"] = ("Bangla", "Bengali", "Bengalí", "Bengali"),
        ["ca"] = ("Catalan", "Catalão", "Catalán", "Katalan"),
        ["cs"] = ("Czech", "Tcheco", "Checo", "Ceko"),
        ["da"] = ("Danish", "Dinamarquês", "Danés", "Dansk"),
        ["de"] = ("German", "Alemão", "Alemán", "Jerman"),
        ["el"] = ("Greek", "Grego", "Griego", "Yunani"),
        ["en"] = ("English", "Inglês", "Inglés", "Inggris"),
        ["eo"] = ("Esperanto", "Esperanto", "Esperanto", "Esperanto"),
        ["es"] = ("Spanish", "Espanhol", "Español", "Spanyol"),
        ["et"] = ("Estonian", "Estoniano", "Estonio", "Estonia"),
        ["eu"] = ("Basque", "Basco", "Euskera", "Basque"),
        ["fa"] = ("Persian", "Persa", "Persa", "Persia"),
        ["fi"] = ("Finnish", "Finlandês", "Finés", "Suomi"),
        ["fr"] = ("French", "Francês", "Francés", "Prancis"),
        ["ga"] = ("Irish", "Irlandês", "Irlandés", "Irlandia"),
        ["gl"] = ("Galician", "Galego", "Gallego", "Galisia"),
        ["he"] = ("Hebrew", "Hebraico", "Hebreo", "Ibrani"),
        ["hi"] = ("Hindi", "Hindi", "Hindi", "Hindi"),
        ["hu"] = ("Hungarian", "Húngaro", "Húngaro", "Hungaria"),
        ["id"] = ("Indonesian", "Indonésio", "Indonesio", "Indonesia"),
        ["it"] = ("Italian", "Italiano", "Italiano", "Italia"),
        ["ja"] = ("Japanese", "Japonês", "Japonés", "Jepang"),
        ["ko"] = ("Korean", "Coreano", "Coreano", "Korea"),
        ["ky"] = ("Kyrgyz", "Quirguiz", "Kirguís", "Kirgiz"),
        ["lt"] = ("Lithuanian", "Lituano", "Lituano", "Lituania"),
        ["lv"] = ("Latvian", "Letão", "Letón", "Latvia"),
        ["ms"] = ("Malay", "Malaio", "Malayo", "Melayu"),
        ["nb"] = ("Norwegian Bokmål", "Bokmål norueguês", "Noruego bokmal", "Bokmål Norwegia"),
        ["nl"] = ("Dutch", "Holandês", "Neerlandés", "Belanda"),
        ["pb"] = ("Portuguese (Brazil)", "Português (Brasil)", "Portugués (Brasil)", "Portugis (Brasil)"),
        ["pl"] = ("Polish", "Polonês", "Polaco", "Polski"),
        ["pt"] = ("Portuguese", "Português", "Portugués", "Portugis"),
        ["pt-pt"] = ("Portuguese (Portugal)", "Português (Portugal)", "Portugués (Portugal)", "Portugis (Portugal)"),
        ["ro"] = ("Romanian", "Romeno", "Rumano", "Rumania"),
        ["ru"] = ("Russian", "Russo", "Ruso", "Rusia"),
        ["sk"] = ("Slovak", "Eslovaco", "Eslovaco", "Slovak"),
        ["sl"] = ("Slovenian", "Esloveno", "Esloveno", "Slovenia"),
        ["sq"] = ("Albanian", "Albanês", "Albanés", "Albania"),
        ["sv"] = ("Swedish", "Sueco", "Sueco", "Swedia"),
        ["sw"] = ("Swahili", "Suaíli", "Suajili", "Swahili"),
        ["th"] = ("Thai", "Tailandês", "Tailandés", "Thai"),
        ["tl"] = ("Filipino", "Filipino", "Filipino", "Filipino"),
        ["tr"] = ("Turkish", "Turco", "Turco", "Turki"),
        ["uk"] = ("Ukrainian", "Ucraniano", "Ucraniano", "Ukraina"),
        ["ur"] = ("Urdu", "Urdu", "Urdu", "Urdu"),
        ["vi"] = ("Vietnamese", "Vietnamita", "Vietnamita", "Vietnam"),
        ["zh"] = ("Simplified Chinese", "Chinês simplificado", "Chino simplificado", "Tionghoa Sederhana"),
        ["zt"] = ("Traditional Chinese", "Chinês tradicional", "Chino tradicional", "Tionghoa Tradisional"),
    };
}
