using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace FrontLineOverlay
{
    internal enum LyricColorGroup { Solid, Gradient, Metallic }

    internal sealed class LyricColorPreset
    {
        private readonly Func<Brush> _factory;

        public string Id { get; }
        public string Name { get; }
        public LyricColorGroup Group { get; }

        public LyricColorPreset(string id, string name, LyricColorGroup group, Func<Brush> factory)
        {
            Id = id; Name = name; Group = group; _factory = factory;
        }

        public Brush CreateBrush() => _factory();
    }

    internal static class LyricColorPresets
    {
        public const string DefaultId = "default";

        public static readonly IReadOnlyList<LyricColorPreset> All = Build();

        public static LyricColorPreset Find(string? id) =>
            All.FirstOrDefault(p => p.Id == id) ?? All[0];

        private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        private static Brush Solid(string hex)
        {
            var b = new SolidColorBrush(C(hex));
            b.Freeze();
            return b;
        }

        private static Brush Linear(bool vertical, params (string hex, double offset)[] stops)
        {
            var b = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = vertical ? new Point(0, 1) : new Point(1, 0)
            };
            foreach (var (hex, offset) in stops)
                b.GradientStops.Add(new GradientStop(C(hex), offset));
            b.Freeze();
            return b;
        }

        private static List<LyricColorPreset> Build()
        {
            var s = LyricColorGroup.Solid;
            var g = LyricColorGroup.Gradient;
            var m = LyricColorGroup.Metallic;

            return new List<LyricColorPreset>
            {
                new("default", "Default",  s, () => Solid("#FFCC33")),
                new("white",   "White",    s, () => Solid("#FFFFFF")),
                new("cyan",    "Cyan",     s, () => Solid("#5CE1E6")),
                new("mint",    "Mint",     s, () => Solid("#7AF59B")),
                new("blue",    "Blue",     s, () => Solid("#5AA9FF")),
                new("purple",  "Purple",   s, () => Solid("#A955FF")),
                new("pink",    "Pink",     s, () => Solid("#FF7AC6")),
                new("red",     "Red",      s, () => Solid("#FF5C5C")),
                new("orange",  "Orange",   s, () => Solid("#FF9F43")),

                new("grad_sunset", "Sunset", g, () => Linear(false, ("#FFD36B", 0), ("#FF7A5C", 0.55), ("#E0407A", 1))),
                new("grad_ocean",  "Ocean",  g, () => Linear(false, ("#5CE1E6", 0), ("#3A8DFF", 1))),
                new("grad_aurora", "Aurora", g, () => Linear(false, ("#7AF59B", 0), ("#5CC8FF", 0.5), ("#7A53E4", 1))),
                new("grad_candy",  "Candy",  g, () => Linear(false, ("#FF7AC6", 0), ("#A955FF", 1))),
                new("grad_fire",   "Fire",   g, () => Linear(false, ("#FFE066", 0), ("#FF9F43", 0.5), ("#FF4E50", 1))),

                new("metal_gold",   "Gold",      m, () => Linear(true, ("#FFF6C2", 0), ("#F5C842", 0.35), ("#B8860B", 0.55), ("#FFE27A", 0.78), ("#C99A1E", 1))),
                new("metal_silver", "Silver",    m, () => Linear(true, ("#FFFFFF", 0), ("#C9CED6", 0.4), ("#8A919C", 0.55), ("#E6E9ED", 0.8), ("#AEB4BE", 1))),
                new("metal_rose",   "Rose gold", m, () => Linear(true, ("#FFE3D6", 0), ("#E8A98F", 0.4), ("#B76E55", 0.58), ("#F3C4AE", 0.85), ("#D18B72", 1))),
                new("metal_copper", "Copper",    m, () => Linear(true, ("#FFD2A8", 0), ("#D9824A", 0.4), ("#8B4A22", 0.58), ("#E8A56B", 0.82), ("#B8683A", 1))),
                new("metal_chrome", "Chrome",    m, () => Linear(true, ("#F4FAFF", 0), ("#9FB6CC", 0.45), ("#3F5568", 0.52), ("#BCD0E2", 0.78), ("#6E8398", 1))),
            };
        }
    }
}