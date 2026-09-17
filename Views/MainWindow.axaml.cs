using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using libvlc_media.Models;
using libvlc_media.ViewModels;
using ReactiveUI;
using ReactiveUI.Avalonia;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace libvlc_media.Views
{
    public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
    {
        private const double DragThreshold = 6;

        private OutputWindow? _output;
        private bool _syncingProjector;
        private MediaClipViewModel? _pendingVideo;
        private bool _isDraggingVideo;
        private Point _dragStart;
        private IPointer? _dragPointer;

        public MainWindow()
        {
            InitializeComponent();
            VideoList.AddHandler(
                PointerPressedEvent,
                OnVideoListPointerPressed,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            AddHandler(PointerMovedEvent, OnDragPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(PointerReleasedEvent, OnDragPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(PointerCaptureLostEvent, OnDragCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);

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

        private void OnVideoListPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (ViewModel is null || IsPlayButton(e.Source))
                return;

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            var clip = FindDataContext<MediaClipViewModel>(e.Source);
            if (clip is null)
                return;

            _pendingVideo = clip;
            _isDraggingVideo = false;
            _dragStart = e.GetPosition(this);
            _dragPointer = e.Pointer;
            ViewModel.SelectedVideo = clip;
            e.Pointer.Capture(this);
        }

        private void OnDragPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_pendingVideo is null || _dragPointer is null)
                return;

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                EndVideoDrag();
                return;
            }

            var position = e.GetPosition(this);
            if (!_isDraggingVideo)
            {
                var delta = position - _dragStart;
                if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
                    return;

                _isDraggingVideo = true;
                DragGhostText.Text = _pendingVideo.Name;
                DragGhost.IsVisible = true;
                Cursor = new Cursor(StandardCursorType.DragCopy);
            }

            Canvas.SetLeft(DragGhost, position.X + 14);
            Canvas.SetTop(DragGhost, position.Y + 14);

            if (ViewModel?.SelectedDeck is not null && IsOverSlideDropTarget(position))
                ViewModel.SetDropInsertIndex(GetInsertIndex(position));
            else
                ViewModel?.ClearDropIndicators();
        }

        private void OnDragPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_pendingVideo is null)
                return;

            var clip = _pendingVideo;
            var wasDragging = _isDraggingVideo;
            var position = e.GetPosition(this);
            EndVideoDrag();

            if (!wasDragging || ViewModel?.SelectedDeck is null || !IsOverSlideDropTarget(position))
                return;

            ViewModel.InsertVideoAt(clip.FilePath, GetInsertIndex(position));
        }

        private void OnDragCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndVideoDrag();

        private void EndVideoDrag()
        {
            if (_dragPointer is not null && ReferenceEquals(_dragPointer.Captured, this))
                _dragPointer.Capture(null);

            _pendingVideo = null;
            _isDraggingVideo = false;
            _dragPointer = null;
            DragGhost.IsVisible = false;
            Cursor = Cursor.Default;
            ViewModel?.ClearDropIndicators();
        }

        private bool IsOverSlideDropTarget(Point windowPosition)
        {
            var local = this.TranslatePoint(windowPosition, SlideDropTarget);
            if (local is null)
                return false;

            var bounds = new Rect(SlideDropTarget.Bounds.Size);
            return bounds.Contains(local.Value);
        }

        private int GetInsertIndex(Point windowPosition)
        {
            var panel = SlideGrid.GetVisualDescendants().OfType<WrapPanel>().FirstOrDefault();
            if (panel is null || panel.Children.Count == 0)
                return 0;

            var pos = this.TranslatePoint(windowPosition, panel) ?? default;
            var closestIndex = 0;
            var closestDistance = double.MaxValue;

            for (var i = 0; i < panel.Children.Count; i++)
            {
                var bounds = panel.Children[i].Bounds;
                var dx = pos.X < bounds.X ? bounds.X - pos.X
                    : pos.X > bounds.Right ? pos.X - bounds.Right
                    : 0;
                var dy = pos.Y < bounds.Y ? bounds.Y - pos.Y
                    : pos.Y > bounds.Bottom ? pos.Y - bounds.Bottom
                    : 0;
                var distance = dx * dx + dy * dy;
                if (distance >= closestDistance)
                    continue;

                closestDistance = distance;
                closestIndex = i;
            }

            var target = panel.Children[closestIndex].Bounds;
            return pos.X < target.Center.X ? closestIndex : closestIndex + 1;
        }

        private static bool IsPlayButton(object? source)
        {
            for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            {
                if (visual is Button)
                    return true;
            }

            return false;
        }

        private static T? FindDataContext<T>(object? source) where T : class
        {
            for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            {
                if (visual is Control { DataContext: T value })
                    return value;
            }

            return null;
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
