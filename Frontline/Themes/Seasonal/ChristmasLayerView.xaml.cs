using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FrontLineOverlay
{
    /// <summary>
    /// Camada de decoração natalina. O único código necessário é o da neve:
    /// uma animação de queda não pode usar "To" = altura da janela em XAML puro
    /// (Storyboards ficam congelados e não aceitam Binding), então a distância
    /// é calculada aqui. Sem timers por floco: são apenas DoubleAnimations,
    /// limitadas a 30 FPS. Um único DispatcherTimer só roda após redimensionar.
    /// </summary>
    public partial class ChristmasLayerView : UserControl
    {
        private const int SnowFps = 30;
        private const double StartY = -30;

        private readonly DispatcherTimer _resizeDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private double _animatedHeight;

        public ChristmasLayerView()
        {
            InitializeComponent();
            _resizeDebounce.Tick += (_, _) => { _resizeDebounce.Stop(); StartSnow(); };
            SizeChanged += OnSizeChanged;
            IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is true) StartSnow(); else StopSnow();
            };
            Unloaded += (_, _) => { _resizeDebounce.Stop(); StopSnow(); };
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!IsVisible) return;
            PositionFlakes();
            if (Math.Abs(e.NewSize.Height - _animatedHeight) > 24)
            {
                _resizeDebounce.Stop();
                _resizeDebounce.Start();
            }
        }

        // Tag do floco: "x dur fase amp"
        //   x = posição horizontal (0..1 da largura), dur = segundos para cruzar a tela,
        //   fase = progresso inicial (0..1), amp = oscilação horizontal em DIPs.
        private static bool TryParse(object? tag, out double x, out double dur, out double phase, out double amp)
        {
            x = dur = phase = amp = 0;
            if (tag is not string s) return false;
            var p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return p.Length == 4
                && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out dur)
                && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out phase)
                && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out amp);
        }

        private void PositionFlakes()
        {
            double w = SnowCanvas.ActualWidth;
            foreach (UIElement child in SnowCanvas.Children)
                if (child is FrameworkElement fe && TryParse(fe.Tag, out var x, out _, out _, out _))
                    Canvas.SetLeft(fe, x * w);
        }

        private void StartSnow()
        {
            double h = SnowCanvas.ActualHeight;
            if (h < 40) return;
            _animatedHeight = h;
            PositionFlakes();

            foreach (UIElement child in SnowCanvas.Children)
            {
                if (child is not FrameworkElement fe || !TryParse(fe.Tag, out _, out var dur, out var phase, out var amp))
                    continue;

                if (fe.RenderTransform is not TranslateTransform t || t.IsFrozen)
                {
                    t = new TranslateTransform(0, StartY);
                    fe.RenderTransform = t;
                }

                var fall = new DoubleAnimation(StartY, h + 10, TimeSpan.FromSeconds(dur))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromSeconds(-phase * dur)   // flocos já espalhados ao iniciar
                };
                Timeline.SetDesiredFrameRate(fall, SnowFps);

                var sway = new DoubleAnimation(-amp, amp, TimeSpan.FromSeconds(dur / 4))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromSeconds(-phase * dur / 4),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                Timeline.SetDesiredFrameRate(sway, SnowFps);

                t.BeginAnimation(TranslateTransform.YProperty, fall);
                t.BeginAnimation(TranslateTransform.XProperty, sway);
            }
        }

        private void StopSnow()
        {
            foreach (UIElement child in SnowCanvas.Children)
                if (child.RenderTransform is TranslateTransform { IsFrozen: false } t)
                {
                    t.BeginAnimation(TranslateTransform.YProperty, null);
                    t.BeginAnimation(TranslateTransform.XProperty, null);
                }
        }
    }
}
