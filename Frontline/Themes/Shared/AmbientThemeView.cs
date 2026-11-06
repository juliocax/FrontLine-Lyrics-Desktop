using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FrontLineOverlay
{
    public abstract class AmbientThemeView : UserControl
    {
        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _clock = new();
        private readonly DispatcherTimer _resizeTimer;
        private double _sceneWidth = -1, _sceneHeight = -1;
        protected virtual int FramesPerSecond => 18;
        protected virtual bool HasAnimation => true;
        private Window? _owner;
        private RectangleGeometry? _clip;

        protected AmbientThemeView()
        {
            IsHitTestVisible = false;
            Focusable = false;
            ClipToBounds = true;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(FramesPerSecond, 1, 30)) };
            _timer.Tick += (_, _) => InvalidateVisual();
            _resizeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
            _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); RebuildIfNeeded(); };
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += (_, _) => UpdateClock();
            SizeChanged += (_, _) =>
            {
                _clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)), 11, 11);
                _clip.Freeze();
                _resizeTimer.Stop();
                if (IsLoaded && IsVisible && _owner?.WindowState != WindowState.Minimized) _resizeTimer.Start();
                InvalidateVisual();
            };
        }
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_owner != null) _owner.StateChanged -= OnOwnerStateChanged;
            _owner = Window.GetWindow(this);
            if (_owner != null) _owner.StateChanged += OnOwnerStateChanged;
            _resizeTimer.Stop();
            RebuildIfNeeded();
            UpdateClock();
        }
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _timer.Stop(); _resizeTimer.Stop(); _clock.Stop();
            if (_owner != null) _owner.StateChanged -= OnOwnerStateChanged;
            _owner = null;
        }
        private void OnOwnerStateChanged(object? sender, EventArgs e) => UpdateClock();
        private void UpdateClock()
        {
            bool shown = IsLoaded && IsVisible && _owner?.WindowState != WindowState.Minimized;
            if (shown && (Math.Abs(_sceneWidth - ActualWidth) > 0.5 || Math.Abs(_sceneHeight - ActualHeight) > 0.5))
                _resizeTimer.Start();
            if (shown && HasAnimation && SystemParameters.ClientAreaAnimation)
            { _clock.Start(); _timer.Start(); }
            else
            {
                _timer.Stop(); _clock.Stop();
                if (!shown) _resizeTimer.Stop();
            }
            InvalidateVisual();
        }
        private void RebuildIfNeeded()
        {
            if (!IsLoaded || !IsVisible || _owner?.WindowState == WindowState.Minimized || ActualWidth <= 0 || ActualHeight <= 0) return;
            if (Math.Abs(_sceneWidth - ActualWidth) <= 0.5 && Math.Abs(_sceneHeight - ActualHeight) <= 0.5) return;
            RebuildScene(ActualWidth, ActualHeight);
            _sceneWidth = ActualWidth; _sceneHeight = ActualHeight;
            InvalidateVisual();
        }
        protected sealed override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            if (_clip != null) dc.PushClip(_clip);
            RenderScene(dc, ActualWidth, ActualHeight, _clock.Elapsed.TotalSeconds);
            if (_clip != null) dc.Pop();
        }
        protected virtual void RebuildScene(double width, double height) { }
        protected abstract void RenderScene(DrawingContext dc, double width, double height, double seconds);

        protected static SolidColorBrush Solid(uint argb)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze(); return b;
        }
        protected static Pen Stroke(uint argb, double width)
        { var p = new Pen(Solid(argb), width); p.Freeze(); return p; }
        protected static LinearGradientBrush Gradient(uint top, uint bottom)
        {
            var b = new LinearGradientBrush(((SolidColorBrush)Solid(top)).Color, ((SolidColorBrush)Solid(bottom)).Color, new Point(0, 0), new Point(0, 1));
            b.Freeze(); return b;
        }
        protected static RadialGradientBrush Glow(uint argb)
        {
            Color c = Solid(argb).Color;
            var b = new RadialGradientBrush(c, Color.FromArgb(0, c.R, c.G, c.B));
            b.Freeze(); return b;
        }
        protected static Geometry Shape(string path)
        { Geometry g = Geometry.Parse(path); g.Freeze(); return g; }
    }

    public sealed class AmbientThemeFrameView : UserControl
    {
        private readonly Pen _pen;
        public AmbientThemeFrameView(Color left, Color right)
        {
            IsHitTestVisible = false; Focusable = false; ClipToBounds = true;
            var brush = new LinearGradientBrush(left, right, new Point(0, 0), new Point(1, 0));
            brush.Freeze(); _pen = new Pen(brush, 1); _pen.Freeze();
            SizeChanged += (_, _) => InvalidateVisual();
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (ActualWidth > 6 && ActualHeight > 6)
                dc.DrawRoundedRectangle(null, _pen, new Rect(1.5, 1.5, ActualWidth - 3, ActualHeight - 3), 11, 11);
        }
    }
}
