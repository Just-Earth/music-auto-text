using System;
using System.Threading.Tasks;

namespace WpfApp1.Services
{
    using Windows.Media.Control;
    using Windows.Storage.Streams;
    using System.IO;
    using System.Runtime.InteropServices.WindowsRuntime;

    // Watches Global System Media Transport Controls sessions and raises media property changes.
    public class MediaSessionWatcher : IDisposable
    {
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;

        public event Action<string, string, string, string?>? OnMediaChanged; // title, artist, album, coverPath (local file)
        // raised when playback state changes: true == playing
        public event Action<bool>? OnPlaybackStateChanged;

        public async Task StartAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
                UpdateCurrentSession();
            }
            catch
            {
                // ignore if WinRT not available
            }
        }

        private void Manager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args)
        {
            UpdateCurrentSession();
        }

        private async void UpdateCurrentSession()
        {
            try
            {
                if (_manager == null) return;
                var sess = _manager.GetCurrentSession();
                if (sess == _session) return;
                if (_session != null)
                {
                    _session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                    try { _session.PlaybackInfoChanged -= Session_PlaybackInfoChanged; } catch { }
                }
                _session = sess;
                if (_session != null)
                {
                    _session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                    try { _session.PlaybackInfoChanged += Session_PlaybackInfoChanged; } catch { }
                }
                await RaiseCurrentPropertiesAsync();
            }
            catch { }
        }

        private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            try
            {
                var info = sender.GetPlaybackInfo();
                bool isPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                OnPlaybackStateChanged?.Invoke(isPlaying);
            }
            catch { }
        }

        private async void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            await RaiseCurrentPropertiesAsync();
        }

        private async Task RaiseCurrentPropertiesAsync()
        {
            try
            {
                if (_session == null)
                {
                    OnMediaChanged?.Invoke(string.Empty, string.Empty, string.Empty, null);
                    return;
                }
                var props = await _session.TryGetMediaPropertiesAsync();
                var title = props?.Title ?? string.Empty;
                var artist = props?.Artist ?? string.Empty;
                var album = props?.AlbumTitle ?? string.Empty;
                string? coverPath = null;
                try
                {
                    var thumbRef = props?.Thumbnail;
                    if (thumbRef != null)
                    {
                        var ras = await thumbRef.OpenReadAsync();
                        using (var s = ras.AsStreamForRead())
                        {
                            var outPath = Path.Combine(Path.GetTempPath(), "smtc_thumb_" + Guid.NewGuid().ToString() + ".jpg");
                            using (var fs = File.Create(outPath))
                            {
                                await s.CopyToAsync(fs);
                            }
                            coverPath = outPath;
                        }
                    }
                }
                catch { coverPath = null; }

                OnMediaChanged?.Invoke(title, artist, album, coverPath);
                try
                {
                    var info = _session.GetPlaybackInfo();
                    bool isPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    OnPlaybackStateChanged?.Invoke(isPlaying);
                }
                catch { }
            }
            catch { }
        }

        public void Dispose()
        {
            try
            {
                if (_manager != null) _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
                if (_session != null)
                {
                    _session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                    try { _session.PlaybackInfoChanged -= Session_PlaybackInfoChanged; } catch { }
                }
            }
            catch { }
        }
    }
}
