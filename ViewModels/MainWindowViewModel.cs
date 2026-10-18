using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using libvlc_media.Models;
using libvlc_media.Services;
using LibVLCSharp.Shared;
using QRCoder;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
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
        private readonly WorkspaceService _workspace = new();
        private readonly List<Bitmap> _retiredImages = new();
        private readonly Dictionary<string, byte[]> _remoteThumbCache = new();
        private readonly object _remoteThumbLock = new();
        private readonly DispatcherTimer _advanceTimer;
        private readonly DispatcherTimer _videoProgressTimer;
        private RemoteServer? _remote;

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

        private long _videoTimeMs;
        private long _videoLengthMs;
        private string _statusMessage = "Import a PDF to build a slide deck, or add an MP4 to play on the projector.";
        private VideoRendererOption _selectedVideoRenderer = VideoRenderers[3];
        private bool _isMuted;
        private double _volume = 80;
        private string _remoteUrl = string.Empty;
        private string _remoteStatus = "Tablet remote is starting…";
        private Bitmap? _remoteQrImage;
        private int _remoteClientCount;
        private int _remoteRevision;
        private string? _workspaceFolder;
        private string? _workspaceName;
        private bool _isWorkspaceDirty;
        private bool _copyVideosIntoWorkspace;
        private bool _suspendDirty;

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

            Decks.CollectionChanged += OnDecksChanged;
            Videos.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasVideos));

            PickPdfFiles = new Interaction<Unit, IReadOnlyList<string>>();
            PickVideoFiles = new Interaction<Unit, IReadOnlyList<string>>();
            PickSaveWorkspacePath = new Interaction<string, string?>();
            PickOpenWorkspacePath = new Interaction<Unit, string?>();
            ConfirmUnsaved = new Interaction<Unit, UnsavedDecision>();

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
            CopyRemoteUrlCommand = ReactiveCommand.CreateFromTask(CopyRemoteUrlAsync);
            OpenRemoteCommand = ReactiveCommand.Create(OpenRemoteInBrowser);
            OpenWorkspaceCommand = ReactiveCommand.CreateFromTask(OpenWorkspaceAsync);
            SaveWorkspaceCommand = ReactiveCommand.CreateFromTask(SaveWorkspaceAsync);
            SaveWorkspaceAsCommand = ReactiveCommand.CreateFromTask(SaveWorkspaceAsAsync);

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

        public Interaction<string, string?> PickSaveWorkspacePath { get; }

        public Interaction<Unit, string?> PickOpenWorkspacePath { get; }

        public Interaction<Unit, UnsavedDecision> ConfirmUnsaved { get; }

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

        public ICommand CopyRemoteUrlCommand { get; }

        public ICommand OpenRemoteCommand { get; }

        public ICommand OpenWorkspaceCommand { get; }

        public ICommand SaveWorkspaceCommand { get; }

        public ICommand SaveWorkspaceAsCommand { get; }

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

        public string WindowTitle
        {
            get
            {
                var name = string.IsNullOrWhiteSpace(_workspaceName) ? "Untitled" : _workspaceName;
                return IsWorkspaceDirty
                    ? $"{name}*  —  Slide Presenter"
                    : $"{name}  —  Slide Presenter";
            }
        }

        public bool IsWorkspaceDirty
        {
            get => _isWorkspaceDirty;
            private set
            {
                if (SetAndRaise(ref _isWorkspaceDirty, value))
                    this.RaisePropertyChanged(nameof(WindowTitle));
            }
        }

        public bool CopyVideosIntoWorkspace
        {
            get => _copyVideosIntoWorkspace;
            set
            {
                if (SetAndRaise(ref _copyVideosIntoWorkspace, value))
                    MarkDirty();
            }
        }

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
                    this.RaisePropertyChanged(nameof(IsAutoAdvanceEnabled));
                    this.RaisePropertyChanged(nameof(IsLoopSlides));
                    this.RaisePropertyChanged(nameof(AutoAdvanceSeconds));
                    RestartAdvanceTimer();
                    RaiseRemoteState();
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
            get => SelectedDeck?.IsAutoAdvanceEnabled ?? false;
            set
            {
                if (SelectedDeck is null || SelectedDeck.IsAutoAdvanceEnabled == value)
                    return;

                SelectedDeck.IsAutoAdvanceEnabled = value;
                this.RaisePropertyChanged(nameof(IsAutoAdvanceEnabled));
                RestartAdvanceTimer();
                MarkDirty();
            }
        }

        public bool IsLoopSlides
        {
            get => SelectedDeck?.IsLoopSlides ?? true;
            set
            {
                if (SelectedDeck is null || SelectedDeck.IsLoopSlides == value)
                    return;

                SelectedDeck.IsLoopSlides = value;
                this.RaisePropertyChanged(nameof(IsLoopSlides));
                MarkDirty();
            }
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
            get => SelectedDeck?.AutoAdvanceSeconds ?? 8;
            set
            {
                if (SelectedDeck is null || SelectedDeck.AutoAdvanceSeconds == value)
                    return;

                SelectedDeck.AutoAdvanceSeconds = value;
                this.RaisePropertyChanged(nameof(AutoAdvanceSeconds));
                RestartAdvanceTimer();
                MarkDirty();
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        public string RemoteUrl
        {
            get => _remoteUrl;
            private set => this.RaiseAndSetIfChanged(ref _remoteUrl, value);
        }

        public string RemoteStatus
        {
            get => _remoteStatus;
            private set => this.RaiseAndSetIfChanged(ref _remoteStatus, value);
        }

        public Bitmap? RemoteQrImage
        {
            get => _remoteQrImage;
            private set
            {
                var previous = _remoteQrImage;
                if (SetAndRaise(ref _remoteQrImage, value))
                {
                    previous?.Dispose();
                    this.RaisePropertyChanged(nameof(HasRemoteQr));
                }
            }
        }

        public bool HasRemoteQr => RemoteQrImage is not null;

        public int RemoteClientCount
        {
            get => _remoteClientCount;
            private set
            {
                if (SetAndRaise(ref _remoteClientCount, value))
                    UpdateRemoteStatus();
            }
        }

        public void StartTabletRemote()
        {
            if (_remote is not null)
                return;

            _remote = new RemoteServer(
                captureState: () => Dispatcher.UIThread.Invoke(BuildRemoteSnapshot),
                getThumbnail: GetRemoteThumbnail,
                applyCommand: command => Dispatcher.UIThread.Post(() => ApplyRemoteCommand(command)));
            _remote.ClientCountChanged += count =>
                Dispatcher.UIThread.Post(() => RemoteClientCount = count);
            _remote.Start();
            UpdateRemoteInfo();
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
                if (string.IsNullOrWhiteSpace(slide.VideoPath) || !File.Exists(slide.VideoPath))
                {
                    StatusMessage = $"Video file was not found: {slide.PageLabel}";
                    NotifyOutputState();
                    return;
                }

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
            MarkDirty();
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
                        RaiseRemoteState();
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
            MarkDirty();
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
            MarkDirty();
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
            MarkDirty();
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
            _remote?.Dispose();
            _remote = null;
            RemoteQrImage = null;
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
                    MarkDirty();
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
            if (added > 0)
                MarkDirty();
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
            MarkDirty();
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
            MarkDirty();
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
            RaiseRemoteState();
        }

        private void OnDecksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            this.RaisePropertyChanged(nameof(HasDecks));
            this.RaisePropertyChanged(nameof(HasSlides));
            if (e.NewItems is not null)
            {
                foreach (SlideDeckViewModel deck in e.NewItems)
                    deck.Slides.CollectionChanged += OnSlidesChanged;
            }

            if (e.OldItems is not null)
            {
                foreach (SlideDeckViewModel deck in e.OldItems)
                    deck.Slides.CollectionChanged -= OnSlidesChanged;
            }

            RaiseRemoteState();
        }

        private void OnSlidesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            this.RaisePropertyChanged(nameof(HasSlides));
            this.RaisePropertyChanged(nameof(SelectedDeckTitle));
            RaiseRemoteState();
        }

        private void RaiseRemoteState() => _remote?.NotifyStateChanged();

        private void UpdateRemoteInfo()
        {
            if (_remote is null)
                return;

            RemoteUrl = _remote.Urls.FirstOrDefault(url => url.Contains("127.0.0.1", StringComparison.Ordinal) is false)
                        ?? _remote.Urls.FirstOrDefault()
                        ?? string.Empty;
            UpdateRemoteStatus();
            BuildRemoteQr();
        }

        private void UpdateRemoteStatus()
        {
            if (_remote is { Error: { Length: > 0 } error })
            {
                RemoteStatus = error;
                return;
            }

            if (string.IsNullOrWhiteSpace(RemoteUrl) || RemoteUrl.Contains("127.0.0.1", StringComparison.Ordinal))
            {
                RemoteStatus = "No LAN address found. Connect this PC to Wi‑Fi or Ethernet so a tablet can reach it.";
                return;
            }

            RemoteStatus = RemoteClientCount == 0
                ? "Waiting for a tablet on the church Wi‑Fi. Same network as this PC."
                : RemoteClientCount == 1
                    ? "1 tablet connected"
                    : $"{RemoteClientCount} tablets connected";
        }

        private void BuildRemoteQr()
        {
            if (string.IsNullOrWhiteSpace(RemoteUrl))
            {
                RemoteQrImage = null;
                return;
            }

            try
            {
                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(RemoteUrl, QRCodeGenerator.ECCLevel.M);
                var png = new PngByteQRCode(data);
                var bytes = png.GetGraphic(6);
                using var stream = new MemoryStream(bytes);
                RemoteQrImage = new Bitmap(stream);
            }
            catch
            {
                RemoteQrImage = null;
            }
        }

        private RemoteSnapshot BuildRemoteSnapshot()
        {
            _remoteRevision++;
            var liveDeck = SelectedSlide is null ? null : FindDeck(SelectedSlide);
            var liveDeckIndex = liveDeck is null ? -1 : Decks.IndexOf(liveDeck);
            var liveSlideIndex = liveDeck is null || SelectedSlide is null
                ? -1
                : liveDeck.Slides.IndexOf(SelectedSlide);

            var snapshot = new RemoteSnapshot
            {
                Revision = _remoteRevision,
                Blanked = IsBlanked,
                OutputCaption = OutputCaption,
                BlankLabel = BlankLabel,
                SelectedDeckIndex = SelectedDeck is null ? -1 : Decks.IndexOf(SelectedDeck),
                SelectedSlideIndex = -1,
                LiveDeckIndex = liveDeckIndex,
                LiveSlideIndex = liveSlideIndex
            };

            if (SelectedDeck is not null && SelectedSlide is not null)
                snapshot.SelectedSlideIndex = SelectedDeck.Slides.IndexOf(SelectedSlide);

            for (var d = 0; d < Decks.Count; d++)
            {
                var deck = Decks[d];
                var dto = new RemoteDeckDto
                {
                    Index = d,
                    Name = deck.Name,
                    Summary = deck.Summary
                };

                for (var s = 0; s < deck.Slides.Count; s++)
                {
                    var slide = deck.Slides[s];
                    var hasFile = !string.IsNullOrWhiteSpace(slide.ImagePath) && File.Exists(slide.ImagePath);
                    var hasThumb = slide.HasThumbnail || hasFile;
                    var version = slide.HasThumbnail ? 1 : 0;
                    dto.Slides.Add(new RemoteSlideDto
                    {
                        Index = s,
                        Label = slide.PageLabel,
                        IsVideo = slide.IsVideo,
                        HasThumb = hasThumb,
                        Thumb = hasThumb ? $"/thumb/{d}/{s}?v={version}" : string.Empty
                    });
                }

                snapshot.Decks.Add(dto);
            }

            return snapshot;
        }

        private void ApplyRemoteCommand(RemoteCommand command)
        {
            switch (command.Type.Trim().ToLowerInvariant())
            {
                case "next":
                    GoToNextSlide(stopAtEnd: false);
                    break;
                case "prev":
                case "previous":
                    GoToPreviousSlide();
                    break;
                case "blank":
                    ToggleBlank();
                    break;
                case "selectdeck":
                    if (command.DeckIndex >= 0 && command.DeckIndex < Decks.Count)
                        SelectedDeck = Decks[command.DeckIndex];
                    break;
                case "present":
                    if (command.DeckIndex < 0 || command.DeckIndex >= Decks.Count)
                        return;
                    var deck = Decks[command.DeckIndex];
                    if (command.SlideIndex < 0 || command.SlideIndex >= deck.Slides.Count)
                        return;
                    SelectedDeck = deck;
                    PresentSlide(deck.Slides[command.SlideIndex]);
                    break;
            }
        }

        private byte[]? GetRemoteThumbnail(int deckIndex, int slideIndex, int width)
        {
            string? path = null;
            Bitmap? memory = null;
            Dispatcher.UIThread.Invoke(() =>
            {
                if (deckIndex < 0 || deckIndex >= Decks.Count)
                    return;
                var deck = Decks[deckIndex];
                if (slideIndex < 0 || slideIndex >= deck.Slides.Count)
                    return;
                var slide = deck.Slides[slideIndex];
                if (!string.IsNullOrWhiteSpace(slide.ImagePath) && File.Exists(slide.ImagePath))
                    path = slide.ImagePath;
                else
                    memory = slide.Thumbnail;
            });

            if (path is not null)
                return EncodeFileThumbnail(path, width);

            if (memory is null)
                return null;

            return Dispatcher.UIThread.Invoke(() =>
            {
                try
                {
                    using var ms = new MemoryStream();
                    memory.Save(ms);
                    return ms.ToArray();
                }
                catch
                {
                    return null;
                }
            });
        }

        private byte[]? EncodeFileThumbnail(string path, int width)
        {
            try
            {
                width = Math.Clamp(width, 160, 1920);
                var info = new FileInfo(path);
                var key = $"{path}:{info.Length}:{info.LastWriteTimeUtc.Ticks}:{width}";
                lock (_remoteThumbLock)
                {
                    if (_remoteThumbCache.TryGetValue(key, out var cached))
                        return cached;
                }

                using var stream = File.OpenRead(path);
                using var bitmap = Bitmap.DecodeToWidth(stream, width);
                using var ms = new MemoryStream();
                bitmap.Save(ms);
                var bytes = ms.ToArray();
                lock (_remoteThumbLock)
                {
                    if (_remoteThumbCache.Count > 120)
                        _remoteThumbCache.Clear();
                    _remoteThumbCache[key] = bytes;
                }

                return bytes;
            }
            catch
            {
                return null;
            }
        }

        private async Task CopyRemoteUrlAsync()
        {
            if (string.IsNullOrWhiteSpace(RemoteUrl))
                return;

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow.Clipboard: { } clipboard
                })
            {
                await clipboard.SetTextAsync(RemoteUrl);
                StatusMessage = "Tablet remote address copied.";
            }
        }

        private void OpenRemoteInBrowser()
        {
            if (string.IsNullOrWhiteSpace(RemoteUrl))
                return;

            Process.Start(new ProcessStartInfo(RemoteUrl) { UseShellExecute = true });
        }

        private void MarkDirty()
        {
            if (_suspendDirty)
                return;
            IsWorkspaceDirty = true;
        }

        private void SetWorkspaceIdentity(string? folder, string? name, bool dirty)
        {
            _workspaceFolder = folder;
            _workspaceName = name;
            IsWorkspaceDirty = dirty;
            this.RaisePropertyChanged(nameof(WindowTitle));
        }

        public async Task<bool> ConfirmDiscardOrSaveAsync()
        {
            if (!IsWorkspaceDirty)
                return true;

            UnsavedDecision decision;
            try
            {
                decision = await ConfirmUnsaved.Handle(Unit.Default);
            }
            catch (UnhandledInteractionException<Unit, UnsavedDecision>)
            {
                return !IsWorkspaceDirty;
            }

            return decision switch
            {
                UnsavedDecision.Discard => true,
                UnsavedDecision.Save => await SaveWorkspaceAsync(),
                _ => false
            };
        }

        private async Task OpenWorkspaceAsync()
        {
            if (IsBusy)
                return;

            if (!await ConfirmDiscardOrSaveAsync())
                return;

            string? picked;
            try
            {
                picked = await PickOpenWorkspacePath.Handle(Unit.Default);
            }
            catch (UnhandledInteractionException<Unit, string?>)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(picked))
                return;

            var folder = WorkspaceService.FindWorkspaceFolder(picked);
            if (folder is null)
            {
                StatusMessage = "That folder is not a slide workspace. Pick a folder that contains workspace.json.";
                return;
            }

            IsBusy = true;
            try
            {
                var loaded = await Task.Run(() => _workspace.Load(folder));
                ApplyLoadedWorkspace(loaded);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not open workspace: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task<bool> SaveWorkspaceAsync()
        {
            if (string.IsNullOrWhiteSpace(_workspaceFolder))
                return await SaveWorkspaceAsAsync();

            return await WriteWorkspaceAsync(_workspaceFolder, _workspaceName ?? Path.GetFileName(_workspaceFolder));
        }

        private async Task<bool> SaveWorkspaceAsAsync()
        {
            string? picked;
            try
            {
                picked = await PickSaveWorkspacePath.Handle(_workspaceName ?? DateTime.Now.ToString("yyyy-MM-dd"));
            }
            catch (UnhandledInteractionException<string, string?>)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(picked))
                return false;

            var folder = WorkspaceService.ResolveFolderFromSavePath(picked);
            var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return await WriteWorkspaceAsync(folder, name);
        }

        private async Task<bool> WriteWorkspaceAsync(string folder, string name)
        {
            if (IsBusy)
                return false;

            IsBusy = true;
            try
            {
                var request = BuildSaveRequest(name);
                var saved = await Task.Run(() =>
                    _workspace.SaveAsync(
                        folder,
                        request,
                        new Progress<string>(message =>
                            Dispatcher.UIThread.Post(() => StatusMessage = message))).GetAwaiter().GetResult());

                ApplySavedWorkspace(saved);
                SetWorkspaceIdentity(saved.Folder, saved.Name, dirty: false);
                StatusMessage = CopyVideosIntoWorkspace
                    ? $"Saved {saved.Name} (videos copied into the workspace folder)."
                    : $"Saved {saved.Name}.";
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not save workspace: {ex.Message}";
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private WorkspaceSaveRequest BuildSaveRequest(string name)
        {
            var request = new WorkspaceSaveRequest
            {
                Name = name,
                CopyVideos = CopyVideosIntoWorkspace
            };

            foreach (var deck in Decks)
            {
                var deckReq = new WorkspaceDeckSaveRequest
                {
                    Name = deck.Name,
                    SourcePdf = deck.SourcePath,
                    AutoAdvance = deck.IsAutoAdvanceEnabled,
                    LoopSlides = deck.IsLoopSlides,
                    AutoAdvanceSeconds = deck.AutoAdvanceSeconds
                };

                foreach (var slide in deck.Slides)
                {
                    deckReq.Slides.Add(new WorkspaceSlideSaveRequest
                    {
                        IsVideo = slide.IsVideo,
                        SourcePath = slide.IsVideo ? slide.VideoPath ?? string.Empty : slide.ImagePath,
                        Label = slide.PageLabel
                    });
                }

                request.Decks.Add(deckReq);
            }

            foreach (var clip in Videos)
                request.LibraryPaths.Add(clip.FilePath);

            return request;
        }

        private void ApplySavedWorkspace(SavedWorkspace saved)
        {
            for (var d = 0; d < Decks.Count && d < saved.Decks.Count; d++)
            {
                var deck = Decks[d];
                var savedDeck = saved.Decks[d];
                var oldCache = deck.CacheDirectory;
                var owned = deck.OwnsCacheDirectory;
                deck.CacheDirectory = savedDeck.Directory;
                deck.OwnsCacheDirectory = false;

                for (var s = 0; s < deck.Slides.Count && s < savedDeck.SlidePaths.Count; s++)
                {
                    var path = savedDeck.SlidePaths[s];
                    if (deck.Slides[s].IsVideo)
                        deck.Slides[s].RetargetVideo(path);
                    else
                        deck.Slides[s].RetargetImage(path);
                }

                if (owned
                    && !string.IsNullOrWhiteSpace(oldCache)
                    && !IsPathUnder(oldCache, saved.Folder))
                    SlideCache.DeleteFolder(oldCache);
            }

            SelectedVideo = null;
            var rebuilt = new List<MediaClipViewModel>();
            foreach (var path in saved.LibraryPaths.Distinct(StringComparer.OrdinalIgnoreCase))
                rebuilt.Add(new MediaClipViewModel(path, ActivateMedia));
            Videos.Clear();
            foreach (var clip in rebuilt)
                Videos.Add(clip);
        }

        private void ApplyLoadedWorkspace(LoadedWorkspace loaded)
        {
            _suspendDirty = true;
            try
            {
                StopVideo();
                PresentedImage = null;
                SelectedSlide = null;
                SelectedVideo = null;
                foreach (var deck in Decks.ToList())
                    deck.Dispose();
                Decks.Clear();
                Videos.Clear();

                CopyVideosIntoWorkspace = loaded.CopyVideos;

                foreach (var loadedDeck in loaded.Decks)
                {
                    var deck = new SlideDeckViewModel(loadedDeck.Name, loadedDeck.SourcePdf ?? string.Empty, loadedDeck.Directory)
                    {
                        OwnsCacheDirectory = false,
                        IsAutoAdvanceEnabled = loadedDeck.AutoAdvance,
                        IsLoopSlides = loadedDeck.LoopSlides,
                        AutoAdvanceSeconds = loadedDeck.AutoAdvanceSeconds
                    };

                    var page = 0;
                    foreach (var loadedSlide in loadedDeck.Slides)
                    {
                        if (loadedSlide.IsVideo)
                        {
                            var item = SlideItemViewModel.FromVideo(
                                loadedSlide.Path,
                                ThumbnailSize,
                                ThumbnailHeight(),
                                PresentSlide,
                                RemoveSlide);
                            deck.Slides.Add(item);
                            if (!loadedSlide.Missing)
                                _ = CaptureVideoThumbnailAsync(item);
                        }
                        else
                        {
                            page++;
                            var pageNumber = int.TryParse(loadedSlide.Label, out var n) ? n : page;
                            deck.Slides.Add(SlideItemViewModel.FromImage(
                                loadedSlide.Path,
                                pageNumber,
                                ThumbnailSize,
                                ThumbnailHeight(),
                                PresentSlide,
                                RemoveSlide));
                        }
                    }

                    Decks.Add(deck);
                }

                foreach (var path in loaded.LibraryPaths)
                {
                    if (Videos.Any(v => string.Equals(v.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    Videos.Add(new MediaClipViewModel(path, ActivateMedia));
                }

                foreach (var deck in Decks)
                {
                    foreach (var slide in deck.Slides.Where(s => s.IsVideo && !s.FileMissing))
                    {
                        if (Videos.Any(v => string.Equals(v.FilePath, slide.VideoPath, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        if (!string.IsNullOrWhiteSpace(slide.VideoPath))
                            Videos.Add(new MediaClipViewModel(slide.VideoPath, ActivateMedia));
                    }
                }

                SelectedDeck = Decks.FirstOrDefault();
                SetWorkspaceIdentity(loaded.Folder, loaded.Name, dirty: false);
                NotifyOutputState();

                if (loaded.Warnings.Count == 0)
                    StatusMessage = $"Opened {loaded.Name} — {Decks.Count} deck{(Decks.Count == 1 ? string.Empty : "s")}.";
                else
                    StatusMessage = $"Opened {loaded.Name} with {loaded.Warnings.Count} warning{(loaded.Warnings.Count == 1 ? string.Empty : "s")}: {loaded.Warnings[0]}";
            }
            finally
            {
                _suspendDirty = false;
            }
        }

        private static bool IsPathUnder(string path, string root)
        {
            var full = Path.GetFullPath(path);
            var prefix = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
