using libvlc_media.Models;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.IO;

namespace libvlc_media.ViewModels
{
    public sealed class SlideDeckViewModel : ViewModelBase, IDisposable
    {
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

        public string CacheDirectory { get; }

        public ObservableCollection<SlideItemViewModel> Slides { get; }

        public string Summary => $"{Slides.Count} slide{(Slides.Count == 1 ? string.Empty : "s")}";

        public string SourceFileName => Path.GetFileName(SourcePath);

        public void Dispose()
        {
            foreach (var slide in Slides)
                slide.Dispose();

            Slides.Clear();
            SlideCache.DeleteFolder(CacheDirectory);
        }
    }
}
