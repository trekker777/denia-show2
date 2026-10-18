using libvlc_media.Models;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace libvlc_media.ViewModels
{
    public sealed class SlideDeckViewModel : ViewModelBase, IDisposable
    {
        private bool _isAutoAdvanceEnabled;
        private bool _isLoopSlides = true;
        private decimal _autoAdvanceSeconds = 8;

        public SlideDeckViewModel(string name, string sourcePath, string cacheDirectory)
        {
            Name = name;
            SourcePath = sourcePath;
            CacheDirectory = cacheDirectory;
            Slides = new ObservableCollection<SlideItemViewModel>();
            Slides.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(Summary));
        }

        public string Name { get; }

        public string SourcePath { get; }

        public string CacheDirectory { get; set; }

        public bool OwnsCacheDirectory { get; set; } = true;

        public ObservableCollection<SlideItemViewModel> Slides { get; }

        public bool IsAutoAdvanceEnabled
        {
            get => _isAutoAdvanceEnabled;
            set => this.RaiseAndSetIfChanged(ref _isAutoAdvanceEnabled, value);
        }

        public bool IsLoopSlides
        {
            get => _isLoopSlides;
            set => this.RaiseAndSetIfChanged(ref _isLoopSlides, value);
        }

        public decimal AutoAdvanceSeconds
        {
            get => _autoAdvanceSeconds;
            set => this.RaiseAndSetIfChanged(ref _autoAdvanceSeconds, value);
        }

        public string Summary
        {
            get
            {
                var videos = Slides.Count(s => s.IsVideo);
                var stills = Slides.Count - videos;
                if (videos == 0)
                    return $"{stills} slide{(stills == 1 ? string.Empty : "s")}";
                return $"{stills} slide{(stills == 1 ? string.Empty : "s")}, {videos} video{(videos == 1 ? string.Empty : "s")}";
            }
        }

        public string SourceFileName => Path.GetFileName(SourcePath);

        public void Dispose()
        {
            foreach (var slide in Slides)
                slide.Dispose();

            Slides.Clear();
            if (OwnsCacheDirectory)
                SlideCache.DeleteFolder(CacheDirectory);
        }
    }
}
