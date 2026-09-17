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
            PlayCommand = ReactiveCommand.Create(() => play(this));
        }

        public string FilePath { get; }

        public string Name { get; }

        public ICommand PlayCommand { get; }
    }
}
