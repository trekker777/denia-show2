using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using libvlc_media.Models;
using libvlc_media.Services;
using libvlc_media.ViewModels;
using libvlc_media.Views;
using System;

namespace libvlc_media
{
    public partial class App : Application
    {
        private VlcPlaybackService? _vlc;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                SlideCache.ClearAll();

                string? vlcError = null;
                try
                {
                    _vlc = new VlcPlaybackService();
                }
                catch (Exception ex)
                {
                    _vlc = null;
                    vlcError = ex.Message;
                }

                var viewModel = new MainWindowViewModel(_vlc);
                if (vlcError is not null)
                {
                    viewModel.StatusMessage =
                        $"LibVLC failed to start ({vlcError}). Slides still work; video playback is unavailable.";
                }

                desktop.MainWindow = new MainWindow
                {
                    DataContext = viewModel,
                };

                desktop.Exit += (_, _) =>
                {
                    viewModel.Dispose();
                    _vlc?.Dispose();
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
