using Frontline.Themes.Seasonal;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace FrontLineOverlay
{
    public partial class MainWindow
    {
        private const string ThemeAuto = "auto";

        private string _themeId = ThemeAuto;
        private string? _userThemeLoadedId;
        private UserThemeDef? _appliedUserTheme;

        private UserThemeDef? _appliedAdornTheme;
        private string? _adornLoadedId;
        private const string SantaThemeId = "santa";

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

        private UserThemeDef? AdornThemeFor(SeasonalTheme seasonal, UserThemeDef? user) =>
            seasonal == SeasonalTheme.Christmas ? UserThemes.FindSeasonal(SantaThemeId)
            : seasonal != SeasonalTheme.None ? null
            : user;

        private void EnsureUserThemeContent(UserThemeDef? user, UserThemeDef? adorn)
        {
            string? wantUser = user?.Id;
            if (_userThemeLoadedId != wantUser)
            {
                try { if (UserThemeGround != null) UserThemeGround.Content = user?.CreateGround?.Invoke(); }
                catch (Exception ex) { CrashReporter.Log(ex, "UserThemeGround"); }

                try { if (UserThemeLayer != null) UserThemeLayer.Content = user?.CreateLayer?.Invoke(); }
                catch (Exception ex) { CrashReporter.Log(ex, "UserThemeLayer"); }

                _userThemeLoadedId = wantUser;
            }

            string? wantAdorn = adorn?.Id;
            if (_adornLoadedId != wantAdorn)
            {
                try { if (UserThemeLogoAdorn != null) UserThemeLogoAdorn.Content = adorn?.CreateLogoAdorn?.Invoke(); }
                catch (Exception ex) { CrashReporter.Log(ex, "UserThemeLogoAdorn"); }

                try { if (UserThemeDockAdorn != null) UserThemeDockAdorn.Content = adorn?.CreateDockAdorn?.Invoke(); }
                catch (Exception ex) { CrashReporter.Log(ex, "UserThemeDockAdorn"); }

                try { ApplyUserThemeDock(adorn); }
                catch (Exception ex) { CrashReporter.Log(ex, "ApplyUserThemeDock"); }

                _adornLoadedId = wantAdorn;
            }
        }

        private void ApplyUserThemeDock(UserThemeDef? user)
        {
            if (HomeDock != null)
            {
                HomeDock.Background = user?.DockBackground ?? UserThemes.DefaultDockBackground;
                HomeDock.BorderBrush = user?.DockBorder ?? UserThemes.DefaultDockBorder;
                HomeDock.CornerRadius = user?.DockCornerRadius ?? UserThemes.DefaultDockCornerRadius;
            }
            foreach (FrameworkElement? host in new FrameworkElement?[] { HomeControls, PlayingControls })
            {
                if (host == null) continue;
                host.Resources.MergedDictionaries.Clear();
                if (user?.DockResources != null)
                    host.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = user.DockResources });
            }
        }

        private const double LogoAdornWidth = 1000;
        private bool _logoShown;

        private void UpdateLogoAdornSpace(int pixelWidth, int pixelHeight)
        {
            _logoShown = true;
            if (LogoAdornSpace != null && pixelWidth > 0)
            {
                LogoAdornSpace.Width = LogoAdornWidth;
                LogoAdornSpace.Height = LogoAdornWidth * pixelHeight / pixelWidth;
            }
            RefreshUserThemeSlots();
        }

        private void SetLogoAdornShown(bool shown)
        {
            _logoShown = shown;
            RefreshUserThemeSlots();
        }

        private void RefreshUserThemeSlots()
        {
            var user = _appliedAdornTheme;
            bool on = user != null && !(isGhostMode || isResizing);
            if (LogoAdornHost != null)
                LogoAdornHost.Visibility = on && _logoShown && user!.CreateLogoAdorn != null && UserThemeLogoAdorn?.Content != null ? Visibility.Visible : Visibility.Collapsed;
            if (UserThemeDockAdorn != null)
                UserThemeDockAdorn.Visibility = on && user!.CreateDockAdorn != null ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}