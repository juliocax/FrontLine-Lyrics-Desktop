using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FrontLineOverlay
{
    // Native WPF drawing: one 30 FPS clock, no bitmap/video assets or audio polling.
    public sealed class NeonNightsGroundView : UserControl
    {
        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _clock = new();
        private readonly Particle[] _particles = new Particle[24];
        private readonly StreamGeometry[] _waves = new StreamGeometry[3];
        private readonly Pen[] _tails = new Pen[10];
        private readonly RadialGradientBrush _glow = Glow(Color.FromRgb(255, 20, 147));
        private readonly SolidColorBrush _pink = Frozen(Color.FromRgb(255, 20, 147));
        private readonly SolidColorBrush _head = Frozen(Color.FromRgb(255, 170, 218));
        private readonly Pen _spark = FrozenPen(Color.FromArgb(200, 255, 100, 192), 0.9);
        private readonly Pen _wide = FrozenPen(Color.FromArgb(16, 255, 20, 147), 15);
        private readonly Pen _soft = FrozenPen(Color.FromArgb(36, 255, 20, 147), 6);
        private readonly Pen _line = FrozenPen(Color.FromArgb(205, 255, 20, 147), 1.15);
        private readonly Pen _faint = FrozenPen(Color.FromArgb(65, 255, 20, 147), 0.85);
        private Window? _owner;
        private double _width, _height;

        private readonly struct Particle
        {
            public Particle(double x, double phase, double period, double size, bool sparkle)
            { X = x; Phase = phase; Period = period; Size = size; Sparkle = sparkle; }
            public double X { get; }
            public double Phase { get; }
            public double Period { get; }
            public double Size { get; }
            public bool Sparkle { get; }
        }

        public NeonNightsGroundView()
        {
            IsHitTestVisible = false;
            Focusable = false;
            ClipToBounds = true;
            var random = new Random(1937);
            for (int i = 0; i < _particles.Length; i++)
                _particles[i] = new Particle(0.025 + random.NextDouble() * 0.95,
                    random.NextDouble(), 18 + random.NextDouble() * 18,
                    0.7 + random.NextDouble() * 1.3, i % 7 == 0);
            for (int i = 0; i < _tails.Length; i++)
                _tails[i] = FrozenPen(Color.FromArgb((byte)(20 + i * 22), 255, 70, 170), 2.0);
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.0 / 30.0) };
            _timer.Tick += (_, _) => InvalidateVisual();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += (_, _) => UpdateClock();
            SizeChanged += (_, _) => { RebuildWaves(); InvalidateVisual(); };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_owner != null) _owner.StateChanged -= OnOwnerStateChanged;
            _owner = Window.GetWindow(this);
            if (_owner != null) _owner.StateChanged += OnOwnerStateChanged;
            RebuildWaves();
            UpdateClock();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            _clock.Stop();
            if (_owner != null) _owner.StateChanged -= OnOwnerStateChanged;
            _owner = null;
        }

        private void OnOwnerStateChanged(object? sender, EventArgs e) => UpdateClock();

        private void UpdateClock()
        {
            bool animate = IsLoaded && IsVisible && _owner?.WindowState != WindowState.Minimized
                && SystemParameters.ClientAreaAnimation;
            if (animate) { _clock.Start(); _timer.Start(); }
            else { _timer.Stop(); _clock.Stop(); }
            InvalidateVisual();
        }

        private Point WavePoint(double u, int wave)
        {
            double band = Math.Clamp(_height * 0.19, 64, 105);
            double amplitude = Math.Clamp(_height * 0.045, 10, 24);
            double y = _height - band + Math.Sin(u * Math.PI * 3.0 + wave * 0.85) * amplitude
                + Math.Sin(u * Math.PI * 5.0 + wave) * amplitude * 0.28 + wave * 6;
            return new Point(u * _width, y);
        }

        private void RebuildWaves()
        {
            _width = ActualWidth;
            _height = ActualHeight;
            if (_width <= 0 || _height <= 0) return;
            for (int wave = 0; wave < _waves.Length; wave++)
            {
                var geometry = new StreamGeometry();
                using (StreamGeometryContext c = geometry.Open())
                {
                    c.BeginFigure(WavePoint(0, wave), false, false);
                    for (int step = 1; step <= 100; step++)
                        c.LineTo(WavePoint(step / 100.0, wave), true, false);
                }
                geometry.Freeze();
                _waves[wave] = geometry;
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (_width <= 0 || _height <= 0) return;
            double seconds = _clock.Elapsed.TotalSeconds;
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, _width, _height), 11, 11));
            dc.PushOpacity(0.12);
            dc.DrawEllipse(_glow, null, new Point(_width * 0.12, _height * 0.85), _width * 0.4, _height * 0.6);
            dc.DrawEllipse(_glow, null, new Point(_width * 0.88, _height * 0.88), _width * 0.35, _height * 0.5);
            dc.Pop();
            for (int wave = 0; wave < _waves.Length; wave++)
            {
                if (_waves[wave] == null) continue;
                dc.DrawGeometry(null, _wide, _waves[wave]);
                dc.DrawGeometry(null, _soft, _waves[wave]);
                dc.DrawGeometry(null, wave == 0 ? _line : _faint, _waves[wave]);
            }
            // Pulses move along the SAME analytic curves used by the cached wave geometry.
            for (int pulse = 0; pulse < 3; pulse++)
            {
                int wave = pulse % 2;
                double u = (seconds / (10.0 + pulse * 2.0) + pulse * 0.31) % 1.0;
                Point head = WavePoint(u, wave);
                for (int tail = 0; tail < _tails.Length; tail++)
                {
                    double from = u - (_tails.Length - tail) * 0.0038;
                    double to = from + 0.0038;
                    if (from >= 0 && to <= 1)
                        dc.DrawLine(_tails[tail], WavePoint(from, wave), WavePoint(to, wave));
                }
                dc.DrawEllipse(_glow, null, head, 12, 12);
                dc.DrawEllipse(_head, null, head, 2.3, 2.3);
            }
            foreach (Particle particle in _particles)
            {
                double phase = (seconds / particle.Period + particle.Phase) % 1.0;
                double x = particle.X * _width + Math.Sin(seconds * 0.3 + particle.Phase * 9) * 5;
                double y = _height * (0.87 - phase * 0.79);
                double opacity = Math.Sin(phase * Math.PI) * 0.65;
                // The center/right toolbar stays visually quiet.
                if (x > _width * 0.40 && x < _width * 0.93 && y > _height * 0.28 && y < _height * 0.67)
                    opacity *= 0.08;
                dc.PushOpacity(opacity);
                var point = new Point(x, y);
                dc.DrawEllipse(_glow, null, point, particle.Size * 4, particle.Size * 4);
                dc.DrawEllipse(_pink, null, point, particle.Size * 0.65, particle.Size * 0.65);
                if (particle.Sparkle)
                {
                    double radius = particle.Size * 3.2;
                    dc.DrawLine(_spark, new Point(x - radius, y), new Point(x + radius, y));
                    dc.DrawLine(_spark, new Point(x, y - radius), new Point(x, y + radius));
                }
                dc.Pop();
            }
            dc.Pop();
        }

        private static SolidColorBrush Frozen(Color color)
        { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

        private static Pen FrozenPen(Color color, double width)
        { var pen = new Pen(Frozen(color), width); pen.Freeze(); return pen; }

        private static RadialGradientBrush Glow(Color color)
        {
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(150, color.R, color.G, color.B), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(40, color.R, color.G, color.B), 0.35));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
            brush.Freeze();
            return brush;
        }
    }

    // Separate foreground layer: an inset pink rim without intercepting mouse input.
    public sealed class NeonNightsFrameView : UserControl
    {
        private readonly Pen _rim = MakePen(190, 1);
        private readonly Pen _glow = MakePen(28, 5);
        public NeonNightsFrameView()
        {
            IsHitTestVisible = false;
            Focusable = false;
            ClipToBounds = true;
            SizeChanged += (_, _) => InvalidateVisual();
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (ActualWidth < 10 || ActualHeight < 10) return;
            var rect = new Rect(2.5, 2.5, ActualWidth - 5, ActualHeight - 5);
            dc.DrawRoundedRectangle(null, _glow, rect, 11, 11);
            dc.DrawRoundedRectangle(null, _rim, rect, 11, 11);
        }
        private static Pen MakePen(byte alpha, double width)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 255, 20, 147));
            brush.Freeze();
            var pen = new Pen(brush, width); pen.Freeze(); return pen;
        }
    }
}
