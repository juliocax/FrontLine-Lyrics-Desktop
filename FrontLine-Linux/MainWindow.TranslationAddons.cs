using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FrontLine_Linux;

public partial class MainWindow
{
    private string _baseLyricsMode = "original";
    private string _translationTarget = "en";
    private string? _translationSource;
    private bool _sourcePreferenceLoaded;
    private bool _translateEnabled;
    private string _romanizedCurrent = "";
    private JsonElement _translationSourceNotice;
    private bool _japanesePackMissing;
    private TranslationAddonsDialog.Model[] _installedTranslationModels = Array.Empty<TranslationAddonsDialog.Model>();
    private int _addonsRefreshVersion;

    private string EffectiveLyricsMode => _translateEnabled ? _translationTarget : _baseLyricsMode;

    private string TranslationTargetName(string code, string? fallback = null)
    {
        if (code == "romanized") return DialogUi.L(_appLanguage, "Romanization", "Romanização", "Romanización", "Romanisasi");
        var catalogCode = code switch { "pt" => "pb", "zh-cn" => "zh", "zh-tw" => "zt", _ => code };
        return TranslationAddonsDialog.LanguageLabel(_appLanguage, catalogCode, fallback ?? code);
    }

    private void ApplyTranslationOptionsLanguage()
    {
        if (TranslationSourcePicker is null || TranslationTargetPicker is null) return;
        string L(string en, string pt, string es, string id) => DialogUi.L(_appLanguage, en, pt, es, id);
        OriginalModeLabel.Text = L("Original", "Original", "Original", "Asli");
        RomanizedModeLabel.Text = TranslationTargetName("romanized");
        TranslateEnabledLabel.Text = L("Translate", "Traduzir", "Traducir", "Terjemahkan");
        UpdateTranslationSwitches();
        TranslationFromLabel.Text = L("Translate from", "Traduzir de", "Traducir de", "Terjemahkan dari");
        TranslationToLabel.Text = L("To", "Para", "A", "Ke");
        ManageTranslationAddonsButton.Content = L("Manage add-ons", "Gerenciar complementos", "Gestionar complementos", "Kelola add-on");
        ManageTranslationAddonsButton.IsEnabled = _connected;
        RefreshTranslationPickers();
    }

    private static string SettingLanguageCode(string code) =>
        code switch { "pb" => "pt", "pt" => "pt-pt", "zh" => "zh-cn", "zt" => "zh-tw", _ => code };

    private void RefreshTranslationPickers()
    {
        var names = TranslationAddonsDialog.LanguageCodes
            .Select(SettingLanguageCode).Distinct()
            .ToDictionary(code => code, code => TranslationTargetName(code));
        foreach (var model in _installedTranslationModels)
        {
            var from = SettingLanguageCode(model.Source);
            var to = SettingLanguageCode(model.Target);
            names.TryAdd(from, TranslationTargetName(from, model.SourceName));
            names.TryAdd(to, TranslationTargetName(to, model.TargetName));
        }
        names.TryAdd(_translationTarget, TranslationTargetName(_translationTarget));
        if (_translationSource is { } currentSource)
            names.TryAdd(currentSource, TranslationTargetName(currentSource));
        var choices = names.Select(item => new LanguageOption(item.Key, item.Value)).OrderBy(item => item.Label).ToArray();
        var automatic = new LanguageOption(null,
            DialogUi.L(_appLanguage, "Automatic detection", "Detecção automática", "Detección automática", "Deteksi otomatis"));
        LanguagePicker.Bind(_appLanguage, TranslationSourcePicker, new[] { automatic }.Concat(choices).ToArray(), _translationSource, code =>
        {
            if (_translationSource == code) return;
            _translationSource = code;
            _sourcePreferenceLoaded = true;
            ApplyLyricsMode(refresh: _translateEnabled);
        });
        LanguagePicker.Bind(_appLanguage, TranslationTargetPicker, choices, _translationTarget, code =>
        {
            if (code is null || _translationTarget == code) return;
            _translationTarget = code;
            ApplyLyricsMode();
        });
    }

    private async Task RefreshTranslationAddonsAsync()
    {
        int version = ++_addonsRefreshVersion;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await TranslationAddonsDialog.RequestAsync("status", timeout.Token);
            if (version != _addonsRefreshVersion) return;
            _installedTranslationModels = result.Models;
            if (!_sourcePreferenceLoaded && result.Source is { } previous)
            {
                _translationSource = SettingLanguageCode(previous);
                _sourcePreferenceLoaded = true;
                ApplyLyricsMode(refresh: _translateEnabled);
            }
            RefreshTranslationPickers();
        }
        catch (Exception error) { Debug.WriteLine(error.Message); }
    }

    private async void ManageTranslationAddons_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new TranslationAddonsDialog(_appLanguage);
        await dialog.ShowDialog(this);
        await RefreshTranslationAddonsAsync();
        if (dialog.Changed) ApplyLyricsMode(refresh: true);
    }

    private void BaseLyricsMode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode } || _baseLyricsMode == mode) return;
        _baseLyricsMode = mode;
        ApplyLyricsMode();
    }

    private void TranslateEnabled_Click(object? sender, RoutedEventArgs e)
    {
        _translateEnabled = !_translateEnabled;
        ApplyLyricsMode();
    }

    private void UpdateTranslationSwitches()
    {
        OriginalModeIndicator.IsChecked = _baseLyricsMode == "original";
        RomanizedModeIndicator.IsChecked = _baseLyricsMode == "romanized";
        TranslateEnabledIndicator.IsChecked = _translateEnabled;
        OriginalModeButton.Classes.Set("translation-active", _baseLyricsMode == "original");
        RomanizedModeButton.Classes.Set("translation-active", _baseLyricsMode == "romanized");
        TranslateEnabledButton.Classes.Set("translation-active", _translateEnabled);
    }

    private void ApplyLyricsMode(bool refresh = false)
    {
        UpdateTranslationSwitches();
        var requested = EffectiveLyricsMode;
        bool request = _preferredTranslation != requested || refresh;
        _preferredTranslation = requested;
        SavePreferences();
        RefreshSegmentSelections();
        RefreshLyricView();
        if (request)
        {
            _translationUiError = null;
            _translationStarted = false;
            _pendingTranslation = null;
            _autoTranslationKey = "";
            if (_connected)
            {
                _autoTranslationKey = $"{_lastArtistName.Trim()}|{_lastSongTitle.Trim()}|{requested}";
                if (_status == "SYNCED" && requested != "original")
                {
                    _pendingTranslation = requested;
                    _translationRequestedUtc = DateTime.UtcNow;
                }
                _ = SendAsync(new { action = "TRANSLATE", lang = requested, source = _translationSource });
            }
        }
        UpdateTranslationFeedback();
    }

    private void UpdateAddonNotices()
    {
        if (JapaneseDictionaryNotice is null || TranslationSourceNotice is null) return;
        JapaneseDictionaryNotice.IsVisible = _baseLyricsMode == "romanized" && _japanesePackMissing;
        JapaneseDictionaryNotice.Text = DialogUi.L(_appLanguage,
            "Basic Japanese romanization is active. UniDic Lite is available in Add-ons.",
            "A romanização básica do japonês está ativa. O UniDic Lite está disponível em Complementos.",
            "La romanización básica del japonés está activa. UniDic Lite está disponible en Complementos.",
            "Romanisasi dasar bahasa Jepang aktif. UniDic Lite tersedia di Add-on.");

        string message = "";
        if (_translateEnabled && _translationSourceNotice.ValueKind == JsonValueKind.Object)
        {
            bool Flag(string key) => _translationSourceNotice.TryGetProperty(key, out var value)
                                     && value.ValueKind == JsonValueKind.True;
            if (Flag("mismatch"))
                message = DialogUi.L(_appLanguage, "The selected source differs from the detected writing system.",
                    "O idioma de origem escolhido difere da escrita detectada.",
                    "El idioma de origen elegido difiere del sistema de escritura detectado.",
                    "Bahasa sumber yang dipilih berbeda dengan sistem tulisan yang terdeteksi.");
            else if (Flag("uncertain"))
                message = DialogUi.L(_appLanguage, "Some lines may stay in the original language.",
                    "Alguns versos podem ficar no idioma original.",
                    "Algunos versos pueden conservar su idioma original.",
                    "Beberapa baris mungkin tetap dalam bahasa aslinya.");
        }
        TranslationSourceNotice.Text = message;
        TranslationSourceNotice.IsVisible = message.Length > 0;
    }
}
