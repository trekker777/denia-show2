using Avalonia.Media.Imaging;
using Avalonia.Threading;
using libvlc_media.Models;
using libvlc_media.Services;
using LibVLCSharp.Shared;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace libvlc_media.ViewModels
{
    public sealed class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private readonly VlcPlaybackService? _playback;
        private readonly PdfImportService _pdfImport = new();
        private readonly VideoThumbnailService _thumbnails = new();
        private readonly List<Bitmap> _retiredImages = new();
        private readonly DispatcherTimer _advanceTimer;
        private readonly DispatcherTimer _videoProgressTimer;

        private SlideDeckViewModel? _selectedDeck;
        private SlideItemViewModel? _selectedSlide;
        private MediaClipViewModel? _selectedVideo;
        private DisplayInfo? _selectedDisplay;
        private Bitmap? _presentedImage;
        private double _thumbnailSize = 180;
        private bool _isVideoActive;
        private bool _isVideoSurfaceReady;
        private int _playGeneration;
        private bool _isProjectorOpen;
        private bool _isBlanked;
        private bool _isBusy;
        private bool _isPlaying;
        private bool _isPaused;
        private bool _isAutoAdvanceEnabled;
        private bool _isLoopSlides = true;
        private decimal _autoAdvanceSeconds = 8;
        private long _videoTimeMs;
        private long _videoLengthMs;
        private string _statusMessage = "Import a PDF to build a slide deck, or add an MP4 to play on the projector.";
        private VideoRendererOption _selectedVideoRenderer = VideoRenderers[3];
        private bool _isMuted;
        private double _volume = 80;

        public MainWindowViewModel()
            : this(null)
        {
        }

        public MainWindowViewModel(VlcPlaybackService? playback)
        {
            _playback = playback;
            Decks = new ObservableCollection<SlideDeckViewModel>();
            Videos = new ObservableCollection<MediaClipViewModel>();
            Displays = new ObservableCollection<DisplayInfo>();

            Decks.CollectionChanged += (_, _) =>
            {
                this.RaisePropertyChanged(nameof(HasDecks));
                this.RaisePropertyChanged(nameof(HasSlides));
            };
            Videos.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasVideos));

            PickPdfFiles = new Interaction<Unit, IReadOnlyList<string>>();
            PickVideoFiles = new Interaction<Unit, IReadOnlyList<string>>();

            ImportPdfCommand = ReactiveCommand.CreateFromTask(ImportPdfsAsync);
            ImportVideoCommand = ReactiveCommand.CreateFromTask(ImportMediaAsync);
            RemoveSelectedDeckCommand = ReactiveCommand.Create(RemoveSelectedDeck);
            RemoveSelectedVideoCommand = ReactiveCommand.Create(RemoveSelectedVideo);
            RemoveSelectedSlideCommand = ReactiveCommand.Create(RemoveSelectedSlide);
            NextSlideCommand = ReactiveCommand.Create(() => GoToNextSlide(stopAtEnd: false));
            PreviousSlideCommand = ReactiveCommand.Create(GoToPreviousSlide);
            PlaySelectedVideoCommand = ReactiveCommand.CreateFromTask(PlaySelectedVideoAsync);
            TogglePauseCommand = ReactiveCommand.Create(TogglePause);
            StopVideoCommand = ReactiveCommand.Create(StopVideo);
            ToggleBlankCommand = ReactiveCommand.Create(ToggleBlank);
            ToggleProjectorCommand = ReactiveCommand.Create(ToggleProjector);
            ToggleMuteCommand = ReactiveCommand.Create(ToggleMute);

            _advanceTimer = new DispatcherTimer();
            _advanceTimer.Tick += (_, _) => GoToNextSlide(stopAtEnd: true);
            _videoProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _videoProgressTimer.Tick += (_, _) => UpdateVideoProgress();

            if (_playback is not null)
            {
                _playback.PreviewPlayer.EndReached += (_, _) =>
                    Dispatcher.UIThread.Post(OnVideoEnded);
            }
        }

        public ObservableCollection<SlideDeckViewModel> Decks { get; }

        public ObservableCollection<MediaClipViewModel> Videos { get; }

        public ObservableCollection<DisplayInfo> Displays { get; }

        public static VideoRendererOption[] VideoRenderers { get; } =
        [
            new("Automatic", null),
            new("Direct3D11", "direct3d11"),
            new("Direct3D9", "direct3d9"),
            new("OpenGL", "glwin32"),
            new("GDI (compatibility)", "wingdi")
        ];

        public VideoRendererOption SelectedVideoRenderer
        {
            get => _selectedVideoRenderer;
            set => this.RaiseAndSetIfChanged(ref _selectedVideoRenderer, value);
        }

        public Interaction<Unit, IReadOnlyList<string>> PickPdfFiles { get; }

        public Interaction<Unit, IReadOnlyList<string>> PickVideoFiles { get; }

        public ICommand ImportPdfCommand { get; }

        public ICommand ImportVideoCommand { get; }

        public ICommand RemoveSelectedDeckCommand { get; }

        public ICommand RemoveSelectedVideoCommand { get; }

        public ICommand RemoveSelectedSlideCommand { get; }

        public ICommand NextSlideCommand { get; }

        public ICommand PreviousSlideCommand { get; }

        public ICommand PlaySelectedVideoCommand { get; }

        public ICommand TogglePauseCommand { get; }

        public ICommand StopVideoCommand { get; }

        public ICommand ToggleBlankCommand { get; }

        public ICommand ToggleProjectorCommand { get; }

        public ICommand ToggleMuteCommand { get; }

        public MediaPlayer? PreviewMediaPlayer => _playback?.PreviewPlayer;

        public MediaPlayer? OutputMediaPlayer => _playback?.OutputPlayer;

        public bool HasDecks => Decks.Count > 0;

        public bool HasVideos => Videos.Count > 0;

        public bool HasSlides => SelectedDeck is { Slides.Count: > 0 };

        public bool ShowSlide => PresentedImage is not null && !IsVideoSurfaceReady && !IsBlanked;

        public bool ShowVideo => IsVideoSurfaceReady && !IsBlanked;

        public bool ShowIdle => !ShowSlide && !ShowVideo && !IsBlanked;

        public string PauseLabel => IsPaused ? "Resume" : "Pause";

        public string MuteLabel => IsMuted ? "Unmute" : "Mute";

        public string VolumeLabel => $"{Math.Round(Volume):0}";

        public string ProjectorLabel => IsProjectorOpen ? "Close projector" : "Open projector";

        public string BlankLabel => IsBlanked ? "Unblank" : "Blank";

        public string SelectedDeckTitle => SelectedDeck is null
            ? "No deck selected"
            : $"{SelectedDeck.Name}  ·  {SelectedDeck.Summary}";

        public string OutputCaption
        {
            get
            {
                if (IsBlanked)
                    return "BLANK";
                if (IsVideoActive && SelectedSlide is { IsVideo: true })
                    return SelectedSlide.PageLabel;
                if (IsVideoActive && SelectedVideo is not null)
                    return SelectedVideo.Name;
                if (SelectedSlide is not null && SelectedDeck is not null)
                    return SelectedSlide.IsVideo
                        ? $"{SelectedDeck.Name}  ·  {SelectedSlide.PageLabel}"
                        : $"{SelectedDeck.Name}  ·  slide {SelectedSlide.PageNumber}";
                return "No output";
            }
        }

        public SlideDeckViewModel? SelectedDeck
        {
            get => _selectedDeck;
            set
            {
                if (SetAndRaise(ref _selectedDeck, value))
                {
                    ApplyThumbnailSizeToSlides();
                    this.RaisePropertyChanged(nameof(HasSlides));
                    this.RaisePropertyChanged(nameof(SelectedDeckTitle));
                }
            }
        }

        public SlideItemViewModel? SelectedSlide
        {
            get => _selectedSlide;
            private set
            {
                if (SetAndRaise(ref _selectedSlide, value))
                    NotifyOutputState();
            }
        }

        public MediaClipViewModel? SelectedVideo
        {
            get => _selectedVideo;
            set => this.RaiseAndSetIfChanged(ref _selectedVideo, value);
        }

        public DisplayInfo? SelectedDisplay
        {
            get => _selectedDisplay;
            set => this.RaiseAndSetIfChanged(ref _selectedDisplay, value);
        }

        public Bitmap? PresentedImage
        {
            get => _presentedImage;
            private set
            {
                var previous = _presentedImage;
                if (SetAndRaise(ref _presentedImage, value))
                {
                    if (previous is not null)
                    {
                        _retiredImages.Add(previous);
                        while (_retiredImages.Count > 2)
                        {
                            _retiredImages[0].Dispose();
                            _retiredImages.RemoveAt(0);
                        }
                    }

                    NotifyOutputState();
                }
            }
        }

        public double ThumbnailSize
        {
            get => _thumbnailSize;
            set
            {
                if (SetAndRaise(ref _thumbnailSize, value))
                    ApplyThumbnailSizeToSlides();
            }
        }

        public bool IsVideoActive
        {
            get => _isVideoActive;
            private set
            {
                if (SetAndRaise(ref _isVideoActive, value))
                {
                    if (!value)
                        IsVideoSurfaceReady = false;
                    NotifyOutputState();
                    RestartAdvanceTimer();
                    SyncVideoProgressTimer();
                }
            }
        }

        public bool IsVideoSurfaceReady
        {
            get => _isVideoSurfaceReady;
            private set
            {
                if (SetAndRaise(ref _isVideoSurfaceReady, value))
                    NotifyOutputState();
            }
        }

        public bool IsProjectorOpen
        {
            get => _isProjectorOpen;
            set
            {
                if (SetAndRaise(ref _isProjectorOpen, value))
                {
                    this.RaisePropertyChanged(nameof(ProjectorLabel));
                    if (!value)
                        _playback?.StopOutput();
                    ApplyAudio();
                }
            }
        }

        public bool IsBlanked
        {
            get => _isBlanked;
            set
            {
                if (SetAndRaise(ref _isBlanked, value))
                {
                    this.RaisePropertyChanged(nameof(BlankLabel));
                    if (value && IsVideoActive && !IsPaused)
                    {
                        _playback?.SetPause(true);
                        IsPaused = true;
                    }

                    NotifyOutputState();
                    RestartAdvanceTimer();
                }
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            private set
            {
                if (SetAndRaise(ref _isPlaying, value))
                    SyncVideoProgressTimer();
            }
        }

        public bool IsPaused
        {
            get => _isPaused;
            private set
            {
                if (SetAndRaise(ref _isPaused, value))
                {
                    this.RaisePropertyChanged(nameof(PauseLabel));
                    SyncVideoProgressTimer();
                }
            }
        }

        public bool IsAutoAdvanceEnabled
        {
            get => _isAutoAdvanceEnabled;
            set
            {
                if (SetAndRaise(ref _isAutoAdvanceEnabled, value))
                    RestartAdvanceTimer();
            }
        }

        public bool IsLoopSlides
        {
            get => _isLoopSlides;
            set => this.RaiseAndSetIfChanged(ref _isLoopSlides, value);
        }

        public string VideoTimeText => FormatTimestamp(_videoTimeMs);

        public string VideoDurationText => FormatTimestamp(_videoLengthMs);

        public double VideoProgress =>
            _videoLengthMs > 0 ? Math.Clamp(_videoTimeMs / (double)_videoLengthMs, 0, 1) : 0;

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (SetAndRaise(ref _isMuted, value))
                {
                    this.RaisePropertyChanged(nameof(MuteLabel));
                    ApplyAudio();
                }
            }
        }

        public double Volume
        {
            get => _volume;
            set
            {
                if (SetAndRaise(ref _volume, Math.Clamp(value, 0, 100)))
                {
                    this.RaisePropertyChanged(nameof(VolumeLabel));
                    ApplyAudio();
                }
            }
        }

        public decimal AutoAdvanceSeconds
        {
            get => _autoAdvanceSeconds;
            set
            {
                if (SetAndRaise(ref _autoAdvanceSeconds, value))
                    RestartAdvanceTimer();
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        public void SetDisplays(IReadOnlyList<DisplayInfo> displays)
        {
            var selectedIndex = SelectedDisplay?.Index;
            Displays.Clear();
            foreach (var display in displays)
                Displays.Add(display);

            if (selectedIndex is int index)
                SelectedDisplay = Displays.FirstOrDefault(d => d.Index == index);

            SelectedDisplay ??= Displays.FirstOrDefault(d => !d.IsPrimary) ?? Displays.FirstOrDefault();
        }

        public async void OnProjectorSurfaceReady()
        {
            if (_playback is null)
                return;

            ApplyAudio();
            if (!IsVideoActive || _playback.CurrentPath is null)
                return;

            if (await VlcPlaybackService.WaitForHostAsync(_playback.OutputPlayer))
                _playback.PlayOutput(_playback.CurrentPath, SelectedVideoRenderer.Module, _playback.PreviewPlayer.Position);
        }

        public void PresentSlide(SlideItemViewModel slide)
        {
            SelectedDeck ??= FindDeck(slide);
            SelectOnly(slide);
            SelectedSlide = slide;
            IsBlanked = false;

            if (slide.IsVideo)
            {
                if (string.IsNullOrWhiteSpace(slide.VideoPath))
                    return;

                var clip = Videos.FirstOrDefault(v =>
                               string.Equals(v.FilePath, slide.VideoPath, StringComparison.OrdinalIgnoreCase))
                           ?? new MediaClipViewModel(slide.VideoPath, _ => { });
                _ = PlayVideoAsync(clip, fromDeck: true);
                return;
            }

            StopVideo();
            try
            {
                PresentedImage = new Bitmap(slide.ImagePath);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not open slide image: {ex.Message}";
                return;
            }

            StatusMessage = $"Showing {OutputCaption} on preview and projector.";
            RestartAdvanceTimer();
        }

        public void InsertVideoAt(string videoPath, int index)
        {
            if (SelectedDeck is null)
                return;

            index = Math.Clamp(index, 0, SelectedDeck.Slides.Count);
            var item = SlideItemViewModel.FromVideo(
                videoPath,
                ThumbnailSize,
                ThumbnailHeight(),
                PresentSlide,
                RemoveSlide);
            SelectedDeck.Slides.Insert(index, item);
            ApplyThumbnailSizeToSlides();
            this.RaisePropertyChanged(nameof(SelectedDeckTitle));
            this.RaisePropertyChanged(nameof(HasSlides));
            StatusMessage = $"Inserted {Path.GetFileName(videoPath)} at position {index + 1}. Click it to play.";
            _ = CaptureVideoThumbnailAsync(item);
        }

        private async Task CaptureVideoThumbnailAsync(SlideItemViewModel item)
        {
            if (!item.IsVideo || string.IsNullOrWhiteSpace(item.VideoPath))
                return;

            var width = (int)Math.Clamp(ThumbnailSize * 2.3, 160, 1000);
            var file = await _thumbnails.CaptureToFileAsync(item.VideoPath, width);
            if (file is null)
                return;

            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (item.IsDisposed)
                        return;

                    try
                    {
                        using var stream = File.OpenRead(file);
                        item.SetThumbnail(Bitmap.DecodeToWidth(stream, width));
                    }
                    catch
                    {
                        // Keep the play-icon card if the frame cannot be decoded.
                    }
                });
            }
            finally
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Temp cleanup is best-effort.
                }
            }
        }

        public void InsertImageAt(string imagePath, int index)
        {
            if (SelectedDeck is null)
                return;

            index = Math.Clamp(index, 0, SelectedDeck.Slides.Count);
            var item = SlideItemViewModel.FromImage(
                imagePath,
                0,
                ThumbnailSize,
                ThumbnailHeight(),
                PresentSlide,
                RemoveSlide);
            SelectedDeck.Slides.Insert(index, item);
            ApplyThumbnailSizeToSlides();
            this.RaisePropertyChanged(nameof(SelectedDeckTitle));
            this.RaisePropertyChanged(nameof(HasSlides));
            StatusMessage = $"Inserted {Path.GetFileName(imagePath)} at position {index + 1}.";
        }

        public void InsertMediaAt(MediaClipViewModel clip, int index)
        {
            if (clip.IsVideo)
                InsertVideoAt(clip.FilePath, index);
            else
                InsertImageAt(clip.FilePath, index);
        }

        public void MoveSlide(SlideItemViewModel slide, int insertIndex)
        {
            if (SelectedDeck is null)
                return;

            var slides = SelectedDeck.Slides;
            var from = slides.IndexOf(slide);
            if (from < 0)
                return;

            insertIndex = Math.Clamp(insertIndex, 0, slides.Count);
            if (insertIndex == from || insertIndex == from + 1)
                return;

            var to = insertIndex > from ? insertIndex - 1 : insertIndex;
            slides.Move(from, to);
            StatusMessage = $"Moved to position {to + 1}.";
        }

        public void RemoveSlide(SlideItemViewModel slide)
        {
            var deck = FindDeck(slide);
            if (deck is null)
                return;

            var index = deck.Slides.IndexOf(slide);
            var wasSelected = ReferenceEquals(SelectedSlide, slide);

            if (wasSelected)
            {
                if (slide.IsVideo && IsVideoActive)
                    StopVideo();
                PresentedImage = null;
                SelectedSlide = null;
            }

            deck.Slides.Remove(slide);
            slide.Dispose();
            this.RaisePropertyChanged(nameof(SelectedDeckTitle));
            this.RaisePropertyChanged(nameof(HasSlides));

            if (wasSelected && deck.Slides.Count > 0)
            {
                var next = Math.Min(index, deck.Slides.Count - 1);
                PresentSlide(deck.Slides[next]);
            }
            else
            {
                NotifyOutputState();
            }

            StatusMessage = slide.IsVideo
                ? $"Removed {slide.PageLabel} from the deck."
                : $"Removed slide {slide.PageLabel} from the deck.";
        }

        public void SetDropInsertIndex(int? index)
        {
            if (SelectedDeck is null)
                return;

            var slides = SelectedDeck.Slides;
            for (var i = 0; i < slides.Count; i++)
            {
                slides[i].ShowDropBefore = index == i;
                slides[i].ShowDropAfter = index == slides.Count && i == slides.Count - 1;
            }
        }

        public void ClearDropIndicators()
        {
            if (SelectedDeck is null)
                return;

            foreach (var slide in SelectedDeck.Slides)
            {
                slide.ShowDropBefore = false;
                slide.ShowDropAfter = false;
            }
        }

        public void Dispose()
        {
            _advanceTimer.Stop();
            _videoProgressTimer.Stop();
            _playback?.Stop();
            PresentedImage?.Dispose();
            foreach (var image in _retiredImages)
                image.Dispose();
            _retiredImages.Clear();
            foreach (var deck in Decks)
                deck.Dispose();
            Decks.Clear();
            SlideCache.ClearAll();
            _thumbnails.Dispose();
        }

        private async Task ImportPdfsAsync()
        {
            IReadOnlyList<string> files;
            try
            {
                files = await PickPdfFiles.Handle(Unit.Default);
            }
            catch (UnhandledInteractionException<Unit, IReadOnlyList<string>>)
            {
                return;
            }

            if (files.Count == 0)
                return;

            IsBusy = true;
            SlideDeckViewModel? lastDeck = null;
            try
            {
                foreach (var pdfPath in files)
                {
                    var name = Path.GetFileNameWithoutExtension(pdfPath);
                    var cache = SlideCache.NewDeckFolder();
                    StatusMessage = $"Importing {name}…";

                    IReadOnlyList<string> pages;
                    try
                    {
                        pages = await Task.Run(() => _pdfImport.ImportDeck(
                            pdfPath,
                            cache,
                            new Progress<(int Current, int Total)>(p =>
                            {
                                Dispatcher.UIThread.Post(() =>
                                    StatusMessage = $"Rendering {name} — page {p.Current} of {p.Total}");
                            })));
                    }
                    catch (Exception ex)
                    {
                        SlideCache.DeleteFolder(cache);
                        StatusMessage = $"Failed to import {name}: {ex.Message}";
                        continue;
                    }

                    var deck = new SlideDeckViewModel(name, pdfPath, cache);
                    foreach (var (path, index) in pages.Select((path, index) => (path, index)))
                    {
                        deck.Slides.Add(SlideItemViewModel.FromImage(
                            path,
                            index + 1,
                            ThumbnailSize,
                            ThumbnailHeight(),
                            PresentSlide,
                            RemoveSlide));
                    }

                    Decks.Add(deck);
                    lastDeck = deck;
                    StatusMessage = $"Imported {name} — {deck.Summary}. Click a slide to send it to the projector.";
                }

                if (lastDeck is not null)
                    SelectedDeck = lastDeck;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ImportMediaAsync()
        {
            IReadOnlyList<string> files;
            try
            {
                files = await PickVideoFiles.Handle(Unit.Default);
            }
            catch (UnhandledInteractionException<Unit, IReadOnlyList<string>>)
            {
                return;
            }

            if (files.Count == 0)
                return;

            var added = 0;
            foreach (var path in files)
            {
                if (Videos.Any(v => string.Equals(v.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var extension = Path.GetExtension(path);
                if (!MediaClipViewModel.IsVideoExtension(extension)
                    && !MediaClipViewModel.IsImageExtension(extension))
                    continue;

                Videos.Add(new MediaClipViewModel(path, ActivateMedia));
                added++;
            }

            StatusMessage = added == 1
                ? "Imported 1 media item. Drag it onto the slide grid, or use Play/Show."
                : $"Imported {added} media items.";
        }

        private void ActivateMedia(MediaClipViewModel clip)
        {
            if (clip.IsVideo)
                _ = PlayVideoAsync(clip);
            else
                PresentMediaImage(clip);
        }

        private void PresentMediaImage(MediaClipViewModel clip)
        {
            ClearSlideSelection();
            SelectedSlide = null;
            SelectedVideo = clip;
            StopVideo();
            IsBlanked = false;
            try
            {
                PresentedImage = new Bitmap(clip.FilePath);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not open {clip.Name}: {ex.Message}";
                return;
            }

            StatusMessage = $"Showing {clip.Name} on preview and projector.";
            RestartAdvanceTimer();
        }

        private async Task PlaySelectedVideoAsync()
        {
            if (SelectedVideo is null)
            {
                StatusMessage = "Select a media item from the left list, then press Play or Show.";
                return;
            }

            ActivateMedia(SelectedVideo);
            await Task.CompletedTask;
        }

        private async Task PlayVideoAsync(MediaClipViewModel clip, bool fromDeck = false)
        {
            if (_playback is null)
            {
                StatusMessage = "LibVLC is not available, so video playback cannot start.";
                return;
            }

            SelectedVideo = clip;
            if (!fromDeck)
            {
                ClearSlideSelection();
                SelectedSlide = null;
            }

            IsBlanked = false;
            IsVideoSurfaceReady = false;
            IsVideoActive = true;
            NotifyOutputState();

            var generation = ++_playGeneration;

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

            try
            {
                if (!await VlcPlaybackService.WaitForHostAsync(_playback.PreviewPlayer, 4000))
                {
                    if (generation == _playGeneration)
                        IsVideoActive = false;
                    StatusMessage = "Preview surface is not ready. The VLC window handle never attached — try another video renderer.";
                    return;
                }

                var vout = SelectedVideoRenderer.Module;
                if (!_playback.PlayPreview(clip.FilePath, vout))
                {
                    if (generation == _playGeneration)
                        IsVideoActive = false;
                    StatusMessage = "Could not start video on the preview panel.";
                    return;
                }

                var playingOnProjector = false;
                if (IsProjectorOpen)
                {
                    if (await VlcPlaybackService.WaitForHostAsync(_playback.OutputPlayer))
                        playingOnProjector = _playback.PlayOutput(clip.FilePath, vout, _playback.PreviewPlayer.Position);
                }
                else
                {
                    _playback.StopOutput();
                }

                ApplyAudio();
                await _playback.WaitUntilVideoReadyAsync(_playback.PreviewPlayer);

                if (generation != _playGeneration)
                    return;

                IsPaused = false;
                IsPlaying = true;
                IsVideoSurfaceReady = true;
                StatusMessage = playingOnProjector
                    ? $"Playing {clip.Name} on preview and projector ({SelectedVideoRenderer.Name})."
                    : $"Playing {clip.Name} on the preview panel ({SelectedVideoRenderer.Name}).";
            }
            catch (Exception ex)
            {
                if (generation == _playGeneration)
                {
                    IsVideoActive = false;
                    IsPlaying = false;
                }
                StatusMessage = $"Could not play {clip.Name}: {ex.Message}";
            }
        }

        private void TogglePause()
        {
            if (!IsVideoActive || _playback is null)
                return;

            IsPaused = !IsPaused;
            _playback.SetPause(IsPaused);
            StatusMessage = IsPaused ? "Video paused." : "Video playing.";
        }

        private void StopVideo()
        {
            _playGeneration++;
            _playback?.Stop();
            IsPlaying = false;
            IsPaused = false;
            IsVideoSurfaceReady = false;
            if (IsVideoActive)
            {
                IsVideoActive = false;
                StatusMessage = "Video stopped.";
            }

            NotifyOutputState();
        }

        private void OnVideoEnded()
        {
            IsPlaying = false;
            IsPaused = false;

            if (IsAutoAdvanceEnabled
                && !IsBlanked
                && SelectedSlide is { IsVideo: true }
                && SelectedDeck is not null)
            {
                var slides = SelectedDeck.Slides;
                var index = slides.IndexOf(SelectedSlide);
                if (index >= 0 && index + 1 < slides.Count)
                {
                    StatusMessage = "Video finished — advancing.";
                    PresentSlide(slides[index + 1]);
                    return;
                }

                if (IsLoopSlides && slides.Count > 0)
                {
                    StatusMessage = "Video finished — looping to the first slide.";
                    PresentSlide(slides[0]);
                    return;
                }

                IsAutoAdvanceEnabled = false;
                FinishEndedVideo("Reached the last slide. Auto-advance is off.");
                return;
            }

            FinishEndedVideo("Video finished.");
        }

        private void FinishEndedVideo(string message)
        {
            IsVideoActive = false;
            StatusMessage = message;
            NotifyOutputState();
        }

        private void ToggleBlank() => IsBlanked = !IsBlanked;

        private void ToggleMute() => IsMuted = !IsMuted;

        private void ApplyAudio() =>
            _playback?.ApplyAudio(IsMuted, (int)Math.Round(Volume));

        private void ToggleProjector() => IsProjectorOpen = !IsProjectorOpen;

        private void RemoveSelectedDeck()
        {
            if (SelectedDeck is null)
                return;

            var deck = SelectedDeck;
            if (SelectedSlide is not null && deck.Slides.Contains(SelectedSlide))
            {
                SelectedSlide = null;
                PresentedImage = null;
            }

            Decks.Remove(deck);
            deck.Dispose();
            SelectedDeck = Decks.FirstOrDefault();
            StatusMessage = $"Removed {deck.Name}.";
            NotifyOutputState();
        }

        private void RemoveSelectedSlide()
        {
            if (SelectedSlide is not null)
                RemoveSlide(SelectedSlide);
        }

        private void RemoveSelectedVideo()
        {
            if (SelectedVideo is null)
                return;

            var clip = SelectedVideo;
            if (IsVideoActive && _playback?.CurrentPath == clip.FilePath)
                StopVideo();

            Videos.Remove(clip);
            SelectedVideo = null;
            StatusMessage = $"Removed {clip.Name}.";
        }

        private void GoToNextSlide(bool stopAtEnd)
        {
            if (SelectedDeck is null || SelectedDeck.Slides.Count == 0)
                return;

            var slides = SelectedDeck.Slides;
            var index = SelectedSlide is null ? -1 : slides.IndexOf(SelectedSlide);
            var next = index + 1;
            if (next >= slides.Count)
            {
                if (IsLoopSlides)
                {
                    next = 0;
                }
                else if (stopAtEnd)
                {
                    IsAutoAdvanceEnabled = false;
                    StatusMessage = "Reached the last slide. Auto-advance is off.";
                    return;
                }
                else
                {
                    next = 0;
                }
            }

            PresentSlide(slides[next]);
        }

        private void GoToPreviousSlide()
        {
            if (SelectedDeck is null || SelectedDeck.Slides.Count == 0)
                return;

            var slides = SelectedDeck.Slides;
            var index = SelectedSlide is null ? 0 : slides.IndexOf(SelectedSlide);
            var previous = index <= 0 ? slides.Count - 1 : index - 1;
            PresentSlide(slides[previous]);
        }

        private void ApplyThumbnailSizeToSlides()
        {
            var height = ThumbnailHeight();
            if (SelectedDeck is null)
                return;

            foreach (var slide in SelectedDeck.Slides)
            {
                slide.DisplayWidth = ThumbnailSize;
                slide.DisplayHeight = height;
            }
        }

        private double ThumbnailHeight() => Math.Round(ThumbnailSize * 9d / 16d);

        private void ClearSlideSelection()
        {
            foreach (var deck in Decks)
            {
                foreach (var slide in deck.Slides)
                    slide.IsSelected = false;
            }
        }

        private void SelectOnly(SlideItemViewModel slide)
        {
            foreach (var deck in Decks)
            {
                foreach (var item in deck.Slides)
                    item.IsSelected = item == slide;
            }
        }

        private SlideDeckViewModel? FindDeck(SlideItemViewModel slide) =>
            Decks.FirstOrDefault(d => d.Slides.Contains(slide));

        private void SyncVideoProgressTimer()
        {
            if (IsVideoActive && IsPlaying && !IsPaused)
            {
                _videoProgressTimer.Start();
                UpdateVideoProgress();
                return;
            }

            _videoProgressTimer.Stop();
            if (!IsVideoActive)
            {
                _videoTimeMs = 0;
                _videoLengthMs = 0;
                RaiseVideoProgress();
            }
        }

        private void UpdateVideoProgress()
        {
            if (_playback is null || !IsVideoActive)
                return;

            _videoTimeMs = Math.Max(0, _playback.PreviewPlayer.Time);
            var length = _playback.PreviewPlayer.Length;
            if (length > 0)
                _videoLengthMs = length;

            RaiseVideoProgress();
        }

        private void RaiseVideoProgress()
        {
            this.RaisePropertyChanged(nameof(VideoTimeText));
            this.RaisePropertyChanged(nameof(VideoDurationText));
            this.RaisePropertyChanged(nameof(VideoProgress));
        }

        private static string FormatTimestamp(long milliseconds)
        {
            if (milliseconds < 0)
                milliseconds = 0;

            var totalSeconds = milliseconds / 1000;
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;
            if (minutes >= 60)
                return $"{minutes / 60}:{(minutes % 60):00}:{seconds:00}";

            return $"{minutes}:{seconds:00}";
        }

        private void RestartAdvanceTimer()
        {
            _advanceTimer.Stop();
            if (!IsAutoAdvanceEnabled || IsVideoActive || IsBlanked || SelectedDeck is null || SelectedDeck.Slides.Count == 0)
                return;

            var seconds = (double)Math.Max(1, AutoAdvanceSeconds);
            _advanceTimer.Interval = TimeSpan.FromSeconds(seconds);
            _advanceTimer.Start();
        }

        private void NotifyOutputState()
        {
            this.RaisePropertyChanged(nameof(ShowSlide));
            this.RaisePropertyChanged(nameof(ShowVideo));
            this.RaisePropertyChanged(nameof(ShowIdle));
            this.RaisePropertyChanged(nameof(OutputCaption));
        }
    }
}
