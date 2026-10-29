using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;

namespace FrontLineOverlay
{
    internal sealed class UserThemeDef
    {
        public string Id { get; init; } = "";
        public IReadOnlyDictionary<string, string> Names { get; init; } = new Dictionary<string, string>();
        public Func<UserControl>? CreateGround { get; init; }
        public Func<UserControl>? CreateLayer { get; init; }

        public Brush PageVeil { get; init; } = UserThemes.Solid(0x99, 0x05, 0x08, 0x10);
        public Brush CardBackground { get; init; } = UserThemes.Solid(0xFF, 0x0A, 0x0A, 0x0A);
        public Brush CardBorder { get; init; } = UserThemes.Solid(0x00, 0x00, 0x00, 0x00);

        public Brush LogoColor { get; init; } = UserThemes.Solid(0xFF, 0xFF, 0xFF, 0xFF);

        public Func<UserControl>? CreateLogoAdorn { get; init; }

        public Func<UserControl>? CreateDockAdorn { get; init; }
        public Uri? DockResources { get; init; }                
        public Brush? DockBackground { get; init; }
        public Brush? DockBorder { get; init; }
        public System.Windows.CornerRadius? DockCornerRadius { get; init; }

        public string DisplayName(string lang) =>
            Names.TryGetValue(lang, out var n) ? n :
            Names.TryGetValue("en", out var en) ? en : Id;
    }

    internal static class UserThemes
    {

        public const string DefaultId = "default";

        public static readonly Brush DefaultDockBackground = Solid(0xFA, 0x0A, 0x0A, 0x0A);
        public static readonly Brush DefaultDockBorder = Solid(0x50, 0xFF, 0xFF, 0xFF);
        public static readonly System.Windows.CornerRadius DefaultDockCornerRadius = new(25);

        public static SolidColorBrush Solid(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Brush RainbowBorder()
        {
            var b = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 1) };
            var colors = new[] { 0xE40303, 0xFF8C00, 0xFFED00, 0x00A63A, 0x3A6BFF, 0xA03CC8 };
            for (int i = 0; i < colors.Length; i++)
            {
                int c = colors[i];
                b.GradientStops.Add(new GradientStop(
                    Color.FromArgb(0xB0, (byte)(c >> 16), (byte)(c >> 8), (byte)c),
                    i / (double)(colors.Length - 1)));
            }
            b.Freeze();
            return b;
        }

        private static Brush LogoGradient(System.Windows.Point start, System.Windows.Point end, params (uint argb, double offset)[] stops)
        {
            var b = new LinearGradientBrush { StartPoint = start, EndPoint = end };
            foreach (var (argb, offset) in stops)
                b.GradientStops.Add(new GradientStop(
                    Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb), offset));
            b.Freeze();
            return b;
        }

        private static Brush LoadBrush(string key, Brush fallback)
        {
            try
            {
                var dict = new System.Windows.ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Themes/Seasonal/ChristmasResources.xaml", UriKind.Absolute)
                };
                if (dict[key] is Brush b)
                {
                    if (b.CanFreeze) b.Freeze();
                    return b;
                }
            }
            catch { /* recurso ausente: usa o fallback */ }
            return fallback;
        }

        private static Dictionary<string, string> N(string en, string pt, string es, string id) =>
            new() { ["en"] = en, ["pt"] = pt, ["es"] = es, ["id"] = id };

        public static readonly IReadOnlyList<UserThemeDef> All = new List<UserThemeDef>
        {

            new UserThemeDef
            {
                Id = "goldenedition",
                Names = N("Golden Edition", "Edição de Ouro", "Edición Dorada", "Edisi Emas"),
                CreateGround = () => new GoldVinylGroundView(),
                CreateLayer = () => new GoldVinylLayerView(),
                PageVeil = Solid(0x99, 0x0B, 0x07, 0x03),
                CardBackground = Solid(0xF2, 0x14, 0x0E, 0x07),
                CardBorder = Solid(0x00, 0x00, 0x00, 0x00),
                LogoColor = LogoGradient(
                    new System.Windows.Point(0, 0), new System.Windows.Point(1, 1),
                    (0xFF5A3705, 0.00),   
                    (0xFFB8801A, 0.22),   
                    (0xFFFFD84A, 0.45),   
                    (0xFFFFFBE0, 0.58),   
                    (0xFFE0A826, 0.78),   
                    (0xFF6E4508, 1.00)),  
            },

            new UserThemeDef
            {
                Id = "backstage",
                Names = N("Backstage", "Bastidores", "Backstage", "Belakang Panggung"),
                CreateGround = () => new GrungeGroundView(),
                CreateLayer = () => new GrungeLayerView(),
                PageVeil = Solid(0x88, 0x10, 0x10, 0x12),
                CardBackground = Solid(0xF0, 0x1C, 0x1C, 0x1F),
                CardBorder = Solid(0x00, 0x00, 0x00, 0x00),
                LogoColor = LogoGradient(
                    new System.Windows.Point(0, 1), new System.Windows.Point(1, 0),
                    (0xFFFF5A00, 0.00),   
                    (0xFFFFA800, 0.35),   
                    (0xFFFCE205, 0.65),   
                    (0xFFFFFFA0, 1.00)),  
            },
            new UserThemeDef
            {
                Id = "mainstage",
                Names = N("Main Stage", "Palco Principal", "Escenario Principal", "Panggung Utama"),
                CreateGround = () => new MainStageView(),
                CreateLayer = () => new MainStageLayerView(),
                PageVeil = Solid(0x99, 0x10, 0x02, 0x04),
                CardBackground = Solid(0xF2, 0x14, 0x05, 0x08),
                CardBorder = Solid(0x00, 0x00, 0x00, 0x00),
                LogoColor = LogoGradient(
                    new System.Windows.Point(0, 0), new System.Windows.Point(0, 1),
                    (0xFFFF8A3D, 0.00),   
                    (0xFFFF2A3A, 0.35),   
                    (0xFFB0121C, 0.70),   
                    (0xFF5E060C, 1.00)),  
            },

            new UserThemeDef
            {
                Id = "santa",
                Names = N("Ugly Sweater Christmas", "Natal do Suéter Feio", "Navidad del Suéter Feo", "Natal Sweter Jelek"),
                CreateGround = () => new ChristmasGroundView(),
                CreateLayer = () => new ChristmasLayerView(),
                CreateLogoAdorn = () => new SantaLogoAdorn(),
                DockResources = new Uri("pack://application:,,,/Themes/Seasonal/SantaDockResources.xaml"),
                DockBackground = Solid(0xFA, 0x14, 0x0A, 0x0C),
                DockBorder = Solid(0x90, 0xE0, 0x3A, 0x3A),
                DockCornerRadius = new System.Windows.CornerRadius(22),
                PageVeil = Solid(0x66, 0x10, 0x18, 0x24),
                CardBackground = Solid(0xF0, 0x10, 0x14, 0x1C),
                CardBorder = Solid(0x00, 0x00, 0x00, 0x00),
                LogoColor = LoadBrush("Xmas.SweaterBrush", Solid(0xFF, 0xFF, 0xFF, 0xFF)),
            },

        };

        public static UserThemeDef? Find(string? id) =>
            All.FirstOrDefault(t => t.Id == id);
    }
}