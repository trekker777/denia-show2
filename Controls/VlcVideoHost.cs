using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using LibVLCSharp.Shared;
using System;
using System.Runtime.InteropServices;

namespace libvlc_media.Controls
{
    public sealed class VlcVideoHost : NativeControlHost
    {
        public static readonly DirectProperty<VlcVideoHost, MediaPlayer?> MediaPlayerProperty =
            AvaloniaProperty.RegisterDirect<VlcVideoHost, MediaPlayer?>(
                nameof(MediaPlayer),
                o => o.MediaPlayer,
                (o, v) => o.MediaPlayer = v);

        public static readonly StyledProperty<bool> NativeVisibleProperty =
            AvaloniaProperty.Register<VlcVideoHost, bool>(nameof(NativeVisible), defaultValue: false);

        private IPlatformHandle? _platformHandle;
        private MediaPlayer? _mediaPlayer;

        static VlcVideoHost()
        {
            NativeVisibleProperty.Changed.AddClassHandler<VlcVideoHost>((host, _) =>
            {
                host.IsHitTestVisible = host.NativeVisible;
                host.ApplyNativeVisibility();
                host.InvalidateMeasure();
                host.InvalidateArrange();
            });
        }

        public VlcVideoHost()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            IsHitTestVisible = false;
        }

        public MediaPlayer? MediaPlayer
        {
            get => _mediaPlayer;
            set
            {
                if (ReferenceEquals(_mediaPlayer, value))
                    return;

                ClearHandle();
                SetAndRaise(MediaPlayerProperty, ref _mediaPlayer, value);
                ApplyHandle();
            }
        }

        public bool NativeVisible
        {
            get => GetValue(NativeVisibleProperty);
            set => SetValue(NativeVisibleProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize) =>
            NativeVisible ? availableSize : default;

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (!NativeVisible)
                return default;

            var arranged = base.ArrangeOverride(finalSize);
            SyncNativeSize(arranged);
            return arranged;
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            _platformHandle = base.CreateNativeControlCore(parent);
            ApplyHandle();
            ApplyNativeVisibility();
            return _platformHandle;
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            ClearHandle();
            _platformHandle = null;
            base.DestroyNativeControlCore(control);
        }

        private void ApplyHandle()
        {
            if (_mediaPlayer is null || _platformHandle is null)
                return;

            if (OperatingSystem.IsWindows())
                _mediaPlayer.Hwnd = _platformHandle.Handle;
            else if (OperatingSystem.IsLinux())
                _mediaPlayer.XWindow = (uint)_platformHandle.Handle;
            else if (OperatingSystem.IsMacOS())
                _mediaPlayer.NsObject = _platformHandle.Handle;
        }

        private void ClearHandle()
        {
            if (_mediaPlayer is null)
                return;

            if (OperatingSystem.IsWindows())
                _mediaPlayer.Hwnd = IntPtr.Zero;
            else if (OperatingSystem.IsLinux())
                _mediaPlayer.XWindow = 0;
            else if (OperatingSystem.IsMacOS())
                _mediaPlayer.NsObject = IntPtr.Zero;
        }

        private void ApplyNativeVisibility()
        {
            if (_platformHandle is null || !OperatingSystem.IsWindows())
                return;

            var hwnd = _platformHandle.Handle;
            if (NativeVisible)
            {
                ShowWindow(hwnd, SwShow);
                SyncNativeSize(Bounds.Size);
            }
            else
            {
                ShowWindow(hwnd, SwHide);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoZOrder | SwpNoActivate | SwpNoMove);
            }
        }

        private void SyncNativeSize(Size dipSize)
        {
            if (!OperatingSystem.IsWindows() || _platformHandle is null || !NativeVisible)
                return;

            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            if (scaling <= 0)
                scaling = 1.0;
            var cx = Math.Max(0, (int)Math.Round(dipSize.Width * scaling));
            var cy = Math.Max(0, (int)Math.Round(dipSize.Height * scaling));
            SetWindowPos(
                _platformHandle.Handle,
                IntPtr.Zero,
                0,
                0,
                cx,
                cy,
                SwpNoZOrder | SwpNoActivate | SwpNoMove);
        }

        private const int SwHide = 0;
        private const int SwShow = 5;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);
    }
}
