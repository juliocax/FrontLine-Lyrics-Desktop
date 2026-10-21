using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace FrontLineOverlay
{
    public partial class MainWindow
    {
        private const string ThemeAuto = "auto";

        private string _themeId = ThemeAuto;
        private string? _userThemeLoadedId;
        private UserThemeDef? _appliedUserTheme;

        private static readonly Dictionary<string, Dictionary<string, string>> UserThemeStrings = new()
        {
            ["en"] = new() { ["Title"] = "THEME", ["Auto"] = "Automatic (seasonal)", ["Default"] = "Default" },
            ["pt"] = new() { ["Title"] = "TEMA", ["Auto"] = "Automático (sazonal)", ["Default"] = "Padrão" },
            ["es"] = new() { ["Title"] = "TEMA", ["Auto"] = "Automático (temporada)", ["Default"] = "Predeterminado" },
            ["id"] = new() { ["Title"] = "TEMA", ["Auto"] = "Otomatis (musiman)", ["Default"] = "Bawaan" },
        };

        private string UserThemeText(string key) =>
            UserThemeStrings.TryGetValue(currentAppLanguage, out var d) && d.TryGetValue(key, out var v)
                ? v
                : UserThemeStrings["en"][key];

        private static bool IsValidThemeId(string id) =>
            id == ThemeAuto || id == UserThemes.DefaultId || UserThemes.Find(id) != null;

        private void InitUserTheme()
        {
            bool legacySeasonal = AppSettings.GetBool("SeasonalEnabled", true);
            _themeId = AppSettings.GetString("AppTheme", legacySeasonal ? ThemeAuto : UserThemes.DefaultId);
            if (!IsValidThemeId(_themeId)) _themeId = ThemeAuto;
            BuildUserThemeComboBox();
        }

        private void PersistUserTheme() => AppSettings.SetString("AppTheme", _themeId);

        private void ApplyUserThemeLanguage()
        {
            if (LblUserThemeTitle != null) LblUserThemeTitle.Text = UserThemeText("Title");
            BuildUserThemeComboBox();
        }

        private void BuildUserThemeComboBox()
        {
            if (CmbUserTheme == null) return;
            CmbUserTheme.Items.Clear();
            CmbUserTheme.Items.Add(new ComboBoxItem { Content = UserThemeText("Auto"), Tag = ThemeAuto });
            CmbUserTheme.Items.Add(new ComboBoxItem { Content = UserThemeText("Default"), Tag = UserThemes.DefaultId });
            foreach (var theme in UserThemes.All)
                CmbUserTheme.Items.Add(new ComboBoxItem { Content = theme.DisplayName(currentAppLanguage), Tag = theme.Id });

            SelectCombo(CmbUserTheme, _themeId);
        }

        private void CmbUserTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbUserTheme?.SelectedItem is ComboBoxItem item && item.Tag is string id && id != _themeId)
            {
                _themeId = id;
                ApplySeasonalTheme();   
                ScheduleSave();
            }
        }

        private SeasonalTheme ResolveSeasonalTheme() =>
            _themeId == ThemeAuto ? SeasonalThemeProvider.GetCurrent() : SeasonalTheme.None;

        private UserThemeDef? ActiveUserTheme(SeasonalTheme seasonal) =>
            seasonal != SeasonalTheme.None ? null : UserThemes.Find(_themeId);

        private void EnsureUserThemeContent(UserThemeDef? user)
        {
            string? want = user?.Id;
            if (_userThemeLoadedId == want) return;
            _userThemeLoadedId = want;
            try
            {
                if (UserThemeGround != null) UserThemeGround.Content = user?.CreateGround?.Invoke();
                if (UserThemeLayer != null) UserThemeLayer.Content = user?.CreateLayer?.Invoke();
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "EnsureUserThemeContent");
                if (UserThemeGround != null) UserThemeGround.Content = null;
                if (UserThemeLayer != null) UserThemeLayer.Content = null;
            }
        }
    }
}