using Avalonia.Media.Imaging;
using ReactiveUI;
using System;
using System.IO;
using System.Windows.Input;

namespace libvlc_media.ViewModels
{
    public sealed class SlideItemViewModel : ViewModelBase, IDisposable
    {
        private bool _isSelected;
        private bool _showDropBefore;
        private bool _showDropAfter;
        private double _displayWidth;
        private double _displayHeight;
        private Bitmap? _thumbnail;
        private bool _isDisposed;

        private SlideItemViewModel(
            bool isVideo,
            string path,
            int pageNumber,
            double displayWidth,
            double displayHeight,
            Action<SlideItemViewModel> present,
            Action<SlideItemViewModel> delete)
        {
            IsVideo = isVideo;
            _displayWidth = displayWidth;
            _displayHeight = displayHeight;
            PresentCommand = ReactiveCommand.Create(() => present(this));
            DeleteCommand = ReactiveCommand.Create(() => delete(this));

            if (isVideo)
            {
                VideoPath = path;
                ImagePath = string.Empty;
                PageNumber = 0;
                PageLabel = FileMissing
                    ? Path.GetFileName(path) + " (missing)"
                    : Path.GetFileName(path);
                _thumbnail = null;
            }
            else
            {
                ImagePath = path;
                VideoPath = null;
                PageNumber = pageNumber;
                PageLabel = pageNumber > 0 ? pageNumber.ToString() : Path.GetFileName(path);
                _thumbnail = LoadThumbnail(path, displayWidth);
            }
        }

        public static SlideItemViewModel FromImage(
            string imagePath,
            int pageNumber,
            double displayWidth,
            double displayHeight,
            Action<SlideItemViewModel> present,
            Action<SlideItemViewModel> delete) =>
            new(false, imagePath, pageNumber, displayWidth, displayHeight, present, delete);

        public static SlideItemViewModel FromVideo(
            string videoPath,
            double displayWidth,
            double displayHeight,
            Action<SlideItemViewModel> present,
            Action<SlideItemViewModel> delete) =>
            new(true, videoPath, 0, displayWidth, displayHeight, present, delete);

        public bool IsVideo { get; }

        public string ImagePath { get; private set; }

        public string? VideoPath { get; private set; }

        public int PageNumber { get; }

        public string PageLabel { get; private set; }

        public bool FileMissing =>
            IsVideo
                ? string.IsNullOrWhiteSpace(VideoPath) || !File.Exists(VideoPath)
                : string.IsNullOrWhiteSpace(ImagePath) || !File.Exists(ImagePath);

        public void RetargetImage(string path)
        {
            ImagePath = path;
            this.RaisePropertyChanged(nameof(ImagePath));
            this.RaisePropertyChanged(nameof(FileMissing));
        }

        public void RetargetVideo(string path)
        {
            VideoPath = path;
            PageLabel = File.Exists(path)
                ? Path.GetFileName(path)
                : Path.GetFileName(path) + " (missing)";
            this.RaisePropertyChanged(nameof(VideoPath));
            this.RaisePropertyChanged(nameof(PageLabel));
            this.RaisePropertyChanged(nameof(FileMissing));
        }

        public Bitmap? Thumbnail
        {
            get => _thumbnail;
            private set
            {
                var previous = _thumbnail;
                if (SetAndRaise(ref _thumbnail, value))
                {
                    previous?.Dispose();
                    this.RaisePropertyChanged(nameof(HasThumbnail));
                }
            }
        }

        public bool HasThumbnail => _thumbnail is not null;

        public bool IsDisposed => _isDisposed;

        public void SetThumbnail(Bitmap bitmap)
        {
            if (_isDisposed)
            {
                bitmap.Dispose();
                return;
            }

            Thumbnail = bitmap;
        }

        public ICommand PresentCommand { get; }

        public ICommand DeleteCommand { get; }

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

        public bool ShowDropBefore
        {
            get => _showDropBefore;
            set => this.RaiseAndSetIfChanged(ref _showDropBefore, value);
        }

        public bool ShowDropAfter
        {
            get => _showDropAfter;
            set => this.RaiseAndSetIfChanged(ref _showDropAfter, value);
        }

        public void Dispose()
        {
            _isDisposed = true;
            Thumbnail = null;
        }

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
