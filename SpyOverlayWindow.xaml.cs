using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace GlassLinq.UIExplorer
{
    public partial class SpyOverlayWindow : Window
    {
        // --- Win32 Interop Constants to make the window truly click-through ---
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int GWL_EXSTYLE = -20;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        public SpyOverlayWindow()
        {
            InitializeComponent();
        }

        // This method fires right after the window handle (HWND) is created, 
        // but before the window is rendered on screen.
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Get the window handle
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            // Change the extended window style to include WS_EX_TRANSPARENT
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);
        }

        public void UpdatePosition(Rect rect, string elementName = "")
        {
            if (rect.IsEmpty) { Hide(); return; }

            // 1. Get the DPI scaling factor (e.g., 1.25 for 125% scaling)
            var source = PresentationSource.FromVisual(this);
            double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            // 2. Adjust the rectangle to WPF units by dividing by the scale
            HighlightBorder.Width = rect.Width / scaleX;
            HighlightBorder.Height = rect.Height / scaleY;

            // Position relative to the Virtual Screen (handles multiple monitors)
            Canvas.SetLeft(HighlightBorder, (rect.Left - SystemParameters.VirtualScreenLeft) / scaleX);
            Canvas.SetTop(HighlightBorder, (rect.Top - SystemParameters.VirtualScreenTop) / scaleY);

            HighlightBorder.Visibility = Visibility.Visible;

            if (!string.IsNullOrWhiteSpace(elementName))
            {
                InfoText.Text = elementName;
                InfoLabel.Visibility = Visibility.Visible;
                Canvas.SetLeft(InfoLabel, (rect.Left - SystemParameters.VirtualScreenLeft) / scaleX);
                Canvas.SetTop(InfoLabel, ((rect.Top - SystemParameters.VirtualScreenTop) / scaleY) - 35);
            }
        }

        public void Hide()
        {
            HighlightBorder.Visibility = Visibility.Collapsed;
            InfoLabel.Visibility = Visibility.Collapsed;
        }
    }
}