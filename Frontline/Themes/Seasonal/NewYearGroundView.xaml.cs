using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FrontLineOverlay
{
    public partial class NewYearGroundView : UserControl
    {
        private readonly TranslateTransform _pulseA = new TranslateTransform();
        private readonly TranslateTransform _pulseB = new TranslateTransform();
        private readonly DispatcherTimer _pulseTimer;
        private readonly Stopwatch _pulseClock = new Stopwatch();
        private Window? _ownerWindow;
        private bool _pulseLoaded;
        private double _pulseSeconds = 1.4;

        public NewYearGroundView()
        {
            InitializeComponent();


            var first = CreateGoldPulse(_pulseA, 440);
            GoldPulseAHalo.Stroke = first;
            GoldPulseAGlow.Stroke = first;
            GoldPulseACore.Stroke = first;
            var second = CreateGoldPulse(_pulseB, 400);
            GoldPulseBHalo.Stroke = second;
            GoldPulseBGlow.Stroke = second;
            GoldPulseBCore.Stroke = second;
            UpdatePulsePositions();

            _pulseTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / 30.0)
            };
            _pulseTimer.Tick += PulseTimer_Tick;
            Loaded += Pulse_Loaded;
            Unloaded += Pulse_Unloaded;
            IsVisibleChanged += (_, _) => UpdatePulseActivity();
        }

        private static LinearGradientBrush CreateGoldPulse(TranslateTransform transform, double width)
        {
            var brush = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(width, 0),
                Transform = transform,
                SpreadMethod = GradientSpreadMethod.Pad
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xD2, 0x3F), 0.00));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x18, 0xFF, 0xD2, 0x3F), 0.15));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x80, 0xFF, 0xD2, 0x3F), 0.34));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xD2, 0x3F), 0.54));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xCF, 0xFF, 0xD2, 0x3F), 0.70));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x40, 0xFF, 0xD2, 0x3F), 0.86));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xD2, 0x3F), 1.00));
            return brush;
        }

        private void Pulse_Loaded(object sender, RoutedEventArgs e)
        {
            _pulseLoaded = true;
            AttachPulseWindow(Window.GetWindow(this));
            UpdatePulseActivity();
        }

        private void Pulse_Unloaded(object sender, RoutedEventArgs e)
        {
            _pulseLoaded = false;
            UpdatePulseActivity();
            AttachPulseWindow(null);
        }

        private void AttachPulseWindow(Window? window)
        {
            if (ReferenceEquals(_ownerWindow, window)) return;
            if (_ownerWindow != null)
                _ownerWindow.StateChanged -= PulseWindow_StateChanged;
            _ownerWindow = window;
            if (_ownerWindow != null)
                _ownerWindow.StateChanged += PulseWindow_StateChanged;
        }

        private void PulseWindow_StateChanged(object? sender, EventArgs e) => UpdatePulseActivity();

        private void UpdatePulseActivity()
        {
            bool active = _pulseLoaded && IsVisible &&
                _ownerWindow?.WindowState != WindowState.Minimized && SystemParameters.ClientAreaAnimation;
            if (active)
            {
                if (_pulseTimer.IsEnabled) return;
                _pulseClock.Restart();
                _pulseTimer.Start();
            }
            else
            {
                _pulseTimer.Stop();
                _pulseClock.Stop();
            }
        }

        private void PulseTimer_Tick(object? sender, EventArgs e)
        {
            double seconds = Math.Min(_pulseClock.Elapsed.TotalSeconds, 0.1);
            _pulseClock.Restart();
            _pulseSeconds += seconds;
            UpdatePulsePositions();
        }

        private void UpdatePulsePositions()
        {
            double phaseA = (_pulseSeconds / 9.6) % 1.0;
            double phaseB = (_pulseSeconds / 11.8 + 0.46) % 1.0;
            _pulseA.X = -460 + 1830 * phaseA;
            _pulseB.X = 1370 - 1830 * phaseB;
        }
    }
}
