using System;
using System.Windows;
using System.Windows.Media;

namespace FrontLineOverlay
{
    public sealed class CozyCatsGroundView : AmbientThemeView
    {
        private readonly Brush _background = Gradient(0xFF18121D, 0xFF24182C);
        private readonly Brush _floor = Gradient(0xFF49304D, 0xFF281C31);
        private readonly Brush _fur = Gradient(0xFFB794C6, 0xFF7A5486);
        private readonly Brush _muzzle = Gradient(0xFFEAD3EC, 0xFFCDA8D0);
        private readonly Brush _paw = Gradient(0xFFDDB7DE, 0xFFA97DB3);
        private readonly Brush _ear = Gradient(0xFFEFB3CF, 0xFFC882B2);
        private readonly Brush _nose = Solid(0xFFEE98BA);
        private readonly Brush _pawPrint = Solid(0x80BE9DD1);
        private readonly Brush _body = Gradient(0xFFB48FC4, 0xFF724D81);
        private readonly Brush _belly = Gradient(0xFFD3B5DB, 0xFFA77FB3);
        private readonly Brush _haunch = Gradient(0xFFBB97CA, 0xFF7E5A8C);
        private readonly Brush _shadow = Solid(0x550B0810);
        private readonly Brush _highlight = Glow(0x38FFFFFF);
        private readonly Brush _blush = Solid(0x55F2A0C4);
        private readonly Brush _tailTip = Gradient(0xFFE2C6E6, 0xFFC7A0CE);

        private static readonly (double X, double Y, double Angle)[] FloatingPaws =
        {
            (0.08, 0.22, -18), (0.28, 0.15, 16), (0.52, 0.18, -20),
            (0.83, 0.18, 12), (0.95, 0.36, 20), (0.72, 0.42, -12),
            (0.46, 0.63, -22), (0.16, 0.65, 10), (0.38, 0.82, -6),
            (0.67, 0.76, 18), (0.89, 0.68, -18), (0.58, 0.36, 12),
        };

        private readonly Brush _sparkGlow = Glow(0xB0F8C99E);
        private readonly Brush _spark = Solid(0xFFEFC9A9);
        private readonly Brush _yarn = Gradient(0xFFE8A6B4, 0xFFAC6E91);

        private readonly Pen _faceLine = Stroke(0xFF4A2E4B, 2.0);
        private readonly Pen _mouthLine = Stroke(0xFF5A3A5C, 1.3);
        private readonly Pen _whisker = Stroke(0xAAE4BDD8, 0.7);
        private readonly Pen _whiskerFace = Stroke(0xCCF0D2E8, 1.0);
        private readonly Pen _toeLine = Stroke(0x88704A7C, 1.0);
        private readonly Pen _stripe = Stroke(0x884E2F5C, 3.2);
        private readonly Pen _foreheadStripe = Stroke(0xAA4E2F5C, 2.2);
        private readonly Pen _yarnLine = Stroke(0xAAC784A2, 1.3);
        private readonly Pen _strand = Stroke(0xFFC488A4, 2.2);
        private readonly Pen _zPen = Stroke(0xFFE9C9F0, 1.8);
        private readonly Pen _tail = TailPen();

        private readonly Geometry _head = Shape("M-46,6 C-49,-24 -27,-40 0,-40 C27,-40 49,-24 46,6 C47,30 27,40 0,40 C-27,40 -47,30 -46,6 Z");
        private readonly Geometry _earL = Shape("M-43,-8 Q-46,-46 -33,-55 Q-19,-47 -9,-36 Z");
        private readonly Geometry _earR = Shape("M43,-8 Q46,-46 33,-55 Q19,-47 9,-36 Z");
        private readonly Geometry _earLInner = Shape("M-37,-17 Q-39,-40 -33,-47 Q-25,-41 -19,-34 Z");
        private readonly Geometry _earRInner = Shape("M37,-17 Q39,-40 33,-47 Q25,-41 19,-34 Z");
        private readonly Geometry _closedEyes = Shape("M-29,2 Q-21,10 -13,2 M13,2 Q21,10 29,2");
        private readonly Geometry _noseShape = Shape("M-5.5,8 Q0,5 5.5,8 L0,14 Z");
        private readonly Geometry _mouth = Shape("M0,14 L0,18 M0,18 Q-5,23 -10,19 M0,18 Q5,23 10,19");
        private readonly Geometry _foreheadMarks = Shape("M0,-39 L0,-27 M-9,-38 L-7,-28 M9,-38 L7,-28");
        private readonly Geometry _whiskersL = Shape("M-27,14 Q-45,9 -60,12 M-27,19 Q-45,20 -60,25 M-28,24 Q-42,30 -54,35");
        private readonly Geometry _whiskersR = Shape("M27,14 Q45,9 60,12 M27,19 Q45,20 60,25 M28,24 Q42,30 54,35");

        private readonly Geometry _tailPath = Shape("M169,108 C217,129 252,109 245,82 C242,67 225,60 218,70");
        private readonly Geometry _backStripes = Shape("M128,60 Q134,70 131,80 M148,57 Q155,68 151,79 M169,57 Q176,68 172,79 M189,61 Q195,71 191,81");
        private readonly Geometry _toes = Shape("M-5,-2 L-5,6 M2,-3 L2,7 M9,-2 L9,6");
        private readonly Geometry _zShape = Shape("M-4,-5 L4,-5 L-4,5 L4,5");

        private readonly Geometry _yarnThreads = Shape("M-14,-10 Q6,-7 12,10 M-18,-2 Q0,2 15,16 M-7,-17 Q13,-4 16,8 M-15,9 Q-4,-9 7,-18 M-6,17 Q3,0 17,-8 M-18,3 Q-4,14 9,17");
        private Geometry? _hill;
        private double _hillWidth, _hillHeight;
        private static double Floor(double h) => h - Math.Clamp(h * 0.14, 60, 85);

        protected override void RebuildScene(double w, double h)
        {
            if (w <= 0 || h <= 0) return;
            double floor = Floor(h);
            var geometry = new StreamGeometry();
            using (StreamGeometryContext c = geometry.Open())
            {
                c.BeginFigure(new Point(0, floor + 10), true, true);
                c.BezierTo(new Point(w * 0.20, floor - 20), new Point(w * 0.31, floor + 24), new Point(w * 0.48, floor + 17), true, false);
                c.BezierTo(new Point(w * 0.69, floor + 2), new Point(w * 0.82, floor - 7), new Point(w, floor + 10), true, false);
                c.LineTo(new Point(w, h), true, false);
                c.LineTo(new Point(0, h), true, false);
            }
            geometry.Freeze(); _hill = geometry;
            _hillWidth = w; _hillHeight = h;
        }

        protected override void RenderScene(DrawingContext dc, double w, double h, double seconds)
        {
            dc.DrawRectangle(_background, null, new Rect(0, 0, w, h));
            if (_hill != null && _hillWidth > 0 && _hillHeight > 0)
            {
                dc.PushTransform(new ScaleTransform(w / _hillWidth, h / _hillHeight));
                dc.DrawGeometry(_floor, null, _hill);
                dc.Pop();
            }
            double scale = Math.Clamp(w / 1200, 0.50, 1.10) * Math.Clamp(h / 500, 0.70, 1.0);
            double floor = Floor(h);
            DrawPawPrint(dc, w * 0.28, floor + 15, scale * 0.65, -12);
            DrawPawPrint(dc, w * 0.70, floor + 6, scale * 0.60, 18);
            DrawYarn(dc, w * 0.46, floor + 13, scale);
            DrawCat(dc, w - 260 * scale - 15, floor - 112 * scale, scale, seconds);
            DrawFloatingPaws(dc, w, h, seconds);
            for (int i = 0; i < 7; i++)
            {
                double x = w * (0.04 + i * 0.15);
                double y = floor - 15 - (i % 3) * 9;
                double opacity = 0.15 + 0.28 * (Math.Sin(seconds * 0.7 + i * 1.9) + 1) / 2;
                dc.PushOpacity(opacity);
                dc.DrawEllipse(_sparkGlow, null, new Point(x, y), 5, 5);
                dc.DrawEllipse(_spark, null, new Point(x, y), 0.9, 0.9);
                dc.Pop();
            }
        }

        private void DrawFloatingPaws(DrawingContext dc, double w, double h, double seconds)
        {
            double scale = Math.Clamp(w / 1000.0, 0.7, 1.8) * 1.4;
            for (int i = 0; i < FloatingPaws.Length; i++)
            {
                double phase = (seconds + i * 2.1) % 26.0;
                if (phase >= 6.0) continue;
                double opacity = Math.Pow(Math.Sin(phase * Math.PI / 6.0), 2) * 0.62;
                if (opacity < 0.01) continue;
                var stamp = FloatingPaws[i];
                if (stamp.X > 0.40 && stamp.X < 0.93 && stamp.Y > 0.28 && stamp.Y < 0.67)
                    opacity *= 0.22;
                dc.PushOpacity(opacity);
                DrawPawPrint(dc, stamp.X * w, stamp.Y * h, scale * (0.9 + (i % 3) * 0.12), stamp.Angle);
                dc.Pop();
            }
        }

        private void DrawCat(DrawingContext dc, double x, double y, double scale, double seconds)
        {
            dc.PushTransform(new TranslateTransform(x, y));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawEllipse(_shadow, null, new Point(132, 122), 120, 7);

            dc.PushTransform(new ScaleTransform(1, 1 + Math.Sin(seconds * 0.85) * 0.012, 132, 122));

            dc.PushTransform(new RotateTransform(Math.Sin(seconds * 0.45) * 2.5, 169, 108));
            dc.DrawGeometry(null, _tail, _tailPath);
            dc.DrawEllipse(_tailTip, null, new Point(218, 70), 7.5, 7.5);
            dc.Pop();

            dc.DrawEllipse(_body, null, new Point(160, 90), 72, 34);
            dc.PushOpacity(0.55);
            dc.DrawGeometry(null, _stripe, _backStripes);
            dc.Pop();
            dc.PushOpacity(0.5);
            dc.DrawEllipse(_belly, null, new Point(148, 102), 52, 18);
            dc.Pop();
            dc.PushOpacity(0.5);
            dc.DrawEllipse(_highlight, null, new Point(150, 68), 46, 12);
            dc.Pop();

            dc.DrawEllipse(_haunch, null, new Point(206, 93), 35, 27);
            dc.PushOpacity(0.5);
            dc.DrawEllipse(_highlight, null, new Point(204, 80), 22, 10);
            dc.Pop();
            dc.DrawEllipse(_paw, null, new Point(214, 114), 21, 9);

            DrawHead(dc, seconds);

            dc.DrawEllipse(_paw, null, new Point(56, 111), 21, 9.5);
            dc.DrawEllipse(_paw, null, new Point(94, 113), 20, 9);
            DrawToes(dc, 56, 111);
            DrawToes(dc, 94, 113);

            DrawZzz(dc, seconds);

            dc.Pop(); dc.Pop(); dc.Pop();
        }

        private void DrawHead(DrawingContext dc, double seconds)
        {
            dc.PushTransform(new TranslateTransform(68, 72 + Math.Sin(seconds * 0.85 + 0.6) * 0.8));
            dc.PushTransform(new RotateTransform(-7));

            dc.DrawGeometry(_fur, null, _earL);
            dc.DrawGeometry(_ear, null, _earLInner);
            double phase = seconds % 7.0;
            double twitch = phase < 0.35 ? Math.Sin(phase / 0.35 * Math.PI) * 8 : 0;
            dc.PushTransform(new RotateTransform(twitch, 28, -30));
            dc.DrawGeometry(_fur, null, _earR);
            dc.DrawGeometry(_ear, null, _earRInner);
            dc.Pop();

            dc.DrawGeometry(_fur, null, _head);
            dc.DrawEllipse(_highlight, null, new Point(-10, -22), 30, 15);
            dc.DrawGeometry(null, _foreheadStripe, _foreheadMarks);

            dc.DrawEllipse(_muzzle, null, new Point(0, 17), 21, 14);
            dc.DrawEllipse(_blush, null, new Point(-31, 15), 9, 5.5);
            dc.DrawEllipse(_blush, null, new Point(31, 15), 9, 5.5);

            dc.DrawGeometry(null, _faceLine, _closedEyes);
            dc.DrawGeometry(_nose, null, _noseShape);
            dc.DrawGeometry(null, _mouthLine, _mouth);
            dc.DrawGeometry(null, _whiskerFace, _whiskersL);
            dc.DrawGeometry(null, _whiskerFace, _whiskersR);

            dc.Pop(); dc.Pop();
        }

        private void DrawToes(DrawingContext dc, double x, double y)
        {
            dc.PushTransform(new TranslateTransform(x, y + 1));
            dc.DrawGeometry(null, _toeLine, _toes);
            dc.Pop();
        }

        private void DrawZzz(DrawingContext dc, double seconds)
        {
            for (int i = 0; i < 3; i++)
            {
                double t = (seconds * 0.22 + i / 3.0) % 1.0;
                double x = 112 + t * 26 + Math.Sin(seconds * 1.2 + i) * 2;
                double y = 34 - t * 44;
                double size = 0.7 + t * 0.9;
                double opacity = Math.Sin(t * Math.PI) * 0.75;
                if (opacity < 0.02) continue;
                dc.PushOpacity(opacity);
                dc.PushTransform(new TranslateTransform(x, y));
                dc.PushTransform(new ScaleTransform(size, size));
                dc.DrawGeometry(null, _zPen, _zShape);
                dc.Pop(); dc.Pop(); dc.Pop();
            }
        }

        private void DrawYarn(DrawingContext dc, double x, double y, double scale)
        {
            dc.PushTransform(new TranslateTransform(x, y));
            dc.PushTransform(new ScaleTransform(scale, scale));
            // Static short strand and ball; no additional characters on the left.
            dc.DrawLine(_strand, new Point(-115, 12), new Point(-84, 8));
            dc.DrawLine(_strand, new Point(-84, 8), new Point(-51, 17));
            dc.DrawLine(_strand, new Point(-51, 17), new Point(-16, 6));
            dc.DrawEllipse(_yarn, null, new Point(0, 0), 18, 18);
            dc.PushClip(new EllipseGeometry(new Point(0, 0), 18, 18));
            dc.DrawGeometry(null, _yarnLine, _yarnThreads);
            dc.Pop(); dc.Pop(); dc.Pop();
        }

        private void DrawPawPrint(DrawingContext dc, double x, double y, double scale, double angle)
        {
            dc.PushTransform(new TranslateTransform(x, y));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new RotateTransform(angle));
            dc.DrawEllipse(_pawPrint, null, new Point(0, 4), 6, 4.5);
            dc.DrawEllipse(_pawPrint, null, new Point(-7, -2), 2.3, 3);
            dc.DrawEllipse(_pawPrint, null, new Point(-2.5, -6), 2.3, 3);
            dc.DrawEllipse(_pawPrint, null, new Point(3, -6), 2.3, 3);
            dc.DrawEllipse(_pawPrint, null, new Point(7.5, -1), 2.3, 3);
            dc.Pop(); dc.Pop(); dc.Pop();
        }

        private static Pen TailPen()
        {
            var pen = new Pen(Gradient(0xFFA682B6, 0xFF74508A), 15)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze(); return pen;
        }
    }
}
