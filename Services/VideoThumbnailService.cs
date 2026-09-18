using LibVLCSharp.Shared;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace libvlc_media.Services
{
    public sealed class VideoThumbnailService : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        public async Task<string?> CaptureToFileAsync(string videoPath, int decodeWidth, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(videoPath))
                return null;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await Task.Run(() => CaptureCore(videoPath, decodeWidth), cancellationToken);
            }
            catch
            {
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static string? CaptureCore(string videoPath, int decodeWidth)
        {
            var output = Path.Combine(Path.GetTempPath(), $"libvlc-thumb-{Guid.NewGuid():N}.png");
            var hwnd = CreateOffscreenWindow();
            LibVLC? lib = null;
            Media? media = null;
            MediaPlayer? player = null;

            try
            {
                lib = new LibVLC("--intf", "dummy", "--vout", "wingdi", "--no-audio", "--quiet", "--no-video-title-show");
                media = new Media(lib, videoPath, FromType.FromPath);
                player = new MediaPlayer(media)
                {
                    Mute = true,
                    Volume = 0,
                    EnableHardwareDecoding = false
                };

                if (hwnd != IntPtr.Zero)
                    player.Hwnd = hwnd;

                var taken = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                player.SnapshotTaken += (_, _) => taken.TrySetResult(true);
                player.EncounteredError += (_, _) => taken.TrySetResult(false);

                if (!player.Play())
                    return null;

                var started = DateTime.UtcNow;
                while (player.Time <= 0 && DateTime.UtcNow - started < TimeSpan.FromSeconds(2))
                    Thread.Sleep(50);

                player.SetPause(true);
                Thread.Sleep(80);

                var width = (uint)Math.Clamp(decodeWidth, 160, 800);
                if (!player.TakeSnapshot(0, output, width, 0))
                    return null;

                taken.Task.Wait(TimeSpan.FromSeconds(2));
                player.Stop();

                if (!File.Exists(output) || new FileInfo(output).Length == 0)
                {
                    try
                    {
                        File.Delete(output);
                    }
                    catch
                    {
                        // ignored
                    }

                    return null;
                }

                return output;
            }
            finally
            {
                try
                {
                    player?.Stop();
                }
                catch
                {
                    // ignored
                }

                player?.Dispose();
                media?.Dispose();
                lib?.Dispose();
                if (hwnd != IntPtr.Zero)
                    DestroyWindow(hwnd);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _gate.Dispose();
        }

        private static IntPtr CreateOffscreenWindow()
        {
            try
            {
                const int wsPopup = unchecked((int)0x80000000);
                const int wsExNoActivate = 0x08000000;
                const int wsExToolWindow = 0x00000080;
                return CreateWindowEx(
                    wsExNoActivate | wsExToolWindow,
                    "STATIC",
                    string.Empty,
                    wsPopup,
                    -4000,
                    -4000,
                    320,
                    180,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    GetModuleHandle(null),
                    IntPtr.Zero);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
