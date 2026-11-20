using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace FocusStudy
{
    /// <summary>Covers a secondary monitor during a session so only the main screen is usable.</summary>
    internal sealed class BlackoutWindow : Window
    {
        public bool AllowClose { get; set; }

        public BlackoutWindow(System.Drawing.Rectangle bounds, Window main)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x15));
            Content = new TextBlock
            {
                Text = "Focus mode is active on your main screen.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x64, 0x78)),
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            SourceInitialized += (_, __) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            };
            Loaded += (_, __) => WindowState = WindowState.Maximized;
            PreviewMouseDown += (_, __) => main.Activate();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!AllowClose && !App.ForceClose) e.Cancel = true;
            base.OnClosing(e);
        }
    }
}
