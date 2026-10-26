using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace libvlc_media.Views
{
    public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
    {
        private const double DragThreshold = 6;

        private OutputWindow? _output;
        private CountdownOverlayWindow? _previewCountdown;
        private CountdownOverlayWindow? _projectorCountdown;
        private bool _syncingCountdown;
        private bool _syncingProjector;
        private bool _fittingProjector;
        private MediaClipViewModel? _pendingVideo;
        private SlideItemViewModel? _pendingSlide;
        private bool _isDraggingVideo;
        private bool _isDraggingSlide;
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
            SlideGrid.AddHandler(
                PointerPressedEvent,
                OnSlideGridPointerPressed,
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
                disposables.Add(ViewModel.PickSaveWorkspacePath.RegisterHandler(PickSaveWorkspace));
                disposables.Add(ViewModel.PickOpenWorkspacePath.RegisterHandler(PickOpenWorkspace));
                disposables.Add(ViewModel.ConfirmUnsaved.RegisterHandler(ConfirmUnsaved));
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
                disposables.Add(ViewModel.WhenAnyValue(x => x.SelectedSlide, x => x.SelectedDeck)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => QueueScrollLiveSlideIntoView()));
                disposables.Add(ViewModel.WhenAnyValue(
                        x => x.IsCountdownVisible,
                        x => x.CountdownText,
                        x => x.CountdownFontSize)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => SyncCountdownOverlays()));
            });

            Opened += (_, _) =>
            {
                RefreshDisplays();
                SyncCountdownOverlays();
            };
            Closed += (_, _) =>
            {
                CloseProjectorWindow();
                CloseCountdownOverlays();
            };
            PositionChanged += (_, _) => SyncCountdownOverlays();
            SizeChanged += (_, _) => SyncCountdownOverlays();
            PreviewSurface.LayoutUpdated += (_, _) => SyncCountdownOverlays();
            Closing += OnClosing;
        }

        private bool _forceClose;

        private async void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            if (_forceClose || ViewModel is null || !ViewModel.IsWorkspaceDirty)
                return;

            e.Cancel = true;
            if (await ViewModel.ConfirmDiscardOrSaveAsync())
            {
                _forceClose = true;
                Close();
            }
        }

        private void QueueScrollLiveSlideIntoView() =>
            Dispatcher.UIThread.Post(ScrollLiveSlideIntoView, DispatcherPriority.Loaded);

        private void ScrollLiveSlideIntoView()
        {
            var slide = ViewModel?.SelectedSlide;
            if (slide is null || ViewModel?.SelectedDeck is null || !ViewModel.SelectedDeck.Slides.Contains(slide))
                return;

            var container = SlideGrid.ContainerFromItem(slide)
                            ?? SlideGrid.GetVisualDescendants()
                                .OfType<Control>()
                                .FirstOrDefault(c => c.DataContext == slide && c.Classes.Contains("slide-card"));
            if (container is null)
                return;

            var bounds = container.Bounds;
            container.BringIntoView(new Rect(-12, -12, bounds.Width + 24, bounds.Height + 24));
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
                var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
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
                    Scaling = scaling,
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
                    _output.Opened += OnProjectorOpened;
                    _output.ScalingChanged += OnProjectorScalingChanged;
                    PlaceOnDisplay(_output, ViewModel.SelectedDisplay);
                }
                else
                {
                    PlaceOnDisplay(_output, ViewModel.SelectedDisplay);
                    ViewModel.OnProjectorSurfaceReady();
                }

                SyncCountdownOverlays();
            }
            finally
            {
                _syncingProjector = false;
            }
        }

        private void OnOutputClosed(object? sender, EventArgs e)
        {
            CloseOverlay(ref _projectorCountdown);
            if (_output is not null)
            {
                _output.Closed -= OnOutputClosed;
                _output.Opened -= OnProjectorOpened;
                _output.ScalingChanged -= OnProjectorScalingChanged;
            }

            _output = null;
            if (ViewModel is { IsProjectorOpen: true })
                ViewModel.IsProjectorOpen = false;
        }

        private void CloseProjectorWindow()
        {
            CloseOverlay(ref _projectorCountdown);
            if (_output is null)
                return;

            var window = _output;
            _output = null;
            window.Closed -= OnOutputClosed;
            window.Opened -= OnProjectorOpened;
            window.ScalingChanged -= OnProjectorScalingChanged;
            window.Close();
        }

        private void OnProjectorOpened(object? sender, EventArgs e)
        {
            FitProjectorToDisplay();
            Dispatcher.UIThread.Post(() =>
            {
                FitProjectorToDisplay();
                ViewModel?.OnProjectorSurfaceReady();
                SyncCountdownOverlays();
            }, DispatcherPriority.Loaded);
        }

        private void OnProjectorScalingChanged(object? sender, EventArgs e)
        {
            // DPI updates after the window lands on the projector. If we are already
            // fitting, wait until this pass finishes so we size with the new scale.
            if (_fittingProjector)
            {
                Dispatcher.UIThread.Post(FitProjectorToDisplay, DispatcherPriority.Loaded);
                return;
            }

            FitProjectorToDisplay();
            SyncCountdownOverlays();
        }

        private void FitProjectorToDisplay()
        {
            if (_output is null || _fittingProjector)
                return;

            _fittingProjector = true;
            try
            {
                PlaceOnDisplay(_output, ViewModel?.SelectedDisplay);
            }
            finally
            {
                _fittingProjector = false;
            }
        }

        private void SyncCountdownOverlays()
        {
            if (_syncingCountdown || ViewModel is null || !IsVisible)
                return;

            _syncingCountdown = true;
            try
            {
                SyncOverlay(
                    ref _previewCountdown,
                    PreviewSurface,
                    this,
                    topmost: false);

                if (_output is not null && ViewModel.IsProjectorOpen)
                {
                    SyncOverlay(
                        ref _projectorCountdown,
                        _output,
                        _output,
                        topmost: true);
                }
                else
                {
                    CloseOverlay(ref _projectorCountdown);
                }
            }
            finally
            {
                _syncingCountdown = false;
            }
        }

        private void SyncOverlay(
            ref CountdownOverlayWindow? overlay,
            Visual target,
            Window owner,
            bool topmost)
        {
            if (ViewModel is null || !ViewModel.IsCountdownVisible || string.IsNullOrEmpty(ViewModel.CountdownText))
            {
                overlay?.Hide();
                return;
            }

            overlay ??= CreateCountdownOverlay(topmost);
            overlay.Topmost = topmost;

            if (!overlay.IsVisible)
            {
                overlay.Position = new PixelPoint(-32000, -32000);
                overlay.Show(owner);
                overlay.Topmost = topmost;
            }

            overlay.FitTopRightOf(target, ViewModel.CountdownText, ViewModel.CountdownFontSize);
        }

        private CountdownOverlayWindow CreateCountdownOverlay(bool topmost)
        {
            var overlay = new CountdownOverlayWindow
            {
                DataContext = ViewModel,
                Topmost = topmost,
                ShowActivated = false
            };
            overlay.ScalingChanged += (_, _) => SyncCountdownOverlays();
            return overlay;
        }

        private static void CloseOverlay(ref CountdownOverlayWindow? overlay)
        {
            if (overlay is null)
                return;

            var window = overlay;
            overlay = null;
            window.Close();
        }

        private void CloseCountdownOverlays()
        {
            CloseOverlay(ref _previewCountdown);
            CloseOverlay(ref _projectorCountdown);
        }

        private void PlaceOnDisplay(Window window, DisplayInfo? display)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.WindowState = WindowState.Normal;
            window.WindowDecorations = WindowDecorations.None;
            window.CanResize = false;
            window.Topmost = true;

            var screen = FindScreen(window, display);
            var bounds = screen?.Bounds
                         ?? (display is null
                             ? (PixelRect?)null
                             : new PixelRect(display.X, display.Y, display.Width, display.Height));

            if (bounds is null)
            {
                if (!window.IsVisible)
                    window.Show();
                window.WindowState = WindowState.FullScreen;
                return;
            }

            var pixelBounds = bounds.Value;
            window.Position = pixelBounds.Position;

            if (!window.IsVisible)
                window.Show();

            window.Position = pixelBounds.Position;

            // DIP size must use the window's *current* scale. FullScreen on display 2
            // often keeps the PC's DPI, so a 1920×1080 projector is opened at
            // 1920×1.5 by 1080×1.5 physical pixels and the picture overruns the edge.
            var scaling = ResolveScaling(window, screen, display, pixelBounds);
            var dipWidth = pixelBounds.Width / scaling;
            var dipHeight = pixelBounds.Height / scaling;

            if (WindowMatchesDisplay(window, pixelBounds, dipWidth, dipHeight))
            {
                PinWindowToPixels(window, pixelBounds);
                return;
            }

            window.MinWidth = 0;
            window.MaxWidth = double.PositiveInfinity;
            window.MinHeight = 0;
            window.MaxHeight = double.PositiveInfinity;
            window.Width = dipWidth;
            window.Height = dipHeight;
            window.MinWidth = dipWidth;
            window.MaxWidth = dipWidth;
            window.MinHeight = dipHeight;
            window.MaxHeight = dipHeight;

            PinWindowToPixels(window, pixelBounds);
        }

        private Screen? FindScreen(Window window, DisplayInfo? display)
        {
            var screens = window.Screens.All.Count > 0 ? window.Screens.All : Screens.All;
            if (screens.Count == 0)
                return null;

            if (display is null)
                return ScreenOf(window) ?? screens[0];

            foreach (var candidate in screens)
            {
                var b = candidate.Bounds;
                if (b.X == display.X && b.Y == display.Y
                    && b.Width == display.Width && b.Height == display.Height)
                    return candidate;
            }

            if (display.Index >= 0 && display.Index < screens.Count)
                return screens[display.Index];

            return window.Screens.ScreenFromPoint(new PixelPoint(display.X + 1, display.Y + 1));
        }

        private static Screen? ScreenOf(Window window)
        {
            try
            {
                return window.Screens.ScreenFromWindow(window);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static double ResolveScaling(
            Window window,
            Screen? screen,
            DisplayInfo? display,
            PixelRect bounds)
        {
            if (window.RenderScaling > 0)
            {
                var current = ScreenOf(window);
                if (current is not null && current.Bounds.Position == bounds.Position)
                    return window.RenderScaling;
            }

            if (screen is not null && screen.Scaling > 0)
                return screen.Scaling;

            if (display is not null && display.Scaling > 0)
                return display.Scaling;

            return window.RenderScaling > 0 ? window.RenderScaling : 1;
        }

        private static bool WindowMatchesDisplay(
            Window window,
            PixelRect bounds,
            double dipWidth,
            double dipHeight)
        {
            const double tolerance = 2;
            var physicalWidth = window.ClientSize.Width * window.RenderScaling;
            var physicalHeight = window.ClientSize.Height * window.RenderScaling;
            return window.Position == bounds.Position
                   && Math.Abs(physicalWidth - bounds.Width) < tolerance
                   && Math.Abs(physicalHeight - bounds.Height) < tolerance
                   && Math.Abs(window.Width - dipWidth) < tolerance
                   && Math.Abs(window.Height - dipHeight) < tolerance;
        }

        private static void PinWindowToPixels(Window window, PixelRect bounds)
        {
            if (!OperatingSystem.IsWindows())
                return;

            var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
                return;

            SetWindowPos(
                hwnd,
                HwndTopmost,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                SwpNoActivate | SwpShowWindow | SwpFrameChanged);
        }

        private static readonly IntPtr HwndTopmost = new(-1);
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint SwpFrameChanged = 0x0020;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);

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
            _pendingSlide = null;
            _isDraggingVideo = false;
            _isDraggingSlide = false;
            _dragStart = e.GetPosition(this);
            _dragPointer = e.Pointer;
            ViewModel.SelectedVideo = clip;
            e.Pointer.Capture(this);
        }

        private void OnSlideGridPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (ViewModel is null || IsPlayButton(e.Source))
                return;

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            var slide = FindDataContext<SlideItemViewModel>(e.Source);
            if (slide is null)
                return;

            _pendingSlide = slide;
            _pendingVideo = null;
            _isDraggingSlide = false;
            _isDraggingVideo = false;
            _dragStart = e.GetPosition(this);
            _dragPointer = e.Pointer;
            e.Pointer.Capture(this);
        }

        private void OnDragPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_dragPointer is null || (_pendingVideo is null && _pendingSlide is null))
                return;

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                EndDrag();
                return;
            }

            var position = e.GetPosition(this);
            var dragging = _isDraggingVideo || _isDraggingSlide;
            if (!dragging)
            {
                var delta = position - _dragStart;
                if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
                    return;

                if (_pendingVideo is not null)
                {
                    _isDraggingVideo = true;
                    DragGhostText.Text = _pendingVideo.Name;
                    Cursor = new Cursor(StandardCursorType.DragCopy);
                }
                else if (_pendingSlide is not null)
                {
                    _isDraggingSlide = true;
                    DragGhostText.Text = _pendingSlide.IsVideo
                        ? _pendingSlide.PageLabel
                        : $"Slide {_pendingSlide.PageLabel}";
                    Cursor = new Cursor(StandardCursorType.DragMove);
                }

                DragGhost.IsVisible = true;
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
            var clip = _pendingVideo;
            var slide = _pendingSlide;
            var wasDraggingVideo = _isDraggingVideo;
            var wasDraggingSlide = _isDraggingSlide;
            var position = e.GetPosition(this);
            EndDrag();

            if (ViewModel?.SelectedDeck is null)
                return;

            if (wasDraggingVideo && clip is not null && IsOverSlideDropTarget(position))
            {
                ViewModel.InsertMediaAt(clip, GetInsertIndex(position));
                return;
            }

            if (wasDraggingSlide && slide is not null && IsOverSlideDropTarget(position))
            {
                ViewModel.MoveSlide(slide, GetInsertIndex(position));
                return;
            }

            if (!wasDraggingSlide && !wasDraggingVideo && slide is not null)
                ViewModel.PresentSlide(slide);
        }

        private void OnDragCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

        private void EndDrag()
        {
            if (_dragPointer is not null && ReferenceEquals(_dragPointer.Captured, this))
                _dragPointer.Capture(null);

            _pendingVideo = null;
            _pendingSlide = null;
            _isDraggingVideo = false;
            _isDraggingSlide = false;
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
                Title = "Import media",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Media") { Patterns = ["*.mp4", "*.png"] },
                    new FilePickerFileType("MP4 video") { Patterns = ["*.mp4"] },
                    new FilePickerFileType("PNG image") { Patterns = ["*.png"] }
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

        private async Task PickSaveWorkspace(IInteractionContext<string, string?> context)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save workspace as",
                SuggestedFileName = context.Input + ".json",
                DefaultExtension = "json",
                FileTypeChoices =
                [
                    new FilePickerFileType("Slide workspace") { Patterns = ["*.json"] }
                ]
            });

            context.SetOutput(file?.TryGetLocalPath());
        }

        private async Task PickOpenWorkspace(IInteractionContext<Unit, string?> context)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Open workspace folder",
                AllowMultiple = false
            });

            context.SetOutput(folders.Count > 0 ? folders[0].TryGetLocalPath() : null);
        }

        private async Task ConfirmUnsaved(IInteractionContext<Unit, UnsavedDecision> context)
        {
            var choice = UnsavedDecision.Cancel;
            var save = new Button { Content = "Save", MinWidth = 90 };
            var discard = new Button { Content = "Don't save", MinWidth = 90 };
            var cancel = new Button { Content = "Cancel", MinWidth = 90 };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(save);
            buttons.Children.Add(discard);
            buttons.Children.Add(cancel);

            var dialog = new Window
            {
                Title = "Unsaved changes",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(20),
                    Spacing = 18,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Save the current workspace before continuing?",
                            TextWrapping = TextWrapping.Wrap
                        },
                        buttons
                    }
                }
            };

            save.Click += (_, _) =>
            {
                choice = UnsavedDecision.Save;
                dialog.Close();
            };
            discard.Click += (_, _) =>
            {
                choice = UnsavedDecision.Discard;
                dialog.Close();
            };
            cancel.Click += (_, _) => dialog.Close();

            await dialog.ShowDialog(this);
            context.SetOutput(choice);
        }
    }
}
