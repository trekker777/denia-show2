using Avalonia.Media.Imaging;
using ReactiveUI;
using System;
using System.IO;
using System.Reactive;
using System.Windows.Input;

namespace libvlc_media.ViewModels
{
    public sealed class SlideItemViewModel : ViewModelBase, IDisposable
    {
        private bool _isSelected;
        private double _displayWidth;
        private double _displayHeight;

        public SlideItemViewModel(
            string imagePath,
            int pageNumber,
            double displayWidth,
            double displayHeight,
            Action<SlideItemViewModel> present)
        {
            ImagePath = imagePath;
            PageNumber = pageNumber;
            PageLabel = pageNumber.ToString();
            _displayWidth = displayWidth;
            _displayHeight = displayHeight;
            Thumbnail = LoadThumbnail(imagePath, displayWidth);
            PresentCommand = ReactiveCommand.Create(() => present(this));
        }

        public string ImagePath { get; }

        public int PageNumber { get; }

        public string PageLabel { get; }

        public Bitmap? Thumbnail { get; }

        public ICommand PresentCommand { get; }

        public double DisplayWidth
        {
            get => _displayWidth;
            set => this.RaiseAndSetIfChanged(ref _displayWidth, value);
        }

        public double DisplayHeight
        {
            get => _displayHeight;
            set => this.RaiseAndSetIfChanged(ref _displayHeight, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        public void Dispose() => Thumbnail?.Dispose();

        private static Bitmap? LoadThumbnail(string path, double displayWidth)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var decodeWidth = (int)Math.Clamp(displayWidth * 2, 160, 800);
                return Bitmap.DecodeToWidth(stream, decodeWidth);
            }
            catch
            {
                return null;
            }
        }
    }
}
