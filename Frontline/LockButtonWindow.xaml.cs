using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FrontLineOverlay
{
    public partial class LockButtonWindow : Window
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr h, int i, int v);

        public void SetTip(string text) => Hit.ToolTip = text;

        public event Action? Clicked;

        public LockButtonWindow() => InitializeComponent();

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            const int GWL_EXSTYLE = -20;
            long ex = IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64()
                                       : GetWindowLong32(hwnd, GWL_EXSTYLE);
            ex |= 0x08000000L | 0x80L;
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(ex));
            else SetWindowLong32(hwnd, GWL_EXSTYLE, (int)ex);
        }

        private void Hit_MouseEnter(object s, MouseEventArgs e) => Pad.Opacity = 0.9;
        private void Hit_MouseLeave(object s, MouseEventArgs e) => Pad.Opacity = 0.25;
        private void Hit_MouseLeftButtonUp(object s, MouseButtonEventArgs e) => Clicked?.Invoke();
    }
}