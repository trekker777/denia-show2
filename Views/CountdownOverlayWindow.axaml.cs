using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.Runtime.InteropServices;

namespace libvlc_media.Views
{
    public partial class CountdownOverlayWindow : Window
    {
        private string _lastText = string.Empty;
        private double _lastFontSize;
        private PixelPoint _lastTopRight;
        private PixelPoint _lastPosition;

        public CountdownOverlayWindow()
        {
            InitializeComponent();
            IsHitTestVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Background = Brushes.Transparent;
            Opened += (_, _) => ApplyClickThrough();
        }

        public void SetDisplay(string text, double fontSize)
        {
            BadgeText.Text = text;
            BadgeText.FontSize = fontSize;
            InvalidateMeasure();
            InvalidateArrange();
            UpdateLayout();
        }

        public void FitTopRightOf(Visual target, string text, double designFontSize)
        {
            if (target.Bounds.Width < 2 || target.Bounds.Height < 2)
                return;

            var fontSize = Math.Clamp(designFontSize * (target.Bounds.Height / 1080.0), 12, 420);
            var topRight = target.PointToScreen(new Point(target.Bounds.Width, 0));
            if (text == _lastText
                && Math.Abs(fontSize - _lastFontSize) < 0.5
                && topRight == _lastTopRight
                && Position == _lastPosition)
                return;

            SetDisplay(text, fontSize);

            var topLevel = TopLevel.GetTopLevel(target);
            var targetScale = topLevel?.RenderScaling > 0 ? topLevel.RenderScaling : 1;
            var overlayScale = RenderScaling > 0 ? RenderScaling : targetScale;
            var widthPx = Math.Max(1, (int)Math.Round(Bounds.Width * overlayScale));
            var marginPx = Math.Max(6, (int)Math.Round(target.Bounds.Height * 0.035 * targetScale));
            var position = new PixelPoint(
                topRight.X - widthPx - marginPx,
                topRight.Y + marginPx);

            _lastText = text;
            _lastFontSize = fontSize;
            _lastTopRight = topRight;
            _lastPosition = position;
            Position = position;
        }

        private void ApplyClickThrough()
        {
            if (!OperatingSystem.IsWindows())
                return;

            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
                return;

            var ex = GetWindowLong(hwnd, GwlExStyle);
            SetWindowLong(
                hwnd,
                GwlExStyle,
                ex | WsExLayered | WsExTransparent | WsExNoActivate | WsExToolWindow);
        }

        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExLayered = 0x00080000;
        private const int WsExNoActivate = 0x08000000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
