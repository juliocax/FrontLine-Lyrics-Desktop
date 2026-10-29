using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FrontLineOverlay
{
    public partial class MainWindow
    {
        private string _lyricColorId = LyricColorPresets.DefaultId;
        private string _lyricFontId = LyricFonts.DefaultId;
        private string _lyricWeightId = LyricWeights.DefaultId;
        private string _lyricStyleId = LyricStyles.DefaultId;
        private readonly Dictionary<string, Border> _lyricSwatches = new();
        private SeasonalTheme? _appliedTheme;

        private static readonly Dictionary<string, Dictionary<string, string>> AppearanceStrings = new()
        {
            ["en"] = new() { ["LyricColor"] = "LYRIC COLOR", ["LyricFont"] = "LYRIC FONT", ["LyricWeight"] = "LYRIC WEIGHT", ["LyricStyle"] = "LYRIC STYLE", ["Seasonal"] = "Seasonal themes", ["Solid"] = "Solid", ["Gradient"] = "Gradient", ["Metallic"] = "Metallic", ["Light"] = "Light", ["Regular"] = "Regular", ["SemiBold"] = "SemiBold", ["Bold"] = "Bold", ["ExtraBold"] = "ExtraBold", ["Upright"] = "Upright", ["Italic"] = "Italic", ["FontGroupSans"] = "Sans-serif (free)", ["FontGroupSerif"] = "Serif (free)", ["FontGroupDisplay"] = "Display (free)", ["FontGroupHand"] = "Handwriting (free)", ["FontGroupWindows"] = "Windows fonts" },
            ["pt"] = new() { ["LyricColor"] = "COR DA LETRA", ["LyricFont"] = "FONTE DA LETRA", ["LyricWeight"] = "PESO DA LETRA", ["LyricStyle"] = "ESTILO DA LETRA", ["Seasonal"] = "Temas sazonais", ["Solid"] = "Sólidas", ["Gradient"] = "Degradê", ["Metallic"] = "Metálicas", ["Light"] = "Leve", ["Regular"] = "Regular", ["SemiBold"] = "Seminegrito", ["Bold"] = "Negrito", ["ExtraBold"] = "Extra negrito", ["Upright"] = "Normal", ["Italic"] = "Itálico", ["FontGroupSans"] = "Sem serifa (livre)", ["FontGroupSerif"] = "Com serifa (livre)", ["FontGroupDisplay"] = "Destaque (livre)", ["FontGroupHand"] = "Manuscrita (livre)", ["FontGroupWindows"] = "Fontes do Windows" },
            ["es"] = new() { ["LyricColor"] = "COLOR DE LA LETRA", ["LyricFont"] = "FUENTE DE LA LETRA", ["LyricWeight"] = "GROSOR DE LA LETRA", ["LyricStyle"] = "ESTILO DE LA LETRA", ["Seasonal"] = "Temas de temporada", ["Solid"] = "Sólidos", ["Gradient"] = "Degradado", ["Metallic"] = "Metálicos", ["Light"] = "Ligera", ["Regular"] = "Regular", ["SemiBold"] = "Seminegrita", ["Bold"] = "Negrita", ["ExtraBold"] = "Extra negrita", ["Upright"] = "Normal", ["Italic"] = "Cursiva", ["FontGroupSans"] = "Sin serifa (libre)", ["FontGroupSerif"] = "Con serifa (libre)", ["FontGroupDisplay"] = "Display (libre)", ["FontGroupHand"] = "Manuscrita (libre)", ["FontGroupWindows"] = "Fuentes de Windows" },
            ["id"] = new() { ["LyricColor"] = "WARNA LIRIK", ["LyricFont"] = "FON LIRIK", ["LyricWeight"] = "TEBAL LIRIK", ["LyricStyle"] = "GAYA LIRIK", ["Seasonal"] = "Tema musiman", ["Solid"] = "Warna solid", ["Gradient"] = "Gradasi", ["Metallic"] = "Metalik", ["Light"] = "Tipis", ["Regular"] = "Regular", ["SemiBold"] = "Semi tebal", ["Bold"] = "Tebal", ["ExtraBold"] = "Sangat tebal", ["Upright"] = "Tegak", ["Italic"] = "Miring", ["FontGroupSans"] = "Sans-serif (gratis)", ["FontGroupSerif"] = "Serif (gratis)", ["FontGroupDisplay"] = "Display (gratis)", ["FontGroupHand"] = "Tulisan tangan (gratis)", ["FontGroupWindows"] = "Font Windows" },
        };

        private string AppearanceText(string key) =>
            AppearanceStrings.TryGetValue(currentAppLanguage, out var d) && d.TryGetValue(key, out var v)
                ? v
                : AppearanceStrings["en"][key];

        private void InitAppearance()
        {
            try
            {
                InitUserTheme();

                _lyricColorId = AppSettings.GetString("LyricColor", LyricColorPresets.DefaultId);
                _lyricFontId = AppSettings.GetString("LyricFont", LyricFonts.DefaultId);
                _lyricWeightId = AppSettings.GetString("LyricWeight", LyricWeights.DefaultId);
                _lyricStyleId = AppSettings.GetString("LyricStyle", LyricStyles.DefaultId);
                BuildLyricColorComboBox();
                BuildLyricFontComboBox();
                BuildLyricWeightComboBox();
                BuildLyricStyleComboBox();
                ApplyLyricColor(_lyricColorId);
                ApplyLyricType();

                ApplySeasonalTheme();
                ClearCoverFrame();
                Loaded += (_, _) =>
                {
                    ClearCoverFrame();
                    ApplyLyricType();
                    RefreshSeasonalChrome();
                };
                Activated += (_, _) => ApplySeasonalTheme();
            }
            catch (Exception ex) { CrashReporter.Log(ex, "InitAppearance"); }
        }

        private void ApplyAppearanceLanguage()
        {
            if (LblLyricColorTitle != null) LblLyricColorTitle.Text = AppearanceText("LyricColor");
            if (LblLyricFontTitle != null) LblLyricFontTitle.Text = AppearanceText("LyricFont");
            if (LblLyricWeightTitle != null) LblLyricWeightTitle.Text = AppearanceText("LyricWeight");
            if (LblLyricStyleTitle != null) LblLyricStyleTitle.Text = AppearanceText("LyricStyle");
            ApplyUserThemeLanguage();
            if (CmbLyricColor != null)
            {
                BuildLyricColorComboBox();
                ApplyLyricColor(_lyricColorId);
            }
            if (CmbLyricFont != null)
            {
                BuildLyricFontComboBox();
                ApplyLyricType();
            }
            if (CmbLyricWeight != null)
            {
                BuildLyricWeightComboBox();
                ApplyLyricType();
            }
            if (CmbLyricStyle != null)
            {
                BuildLyricStyleComboBox();
                ApplyLyricType();
            }
        }

        private void PersistAppearance()
        {
            AppSettings.SetString("LyricColor", _lyricColorId);
            AppSettings.SetString("LyricFont", _lyricFontId);
            AppSettings.SetString("LyricWeight", _lyricWeightId);
            AppSettings.SetString("LyricStyle", _lyricStyleId);
            PersistUserTheme();
        }

        private void BuildLyricColorComboBox()
        {
            if (CmbLyricColor == null) return;
            CmbLyricColor.Items.Clear();

            foreach (var group in LyricColorPresets.All.GroupBy(p => p.Group))
            {
                CmbLyricColor.Items.Add(new ComboBoxItem
                {
                    Content = new TextBlock { Text = AppearanceText(group.Key.ToString()), FontWeight = FontWeights.Bold, Opacity = 0.5, FontSize = 10 },
                    IsEnabled = false
                });

                foreach (var preset in group)
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal };
                    sp.Children.Add(new Border
                    {
                        Width = 14,
                        Height = 14,
                        CornerRadius = new CornerRadius(7),
                        Background = preset.CreateBrush(),
                        Margin = new Thickness(10, 0, 8, 0),
                        BorderBrush = Brushes.White,
                        BorderThickness = new Thickness(1)
                    });
                    sp.Children.Add(new TextBlock { Text = preset.Name, VerticalAlignment = VerticalAlignment.Center });

                    CmbLyricColor.Items.Add(new ComboBoxItem { Content = sp, Tag = preset.Id });
                }
            }
        }

        private void ApplyLyricColor(string? id)
        {
            var preset = LyricColorPresets.Find(id);
            _lyricColorId = preset.Id;

            var brush = preset.CreateBrush();
            if (LblCurrent != null) LblCurrent.Foreground = brush;
            if (LblPreviewCurrent != null) LblPreviewCurrent.Foreground = brush;

            if (CmbLyricColor != null)
            {
                foreach (ComboBoxItem item in CmbLyricColor.Items)
                {
                    if (item.Tag as string == preset.Id)
                    {
                        CmbLyricColor.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        private void CmbLyricColor_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbLyricColor?.SelectedItem is ComboBoxItem item && item.Tag is string id)
            {
                if (_lyricColorId != id)
                {
                    ApplyLyricColor(id);
                    ScheduleSave();
                }
            }
        }

        private void BtnResetLyricColor_Click(object sender, RoutedEventArgs e)
        {
            ApplyLyricColor(LyricColorPresets.DefaultId);
            ScheduleSave();
        }

        private void BuildLyricFontComboBox()
        {
            if (CmbLyricFont == null) return;
            CmbLyricFont.Items.Clear();
            foreach (var group in LyricFonts.Installed().GroupBy(f => f.Category))
            {
                CmbLyricFont.Items.Add(new ComboBoxItem
                {
                    Content = new TextBlock { Text = AppearanceText(group.Key), FontWeight = FontWeights.Bold, Opacity = 0.5, FontSize = 10 },
                    IsEnabled = false
                });
                foreach (var font in group)
                {
                    CmbLyricFont.Items.Add(new ComboBoxItem
                    {
                        Content = new TextBlock { Text = font.Name, FontFamily = font.Family, FontSize = 14 },
                        Tag = font.Id
                    });
                }
            }
            SelectCombo(CmbLyricFont, _lyricFontId);
        }

        private void BuildLyricWeightComboBox()
        {
            if (CmbLyricWeight == null) return;
            CmbLyricWeight.Items.Clear();
            foreach (var w in LyricWeights.All)
            {
                CmbLyricWeight.Items.Add(new ComboBoxItem
                {
                    Content = new TextBlock { Text = AppearanceText(w.Key), FontWeight = w.Weight },
                    Tag = w.Id
                });
            }
            SelectCombo(CmbLyricWeight, _lyricWeightId);
        }

        private void BuildLyricStyleComboBox()
        {
            if (CmbLyricStyle == null) return;
            CmbLyricStyle.Items.Clear();
            foreach (var style in LyricStyles.All)
            {
                CmbLyricStyle.Items.Add(new ComboBoxItem
                {
                    Content = new TextBlock { Text = AppearanceText(style.Key), FontStyle = style.Style },
                    Tag = style.Id
                });
            }
            SelectCombo(CmbLyricStyle, _lyricStyleId);
        }

        private static void SelectCombo(ComboBox box, string id)
        {
            foreach (ComboBoxItem item in box.Items)
            {
                if (item.Tag as string == id)
                {
                    box.SelectedItem = item;
                    return;
                }
            }
            foreach (ComboBoxItem item in box.Items)
            {
                if (item.Tag is string)
                {
                    box.SelectedItem = item;
                    return;
                }
            }
        }

        private void ApplyLyricType()
        {
            var family = LyricFonts.Resolve(_lyricFontId);
            var weight = LyricWeights.Resolve(_lyricWeightId);
            var style = LyricStyles.Resolve(_lyricStyleId);
            foreach (var block in new[]
            {
                LblCurrent, LblPrevious, LblNext, LblCurrentOriginal,
                LblPreviewCurrent, LblPreviewPrevious, LblPreviewNext, LblPreviewCurrentOriginal
            })
            {
                if (block == null) continue;
                block.FontFamily = family;
                block.FontWeight = weight;
                block.FontStyle = style;
            }
            if (FullLyricsList != null)
            {
                FullLyricsList.FontFamily = family;
                FullLyricsList.FontWeight = weight;
                FullLyricsList.FontStyle = style;
            }
            if (CmbLyricFont != null) SelectCombo(CmbLyricFont, _lyricFontId);
            if (CmbLyricWeight != null) SelectCombo(CmbLyricWeight, _lyricWeightId);
            if (CmbLyricStyle != null) SelectCombo(CmbLyricStyle, _lyricStyleId);
        }

        private void LyricCombo_DropDownOpened(object sender, EventArgs e)
        {
            if (SettingsPopup != null) SettingsPopup.StaysOpen = true;
        }

        private void LyricCombo_DropDownClosed(object sender, EventArgs e)
        {
            if (SettingsPopup != null) SettingsPopup.StaysOpen = false;
        }

        private void CmbLyricFont_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbLyricFont?.SelectedItem is ComboBoxItem item && item.Tag is string id && id != _lyricFontId)
            {
                _lyricFontId = id;
                ApplyLyricType();
                ScheduleSave();
            }
        }

        private void CmbLyricWeight_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbLyricWeight?.SelectedItem is ComboBoxItem item && item.Tag is string id && id != _lyricWeightId)
            {
                _lyricWeightId = id;
                ApplyLyricType();
                ScheduleSave();
            }
        }

        private void BtnResetLyricFont_Click(object sender, RoutedEventArgs e)
        {
            _lyricFontId = LyricFonts.DefaultId;
            ApplyLyricType();
            ScheduleSave();
        }

        private void BtnResetLyricWeight_Click(object sender, RoutedEventArgs e)
        {
            _lyricWeightId = LyricWeights.DefaultId;
            ApplyLyricType();
            ScheduleSave();
        }

        private void CmbLyricStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbLyricStyle?.SelectedItem is ComboBoxItem item && item.Tag is string id && id != _lyricStyleId)
            {
                _lyricStyleId = id;
                ApplyLyricType();
                ScheduleSave();
            }
        }

        private void BtnResetLyricStyle_Click(object sender, RoutedEventArgs e)
        {
            _lyricStyleId = LyricStyles.DefaultId;
            ApplyLyricType();
            ScheduleSave();
        }

        private bool _seasonHooks;

        private void ApplySeasonalTheme()
        {
            var theme = ResolveSeasonalTheme();
            var user = ActiveUserTheme(theme);
            if (_appliedTheme != theme || _appliedUserTheme?.Id != user?.Id)
            {
                _appliedTheme = theme;
                _appliedUserTheme = user;

                if (HalloweenLayer != null)
                    HalloweenLayer.Visibility = theme == SeasonalTheme.Halloween ? Visibility.Visible : Visibility.Collapsed;
                if (ChristmasLayer != null)
                    ChristmasLayer.Visibility = theme == SeasonalTheme.Christmas ? Visibility.Visible : Visibility.Collapsed;
                if (NewYearLayer != null)
                    NewYearLayer.Visibility = theme == SeasonalTheme.NewYear ? Visibility.Visible : Visibility.Collapsed;

                if (IsLoaded) LoadAppIcon();
                ApplyMenuLogo();
            }

            EnsureSeasonHooks();
            RefreshSeasonalChrome();
        }

        private void EnsureSeasonHooks()
        {
            if (_seasonHooks) return;
            _seasonHooks = true;
            if (PlayingControls != null)
                PlayingControls.IsVisibleChanged += (_, _) => RefreshSeasonalChrome();
            if (SearchInputPanel != null)
                SearchInputPanel.IsVisibleChanged += (_, _) => RefreshSeasonalChrome();
            if (FestivalHubPanel != null)
                FestivalHubPanel.IsVisibleChanged += (_, _) => RefreshSeasonalChrome();
        }

        private const double SeasonGroundHeight = 52;
        private const double SeasonVeilBottom = 42;

        private static void SetLayer(UIElement? element, bool on)
        {
            if (element != null) element.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshSeasonalChrome()
        {
            var theme = _appliedTheme ?? SeasonalTheme.None;
            bool lyricsOnly = isGhostMode || isResizing;
            var user = ActiveUserTheme(theme);          
            EnsureUserThemeContent(user);
            bool themed = (theme != SeasonalTheme.None || user != null) && !lyricsOnly;

            SetLayer(SeasonalLayer, themed);
            SetLayer(UserThemeGround, themed && user != null);
            SetLayer(UserThemeLayer, themed && user != null);
            SetLayer(ChristmasSnowLayer, themed && theme == SeasonalTheme.Christmas);
            SetLayer(HalloweenGround, themed && theme == SeasonalTheme.Halloween);
            SetLayer(NewYearGround, themed && theme == SeasonalTheme.NewYear);

            bool veilOpen = SearchInputPanel?.Visibility == Visibility.Visible || FestivalHubPanel?.Visibility == Visibility.Visible;
            int groundZ = veilOpen ? 10 : 0;
            if (ChristmasSnowLayer != null) Panel.SetZIndex(ChristmasSnowLayer, groundZ);
            if (HalloweenGround != null) Panel.SetZIndex(HalloweenGround, groundZ);
            if (NewYearGround != null) Panel.SetZIndex(NewYearGround, groundZ);
            if (UserThemeGround != null) Panel.SetZIndex(UserThemeGround, groundZ);

            if (ScreenBackdrop != null)
            {
                ScreenBackdrop.Visibility = veilOpen ? Visibility.Visible : Visibility.Collapsed;
                ScreenBackdrop.Margin = themed ? new Thickness(-20, -20, -20, -SeasonGroundHeight) : new Thickness(-20);
                Panel.SetZIndex(ScreenBackdrop, 9);
            }

            if (MainBorder != null)
                MainBorder.Padding = themed ? new Thickness(20, 20, 20, SeasonGroundHeight) : new Thickness(20);

            if (SeasonalLayer != null)
                SeasonalLayer.Margin = new Thickness(0);

            var veilMargin = themed ? new Thickness(-20, -20, -20, SeasonVeilBottom - SeasonGroundHeight) : new Thickness(-20);
            var veil = user?.PageVeil ?? PageVeil(theme);
            if (SearchInputPanel != null) { SearchInputPanel.Margin = veilMargin; SearchInputPanel.Background = veil; }
            if (FestivalHubPanel != null) { FestivalHubPanel.Margin = veilMargin; FestivalHubPanel.Background = veil; }

            PaintSeasonalCard(SearchContentBorder, theme, user);
            PaintSeasonalCard(FestivalContentBorder, theme, user);
        }

        private static SolidColorBrush Freeze(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Brush PageVeil(SeasonalTheme theme) => theme switch
        {
            SeasonalTheme.Halloween => Freeze(0x88, 0x14, 0x0C, 0x20),
            SeasonalTheme.Christmas => Freeze(0x55, 0x10, 0x18, 0x24),
            SeasonalTheme.NewYear => Freeze(0x66, 0x14, 0x10, 0x28),
            _ => Freeze(0x99, 0x05, 0x08, 0x10),
        };

        private static void PaintSeasonalCard(Border? card, SeasonalTheme theme, UserThemeDef? user = null)
        {
            if (card == null) return;
            if (user != null)
            {
                card.Background = user.CardBackground;
                card.BorderBrush = user.CardBorder;
                return;
            }
            card.Background = theme switch
            {
                SeasonalTheme.Halloween => Freeze(0xF2, 0x14, 0x0C, 0x18),
                SeasonalTheme.Christmas => Freeze(0xF0, 0x10, 0x14, 0x1C),
                SeasonalTheme.NewYear => Freeze(0xF2, 0x12, 0x10, 0x18),
                _ => Freeze(0xFF, 0x0A, 0x0A, 0x0A),
            };
            card.BorderBrush = theme switch
            {
                SeasonalTheme.Halloween => Freeze(0x00, 0xA0, 0x6A, 0xE0),
                SeasonalTheme.Christmas => Freeze(0x00, 0xD0, 0xE4, 0xF5),
                SeasonalTheme.NewYear => Freeze(0x00, 0xE0, 0xC5, 0x6A),
                _ => Freeze(0x00, 0xFF, 0xFF, 0xFF),
            };
        }

        private static Color GetSeasonalLogoColor(SeasonalTheme theme) => theme switch
        {
            SeasonalTheme.Halloween => Color.FromRgb(160, 106, 224),
            SeasonalTheme.Christmas => Color.FromRgb(208, 228, 245),
            SeasonalTheme.NewYear => Color.FromRgb(224, 197, 106),
            _ => Color.FromRgb(255, 255, 255)
        };

        private Color GetThemeLogoColor()
        {
            if (_appliedUserTheme != null)
            {
                if (_appliedUserTheme.LogoColor is SolidColorBrush solidBrush)
                {
                    return solidBrush.Color;
                }
                else if (_appliedUserTheme.LogoColor is LinearGradientBrush gradientBrush && gradientBrush.GradientStops.Count > 0)
                {
                    return gradientBrush.GradientStops[0].Color;
                }
            }

            return GetSeasonalLogoColor(_appliedTheme ?? SeasonalTheme.None);
        }

        private static BitmapSource TintBitmap(BitmapSource source, Color tintColor)
        {
            BitmapSource bgra = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            int stride = w * 4;
            byte[] pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);

            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] > 0)
                {
                    pixels[i] = tintColor.B; // Blue
                    pixels[i + 1] = tintColor.G; // Green
                    pixels[i + 2] = tintColor.R; // Red
                }
            }

            var wb = new WriteableBitmap(w, h, bgra.DpiX, bgra.DpiY, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
            wb.Freeze();
            return wb;
        }

        private static BitmapSource TintBitmap(BitmapSource source, Brush brush)
        {
            if (brush is SolidColorBrush solid) return TintBitmap(source, solid.Color);
            if (brush is not LinearGradientBrush grad || grad.GradientStops.Count == 0) return source;

            BitmapSource bgra = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            int stride = w * 4;
            byte[] pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);

            var stops = grad.GradientStops.OrderBy(st => st.Offset).ToList();
            double sx = grad.StartPoint.X, sy = grad.StartPoint.Y;
            double dx = grad.EndPoint.X - sx, dy = grad.EndPoint.Y - sy;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-9) len2 = 1;

            Color Sample(double t)
            {
                t = Math.Clamp(t, 0, 1);
                if (t <= stops[0].Offset) return stops[0].Color;
                for (int k = 1; k < stops.Count; k++)
                {
                    if (t <= stops[k].Offset)
                    {
                        var a = stops[k - 1];
                        var b = stops[k];
                        double f = (t - a.Offset) / Math.Max(b.Offset - a.Offset, 1e-9);
                        return Color.FromRgb(
                            (byte)(a.Color.R + (b.Color.R - a.Color.R) * f),
                            (byte)(a.Color.G + (b.Color.G - a.Color.G) * f),
                            (byte)(a.Color.B + (b.Color.B - a.Color.B) * f));
                    }
                }
                return stops[stops.Count - 1].Color;
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * stride + x * 4;
                    if (pixels[i + 3] == 0) continue;

                    double px = (x + 0.5) / w, py = (y + 0.5) / h;
                    double t = ((px - sx) * dx + (py - sy) * dy) / len2;
                    Color c = Sample(t);
                    pixels[i] = c.B;
                    pixels[i + 1] = c.G;
                    pixels[i + 2] = c.R;
                }
            }

            var wb = new WriteableBitmap(w, h, bgra.DpiX, bgra.DpiY, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
            wb.Freeze();
            return wb;
        }

        private void LoadAppIcon()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "logo.png");
                if (!File.Exists(path)) return;

                BitmapSource baseLogo = DecodeBitmap(new Uri(path, UriKind.Absolute), null);
                if (_appliedUserTheme?.LogoColor is LinearGradientBrush gradient)
                {
                    Icon = TintBitmap(baseLogo, gradient);
                }
                else
                {
                    Color themeColor = GetThemeLogoColor();
                    Icon = themeColor == Color.FromRgb(255, 255, 255)
                        ? BitmapFrame.Create(new Uri(path, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad)
                        : TintBitmap(baseLogo, themeColor);
                }
            }
            catch (Exception ex) { CrashReporter.Log(ex, "LoadAppIcon"); }
        }

        private void ApplyMenuLogo(bool force = false)
        {
            try
            {
                ClearCoverFrame();
                if (AlbumCoverImg == null) return;
                if (!force && !string.IsNullOrEmpty(currentCoverUrl)) return;

                RenderOptions.SetBitmapScalingMode(AlbumCoverImg, BitmapScalingMode.HighQuality);

                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "logo.png");
                if (!File.Exists(path))
                {
                    AlbumCoverImg.Source = null;
                    return;
                }

                BitmapSource baseLogo = LoadSharpLogo(new Uri(path, UriKind.Absolute));
                if (_appliedUserTheme?.LogoColor is LinearGradientBrush gradient)
                {
                    AlbumCoverImg.Source = TintBitmap(baseLogo, gradient);
                }
                else
                {
                    Color themeColor = GetThemeLogoColor();
                    AlbumCoverImg.Source = themeColor == Color.FromRgb(255, 255, 255)
                        ? baseLogo
                        : TintBitmap(baseLogo, themeColor);
                }
            }
            catch (Exception ex) { CrashReporter.Log(ex, "ApplyMenuLogo"); }
        }

        private void ClearCoverFrame()
        {
            if (AlbumCoverImg == null) return;
            RenderOptions.SetBitmapScalingMode(AlbumCoverImg, BitmapScalingMode.HighQuality);
            AlbumCoverImg.Stretch = Stretch.Uniform;

            DependencyObject? node = AlbumCoverImg;
            for (int i = 0; i < 8 && node != null; i++)
            {
                node = LogicalTreeHelper.GetParent(node) ?? VisualTreeHelper.GetParent(node);
                if (node is not Border frame || ReferenceEquals(frame, MainBorder)) continue;
                frame.Background = Brushes.Transparent;
                frame.BorderBrush = Brushes.Transparent;
                frame.BorderThickness = new Thickness(0);
                frame.CornerRadius = new CornerRadius(0);
                frame.Padding = new Thickness(0);
                frame.ClipToBounds = false;
                frame.Effect = null;
            }
        }

        private BitmapSource LoadSharpLogo(Uri uri)
        {
            BitmapSource src = DecodeBitmap(uri, null);
            src = StripConnectedBackdrop(src);
            if (src.CanFreeze && !src.IsFrozen) src.Freeze();
            return src;
        }

        private static BitmapSource StripConnectedBackdrop(BitmapSource source)
        {
            BitmapSource bgra = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            if (w < 8 || h < 8) return source;
            int stride = w * 4;
            var pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);
            if (!TryBackdropSeed(pixels, w, h, stride, out byte sr, out byte sg, out byte sb))
                return source;

            var seen = new bool[w * h];
            var queue = new Queue<int>();
            void Consider(int x, int y)
            {
                if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
                int i = y * w + x;
                if (seen[i]) return;
                int o = i * 4;
                byte b = pixels[o], g = pixels[o + 1], r = pixels[o + 2], a = pixels[o + 3];
                if (a >= 16 && !IsBackdropPixel(r, g, b, sr, sg, sb)) return;
                seen[i] = true;
                queue.Enqueue(i);
            }

            for (int x = 0; x < w; x++) { Consider(x, 0); Consider(x, h - 1); }
            for (int y = 0; y < h; y++) { Consider(0, y); Consider(w - 1, y); }

            int cleared = 0;
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % w, y = i / w, o = i * 4;
                if (pixels[o + 3] != 0)
                {
                    pixels[o + 3] = 0;
                    cleared++;
                }
                Consider(x - 1, y);
                Consider(x + 1, y);
                Consider(x, y - 1);
                Consider(x, y + 1);
            }

            int area = w * h;
            if (cleared < area / 12 || cleared > area * 72 / 100) return source;
            var wb = new WriteableBitmap(w, h, bgra.DpiX, bgra.DpiY, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
            wb.Freeze();
            return wb;
        }

        private static bool TryBackdropSeed(byte[] pixels, int w, int h, int stride, out byte sr, out byte sg, out byte sb)
        {
            sr = sg = sb = 0;
            var samples = new List<(byte r, byte g, byte b)>();
            void Walk(int x, int y, int dx, int dy)
            {
                for (int step = 0; step < Math.Min(w, h); step++)
                {
                    int px = x + dx * step, py = y + dy * step;
                    if ((uint)px >= (uint)w || (uint)py >= (uint)h) return;
                    int o = py * stride + px * 4;
                    if (pixels[o + 3] < 16) continue;
                    byte b = pixels[o], g = pixels[o + 1], r = pixels[o + 2];
                    if (Chroma(r, g, b) > 18) return;
                    samples.Add((r, g, b));
                    return;
                }
            }
            Walk(0, 0, 1, 1);
            Walk(w - 1, 0, -1, 1);
            Walk(0, h - 1, 1, -1);
            Walk(w - 1, h - 1, -1, -1);
            if (samples.Count < 2) return false;
            sr = (byte)samples.Average(s => s.r);
            sg = (byte)samples.Average(s => s.g);
            sb = (byte)samples.Average(s => s.b);
            int brightness = (sr + sg + sb) / 3;
            return Chroma(sr, sg, sb) <= 18 && brightness >= 18 && brightness <= 70;
        }

        private static bool IsBackdropPixel(byte r, byte g, byte b, byte sr, byte sg, byte sb)
        {
            if (Chroma(r, g, b) > 18) return false;
            if (Math.Abs(r - sr) > 22 || Math.Abs(g - sg) > 22 || Math.Abs(b - sb) > 22) return false;
            if (sr + sg + sb > 48 && r + g + b + 36 < sr + sg + sb) return false;
            return true;
        }

        private static int Chroma(byte r, byte g, byte b) =>
            Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
    }

    internal sealed class LyricFontItem
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Category { get; init; } = "";
        public FontFamily Family { get; init; } = new FontFamily("Segoe UI");
    }

    internal static class LyricFonts
    {
        public const string DefaultId = "segoe";

        private static readonly (string Id, string Name, string Group, string File)[] Bundled =
        {
            ("poppins", "Poppins", "FontGroupSans", "Poppins_400Regular.ttf"),
            ("montserrat", "Montserrat", "FontGroupSans", "Montserrat_400Regular.ttf"),
            ("inter", "Inter", "FontGroupSans", "Inter_400Regular.ttf"),
            ("nunito", "Nunito", "FontGroupSans", "Nunito_400Regular.ttf"),
            ("quicksand", "Quicksand", "FontGroupSans", "Quicksand_400Regular.ttf"),
            ("raleway", "Raleway", "FontGroupSans", "Raleway_400Regular.ttf"),
            ("lora", "Lora", "FontGroupSerif", "Lora_400Regular.ttf"),
            ("playfair", "Playfair Display", "FontGroupSerif", "PlayfairDisplay_400Regular.ttf"),
            ("cinzel", "Cinzel", "FontGroupSerif", "Cinzel_400Regular.ttf"),
            ("oswald", "Oswald", "FontGroupDisplay", "Oswald_400Regular.ttf"),
            ("bebas", "Bebas Neue", "FontGroupDisplay", "BebasNeue_400Regular.ttf"),
            ("anton", "Anton", "FontGroupDisplay", "Anton_400Regular.ttf"),
            ("orbitron", "Orbitron", "FontGroupDisplay", "Orbitron_400Regular.ttf"),
            ("bangers", "Bangers", "FontGroupDisplay", "Bangers_400Regular.ttf"),
            ("pacifico", "Pacifico", "FontGroupHand", "Pacifico_400Regular.ttf"),
            ("lobster", "Lobster", "FontGroupHand", "Lobster_400Regular.ttf"),
            ("dancing", "Dancing Script", "FontGroupHand", "DancingScript_400Regular.ttf"),
            ("caveat", "Caveat", "FontGroupHand", "Caveat_400Regular.ttf"),
            ("marker", "Permanent Marker", "FontGroupHand", "PermanentMarker_400Regular.ttf"),
        };

        private static readonly (string Id, string Name, string Family)[] WindowsCatalog =
        {
            ("segoe", "Segoe UI", "Segoe UI"),
            ("calibri", "Calibri", "Calibri"),
            ("candara", "Candara", "Candara"),
            ("corbel", "Corbel", "Corbel"),
            ("constantia", "Constantia", "Constantia"),
            ("cambria", "Cambria", "Cambria"),
            ("georgia", "Georgia", "Georgia"),
            ("palatino", "Palatino", "Palatino Linotype"),
            ("antiqua", "Book Antiqua", "Book Antiqua"),
            ("garamond", "Garamond", "Garamond"),
            ("century", "Century Gothic", "Century Gothic"),
            ("verdana", "Verdana", "Verdana"),
            ("trebuchet", "Trebuchet MS", "Trebuchet MS"),
            ("tahoma", "Tahoma", "Tahoma"),
            ("franklin", "Franklin Gothic", "Franklin Gothic Medium"),
            ("lucida", "Lucida Sans", "Lucida Sans Unicode"),
            ("bahnschrift", "Bahnschrift", "Bahnschrift"),
            ("sitka", "Sitka Text", "Sitka Text"),
            ("gabriola", "Gabriola", "Gabriola"),
            ("script", "Segoe Script", "Segoe Script"),
            ("ink", "Ink Free", "Ink Free"),
            ("consolas", "Consolas", "Consolas"),
            ("cascadia", "Cascadia Mono", "Cascadia Mono"),
            ("cascadia-code", "Cascadia Code", "Cascadia Code"),
        };

        private static List<LyricFontItem>? _cache;
        private static readonly object Gate = new();

        public static string BundledDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "fonts");

        public static IReadOnlyList<LyricFontItem> Installed()
        {
            lock (Gate)
            {
                return _cache ??= Build();
            }
        }

        private static List<LyricFontItem> Build()
        {
            var list = new List<LyricFontItem>();

            string dir = BundledDir;
            Uri? baseUri = null;
            try
            {
                if (Directory.Exists(dir))
                    baseUri = new Uri(dir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, UriKind.Absolute);
            }
            catch { baseUri = null; }

            if (baseUri != null)
            {
                foreach (var f in Bundled)
                {
                    if (!File.Exists(Path.Combine(dir, f.File))) continue;
                    try
                    {
                        var family = new FontFamily(baseUri, "./#" + f.Name);
                        if (!Loads(family, f.Name))
                        {
                            CrashReporter.Log(new InvalidOperationException("Fonte não carregou: " + f.Name), "LyricFonts.Build");
                            continue;
                        }
                        list.Add(new LyricFontItem
                        {
                            Id = f.Id,
                            Name = f.Name,
                            Category = f.Group,
                            Family = family,
                        });
                    }
                    catch (Exception ex) { CrashReporter.Log(ex, "LyricFonts.Build"); }
                }
            }

            HashSet<string> installed;
            bool known = true;
            try
            {
                installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var family in Fonts.SystemFontFamilies)
                {
                    installed.Add(family.Source);
                    foreach (var n in family.FamilyNames.Values)
                        installed.Add(n);
                }
            }
            catch
            {
                installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                known = false;
            }

            foreach (var w in WindowsCatalog)
            {
                if (known && !installed.Contains(w.Family) && w.Id != DefaultId) continue;
                list.Add(new LyricFontItem
                {
                    Id = w.Id,
                    Name = w.Name,
                    Category = "FontGroupWindows",
                    Family = new FontFamily(w.Family),
                });
            }
            return list;
        }
        private static bool Loads(FontFamily family, string name)
        {
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal, new FontFamily("Segoe UI"));
            if (!typeface.TryGetGlyphTypeface(out var glyphs)) return false;
            return glyphs.FamilyNames.Values.Concat(glyphs.Win32FamilyNames.Values)
                         .Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }

        public static FontFamily Resolve(string? id)
        {
            var all = Installed();
            var hit = all.FirstOrDefault(x => x.Id == id) ?? all.FirstOrDefault(x => x.Id == DefaultId);
            return hit?.Family ?? new FontFamily("Segoe UI");
        }
    }

    internal static class LyricWeights
    {
        public const string DefaultId = "bold";

        public static readonly (string Id, string Key, FontWeight Weight)[] All =
        {
            ("light", "Light", FontWeights.Light),
            ("regular", "Regular", FontWeights.Normal),
            ("semibold", "SemiBold", FontWeights.SemiBold),
            ("bold", "Bold", FontWeights.Bold),
            ("extrabold", "ExtraBold", FontWeights.ExtraBold),
        };

        public static FontWeight Resolve(string? id)
        {
            var hit = All.FirstOrDefault(x => x.Id == id);
            return string.IsNullOrEmpty(hit.Id) ? FontWeights.Bold : hit.Weight;
        }
    }

    internal static class LyricStyles
    {
        public const string DefaultId = "upright";

        public static readonly (string Id, string Key, FontStyle Style)[] All =
        {
            ("upright", "Upright", FontStyles.Normal),
            ("italic", "Italic", FontStyles.Italic),
        };

        public static FontStyle Resolve(string? id)
        {
            var hit = All.FirstOrDefault(x => x.Id == id);
            return string.IsNullOrEmpty(hit.Id) ? FontStyles.Normal : hit.Style;
        }
    }
}