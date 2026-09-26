using LibVLCSharp.Shared;
using System;
using System.Threading.Tasks;

namespace libvlc_media.Services
{
    public sealed class VlcPlaybackService : IDisposable
    {
        private Media? _previewMedia;
        private Media? _outputMedia;
        private bool _disposed;

        public VlcPlaybackService()
        {
            Core.Initialize();
            LibVLC = new LibVLC("--no-osd", "--quiet", "--no-video-title-show", "--autoscale");
            PreviewPlayer = new MediaPlayer(LibVLC)
            {
                EnableHardwareDecoding = false,
                Mute = false,
                Volume = 80,
                Scale = 0
            };
            OutputPlayer = new MediaPlayer(LibVLC)
            {
                EnableHardwareDecoding = false,
                Mute = true,
                Scale = 0
            };
        }

        public LibVLC LibVLC { get; }

        public MediaPlayer PreviewPlayer { get; }

        public MediaPlayer OutputPlayer { get; }

        public string? CurrentPath { get; private set; }

        public static bool HasVideoHost(MediaPlayer player)
        {
            if (OperatingSystem.IsWindows())
                return player.Hwnd != IntPtr.Zero;
            if (OperatingSystem.IsLinux())
                return player.XWindow != 0;
            if (OperatingSystem.IsMacOS())
                return player.NsObject != IntPtr.Zero;

            return player.Hwnd != IntPtr.Zero;
        }

        public static async Task<bool> WaitForHostAsync(MediaPlayer player, int timeoutMs = 2000)
        {
            var start = Environment.TickCount64;
            while (!HasVideoHost(player) && Environment.TickCount64 - start < timeoutMs)
                await Task.Delay(16);

            if (!HasVideoHost(player))
                return false;

            await Task.Delay(16);
            return HasVideoHost(player);
        }

        public async Task<bool> WaitUntilVideoReadyAsync(MediaPlayer player, int timeoutMs = 1500)
        {
            if (player.IsPlaying && player.Time > 0)
                return true;

            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnPlaying(object? sender, EventArgs e) => ready.TrySetResult(true);
            void OnVout(object? sender, MediaPlayerVoutEventArgs e)
            {
                if (e.Count > 0)
                    ready.TrySetResult(true);
            }

            player.Playing += OnPlaying;
            player.Vout += OnVout;
            try
            {
                if (player.IsPlaying)
                    ready.TrySetResult(true);

                var finished = await Task.WhenAny(ready.Task, Task.Delay(timeoutMs));
                if (finished != ready.Task)
                    return player.IsPlaying;

                var start = Environment.TickCount64;
                while (player.Time <= 0 && Environment.TickCount64 - start < 120)
                    await Task.Delay(16);

                return true;
            }
            finally
            {
                player.Playing -= OnPlaying;
                player.Vout -= OnVout;
            }
        }

        public bool PlayPreview(string path, string? voutModule = null)
        {
            StopPreview();
            CurrentPath = path;

            if (!HasVideoHost(PreviewPlayer))
                return false;

            _previewMedia = CreateMedia(path, voutModule);
            PreviewPlayer.Scale = 0;
            return PreviewPlayer.Play(_previewMedia);
        }

        public bool PlayOutput(string path, string? voutModule = null, float position = 0f)
        {
            StopOutput();

            if (!HasVideoHost(OutputPlayer))
                return false;

            _outputMedia = CreateMedia(path, voutModule);
            OutputPlayer.Scale = 0;
            var started = OutputPlayer.Play(_outputMedia);
            if (started && position > 0f)
                OutputPlayer.Position = position;
            return started;
        }

        private Media CreateMedia(string path, string? voutModule)
        {
            var media = new Media(LibVLC, path, FromType.FromPath);
            media.AddOption(":autoscale");
            if (!string.IsNullOrWhiteSpace(voutModule))
                media.AddOption($":vout={voutModule}");
            return media;
        }

        public void SetPause(bool pause)
        {
            PreviewPlayer.SetPause(pause);
            if (HasVideoHost(OutputPlayer))
                OutputPlayer.SetPause(pause);
        }

        public void StopPreview()
        {
            PreviewPlayer.Stop();
            _previewMedia?.Dispose();
            _previewMedia = null;
        }

        public void StopOutput()
        {
            OutputPlayer.Stop();
            _outputMedia?.Dispose();
            _outputMedia = null;
        }

        public void Stop()
        {
            StopPreview();
            StopOutput();
            CurrentPath = null;
        }

        public void SetPreviewMuted(bool muted) => PreviewPlayer.Mute = muted;

        public void ApplyAudio(bool muted, int volume)
        {
            var level = Math.Clamp(volume, 0, 100);
            PreviewPlayer.Mute = muted;
            PreviewPlayer.Volume = level;
            OutputPlayer.Mute = true;
            OutputPlayer.Volume = 0;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Stop();
            PreviewPlayer.Dispose();
            OutputPlayer.Dispose();
            LibVLC.Dispose();
        }
    }
}
