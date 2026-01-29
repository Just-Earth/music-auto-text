// Minimal shim to allow building without LibVLCSharp package present.
// This provides small subset of types used by the app (for compile/run in development).
using System;
using System.IO;
using NAudio.Wave;
using System.Threading;

namespace WpfApp1.DevShims
{
    public static class Core
    {
        public static void Initialize() { }
    }

    public class LibVLC : IDisposable
    {
        public LibVLC() { }
        public void Dispose() { }
    }

    // Minimal Media shim that holds a URI (file path)
    public class Media : IDisposable
    {
        public Uri Uri { get; }
        public Media(LibVLC lib, Uri uri) { Uri = uri; }
        public void Dispose() { }
    }

    // MediaPlayer implemented using NAudio to provide real MP3 playback in absence of LibVLCSharp.
    public class MediaPlayer : IDisposable
    {
        private LibVLC _lib;
        private IWavePlayer? _waveOut;
        private AudioFileReader? _reader;
        private string? _currentPath;
        private readonly object _lock = new object();

        public MediaPlayer(LibVLC lib) { _lib = lib; }

        public event EventHandler? EndReached;

        // Volume 0..100
        private int _volume = 100;
        public int Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Max(0, Math.Min(100, value));
                lock (_lock)
                {
                    if (_waveOut != null)
                    {
                        try { _waveOut.Volume = _volume / 100f; } catch { }
                    }
                    if (_reader != null)
                    {
                        try { _reader.Volume = 1.0f; } catch { }
                    }
                }
            }
        }

        // milliseconds
        public long Length
        {
            get
            {
                lock (_lock)
                {
                    if (_reader == null) return 0;
                    try { return (long)_reader.TotalTime.TotalMilliseconds; } catch { return 0; }
                }
            }
        }

        public long Time
        {
            get
            {
                lock (_lock)
                {
                    if (_reader == null) return 0;
                    try { return (long)_reader.CurrentTime.TotalMilliseconds; } catch { return 0; }
                }
            }
            set
            {
                lock (_lock)
                {
                    if (_reader == null) return;
                    try { _reader.CurrentTime = TimeSpan.FromMilliseconds(Math.Max(0, Math.Min((long)_reader.TotalTime.TotalMilliseconds, value))); } catch { }
                }
            }
        }

        public void Play()
        {
            lock (_lock)
            {
                if (_waveOut == null && _reader != null)
                {
                    _waveOut = new WaveOutEvent();
                    _waveOut.Init(_reader);
                    _waveOut.Volume = _volume / 100f;
                    _waveOut.PlaybackStopped += OnPlaybackStopped;
                }
                try { _waveOut?.Play(); } catch { }
            }
        }

        public void Play(Media media)
        {
            if (media == null) return;
            var path = media.Uri.IsFile ? media.Uri.LocalPath : media.Uri.ToString();
            if (string.IsNullOrWhiteSpace(path)) return;
            lock (_lock)
            {
                try
                {
                    StopInternal();
                    _reader = new AudioFileReader(path);
                    _reader.Volume = 1.0f;
                    _waveOut = new WaveOutEvent();
                    _waveOut.Init(_reader);
                    _waveOut.PlaybackStopped += OnPlaybackStopped;
                    _waveOut.Volume = _volume / 100f;
                    _currentPath = path;
                    _waveOut.Play();
                }
                catch (Exception)
                {
                    StopInternal();
                    throw;
                }
            }
        }

        public void Pause()
        {
            lock (_lock)
            {
                try { _waveOut?.Pause(); } catch { }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                StopInternal();
            }
        }

        private void StopInternal()
        {
            try
            {
                if (_waveOut != null)
                {
                    _waveOut.PlaybackStopped -= OnPlaybackStopped;
                    try { _waveOut.Stop(); } catch { }
                    try { _waveOut.Dispose(); } catch { }
                    _waveOut = null;
                }
            }
            catch { }
            try
            {
                if (_reader != null)
                {
                    try { _reader.Dispose(); } catch { }
                    _reader = null;
                }
            }
            catch { }
            _currentPath = null;
        }

        private void OnPlaybackStopped(object? s, StoppedEventArgs e)
        {
            // raise EndReached on UI thread if possible
            try
            {
                EndReached?.Invoke(this, EventArgs.Empty);
            }
            catch { }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                StopInternal();
            }
        }
    }
}
