using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Globalization;
using System.Windows.Media;

namespace FrontLineOverlay
{
    public partial class MainWindow
    {
        private sealed record OfflineModel(
            string Source, string Target, string SourceName, string TargetName, bool Installed)
        {
            public string DisplayName => $"{SourceName} → {TargetName}";
        }

        private static string TranslationModelsPath => System.IO.Path.Combine(AddonsDirectory, "translation");

        private static string TargetSettingCode(string argosCode) => argosCode switch
        {
            "pb" => "pt",
            "pt" => "pt-pt",
            "zh" => "zh-cn",
            "zt" => "zh-tw",
            _ => argosCode
        };

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

        private string OfflineLanguageName(string code, string fallback)
        {
            code = code switch { "zh-cn" => "zh", "zh-tw" => "zt", _ => code };
            return LanguageNames.TryGetValue(code, out var names)
                ? SettingsText(names.En, names.Pt, names.Es, names.Id)
                : fallback;
        }

        private void UpdateTranslationSourceNotice(JsonElement notice)
        {
            string message = "";
            if (notice.ValueKind == JsonValueKind.Object)
            {
                string selected = notice.TryGetProperty("selected", out var choice) && choice.ValueKind == JsonValueKind.String
                    ? choice.GetString() ?? "" : "";
                bool uncertain = notice.TryGetProperty("uncertain", out var unknown)
                    && unknown.ValueKind == JsonValueKind.True;
                bool mismatch = notice.TryGetProperty("mismatch", out var wrong) && wrong.ValueKind == JsonValueKind.True;
                if (mismatch)
                {
                    string choiceName = OfflineLanguageName(selected, selected);
                    message = SettingsText(
                        $"The lyrics do not match {choiceName}.",
                        $"A letra não corresponde ao idioma selecionado ({choiceName}).",
                        $"La letra no coincide con el idioma seleccionado ({choiceName}).",
                        $"Lirik tidak cocok dengan bahasa yang dipilih ({choiceName}).");
                }
                if (uncertain)
                {
                    message += (message.Length > 0 ? " " : "") + SettingsText(
                        "Lines whose language could not be identified stay in the original.",
                        "Trechos cujo idioma não pôde ser identificado ficam no original.",
                        "Los versos cuyo idioma no se pudo identificar se mantienen en el original.",
                        "Baris yang bahasanya tidak dapat dikenali tetap dalam bentuk asli.");
                }
            }
            LblTranslationSourceNotice.Text = message;
            LblTranslationSourceNotice.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool _refreshingOfflineSources;

        private static string OfflineSourcePath => System.IO.Path.Combine(TranslationModelsPath, "source.txt");

        private OfflineModel[] ReadInstalledOfflineModels()
        {
            var models = new List<OfflineModel>();
            try
            {
                if (!Directory.Exists(TranslationModelsPath))
                    return Array.Empty<OfflineModel>();

                foreach (string directory in Directory.EnumerateDirectories(TranslationModelsPath))
                {
                    string model = System.IO.Path.Combine(directory, "model", "model.bin");
                    string metadata = System.IO.Path.Combine(directory, "metadata.json");
                    if (!File.Exists(model) || !File.Exists(metadata))
                        continue;
                    using var document = JsonDocument.Parse(File.ReadAllText(metadata));
                    var item = document.RootElement;
                    string source = item.GetProperty("from_code").GetString() ?? "";
                    string code = item.GetProperty("to_code").GetString() ?? "";
                    if (System.IO.Path.GetFileName(directory) != $"{source}_{code}" || code.Length == 0)
                        continue;
                    string sourceName = item.GetProperty("from_name").GetString() ?? source;
                    string targetName = item.GetProperty("to_name").GetString() ?? code;
                    models.Add(new OfflineModel(source, code,
                        OfflineLanguageName(source, sourceName), OfflineLanguageName(code, targetName), true));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
            {
                CrashReporter.Log(ex, "OfflineTranslationTargets");
            }
            return models.ToArray();
        }

        private IEnumerable<(string Code, string Label)> InstalledOfflineTargets() =>
            ReadInstalledOfflineModels().Select(model => (TargetSettingCode(model.Target), model.TargetName)).Distinct();

        private void RefreshOfflineTranslationSources(OfflineModel[] models)
        {
            _refreshingOfflineSources = true;
            try
            {
                string automatic = SettingsText("Automatic", "Automático", "Automático", "Otomatis");
                var options = models.GroupBy(model => model.Source)
                    .Select(group => KeyValuePair.Create(group.Key, group.First().SourceName))
                    .OrderBy(option => option.Value).Prepend(KeyValuePair.Create("", automatic));
                CmbOfflineSource.DisplayMemberPath = "Value";
                CmbOfflineSource.SelectedValuePath = "Key";
                CmbOfflineSource.ItemsSource = options.ToArray();
                CmbOfflineSource.SelectedValue = File.Exists(OfflineSourcePath) ? File.ReadAllText(OfflineSourcePath).Trim() : "";
                if (CmbOfflineSource.SelectedIndex < 0)
                    CmbOfflineSource.SelectedIndex = 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrashReporter.Log(ex, "OfflineTranslationSource");
            }
            finally
            {
                _refreshingOfflineSources = false;
            }
        }

        private void OfflineSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshingOfflineSources || CmbOfflineSource.SelectedValue is not string source)
                return;
            try
            {
                if (source.Length == 0)
                    File.Delete(OfflineSourcePath);
                else
                    File.WriteAllText(OfflineSourcePath, source);
                if (currentAppStatus == "SYNCED" && _settingsPreferredTranslation is not ("original" or "romanized"))
                    RequestSettingsTranslation(LblArtistName.Text, LblSongTitle.Text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrashReporter.Log(ex, "OfflineTranslationSource");
            }
        }

        private string TranslationTargetLabel(string code, string fallback) =>
            OfflineLanguageName(code == "pt" ? "pb" : code, fallback);

        private void RefreshOfflineTranslationTargets()
        {
            var items = CmbTranslationTarget.Items;
            var models = ReadInstalledOfflineModels();
            var available = models.GroupBy(model => TargetSettingCode(model.Target))
                .ToDictionary(group => group.Key, group => group.First().TargetName);
            RefreshOfflineTranslationSources(models);
            foreach (ComboBoxItem item in items.OfType<ComboBoxItem>().ToArray())
            {
                if (item.Tag is string code && !TranslationTargets.Any(target => target.Code == code)
                    && !available.ContainsKey(code))
                    items.Remove(item);
                else if (item.Tag is string retainedCode)
                    item.Content = TranslationTargetLabel(retainedCode, item.Content?.ToString() ?? retainedCode);
            }
            var codes = items.OfType<ComboBoxItem>().Select(item => item.Tag as string).ToHashSet();
            foreach (var item in available)
            {
                if (!codes.Contains(item.Key))
                    items.Add(new ComboBoxItem { Tag = item.Key, Content = TranslationTargetLabel(item.Key, item.Value) });
            }
            if (!items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, _settingsTranslationTarget)))
                _settingsTranslationTarget = "en";
            _syncedTranslationTarget = null;
        }

        private async Task<OfflineModel[]> RunOfflineAddonAsync(string[] args, CancellationToken cancellationToken)
        {
            string executable = _addonsServerExecutable
                ?? throw new InvalidOperationException("FrontlineServer is unavailable.");
            Directory.CreateDirectory(TranslationModelsPath);
            string workspace = System.IO.Path.Combine(TranslationModelsPath, $".operation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workspace);
            string result = System.IO.Path.Combine(workspace, "result.json");
            try
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--offline-addons");
                foreach (string arg in args)
                    start.ArgumentList.Add(arg);
                start.Environment["FRONTLINE_ADDONS_DIR"] = AddonsDirectory;
                start.Environment["FRONTLINE_ADDON_RESULT_FILE"] = result;
                start.Environment["FRONTLINE_ADDON_STAGE_DIR"] = workspace;
                using var process = Process.Start(start)
                    ?? throw new InvalidOperationException("Unable to start FrontlineServer.");
                ChildProcessJob.Attach(process);
                await WaitForAddonProcessAsync(process, cancellationToken);
                string response = File.Exists(result) ? File.ReadAllText(result) : "";
                if (process.ExitCode != 0)
                    throw new IOException(string.IsNullOrWhiteSpace(response) ? $"FrontlineServer exit code {process.ExitCode}" : response);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<OfflineModel[]>(response, options) ?? Array.Empty<OfflineModel>();
            }
            finally
            {
                try { Directory.Delete(workspace, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CrashReporter.Log(ex, "OfflineTranslationCleanup"); }
            }
        }

        private void LanguagePicker_Opened(object sender, EventArgs e)
        {
            if (sender is not ComboBox picker ||
                picker.Template.FindName("LanguageSearch", picker) is not TextBox search) return;
            search.ToolTip = SettingsText("Search languages", "Pesquisar idiomas", "Buscar idiomas", "Cari bahasa");
            System.Windows.Automation.AutomationProperties.SetName(search, (string)search.ToolTip);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                if (picker.IsDropDownOpen) search.Focus();
            }));
        }

        private void LanguagePicker_Closed(object sender, EventArgs e)
        {
            if (sender is ComboBox picker && picker.Template.FindName("LanguageSearch", picker) is TextBox search)
                search.Clear();
        }

        private static string LanguageLabel(object item) => item switch
        {
            KeyValuePair<string, string> entry => entry.Value,
            ComboBoxItem option => option.Content?.ToString() ?? "",
            _ => item.ToString() ?? ""
        };

        private void LanguagePicker_Filter(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox search || search.TemplatedParent is not ComboBox picker) return;
            foreach (var item in picker.Items)
            {
                if (picker.ItemContainerGenerator.ContainerFromItem(item) is not ComboBoxItem container) continue;
                bool match = CultureInfo.CurrentCulture.CompareInfo.IndexOf(LanguageLabel(item), search.Text.Trim(),
                    CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
                container.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void LanguagePicker_SearchKey(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox search || search.TemplatedParent is not ComboBox picker) return;
            if (e.Key == Key.Escape)
            {
                picker.IsDropDownOpen = false;
                picker.Focus();
                e.Handled = true;
            }
            else if (e.Key is Key.Down or Key.Enter)
            {
                var first = picker.Items.Cast<object>().FirstOrDefault(item =>
                    picker.ItemContainerGenerator.ContainerFromItem(item) is ComboBoxItem option && option.IsVisible);
                if (first != null)
                {
                    if (e.Key == Key.Enter)
                    {
                        picker.SelectedItem = first;
                        picker.IsDropDownOpen = false;
                        picker.Focus();
                    }
                    else
                        (picker.ItemContainerGenerator.ContainerFromItem(first) as ComboBoxItem)?.Focus();
                }
                e.Handled = true;
            }
        }

        private void AddOfflineTranslationCard(Grid page, Button close)
        {
            var card = new StackPanel { Margin = new Thickness(16) };
            var cardBorder = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = card
            };
            Grid.SetColumn(cardBorder, 1);
            page.Children.Add(cardBorder);

            var title = Label(SettingsText("Translation models", "Modelos de tradução", "Modelos de traducción", "Model terjemahan"), 17);
            title.FontWeight = FontWeights.SemiBold;
            card.Children.Add(title);
            var description = Label(SettingsText(
                "Translate complete lines on your device for a faster, more natural reading experience.",
                "Traduza versos completos no seu dispositivo para uma leitura mais rápida e natural.",
                "Traduce versos completos en tu dispositivo para una lectura más rápida y natural.",
                "Terjemahkan baris lengkap di perangkat untuk pengalaman membaca yang lebih cepat dan alami."), 13);
            description.Margin = new Thickness(0, 6, 0, 0);
            card.Children.Add(description);
            var manage = AddonsButton(SettingsText("Manage models", "Gerenciar modelos", "Gestionar modelos", "Kelola model"));
            manage.HorizontalAlignment = HorizontalAlignment.Left;
            card.Children.Add(manage);
            var details = new StackPanel { Visibility = Visibility.Collapsed };
            card.Children.Add(details);
            var status = Label("", 12);
            status.Margin = new Thickness(0, 8, 0, 0);
            details.Children.Add(status);
            var progress = AddonActivityBar();
            details.Children.Add(progress);
            var pairRow = new UniformGrid { Columns = 2 };
            details.Children.Add(pairRow);

            ComboBox Picker(string titleText)
            {
                var label = Label(titleText, 13);
                label.Margin = new Thickness(0, 10, 0, 4);
                var cell = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
                pairRow.Children.Add(cell);
                cell.Children.Add(label);
                var picker = new ComboBox
                {
                    Style = TryFindResource("SearchLanguageCombo") as Style,
                    DisplayMemberPath = "Value",
                    SelectedValuePath = "Key"
                };
                System.Windows.Automation.AutomationProperties.SetName(picker, titleText);
                picker.DropDownOpened += LanguagePicker_Opened;
                picker.DropDownClosed += LanguagePicker_Closed;
                cell.Children.Add(picker);
                return picker;
            }

            var source = Picker(SettingsText("From", "De", "De", "Dari"));
            var target = Picker(SettingsText("To", "Para", "A", "Ke"));
            var detail = Label("", 12);
            detail.Margin = new Thickness(0, 8, 0, 0);
            details.Children.Add(detail);
            var install = AddonsButton(SettingsText("Download models", "Baixar modelos", "Descargar modelos", "Unduh model"));
            install.HorizontalAlignment = HorizontalAlignment.Left;
            var actions = new WrapPanel();
            details.Children.Add(actions);
            actions.Children.Add(install);

            var installedHeader = Label(SettingsText("Installed models", "Modelos instalados", "Modelos instalados", "Model terpasang"), 13);
            installedHeader.Margin = new Thickness(0, 16, 0, 4);
            details.Children.Add(installedHeader);
            var installed = new ComboBox { Style = TryFindResource("DarkComboBox") as Style, DisplayMemberPath = "DisplayName" };
            System.Windows.Automation.AutomationProperties.SetName(installed, installedHeader.Text);
            var management = new DockPanel();
            details.Children.Add(management);
            var remove = AddonsButton(SettingsText("Remove", "Remover", "Quitar", "Hapus"));
            remove.HorizontalAlignment = HorizontalAlignment.Left;
            remove.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(remove, Dock.Right);
            management.Children.Add(remove);
            management.Children.Add(installed);
            var refresh = AddonsButton(SettingsText("Refresh catalog", "Atualizar catálogo", "Actualizar catálogo", "Perbarui katalog"));
            refresh.HorizontalAlignment = HorizontalAlignment.Left;
            refresh.Margin = new Thickness(8, 14, 0, 0);
            actions.Children.Add(refresh);
            var cancel = AddonsButton(SettingsText("Cancel download", "Cancelar download", "Cancelar descarga", "Batalkan unduhan"));
            cancel.Visibility = Visibility.Collapsed;
            actions.Children.Add(cancel);
            CancellationTokenSource? download = null;
            bool catalogLoaded = false;
            var models = Array.Empty<OfflineModel>();

            OfflineModel[] Route(string from, string to)
            {
                if (from == to) return Array.Empty<OfflineModel>();
                var direct = models.FirstOrDefault(model => model.Source == from && model.Target == to);
                if (direct != null) return new[] { direct };
                var first = models.FirstOrDefault(model => model.Source == from && model.Target == "en");
                var second = models.FirstOrDefault(model => model.Source == "en" && model.Target == to);
                return first != null && second != null ? new[] { first, second } : Array.Empty<OfflineModel>();
            }

            void UpdateButtons()
            {
                var steps = Route(source.SelectedValue as string ?? "", target.SelectedValue as string ?? "");
                int missing = steps.Count(step => !step.Installed);
                detail.Text = steps.Length == 0 ? "" : string.Join(" · ", steps.Select(step => step.DisplayName)) + " · " +
                    (missing == 0 ? SettingsText("Ready offline", "Disponível offline", "Disponible sin conexión", "Tersedia offline")
                        : $"{missing} " + SettingsText("to download", "para baixar", "para descargar", "untuk diunduh"));
                install.IsEnabled = !_addonsBusy && missing > 0;
                remove.IsEnabled = !_addonsBusy && installed.SelectedItem is OfflineModel;
                refresh.IsEnabled = !_addonsBusy;
                manage.IsEnabled = !_addonsBusy;
                install.Visibility = refresh.Visibility = download == null ? Visibility.Visible : Visibility.Collapsed;
                cancel.Visibility = download != null ? Visibility.Visible : Visibility.Collapsed;
                cancel.IsEnabled = download?.IsCancellationRequested == false;
            }

            void UpdateTargets()
            {
                string previous = target.SelectedValue as string ?? "";
                string from = source.SelectedValue as string ?? "";
                target.ItemsSource = models
                    .GroupBy(model => model.Target)
                    .Where(group => Route(from, group.Key).Length > 0)
                    .Select(group => KeyValuePair.Create(group.Key, group.First().TargetName))
                    .OrderBy(item => item.Value).ToArray();
                target.SelectedValue = previous;
                if (target.SelectedIndex < 0) target.SelectedIndex = 0;
                UpdateButtons();
            }

            async Task LoadAsync(params string[] args)
            {
                if (_addonsBusy) return;
                _addonsBusy = true;
                if (args[0] == "install")
                    download = new CancellationTokenSource();
                close.IsEnabled = false;
                SetAddonActivity(progress, true);
                status.Text = args[0] == "remove"
                    ? SettingsText("Removing model…", "Removendo modelo…", "Quitando modelo…", "Menghapus model…")
                    : args[0] == "install"
                    ? SettingsText("Downloading models…", "Baixando modelos…", "Descargando modelos…", "Mengunduh model…")
                    : SettingsText("Updating…", "Atualizando…", "Actualizando…", "Memperbarui…");
                UpdateButtons();
                try
                {
                    string previous = source.SelectedValue as string ?? "en";
                    models = (await RunOfflineAddonAsync(args, download?.Token ?? CancellationToken.None)).Select(model => model with
                    {
                        SourceName = OfflineLanguageName(model.Source, model.SourceName),
                        TargetName = OfflineLanguageName(model.Target, model.TargetName)
                    }).ToArray();
                    catalogLoaded = true;
                    source.ItemsSource = models.GroupBy(model => model.Source)
                        .Select(group => KeyValuePair.Create(group.Key, group.First().SourceName))
                        .OrderBy(item => item.Value).ToArray();
                    source.SelectedValue = previous;
                    if (source.SelectedIndex < 0) source.SelectedIndex = 0;
                    installed.ItemsSource = models.Where(model => model.Installed).OrderBy(model => model.DisplayName).ToArray();
                    installed.SelectedIndex = 0;
                    UpdateTargets();
                    status.Text = "";
                    RefreshOfflineTranslationTargets();
                    if (args[0] != "list" && currentAppStatus == "SYNCED" &&
                        _settingsPreferredTranslation is not ("original" or "romanized"))
                        RequestSettingsTranslation(LblArtistName.Text, LblSongTitle.Text);
                }
                catch (OperationCanceledException)
                {
                    status.Text = SettingsText("Download canceled.", "Download cancelado.", "Descarga cancelada.", "Unduhan dibatalkan.");
                    var completed = ReadInstalledOfflineModels().Select(model => (model.Source, model.Target)).ToHashSet();
                    models = models.Select(model => model with { Installed = completed.Contains((model.Source, model.Target)) }).ToArray();
                    installed.ItemsSource = models.Where(model => model.Installed).OrderBy(model => model.DisplayName).ToArray();
                    installed.SelectedIndex = 0;
                    RefreshOfflineTranslationTargets();
                }
                catch (Exception ex)
                {
                    CrashReporter.Log(ex, "OfflineTranslationAddons");
                    status.Text = SettingsText("Could not finish. Try again.", "Não foi possível concluir. Tente novamente.",
                        "No se pudo completar. Inténtalo de nuevo.", "Tidak dapat menyelesaikan. Coba lagi.") + "\n" + ex.Message;
                }
                finally
                {
                    download?.Dispose();
                    download = null;
                    _addonsBusy = false;
                    SetAddonActivity(progress, false);
                    close.IsEnabled = true;
                    UpdateButtons();
                }
            }

            source.SelectionChanged += (_, _) => UpdateTargets();
            target.SelectionChanged += (_, _) => UpdateButtons();
            installed.SelectionChanged += (_, _) => UpdateButtons();
            install.Click += async (_, _) => await LoadAsync("install", (string)source.SelectedValue, (string)target.SelectedValue);
            remove.Click += async (_, _) =>
            {
                if (installed.SelectedItem is OfflineModel model)
                    await LoadAsync("remove", model.Source, model.Target);
            };
            refresh.Click += async (_, _) => await LoadAsync("list");
            cancel.Click += (_, _) =>
            {
                download?.Cancel();
                UpdateButtons();
            };
            manage.Click += async (_, _) =>
            {
                if (_addonsBusy) return;
                bool opening = details.Visibility != Visibility.Visible;
                details.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
                page.ColumnDefinitions[1].Width = new GridLength(opening ? 2 : 1, GridUnitType.Star);
                manage.Content = opening
                    ? SettingsText("Hide models", "Ocultar modelos", "Ocultar modelos", "Sembunyikan model")
                    : SettingsText("Manage models", "Gerenciar modelos", "Gestionar modelos", "Kelola model");
                if (opening && !catalogLoaded)
                    await LoadAsync("list");
            };
            UpdateButtons();
        }
    }
}
