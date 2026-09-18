using ReactiveUI;
using System;
using System.IO;
using System.Reactive;
using System.Windows.Input;

namespace libvlc_media.ViewModels
{
    public sealed class MediaClipViewModel : ViewModelBase
    {
        public MediaClipViewModel(string filePath, Action<MediaClipViewModel> play)
        {
            FilePath = filePath;
            Name = Path.GetFileName(filePath);
            var extension = Path.GetExtension(filePath);
            IsVideo = IsVideoExtension(extension);
            IsImage = IsImageExtension(extension);
            ActionLabel = IsVideo ? "Play" : "Show";
            PlayCommand = ReactiveCommand.Create(() => play(this));
        }

        public string FilePath { get; }

        public string Name { get; }

        public bool IsVideo { get; }

        public bool IsImage { get; }

        public string ActionLabel { get; }

        public ICommand PlayCommand { get; }

        public static bool IsVideoExtension(string? extension) =>
            extension is not null && extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase);

        public static bool IsImageExtension(string? extension) =>
            extension is not null && extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
    }
}
