using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using libvlc_media.Models;
using libvlc_media.ViewModels;
using ReactiveUI;
using ReactiveUI.Avalonia;
using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace libvlc_media.Views
{
    public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
    {
        private OutputWindow? _output;
        private bool _syncingProjector;

        public MainWindow()
        {
            InitializeComponent();
            this.WhenActivated(disposables =>
            {
                if (ViewModel is null)
                    return;

                disposables.Add(ViewModel.PickPdfFiles.RegisterHandler(PickPdfs));
                disposables.Add(ViewModel.PickVideoFiles.RegisterHandler(PickVideos));
                disposables.Add(ViewModel.WhenAnyValue(x => x.IsProjectorOpen)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => SyncProjector()));
                disposables.Add(ViewModel.WhenAnyValue(x => x.SelectedDisplay)
                    .Skip(1)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        if (ViewModel.IsProjectorOpen)
                            SyncProjector();
                    }));
            });

            Opened += (_, _) => RefreshDisplays();
            Closed += (_, _) => CloseProjectorWindow();
        }

        private void RefreshDisplays()
        {
            if (ViewModel is null)
                return;

            var screens = Screens.All;
            var list = new List<DisplayInfo>(screens.Count);
            for (var i = 0; i < screens.Count; i++)
            {
                var screen = screens[i];
                var bounds = screen.Bounds;
                var label = string.IsNullOrWhiteSpace(screen.DisplayName)
                    ? $"Display {i + 1}"
                    : screen.DisplayName!;
                var name = $"{label}  {bounds.Width}×{bounds.Height}"
                           + (screen.IsPrimary ? "  (this PC)" : string.Empty);
                list.Add(new DisplayInfo
                {
                    Index = i,
                    Name = name,
                    X = bounds.X,
                    Y = bounds.Y,
                    Width = bounds.Width,
                    Height = bounds.Height,
                    IsPrimary = screen.IsPrimary
                });
            }

            ViewModel.SetDisplays(list);
        }

        private void SyncProjector()
        {
            if (ViewModel is null || _syncingProjector)
                return;

            _syncingProjector = true;
            try
            {
                if (!ViewModel.IsProjectorOpen)
                {
                    CloseProjectorWindow();
                    return;
                }

                if (_output is null)
                    RefreshDisplays();

                if (_output is null)
                {
                    _output = new OutputWindow
                    {
                        DataContext = ViewModel
                    };
                    _output.Closed += OnOutputClosed;
                    _output.Opened += (_, _) => ViewModel?.OnProjectorSurfaceReady();
                    PlaceOnDisplay(_output, ViewModel.SelectedDisplay);
                }
                else
                {
                    PlaceOnDisplay(_output, ViewModel.SelectedDisplay);
                    ViewModel.OnProjectorSurfaceReady();
                }
            }
            finally
            {
                _syncingProjector = false;
            }
        }

        private void OnOutputClosed(object? sender, EventArgs e)
        {
            if (_output is not null)
                _output.Closed -= OnOutputClosed;

            _output = null;
            if (ViewModel is { IsProjectorOpen: true })
                ViewModel.IsProjectorOpen = false;
        }

        private void CloseProjectorWindow()
        {
            if (_output is null)
                return;

            var window = _output;
            _output = null;
            window.Closed -= OnOutputClosed;
            window.Close();
        }

        private static void PlaceOnDisplay(Window window, DisplayInfo? display)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.WindowState = WindowState.Normal;

            if (display is not null)
                window.Position = new PixelPoint(display.X, display.Y);

            if (!window.IsVisible)
                window.Show();

            if (display is not null)
                window.Position = new PixelPoint(display.X, display.Y);

            window.WindowState = WindowState.FullScreen;
        }

        private async Task PickPdfs(IInteractionContext<Unit, IReadOnlyList<string>> context)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import slide deck from PDF",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("PDF slides") { Patterns = ["*.pdf"] }
                ]
            });

            context.SetOutput(ToLocalPaths(files));
        }

        private async Task PickVideos(IInteractionContext<Unit, IReadOnlyList<string>> context)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import videos",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("MP4 video") { Patterns = ["*.mp4"] },
                    new FilePickerFileType("Other video")
                    {
                        Patterns = ["*.mp4", "*.mov", "*.mkv", "*.webm", "*.avi"]
                    }
                ]
            });

            context.SetOutput(ToLocalPaths(files));
        }

        private static IReadOnlyList<string> ToLocalPaths(IReadOnlyList<IStorageFile> files)
        {
            var paths = new List<string>(files.Count);
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }

            return paths;
        }
    }
}
