using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace FrontLineOverlay
{

    public sealed class CityLightsGroundView : AmbientThemeView
    {
        private const bool Animated = true;
        protected override bool HasAnimation => Animated;
        protected override int FramesPerSecond => 10;

        private const int Layers = 3;

        private static readonly uint[] WinRgb = { 0xFFD27A, 0x77DFFF, 0xDE91F5 };  
        private static readonly byte[] WinAlpha = { 0x60, 0xA8, 0xE0 };            

        private static readonly Brush Sky = VGradient(
            (0xFF020711, 0.00), (0xFF0A1330, 0.55), (0xFF1F1A4A, 0.85), (0xFF3C2A6E, 1.00));
        private static readonly Brush WaterBrush = VGradient(
            (0xFF0C1736, 0.00), (0xFF050C20, 0.45), (0xFF02060F, 1.00));
        private static readonly Brush Fog = VGradient(
            (0x00453490, 0.00), (0x3A4A3A9A, 1.00));

        private static readonly Brush[] Wall =
        {
            VGradient((0xFF1D2D58, 0.0), (0xFF2A2A62, 1.0)),
            VGradient((0xFF111E3E, 0.0), (0xFF1B2249, 1.0)),
            VGradient((0xFF08122B, 0.0), (0xFF0C1432, 1.0)),
        };
        private static readonly Brush[] WallBase = { Solid(0xFF2A2A62), Solid(0xFF1B2249), Solid(0xFF0C1432) };
        private static readonly Brush[] Edge = { Solid(0x1CA8C8FF), Solid(0x2CA8C8FF), Solid(0x3CA8C8FF) };
        private static readonly Pen[] AntennaPen = { Stroke(0x806F63A6, 0.7), Stroke(0x806F63A6, 0.7), Stroke(0xAA7684C7, 0.7) };

        private static readonly Brush[,] Win = MakeWindowBrushes(0);
        private static readonly Brush[] Refl = MakeReflBrushes();

        private static readonly Brush HazeBlue = Glow(0x885F49A8);
        private static readonly Brush HazePink = Glow(0x55B0408F);
        private static readonly Brush WaterGlow = Glow(0x403B4FA8);
        private static readonly Brush Shore = Solid(0x334F6FB0);
        private static readonly Brush Ripple = Solid(0x305B8CD0);
        private static readonly Brush MoonBrush = Solid(0xFFDDE6FF);
        private static readonly Brush MoonGlow = Glow(0x38B8CCFF);
        private static readonly Brush MoonRefl = Solid(0x55DDE6FF);
        private static readonly Brush StarDim = Solid(0x70B8C8FF);
        private static readonly Brush StarBright = Solid(0xD0FFFFFF);
        private static readonly Brush[] Twinkle = { Solid(0x30DDE6FF), Solid(0x90DDE6FF), Solid(0xFFFFFFFF) };
        private static readonly Brush TowerBrush = Solid(0xFF0E1A38);
        private static readonly Pen SpirePen = Stroke(0xCC6F7FC7, 1.0);
        private static readonly Brush BeaconCore = Solid(0xFFFF6B78);
        private static readonly Brush BeaconDim = Solid(0x90A03444);
        private static readonly Brush BeaconGlow = Glow(0xCCFF4D5E);
        private static readonly Brush Fallback = Solid(0xFF030B17);

        private DrawingGroup? _scene;
        private double _sceneW, _sceneH;
        private (Point P, double Phase)[] _beacons = Array.Empty<(Point P, double Phase)>();
        private (Point P, double Phase)[] _twinkle = Array.Empty<(Point P, double Phase)>();

        private static double Horizon(double h) => h - Math.Clamp(h * 0.17, 60, 110);

        private static LinearGradientBrush VGradient(params (uint argb, double offset)[] stops)
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            foreach (var (argb, off) in stops)
                b.GradientStops.Add(new GradientStop(
                    Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb), off));
            b.Freeze();
            return b;
        }

        private static Brush[,] MakeWindowBrushes(int _)
        {
            var r = new Brush[Layers, 3];
            for (int l = 0; l < Layers; l++)
                for (int c = 0; c < 3; c++)
                    r[l, c] = Solid(((uint)WinAlpha[l] << 24) | WinRgb[c]);
            return r;
        }

        private static Brush[] MakeReflBrushes()
        {
            var r = new Brush[3];
            for (int c = 0; c < 3; c++) r[c] = Solid((0x3Au << 24) | WinRgb[c]);
            return r;
        }

        private static void AddRect(StreamGeometryContext c, double x, double y, double w, double h)
        {
            c.BeginFigure(new Point(x, y), true, true);
            c.LineTo(new Point(x + w, y), false, false);
            c.LineTo(new Point(x + w, y + h), false, false);
            c.LineTo(new Point(x, y + h), false, false);
        }

        protected override void RebuildScene(double w, double h)
        {
            if (w <= 0 || h <= 0) return;
            var rnd = new Random(7291);
            double horizon = Horizon(h);
            double hs = Math.Clamp(h / 570, 0.45, 1.3);
            double fogH = Math.Clamp(h * 0.22, 50, 130);
            var beacons = new List<(Point P, double Phase)>();
            var twinkle = new List<(Point P, double Phase)>();
            var group = new DrawingGroup();

            using (DrawingContext dc = group.Open())
            {
                dc.DrawRectangle(Sky, null, new Rect(0, 0, w, horizon + 1));
                DrawStars(dc, w, horizon, rnd, twinkle);
                double moonX = DrawMoon(dc, w, horizon);
                dc.DrawEllipse(HazeBlue, null, new Point(w * 0.57, horizon - 12), w * 0.65, Math.Clamp(h * 0.24, 40, 180));
                dc.DrawEllipse(HazePink, null, new Point(w * 0.18, horizon - 6), w * 0.32, Math.Clamp(h * 0.16, 30, 120));
                DrawWater(dc, w, h, horizon, moonX, rnd);

                var refl = new[] { new StreamGeometry(), new StreamGeometry(), new StreamGeometry() };
                using (var r0 = refl[0].Open())
                using (var r1 = refl[1].Open())
                using (var r2 = refl[2].Open())
                {
                    var rc = new[] { r0, r1, r2 };

                    for (int layer = 0; layer < Layers; layer++)
                    {
                        double cell = w / (layer == 0 ? 54.0 : layer == 1 ? 42.0 : 32.0);
                        double ground = horizon - (2 - layer) * 7;
                        double ws = layer == 0 ? 1.2 : layer == 1 ? 1.6 : 2.0;
                        double wh = ws * 1.5;
                        double colStep = layer == 0 ? 3.6 : layer == 1 ? 4.4 : 5.4;
                        double rowStep = colStep * 1.45;

                        dc.DrawRectangle(WallBase[layer], null, new Rect(0, ground - 2, w, horizon - ground + 2));

                        var bGeo = new StreamGeometry();
                        var eGeo = new StreamGeometry();
                        var aGeo = new StreamGeometry();
                        var wGeo = new[] { new StreamGeometry(), new StreamGeometry(), new StreamGeometry() };

                        using (var bc = bGeo.Open())
                        using (var ec = eGeo.Open())
                        using (var ac = aGeo.Open())
                        using (var winA = wGeo[0].Open())
                        using (var winB = wGeo[1].Open())
                        using (var winC = wGeo[2].Open())
                        {
                            var wc = new[] { winA, winB, winC };
                            int count = (int)(w / cell) + 3;

                            for (int i = 0; i < count; i++)
                            {
                                double x = (i - 1) * cell + rnd.NextDouble() * cell * 0.3;
                                double width = Math.Max(4, cell * (0.50 + rnd.NextDouble() * 0.45));
                                double height = (14 + rnd.NextDouble() * (30 + layer * 16)) * hs;
                                if (layer < 2 && rnd.NextDouble() < 0.08) height *= 1.5;
                                double y = ground - height;

                                AddRect(bc, x, y, width, height + 1);
                                AddRect(ec, x, y, width, 1);

                                double top = y;
                                if (rnd.NextDouble() < 0.28)
                                {
                                    double tw = width * (0.5 + rnd.NextDouble() * 0.2);
                                    double th = (5 + rnd.NextDouble() * 9) * hs;
                                    double tx = x + (width - tw) / 2;
                                    AddRect(bc, tx, y - th, tw, th + 1);
                                    AddRect(ec, tx, y - th, tw, 1);
                                    top = y - th;
                                }

                                if (i % 6 == 0)
                                {
                                    double cx = x + width / 2;
                                    double tip = top - (8 + rnd.NextDouble() * 14) * hs;
                                    ac.BeginFigure(new Point(cx, top), false, false);
                                    ac.LineTo(new Point(cx, tip), true, false);
                                    if (layer >= 1 && i % 12 == 0)
                                        beacons.Add((new Point(cx, tip), rnd.NextDouble() * 2.6));
                                }

                                int main = rnd.NextDouble() < 0.5 ? 0 : rnd.NextDouble() < 0.7 ? 1 : 2;
                                double lit = 0.15 + rnd.NextDouble() * 0.35;
                                int cols = (int)((width - 3) / colStep);
                                int rows = (int)((height - 4) / rowStep);
                                for (int row = 0; row < rows; row++)
                                    for (int col = 0; col < cols; col++)
                                    {
                                        if (rnd.NextDouble() > lit) continue;
                                        int ci = rnd.NextDouble() < 0.7 ? main : rnd.Next(3);
                                        double wx = x + 1.5 + col * colStep;
                                        double wy = y + 3 + row * rowStep;
                                        AddRect(wc[ci], wx, wy, ws, wh);

                                        if (layer >= 1 && rnd.NextDouble() < 0.07)
                                        {
                                            double ry = ground + (ground - wy);
                                            double rh = 2 + rnd.NextDouble() * 7;
                                            if (ry + rh < h - 3) AddRect(rc[ci], wx, ry, ws * 0.9, rh);
                                        }
                                    }

                                if (layer > 0 && width > 9 && rnd.NextDouble() < 0.12)
                                    AddRect(wc[main], x + 1, y + height * (0.15 + rnd.NextDouble() * 0.5), width - 2, 1.2);
                            }
                        }

                        bGeo.Freeze(); eGeo.Freeze(); aGeo.Freeze();
                        foreach (var g in wGeo) g.Freeze();

                        dc.DrawGeometry(Wall[layer], null, bGeo);
                        dc.DrawGeometry(Edge[layer], null, eGeo);
                        dc.DrawGeometry(null, AntennaPen[layer], aGeo);
                        for (int c = 0; c < 3; c++)
                            dc.DrawGeometry(Win[layer, c], null, wGeo[c]);

                        if (layer == 1) DrawTower(dc, w * 0.64, ground, hs, beacons);
                        if (layer < 2) dc.DrawRectangle(Fog, null, new Rect(0, horizon - fogH, w, fogH));
                    }
                }

                foreach (var g in refl) g.Freeze();
                for (int c = 0; c < 3; c++) dc.DrawGeometry(Refl[c], null, refl[c]);
            }

            group.Freeze();
            _scene = group;
            _sceneW = w; _sceneH = h;
            _beacons = beacons.ToArray();
            _twinkle = twinkle.ToArray();
        }

        private static void DrawStars(DrawingContext dc, double w, double horizon, Random rnd, List<(Point P, double Phase)> twinkle)
        {
            int n = (int)Math.Clamp(w * horizon / 7000.0, 30, 160);
            var dim = new StreamGeometry();
            var bright = new StreamGeometry();
            using (var dimCtx = dim.Open())
            using (var brightCtx = bright.Open())
            {
                for (int i = 0; i < n; i++)
                {
                    double x = rnd.NextDouble() * w;
                    double y = rnd.NextDouble() * horizon * 0.70;
                    if (rnd.NextDouble() < 0.12) AddRect(brightCtx, x, y, 1.4, 1.4);
                    else AddRect(dimCtx, x, y, 1, 1);
                }
            }
            dim.Freeze(); bright.Freeze();
            dc.DrawGeometry(StarDim, null, dim);
            dc.DrawGeometry(StarBright, null, bright);

            for (int i = 0; i < 14; i++)
                twinkle.Add((new Point(rnd.NextDouble() * w, rnd.NextDouble() * horizon * 0.60), rnd.NextDouble() * 6.28));
        }

        private static double DrawMoon(DrawingContext dc, double w, double horizon)
        {
            double r = Math.Clamp(horizon * 0.05, 8, 20);
            var c = new Point(w * 0.86, horizon * 0.26);
            dc.DrawEllipse(MoonGlow, null, c, r * 5, r * 5);
            var disc = new EllipseGeometry(c, r, r);
            var cut = new EllipseGeometry(new Point(c.X + r * 0.5, c.Y - r * 0.25), r * 0.88, r * 0.88);
            Geometry crescent = Geometry.Combine(disc, cut, GeometryCombineMode.Exclude, null);
            crescent.Freeze();
            dc.DrawGeometry(MoonBrush, null, crescent);
            return c.X;
        }

        private static void DrawWater(DrawingContext dc, double w, double h, double horizon, double moonX, Random rnd)
        {
            double waterH = Math.Max(0, h - horizon);
            dc.DrawRectangle(WaterBrush, null, new Rect(0, horizon, w, waterH));
            dc.DrawEllipse(WaterGlow, null, new Point(w * 0.57, horizon + 8), w * 0.55, Math.Clamp(waterH * 0.5, 14, 50));

            var rip = new StreamGeometry();
            var moon = new StreamGeometry();
            using (var rc = rip.Open())
            using (var mc = moon.Open())
            {
                for (int i = 0; i < 70; i++)
                {
                    double x = rnd.NextDouble() * w;
                    double y = horizon + 4 + rnd.NextDouble() * Math.Max(1, waterH - 8);
                    AddRect(rc, x, y, 4 + rnd.NextDouble() * 22, 1);
                }
                for (int k = 0; k < 5; k++)
                {
                    double y = horizon + 4 + k * 8;
                    if (y > h - 3) break;
                    double lw = 24 - k * 4;
                    AddRect(mc, moonX - lw / 2, y, lw, 1.3);
                }
            }
            rip.Freeze(); moon.Freeze();
            dc.DrawGeometry(Ripple, null, rip);
            dc.DrawGeometry(MoonRefl, null, moon);
            dc.DrawRectangle(Shore, null, new Rect(0, horizon, w, 1));
        }

        private static void DrawTower(DrawingContext dc, double x, double ground, double hs, List<(Point P, double Phase)> beacons)
        {
            double height = 92 * hs;
            double deckY = ground - height * 0.66;
            var shaft = new StreamGeometry();
            using (var c = shaft.Open())
            {
                c.BeginFigure(new Point(x - 7 * hs, ground + 1), true, true);
                c.LineTo(new Point(x - 2.2 * hs, deckY), true, false);
                c.LineTo(new Point(x + 2.2 * hs, deckY), true, false);
                c.LineTo(new Point(x + 7 * hs, ground + 1), true, false);
            }
            shaft.Freeze();
            dc.DrawGeometry(TowerBrush, null, shaft);
            dc.DrawEllipse(TowerBrush, null, new Point(x, deckY), 10 * hs, 3.4 * hs);
            dc.DrawEllipse(Win[1, 0], null, new Point(x, deckY), 8 * hs, 1.1 * hs);
            dc.DrawLine(SpirePen, new Point(x, deckY), new Point(x, ground - height));
            beacons.Add((new Point(x, ground - height), 0.0));
        }

        protected override void RenderScene(DrawingContext dc, double w, double h, double seconds)
        {
            if (_scene == null)
            {
                dc.DrawRectangle(Fallback, null, new Rect(0, 0, w, h));
                return;
            }

            bool scaled = _sceneW > 0 && _sceneH > 0 &&
                          (Math.Abs(w - _sceneW) > 0.5 || Math.Abs(h - _sceneH) > 0.5);
            if (scaled) dc.PushTransform(new ScaleTransform(w / _sceneW, h / _sceneH));

            dc.DrawDrawing(_scene);

            for (int i = 0; i < _twinkle.Length; i++)
            {
                double s = (Math.Sin(seconds * (0.9 + (i % 4) * 0.35) + _twinkle[i].Phase) + 1) * 0.5;
                int level = s < 0.33 ? 0 : s < 0.75 ? 1 : 2;
                double r = level == 2 ? 1.3 : 0.9;
                dc.DrawEllipse(Twinkle[level], null, _twinkle[i].P, r, r);
            }

            for (int i = 0; i < _beacons.Length; i++)
            {
                bool on = (seconds + _beacons[i].Phase) % 2.6 < 0.22;
                var p = _beacons[i].P;
                if (on)
                {
                    dc.DrawEllipse(BeaconGlow, null, p, 4.5, 4.5);
                    dc.DrawEllipse(BeaconCore, null, p, 0.9, 0.9);
                }
                else
                {
                    dc.DrawEllipse(BeaconDim, null, p, 0.8, 0.8);
                }
            }

            if (scaled) dc.Pop();
        }
    }
}
