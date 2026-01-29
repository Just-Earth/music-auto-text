using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Collections.Generic;
using System.Threading.Tasks;
using WpfApp1.Models;
using WpfApp1.Services;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using System.Windows.Input;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Net.Http;
using System.IO.Compression;

// Alias for VLC player
using VlcPlayerType = LibVLCSharp.Shared.MediaPlayer;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        private double _pendingScale = 1.0;
        private double _originalScale = 1.0;

        private readonly LyricsService _lyricsService = new LyricsService();
        private List<LyricsLine> _currentLyrics = new List<LyricsLine>();
        private int _currentLyricIndex = -1;
        private bool _isPlaying = false;

        private LibVLC? _libVLC;
        private VlcPlayerType? _vlcPlayer;
        private DispatcherTimer _progressTimer;

        // keep media reference so we can delay play
        private Media? _pendingMedia = null;
        private string? _pendingFilePath = null;

        // seek state
        private bool _isSeeking = false;

        // whether the volume popup was opened by clicking the button (persist open)
        private bool _popupPersistOpen = false;

        // startup latency for playback
        private double _playbackStartupLatencyMs = 0.0;
        // automatic alignment offset (positive means move text earlier when applied as subtraction)
        private double _autoSyncOffsetMs = 0.0;

        // resync variable
        private CancellationTokenSource? _resyncCts;
        private bool _autoResyncEnabled = true; // enabled by default

        // Whisper usage

        // Real-time music info service
        private MusicInfoService? _musicInfoService = null;
        private bool _realTimeEnabled = false;

        private RealTimeAudioService? _realTimeAudio = null;
        private bool _inMp3Mode = false;
        private MediaSessionWatcher? _mediaWatcher = null;
        private Action<string, string, string, string?>? _mediaWatcherHandler = null;
        // current playback state reported by system media session (true == playing)
        private bool _systemIsPlaying = false;
        // previous band values for smoothing the visualizer
        private double[]? _prevBands = null;
        // (removed unused WhisperClient field)
        // debug UI toggle removed
        // cancellation for concurrent cover image loads
        private CancellationTokenSource? _coverLoadCts = null;

        // Neural aligner optional
        private NeuralAligner? _neuralAligner = null;
        private bool _useNeuralAligner = false; // disabled by default

        public MainWindow()
        {
            InitializeComponent();

            // whisper client removed

            // ensure ProgressCanvas events wired in case XAML not loaded handlers
            ProgressCanvas.MouseLeftButtonDown += ProgressCanvas_MouseLeftButtonDown;
            ProgressCanvas.MouseLeftButtonUp += ProgressCanvas_MouseLeftButtonUp;
            ProgressCanvas.MouseMove += ProgressCanvas_MouseMove;

            // volume handlers
            VolumeButton.Click += VolumeButton_Click;
            VolumeContainer.MouseEnter += (s, e) => OpenVolumePopup();
            VolumeContainer.MouseLeave += (s, e) => CloseVolumePopupIfAppropriate();
            VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;

            // Initialize LibVLC for audio playback
            Core.Initialize();
            _libVLC = new LibVLC();
            _vlcPlayer = new VlcPlayerType(_libVLC!);
            _vlcPlayer.EndReached += VlcPlayer_EndReached;

            LoadMp3Button.Click += LoadMp3Button_Click;
            RealTimeButton.Click += RealTimeButton_Click;
            SettingsButton.Click += SettingsButton_Click;
            CloseSettingsButton.Click += CloseSettingsButton_Click;
            ApplySettingsButton.Click += ApplySettingsButton_Click;
            CancelSettingsButton.Click += CancelSettingsButton_Click;
            ScaleSlider.ValueChanged += ScaleSlider_ValueChanged;

            PlayPauseBottomButton.Click += PlayPauseButton_Click;
            BackButton.Click += BackButton_Click;

            // real-time audio visualizer service
            _realTimeAudio = new RealTimeAudioService(1024);
            _realTimeAudio.OnBandsReady += RealTimeAudio_OnBandsReady;

            // media session watcher (WinRT GSSM) - lazy start
            try
            {
                _mediaWatcher = new MediaSessionWatcher();
                _ = _mediaWatcher.StartAsync();
                _mediaWatcherHandler = (title, artist, album, coverPath) =>
                {
                    // update UI only when we are NOT in MP3 mode and not playing local media
                    Dispatcher.Invoke(() =>
                    {
                        if (!_inMp3Mode && !_isPlaying)
                        {
                            if (string.IsNullOrWhiteSpace(title))
                            {
                                SongTitleText.Text = string.Empty;
                                WallpaperTitleText.Text = string.Empty;
                                AlbumCoverImage.Source = null;
                                WallpaperCoverImage.Source = null;
                            }
                            else
                            {
                                SongTitleText.Text = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}";
                                WallpaperTitleText.Text = SongTitleText.Text;
                                // if SMTC provided a saved cover path, load it (coverPath currently null on some platforms)
                                if (!string.IsNullOrWhiteSpace(coverPath))
                                {
                                    try
                                    {
                                        if (File.Exists(coverPath))
                                        {
                                            var bi = new System.Windows.Media.Imaging.BitmapImage();
                                            bi.BeginInit();
                                            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                            bi.UriSource = new Uri(coverPath);
                                            bi.EndInit();
                                            bi.Freeze();
                                            AlbumCoverImage.Source = bi;
                                            WallpaperCoverImage.Source = bi;
                                            WallpaperPanel.Visibility = Visibility.Visible;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    });
                };
                _mediaWatcher.OnMediaChanged += _mediaWatcherHandler;
                // subscribe to playback state changes so we can silence visualizer when system playback is paused
                try
                {
                    _mediaWatcher.OnPlaybackStateChanged += (isPlaying) =>
                    {
                        _systemIsPlaying = isPlaying;
                        // If system paused and we're not playing local MP3, clear visualizer
                        if (!_inMp3Mode && !_isPlaying && !_systemIsPlaying)
                        {
                            try { Dispatcher.Invoke(() => AudioVisualizerCanvas.Children.Clear()); } catch { }
                        }
                    };
                }
                catch { }
                // also start MusicInfoService as a fallback/cover lookup service
                try
                {
                    _musicInfoService ??= new MusicInfoService();
                    _musicInfoService.OnMusicChanged += MusicInfoService_OnMusicChanged;
                    _musicInfoService.StartListening();
                }
                catch { }
            }
            catch { }

            // install deps button hookup
            InstallDepsButton.Click += InstallDepsButton_Click;

            // Lyrics overlay buttons
            LoadLyricsButton.Click += LoadLyricsButton_Click;
            CancelLyricsButton.Click += CancelLyricsButton_Click;
            RetryLyricsButton.Click += RetryLyricsButton_Click;
            ContinueNoLyricsButton.Click += ContinueNoLyricsButton_Click;
            ExitToMenuButton.Click += ExitToMenuButton_Click;

            // neural aligner buttons
            GenerateSamplesButton.Click += GenerateSamplesButton_Click;
            TrainModelButton.Click += TrainModelButton_Click;
            UseNeuralAlignButton.Click += UseNeuralAlignButton_Click;
            // mini aligner UI hooks
            var useMini = this.FindName("UseMiniAlignCheck") as CheckBox;
            if (useMini != null) useMini.Checked += (s,e) => { /* noop */ };
            var exportBtn = this.FindName("ExportFeaturesButton") as Button;
            if (exportBtn != null) exportBtn.Click += ExportFeaturesButton_Click;
            // reflect initial state
            UseNeuralAlignButton.Content = "Использовать нейрон: выкл";

            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _progressTimer.Tick += ProgressTimer_Tick;

            // restore saved volume from file
            try
            {
                var saved = LoadSavedVolume();
                if (saved.HasValue)
                {
                    VolumeSlider.Value = saved.Value;
                    if (_vlcPlayer != null) _vlcPlayer.Volume = (int)saved.Value;
                }
            }
            catch { }
        }

        private void UseNeuralAlignButton_Click(object? sender, RoutedEventArgs e)
        {
            _useNeuralAligner = !_useNeuralAligner;
            if (_useNeuralAligner)
            {
                // lazy create
                try
                {
                    _neuralAligner ??= new NeuralAligner();
                    var model = _neuralAligner.LoadModel();
                    if (model == null)
                    {
                        MessageBox.Show("Модель нейронной сети не найдена. Обучите модель или отключите использование нейрона.", "Инфо", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch
                {
                    MessageBox.Show("Не удалось инициализировать нейронный модуль.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    _useNeuralAligner = false;
                }
            }
            UseNeuralAlignButton.Content = _useNeuralAligner ? "Использовать нейрон: вкл" : "Использовать нейрон: выкл";
        }

        private void GenerateSamplesButton_Click(object? sender, RoutedEventArgs e)
        {
            // feature potentially unsafe; keep as no-op but inform user
            MessageBox.Show("Генерация обучающих примеров отключена в этой версии.", "Инфо", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void TrainModelButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                _neuralAligner ??= new NeuralAligner();
                bool ok = _neuralAligner.TrainFromDb();
                if (ok) MessageBox.Show("Обучение завершено и модель сохранена.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
                else MessageBox.Show("Нет данных для обучения или ошибка при обучении.", "Инфо", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка обучения: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void VlcPlayer_EndReached(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _isPlaying = false;
                PlayPauseBottomButton.Content = "▶";
                _progressTimer.Stop();

                // stop resync
                _resyncCts?.Cancel();
            });
        }

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (_vlcPlayer is not null && _vlcPlayer.Length > 0)
            {
                var pos = _vlcPlayer.Time / (double)_vlcPlayer.Length;
                UpdateCustomProgress(pos);

                var current = TimeSpan.FromMilliseconds(_vlcPlayer.Time);
                // Use timestamp-based active-line detection (no word-level tracking)
                _currentLyricIndex = LyricsLine.FindActiveLineIndex(_currentLyrics, current, _currentLyricIndex, TimeSpan.FromSeconds(3));
                ApplyIntensityToLyricsByIndex(_currentLyricIndex);

                // update time texts
                ProgressCurrentText.Text = FormatTime(current);
                ProgressTotalText.Text = FormatTime(TimeSpan.FromMilliseconds(_vlcPlayer.Length));
            }
            else
            {
                ProgressCurrentText.Text = "0:00";
                ProgressTotalText.Text = "0:00";
            }
        }

        private string FormatTime(TimeSpan t)
        {
            if (t.TotalHours >= 1)
                return string.Format("{0}:{1:D2}:{2:D2}", (int)t.TotalHours, t.Minutes, t.Seconds);
            return string.Format("{0}:{1:D2}", (int)t.TotalMinutes, t.Seconds);
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vlcPlayer == null) return;

            if (_isPlaying)
            {
                _vlcPlayer.Pause();
                _isPlaying = false;
                PlayPauseBottomButton.Content = "▶";
                _progressTimer.Stop();

                // pause resync
                _resyncCts?.Cancel();
            }
            else
            {
                _vlcPlayer.Play();
                _isPlaying = true;
                PlayPauseBottomButton.Content = "⏸";
                _progressTimer.Start();

                    // live transcriber removed — no action

                // resume resync
                if (_autoResyncEnabled)
                {
                    _resyncCts?.Cancel();
                    _resyncCts = new CancellationTokenSource();
                    _ = StartAutoResyncLoopAsync(_resyncCts.Token);
                }
            }
        }

        private async void LoadMp3Button_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog();
            dlg.Filter = "MP3 files (*.mp3)|*.mp3|All files (*.*)|*.*";
            bool? result = dlg.ShowDialog();
            if (result == true)
            {
                var path = dlg.FileName;
                _pendingFilePath = path;

                // Update UI immediately: hide left column and show player chrome
                Dispatcher.Invoke(() =>
                {
                    var parsedTitle = ParseFilenameForArtistTitle(path);
                    // prefer artist - title if parsed, otherwise filename
                    if (!string.IsNullOrWhiteSpace(parsedTitle.artist) || !string.IsNullOrWhiteSpace(parsedTitle.title))
                    {
                        SongTitleText.Text = string.IsNullOrWhiteSpace(parsedTitle.artist) ? parsedTitle.title : $"{parsedTitle.artist} - {parsedTitle.title}";
                    }
                    else
                    {
                        SongTitleText.Text = System.IO.Path.GetFileNameWithoutExtension(path);
                    }
                    // stop real-time music info to avoid overwriting title
                    try
                    {
                        if (_musicInfoService != null)
                        {
                            _musicInfoService.OnMusicChanged -= MusicInfoService_OnMusicChanged;
                            _musicInfoService.StopListening();
                        }
                    }
                    catch { }
                    try { _realTimeAudio?.Stop(); AudioVisualizerCanvas.Visibility = Visibility.Collapsed; } catch { }
                    LeftButtonsPanel.Visibility = Visibility.Collapsed;
                    // collapse the left column so the player area uses the full window
                    if (RootGrid.ColumnDefinitions.Count > 0)
                    {
                        RootGrid.ColumnDefinitions[0].Width = new GridLength(0);
                    }
                    ProgressAreaGrid.Visibility = Visibility.Visible;
                    PlayPauseBottomButton.Visibility = Visibility.Visible;
                    BackButton.Visibility = Visibility.Visible;
                    // make sure opacities are reset for animation
                    ProgressAreaGrid.Opacity = 0;
                    PlayPauseBottomButton.Opacity = 0;
                    SongTitleText.Opacity = 0;

                    // Start logo move storyboard dynamically
                    StartLogoAnimation();

                    // autofill artist/title and show overlay for user acceptance
                    var parsed = ParseFilenameForArtistTitle(path);
                    ArtistTextBox.Text = parsed.artist;
                    TitleTextBox.Text = parsed.title;
                    // keep _pendingFilePath for later processing
                    _pendingFilePath = path;
                _inMp3Mode = true;
                    // show the lyrics input overlay so the user can confirm artist/title
                    LyricsInputOverlay.Visibility = Visibility.Visible;
                });

                // do not auto-start playback here; wait for user to accept lyrics from Lyrics.ovh
                return;
            }
        }

        private (string artist, string title) ParseFilenameForArtistTitle(string path)
        {
            var filename = System.IO.Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Trim();
            var parts = filename.Split('-', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                return (parts[0].Trim(), parts[1].Trim());
            }
            return (string.Empty, filename);
        }

        

        private async Task StartPlaybackAfterLyricsLoadedAsync()
        {
            // small wait for UI
            await Task.Delay(500);

            Dispatcher.Invoke(() =>
            {
                // fade-in title and player more slowly
                var titleFade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(900))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                SongTitleText.BeginAnimation(UIElement.OpacityProperty, titleFade);

                var progressFade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(900))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, BeginTime = TimeSpan.FromMilliseconds(200) };
                ProgressAreaGrid.BeginAnimation(UIElement.OpacityProperty, progressFade);

                var playFade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(900))) { BeginTime = TimeSpan.FromMilliseconds(200) };
                PlayPauseBottomButton.BeginAnimation(UIElement.OpacityProperty, playFade);
            });

            await Task.Delay(700);

            // start media
            try
            {
                if (_pendingMedia != null && _vlcPlayer != null)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    _vlcPlayer.Play(_pendingMedia);
                    _isPlaying = true;
                    PlayPauseBottomButton.Content = "⏸";

                    // measure startup latency: time from Play() call to when VLC reports >0ms playback time
                    long observedPlayerTime = 0;
                    int waited = 0;
                    while (waited < 1500)
                    {
                        await Task.Delay(30);
                        waited += 30;
                        try { observedPlayerTime = _vlcPlayer.Time; } catch { observedPlayerTime = 0; }
                        if (observedPlayerTime > 0) break;
                    }

                    sw.Stop();
                    // compute latency as wall time elapsed minus player's internal time
                    // if observedPlayerTime is 0 (timeout), set latency to 0
                    if (observedPlayerTime > 0)
                    {
                        var latencyMs = Math.Max(0.0, sw.Elapsed.TotalMilliseconds - observedPlayerTime);
                        _playbackStartupLatencyMs = latencyMs;
                    }
                    else
                    {
                        _playbackStartupLatencyMs = 0.0;
                    }

                    _progressTimer.Start();

                    // start auto-resync loop
                    if (_autoResyncEnabled)
                    {
                        _resyncCts?.Cancel();
                        _resyncCts = new CancellationTokenSource();
                        _ = StartAutoResyncLoopAsync(_resyncCts.Token);
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        private void RetryLyricsButton_Click(object? sender, RoutedEventArgs e)
        {
            // allow user to edit inputs again
            LyricsNotFoundPanel.Visibility = Visibility.Collapsed;
        }

        private async void LoadLyricsButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var artist = ArtistTextBox.Text?.Trim() ?? string.Empty;
                var title = TitleTextBox.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(_pendingFilePath)) return;

                // try lyrics.ovh first
                string? raw = null;
                try
                {
                    raw = await _lyricsService.FetchLyricsRawByArtistTitleAsync(artist, title);
                }
                catch { raw = null; }

                if (!string.IsNullOrWhiteSpace(raw))
                {
                    _currentLyrics = _lyricsService.ParseLyricsToLines(raw);
                }
                else
                {
                    // If Lyrics.ovh didn't return text, require user action — show not-found panel and abort automatic processing.
                    LyricsNotFoundPanel.Visibility = Visibility.Visible;
                    return;
                }

                // Attempt forced-alignment: create a temporary text file with the lyrics and ask Whisper to align words
                string? tempTextFile = null;
                try
                {
                    // write lines to temp file
                    tempTextFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString() + ".txt");
                    var linesText = string.Join("\n", _currentLyrics.Select(l => l.Text));
                    System.IO.File.WriteAllText(tempTextFile, linesText);

                    try
                    {
                        var wcAlign = new WhisperClient();
                        var model = ModelComboBox.SelectedValue as string ?? "small";
                        var device = DeviceComboBox.SelectedValue as string ?? "cpu";
                        var words = await wcAlign.TranscribeWithAlignmentAsync(_pendingFilePath!, tempTextFile, model, device);
                        if (words != null && words.Count > 0)
                        {
                            // align lines by first word
                            _lyricsService.AlignLinesByFirstWord(_currentLyrics, words);
                        }
                    }
                    catch
                    {
                        // alignment failed - keep existing timestamps
                    }
                }
                finally
                {
                    try { if (!string.IsNullOrWhiteSpace(tempTextFile) && System.IO.File.Exists(tempTextFile)) System.IO.File.Delete(tempTextFile); } catch { }
                }

                LyricsInputOverlay.Visibility = Visibility.Collapsed;
                RenderLyrics(_currentLyrics);
                // prepare media and start playback
                try
                {
                    // stop previous
                    _vlcPlayer?.Stop();
                    _pendingMedia?.Dispose();
                    if (!string.IsNullOrWhiteSpace(_pendingFilePath))
                    {
                        _pendingMedia = new Media(_libVLC!, new Uri(_pendingFilePath));
                    }
                    if (_vlcPlayer == null)
                    {
                        _vlcPlayer = new VlcPlayerType(_libVLC!);
                        _vlcPlayer.EndReached += VlcPlayer_EndReached;
                    }
                }
                catch { }

                await StartPlaybackAfterLyricsLoadedAsync();
            }
            catch { LyricsInputOverlay.Visibility = Visibility.Collapsed; }
        }

        private async void ContinueNoLyricsButton_Click(object? sender, RoutedEventArgs e)
        {
            // hide overlay and proceed without lyrics (use fallback from filename)
            LyricsInputOverlay.Visibility = Visibility.Collapsed;

            // generate fallback from pending file title and distribute over media length if possible
            var filename = System.IO.Path.GetFileNameWithoutExtension(_pendingFilePath ?? string.Empty);
            var words = filename.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Where(w => !string.IsNullOrWhiteSpace(w)).ToList();

            _currentLyrics.Clear();

            // Attempt to estimate timestamps from audio file
            List<TimeSpan>? estimates = null;
            if (!string.IsNullOrWhiteSpace(_pendingFilePath) && File.Exists(_pendingFilePath))
            {
                try
                {
                    estimates = await _lyricsService.EstimateTimestampsFromAudioAsync(_pendingFilePath, words.Count);
                }
                catch
                {
                    estimates = null;
                }
            }

            double chunkSeconds = 2.0;
            if (_vlcPlayer != null && _vlcPlayer.Length > 0 && words.Count > 0)
            {
                var totalSec = _vlcPlayer.Length / 1000.0;
                chunkSeconds = Math.Max(0.25, totalSec / words.Count);
            }

            if (estimates != null && estimates.Count == words.Count)
            {
                for (int i = 0; i < words.Count; i++)
                {
                    _currentLyrics.Add(new LyricsLine { Timestamp = estimates[i], Text = words[i] });
                }
            }
            else
            {
                var t = TimeSpan.Zero;
                foreach (var c in words)
                {
                    _currentLyrics.Add(new LyricsLine { Timestamp = t, Text = c });
                    t = t.Add(TimeSpan.FromSeconds(chunkSeconds));
                }
            }

            _currentLyrics = _currentLyrics.OrderBy(x => x.Timestamp).ToList();

            // compute automatic alignment offset for fallback words
            try
            {
                var wordLines = words; // list of words used
                var offsetMs2 = await _lyricsService.ComputeAlignmentOffsetMs(_pendingFilePath ?? string.Empty, wordLines);
                _autoSyncOffsetMs = -offsetMs2;
            }
            catch
            {
                _autoSyncOffsetMs = 0.0;
            }
            RenderLyrics(_currentLyrics);

            // start playback flow
            try
            {
                // prepare media and player
                _vlcPlayer?.Stop();
                _pendingMedia?.Dispose();
                if (!string.IsNullOrWhiteSpace(_pendingFilePath))
                {
                    _pendingMedia = new Media(_libVLC!, new Uri(_pendingFilePath));
                }
                if (_vlcPlayer == null)
                {
                    _vlcPlayer = new VlcPlayerType(_libVLC!);
                    _vlcPlayer.EndReached += VlcPlayer_EndReached;
                }
            }
            catch { }

            _ = StartPlaybackAfterLyricsLoadedAsync();
        }

        private void ExitToMenuButton_Click(object? sender, RoutedEventArgs e)
        {
            // stop and return to main menu
            LyricsInputOverlay.Visibility = Visibility.Collapsed;
            _vlcPlayer?.Stop();
            _resyncCts?.Cancel();
            _pendingMedia?.Dispose();
            _pendingMedia = null;
            _isPlaying = false;

            _inMp3Mode = false;

            // restore UI
            ProgressAreaGrid.Visibility = Visibility.Collapsed;
            PlayPauseBottomButton.Visibility = Visibility.Collapsed;
            BackButton.Visibility = Visibility.Collapsed;
            SongTitleText.Text = string.Empty;

            if (RootGrid.ColumnDefinitions.Count > 0)
            {
                RootGrid.ColumnDefinitions[0].Width = new GridLength(280);
            }
            LeftButtonsPanel.Visibility = Visibility.Visible;
            LyricsPanel.Children.Clear();

            _progressTimer.Stop();

            // animate logo back
            var duration = new Duration(TimeSpan.FromMilliseconds(520));
            var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            var scaleXAnim = new DoubleAnimation(LargeLogoScale.ScaleX, 1.0, duration) { EasingFunction = easing };
            var scaleYAnim = new DoubleAnimation(LargeLogoScale.ScaleY, 1.0, duration) { EasingFunction = easing };
            var transXAnim = new DoubleAnimation(LargeLogoTranslate.X, 0.0, duration) { EasingFunction = easing };
            var transYAnim = new DoubleAnimation(LargeLogoTranslate.Y, 0.0, duration) { EasingFunction = easing };

            transYAnim.Completed += (s, ev) =>
            {
                LargeLogoTranslate.X = 0.0;
                LargeLogoTranslate.Y = 0.0;
                LargeLogoScale.ScaleX = 1.0;
                LargeLogoScale.ScaleY = 1.0;

                // ensure opacity/reset of player elements
                ProgressAreaGrid.Opacity = 0;
                PlayPauseBottomButton.Opacity = 0;
                SongTitleText.Opacity = 0;
            };

            LargeLogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
            LargeLogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
            LargeLogoTranslate.BeginAnimation(TranslateTransform.XProperty, transXAnim);
            LargeLogoTranslate.BeginAnimation(TranslateTransform.YProperty, transYAnim);
        }

        private void CancelLyricsButton_Click(object? sender, RoutedEventArgs e)
        {
            // close overlay and do nothing
            LyricsInputOverlay.Visibility = Visibility.Collapsed;
        }

        private void StartLogoAnimation()
        {
            // compute final transform so logo moves to bottom-right corner with margin
            const double finalScale = 0.28;
            const double marginRight = 8.0;
            const double marginBottom = 8.0;

            // force layout so ActualWidth/Height are valid
            RootGrid.UpdateLayout();
            LargeLogoText.UpdateLayout();

            double rootW = RootGrid.ActualWidth;
            double rootH = RootGrid.ActualHeight;

            // use RenderSize which is more reliable for the immediate visual size
            double logoW = LargeLogoText.RenderSize.Width;
            double logoH = LargeLogoText.RenderSize.Height;

            // compute final scaled size
            double finalW = logoW * finalScale;
            double finalH = logoH * finalScale;

            // center positions
            double initialCenterX = rootW / 2.0;
            double initialCenterY = rootH / 2.0;

            double targetCenterX = rootW - marginRight - finalW / 2.0;
            double targetCenterY = rootH - marginBottom - finalH / 2.0;

            double translateX = targetCenterX - initialCenterX;
            double translateY = targetCenterY - initialCenterY;

            // Create animations
            var duration = new Duration(TimeSpan.FromMilliseconds(800));
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            var scaleXAnim = new DoubleAnimation(LargeLogoScale.ScaleX, finalScale, duration) { EasingFunction = easing };
            var scaleYAnim = new DoubleAnimation(LargeLogoScale.ScaleY, finalScale, duration) { EasingFunction = easing };

            var transXAnim = new DoubleAnimation(LargeLogoTranslate.X, translateX, duration) { EasingFunction = easing };
            var transYAnim = new DoubleAnimation(LargeLogoTranslate.Y, translateY, duration) { EasingFunction = easing };

            // Fade-ins for title and player (slower)
            var titleFade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(1100))) { BeginTime = TimeSpan.FromMilliseconds(450), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            var progressFade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(1100))) { BeginTime = TimeSpan.FromMilliseconds(650), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            var playFade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(1100))) { BeginTime = TimeSpan.FromMilliseconds(650) };

            // Apply animations directly to transforms (more reliable than constructing Storyboard)
            LargeLogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
            LargeLogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);

            LargeLogoTranslate.BeginAnimation(TranslateTransform.XProperty, transXAnim);
            LargeLogoTranslate.BeginAnimation(TranslateTransform.YProperty, transYAnim);

            SongTitleText.BeginAnimation(UIElement.OpacityProperty, titleFade);
            ProgressAreaGrid.BeginAnimation(UIElement.OpacityProperty, progressFade);
            PlayPauseBottomButton.BeginAnimation(UIElement.OpacityProperty, playFade);

            // Ensure final values are applied when animations complete
            transXAnim.Completed += (s, e) =>
            {
                LargeLogoTranslate.X = translateX;
            };
            transYAnim.Completed += (s, e) =>
            {
                LargeLogoTranslate.Y = translateY;
            };
            scaleXAnim.Completed += (s, e) =>
            {
                LargeLogoScale.ScaleX = finalScale;
            };
            scaleYAnim.Completed += (s, e) =>
            {
                LargeLogoScale.ScaleY = finalScale;
            };
        }

        private void BackButton_Click(object sender, RoutedEventArgs? e)
        {
            // stop playback and return to main menu
            _vlcPlayer?.Stop();
            _resyncCts?.Cancel();
            _pendingMedia?.Dispose();
            _pendingMedia = null;
            _isPlaying = false;

            // hide and fade out player areas
            var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(360))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            ProgressAreaGrid.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            SongTitleText.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            PlayPauseBottomButton.BeginAnimation(UIElement.OpacityProperty, fadeOut);

            // collapse player UI after fade
            Task.Delay(380).ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                ProgressAreaGrid.Visibility = Visibility.Collapsed;
                PlayPauseBottomButton.Visibility = Visibility.Collapsed;
                BackButton.Visibility = Visibility.Collapsed;
            }));

            // clear displayed song title/artist
            SongTitleText.Text = string.Empty;
            WallpaperTitleText.Text = string.Empty;
            // reset background and covers immediately
            try
            {
                // only reset to black when not in Real Time mode; in Real Time we want the blurred cover background
                if (!_realTimeEnabled)
                {
                    StaticBackgroundRect.Fill = new SolidColorBrush(System.Windows.Media.Colors.Black);
                }
            }
            catch { }

            // stop real-time services and hide visual elements
            try
            {
                if (_musicInfoService != null)
                {
                    _musicInfoService.OnMusicChanged -= MusicInfoService_OnMusicChanged;
                    _musicInfoService.StopListening();
                }
            }
            catch { }
            try { _realTimeAudio?.Stop(); } catch { }
            AudioVisualizerCanvas.Visibility = Visibility.Collapsed;
            AlbumCoverImage.Visibility = Visibility.Collapsed;
            AlbumCoverImageLeft.Source = null;
            AlbumCoverImageLeft.Visibility = Visibility.Collapsed;
            WallpaperPanel.Visibility = Visibility.Collapsed;

            // detach media watcher handler
            try { if (_mediaWatcher != null && _mediaWatcherHandler != null) _mediaWatcher.OnMediaChanged -= _mediaWatcherHandler; } catch { }

            // restore left column and show main buttons
            if (RootGrid.ColumnDefinitions.Count > 0)
            {
                RootGrid.ColumnDefinitions[0].Width = new GridLength(280);
            }
            LeftButtonsPanel.Visibility = Visibility.Visible;
            LyricsPanel.Children.Clear();

            // reset mp3 mode flag so system sessions can update title again
            _inMp3Mode = false;

            _progressTimer.Stop();

            // animate logo back to center and restore scale using direct BeginAnimation
            var duration = new Duration(TimeSpan.FromMilliseconds(520));
            var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            var scaleXAnim = new DoubleAnimation(LargeLogoScale.ScaleX, 1.0, duration) { EasingFunction = easing };
            var scaleYAnim = new DoubleAnimation(LargeLogoScale.ScaleY, 1.0, duration) { EasingFunction = easing };
            var transXAnim = new DoubleAnimation(LargeLogoTranslate.X, 0.0, duration) { EasingFunction = easing };
            var transYAnim = new DoubleAnimation(LargeLogoTranslate.Y, 0.0, duration) { EasingFunction = easing };

            // when finished, ensure final values are set and remove animations
            transYAnim.Completed += (s, ev) =>
            {
                LargeLogoTranslate.X = 0.0;
                LargeLogoTranslate.Y = 0.0;
                LargeLogoScale.ScaleX = 1.0;
                LargeLogoScale.ScaleY = 1.0;

                // ensure opacity/reset of player elements
                ProgressAreaGrid.Opacity = 0;
                PlayPauseBottomButton.Opacity = 0;
                SongTitleText.Opacity = 0;
            };

            LargeLogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
            LargeLogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
            LargeLogoTranslate.BeginAnimation(TranslateTransform.XProperty, transXAnim);
            LargeLogoTranslate.BeginAnimation(TranslateTransform.YProperty, transYAnim);
        }

        private void RenderLyrics(List<LyricsLine> lyrics)
        {
            LyricsPanel.Children.Clear();
            if (lyrics == null || lyrics.Count == 0)
            {
                var noTb = new TextBlock { Text = "Текст не найден", Foreground = Brushes.Gray, FontSize = 16, Margin = new Thickness(6), Opacity = 0.9 };
                LyricsPanel.Children.Add(noTb);
                return;
            }

            foreach (var line in lyrics)
            {
                var tb = new TextBlock { Text = line.Text, Tag = line };
                // apply default style
                tb.Style = (Style)TryFindResource("LyricLineStyle");
                if (line.IsScream)
                {
                    tb.Foreground = Brushes.Red;
                    tb.FontWeight = FontWeights.Bold;
                    tb.Opacity = 1.0;
                }
                LyricsPanel.Children.Add(tb);
            }

            // scroll to top so user sees the first lines
            try
            {
                var sv = LyricsPanel.Parent as ScrollViewer;
                if (sv != null) sv.ScrollToTop();
            }
            catch { }
        }

        private void ApplyIntensityToLyricsStatic(TimeSpan time)
        {
            // adjust time by measured startup latency so display lines align with real audio
            var adjusted = time;
            if (_playbackStartupLatencyMs > 1.0)
            {
                adjusted = time - TimeSpan.FromMilliseconds(_playbackStartupLatencyMs);
                if (adjusted < TimeSpan.Zero) adjusted = TimeSpan.Zero;
            }

            // apply automatic offset
            adjusted = adjusted.Add(TimeSpan.FromMilliseconds(-_autoSyncOffsetMs));

            // Deprecated: word-level tracking removed. Keep simple highlighting by index.
            // No-op here; use ApplyIntensityToLyricsByIndex instead.
        }

        private void ApplyIntensityToLyricsByIndex(int index)
        {
            for (int i = 0; i < LyricsPanel.Children.Count; i++)
            {
                var child = LyricsPanel.Children[i];
                if (child is TextBlock tb)
                {
                    if (i == index)
                    {
                        tb.Style = (Style)TryFindResource("LyricActiveStyle");
                        try { tb.BringIntoView(); } catch { }
                    }
                    else
                    {
                        tb.Style = (Style)TryFindResource("LyricLineStyle");
                    }
                }
            }
        }

        private void ExportFeaturesButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_pendingFilePath) || !File.Exists(_pendingFilePath))
                {
                    MessageBox.Show("Нет загруженного файла для извлечения features.", "Инфо", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var winText = this.FindName("MiniAlignWindowText") as TextBox;
                int wms = 50;
                if (winText != null && int.TryParse(winText.Text, out var tmp)) wms = Math.Max(10, Math.Min(500, tmp));

                var na = new NeuralAligner();
                var (features, timestamps) = na.ExtractFeaturesFromFile(_pendingFilePath!, wms);
                // auto-save CSV to application Data folder without user prompt
                try
                {
                    var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
                    Directory.CreateDirectory(dataDir);
                    var outPath = Path.Combine(dataDir, "features_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
                    using var sw = new StreamWriter(outPath);
                    sw.WriteLine("time_sec,rms,zcr,specCent,maxAbs");
                    for (int i = 0; i < features.Length; i++)
                    {
                        var f = features[i];
                        sw.WriteLine($"{timestamps[i]:F3},{f[0]:F6},{f[1]:F6},{f[2]:F6},{f[3]:F6}");
                    }
                    // update small UI hint
                    try { LyricsSourceText.Text = "features saved: " + Path.GetFileName(outPath); } catch { }
                }
                catch { }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка экспорта: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Live transcriber removed — method deleted

        private async void LoadMp3Button_Click_old(object sender, RoutedEventArgs e)
        {
            // kept for reference
        }

        private void RealTimeButton_Click(object sender, RoutedEventArgs e)
        {
            // Enter or exit Real Time mode. When entering, show player-like UI (logo animation + cover + title)
            if (!_realTimeEnabled)
            {
                _musicInfoService ??= new MusicInfoService();
                _musicInfoService.OnMusicChanged += MusicInfoService_OnMusicChanged;
                _musicInfoService.StartListening();
                _realTimeEnabled = true;

                // Show UI similar to loading mp3 but without playback controls
                Dispatcher.Invoke(() =>
                {
                    LeftButtonsPanel.Visibility = Visibility.Collapsed;
                    if (RootGrid.ColumnDefinitions.Count > 0)
                        RootGrid.ColumnDefinitions[0].Width = new GridLength(0);

                    // hide any player chrome
                    ProgressAreaGrid.Visibility = Visibility.Collapsed;
                    PlayPauseBottomButton.Visibility = Visibility.Collapsed;

                    BackButton.Visibility = Visibility.Visible;

                    AlbumCoverImage.Visibility = Visibility.Visible;
                    SongTitleText.Opacity = 0;

                    // disable lyrics overlay background in Real Time mode
                    try
                    {
                        LyricsInputOverlay.Background = System.Windows.Media.Brushes.Transparent;
                        if (LyricsInputInnerBorder != null) LyricsInputInnerBorder.Background = System.Windows.Media.Brushes.Transparent;
                    }
                    catch { }

                    // animate logo to bottom-right as in MP3 mode
                    StartLogoAnimation();
                });

                // start audio visualizer
                try { _realTimeAudio?.Start(); AudioVisualizerCanvas.Visibility = Visibility.Visible; } catch { }
            }
            else
            {
                // stop real-time service and restore UI
                try
                {
                    if (_musicInfoService != null)
                    {
                        _musicInfoService.OnMusicChanged -= MusicInfoService_OnMusicChanged;
                        _musicInfoService.StopListening();
                    }
                }
                catch { }
                _realTimeEnabled = false;

                try { _realTimeAudio?.Stop(); AudioVisualizerCanvas.Visibility = Visibility.Collapsed; } catch { }
                // restore overlay backgrounds when exiting Real Time
                try
                {
                    LyricsInputOverlay.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CC000000"));
                    if (LyricsInputInnerBorder != null) LyricsInputInnerBorder.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#111111"));
                }
                catch { }

                // reuse BackButton flow to restore UI
                BackButton_Click(this, null);
            }
        }

        private void RealTimeAudio_OnBandsReady(double[] bands, double ts)
        {
            // bands [low, mid, high] in 0..1
            try
            {
                // If we're not in MP3 mode and system playback is paused, keep visualizer silent
                if (!_inMp3Mode && !_systemIsPlaying)
                {
                    try { Dispatcher.Invoke(() => AudioVisualizerCanvas.Children.Clear()); } catch { }
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    DrawVisualizer(bands);
                });
            }
            catch { }
        }

        private void DrawVisualizer(double[] bands)
        {
            if (AudioVisualizerCanvas == null) return;
            AudioVisualizerCanvas.Children.Clear();
            double w = AudioVisualizerCanvas.ActualWidth;
            double h = AudioVisualizerCanvas.ActualHeight;
            if (w <= 0) w = 600;
            if (h <= 0) h = 120;
            int count = Math.Max(8, bands.Length); // use band count from service
            // ensure smoothing buffer
            if (_prevBands == null || _prevBands.Length != bands.Length) _prevBands = new double[bands.Length];

            double spacing = 3.0;
            double totalSpacing = spacing * (count - 1);
            double bw = Math.Max(2, (w - totalSpacing) / count);

            var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 211, 211));
            for (int i = 0; i < count; i++)
            {
                double raw = (i < bands.Length) ? bands[i] : 0.0;
                raw = Math.Min(1.0, Math.Max(0.0, raw));

                // simple exponential smoothing to reduce flicker
                double prev = _prevBands[i];
                double smooth = prev * 0.7 + raw * 0.3;
                _prevBands[i] = smooth;

                double bh = smooth * h;
                var rect = new System.Windows.Shapes.Rectangle { Width = bw, Height = bh, Fill = brush, RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(rect, i * (bw + spacing));
                Canvas.SetTop(rect, h - bh);
                AudioVisualizerCanvas.Children.Add(rect);
            }
        }

        private async void MusicInfoService_OnMusicChanged(string title, string artist, string album, string? coverUrl)
        {
            try
            {
                // log incoming music info for debugging cover issues
                try
                {
                    var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "musicinfo_events.txt");
                    var ln = $"{DateTime.Now:o}\tTitle:{title}\tArtist:{artist}\tAlbum:{album}\tCoverUrl:{coverUrl}\n";
                    System.IO.File.AppendAllText(logPath, ln);
                }
                catch { }

                // update title on UI thread immediately so UI reflects detection even if cover loading fails
                await Dispatcher.InvokeAsync(() =>
                {
                    SongTitleText.Text = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}";
                    // show wallpaper micro panel with title immediately
                    WallpaperTitleText.Text = SongTitleText.Text;
                    WallpaperPanel.Visibility = Visibility.Visible;

                    // fade-in title
                    var titleFade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(600))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                    SongTitleText.BeginAnimation(UIElement.OpacityProperty, titleFade);
                });

                // clear previous covers immediately to avoid stale art while we fetch new
                await Dispatcher.InvokeAsync(() =>
                {
                    AlbumCoverImageLeft.Source = null;
                    AlbumCoverImage.Source = null;
                    WallpaperCoverImage.Source = null;
                });

                // try fetch cover via provided URL first; otherwise attempt service lookup
                System.Windows.Media.ImageSource? img = null;
                string? triedUrl = null;
                if (!string.IsNullOrWhiteSpace(coverUrl))
                {
                    triedUrl = coverUrl;
                    _coverLoadCts?.Cancel();
                    _coverLoadCts = new CancellationTokenSource();

                    // if coverUrl is a local file path, load from file; otherwise treat as http(s)
                    try
                    {
                        if (System.IO.File.Exists(coverUrl))
                        {
                            img = await LoadImageFromFileAsync(coverUrl);
                        }
                        else if (coverUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            img = await LoadImageFromUrlAsync(coverUrl, _coverLoadCts.Token);
                        }
                    }
                    catch { img = null; }
                }

                // log whether image was loaded
                try
                {
                    var logPath2 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "musicinfo_events.txt");
                    var ln2 = $"{DateTime.Now:o}\tTriedUrl:{triedUrl}\tLoaded:{(img != null)}\n";
                    System.IO.File.AppendAllText(logPath2, ln2);
                }
                catch { }

                if (img != null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        AlbumCoverImageLeft.Source = img;
                        AlbumCoverImage.Source = img;
                        AlbumCoverImage.Visibility = _realTimeEnabled ? Visibility.Visible : Visibility.Collapsed;
                        WallpaperCoverImage.Source = img;

                        // set static background to a dimmed version of the cover (no blur)
                        try
                        {
                            if (img is System.Windows.Media.Imaging.BitmapSource bs)
                            {
                                var brush = new ImageBrush(bs) { Stretch = System.Windows.Media.Stretch.UniformToFill, Opacity = 0.28 };
                                StaticBackgroundRect.Fill = brush;
                                try
                                {
                                    StaticBackgroundRect.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 24 };
                                }
                                catch { }
                            }
                        }
                        catch { }
                    });
                }
                else
                {
                    // if no cover found, ensure cover controls cleared and wallpaper panel kept visible
                    await Dispatcher.InvokeAsync(() =>
                    {
                        AlbumCoverImageLeft.Source = null;
                        AlbumCoverImage.Source = null;
                        WallpaperCoverImage.Source = null;
                        AlbumCoverImage.Visibility = Visibility.Collapsed;
                        WallpaperPanel.Visibility = Visibility.Visible;
                    });
                }
            }
            catch { }
        }

        private async Task<System.Windows.Media.ImageSource?> LoadImageFromUrlAsync(string url, CancellationToken ct)
        {
            try
            {
                using var client = new HttpClient();
                using var resp = await client.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                return await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var ms = new System.IO.MemoryStream(bytes);
                        var bi = new System.Windows.Media.Imaging.BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bi.StreamSource = ms;
                        bi.EndInit();
                        bi.Freeze();
                        return (System.Windows.Media.ImageSource)bi;
                    }
                    catch { return null; }
                });
            }
            catch (OperationCanceledException) { return null; }
            catch { return null; }
        }

        private async Task<System.Windows.Media.ImageSource?> LoadImageFromFileAsync(string path)
        {
            try
            {
                return await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var bi = new System.Windows.Media.Imaging.BitmapImage();
                        using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
                        {
                            bi.BeginInit();
                            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bi.StreamSource = fs;
                            bi.EndInit();
                            bi.Freeze();
                        }
                        return (System.Windows.Media.ImageSource)bi;
                    }
                    catch { return null; }
                });
            }
            catch { return null; }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            _originalScale = MainScale?.ScaleX ?? 1.0;
            _pendingScale = _originalScale;
            ScaleSlider.Value = _pendingScale;
            SettingsOverlay.Visibility = Visibility.Visible;

            var sb = TryFindResource("SlideInStoryboard") as Storyboard;
            sb?.Begin();
        }

        private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            RevertToOriginal();
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        private void ApplySettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (MainScale != null)
            {
                MainScale.ScaleX = _pendingScale;
                MainScale.ScaleY = _pendingScale;
            }
            _originalScale = _pendingScale;
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        private void CancelSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            RevertToOriginal();
            _pendingScale = _originalScale;
            ScaleSlider.Value = _pendingScale;
        }

        private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _pendingScale = e.NewValue;
        }

        private void RevertToOriginal()
        {
            if (MainScale != null)
            {
                MainScale.ScaleX = _originalScale;
                MainScale.ScaleY = _originalScale;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _resyncCts?.Cancel();
            _vlcPlayer?.Dispose();
            _libVLC?.Dispose();
            _pendingMedia?.Dispose();
            base.OnClosed(e);
        }

        private void UpdateCustomProgress(double pos)
        {
            // compute using fixed track width (matches XAML) so the visual center aligns with window center
            var trackWidth = ProgressTrackRect.Width;
            var fillWidth = pos * trackWidth;
            ProgressFillRect.Width = fillWidth;

            // knob position should be centered relative to the track
            var knobLeft = (ProgressCanvas.Width - trackWidth) / 2.0 + fillWidth - (ProgressKnobEllipse.Width / 2.0);
            Canvas.SetLeft(ProgressKnobEllipse, Math.Max(0, knobLeft));
        }

        private void ProgressCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isSeeking = true;
            ProgressCanvas.CaptureMouse();
            var pt = e.GetPosition(ProgressCanvas);
            SeekToPosition(pt.X);
        }

        private void ProgressCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isSeeking) return;
            var pt = e.GetPosition(ProgressCanvas);
            SeekToPosition(pt.X);
        }

        private void ProgressCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isSeeking) return;
            _isSeeking = false;
            ProgressCanvas.ReleaseMouseCapture();
            var pt = e.GetPosition(ProgressCanvas);
            SeekToPosition(pt.X);
        }

        private void SeekToPosition(double canvasX)
        {
            // compute relative position inside track
            var trackWidth = ProgressTrackRect.Width;
            // ProgressCanvas may be wider; track is centered horizontally in canvas
            var offset = (ProgressCanvas.Width - trackWidth) / 2.0;
            var relative = (canvasX - offset) / trackWidth;
            var pos = Math.Max(0.0, Math.Min(1.0, relative));

            // update visuals immediately
            UpdateCustomProgress(pos);

            // seek media if available
            if (_vlcPlayer != null && _vlcPlayer.Length > 0)
            {
                try
                {
                    var target = (long)(pos * _vlcPlayer.Length);
                    // clamp
                    target = Math.Max(0, Math.Min(target, (long)_vlcPlayer.Length));
                    _vlcPlayer.Time = target;
                }
                catch
                {
                    // ignore seek errors
                }
            }
        }

        private void VolumeButton_Click(object? sender, RoutedEventArgs e)
        {
            // toggle popup persistently
            if (VolumePopup.IsOpen)
            {
                _popupPersistOpen = false;
                CloseVolumePopup();
            }
            else
            {
                _popupPersistOpen = true;
                OpenVolumePopup();
            }
        }

        private void OpenVolumePopup()
        {
            VolumePopup.IsOpen = true;
            // animate scale from 0.9 to 1.0 and fade in
            var sb = new Storyboard();
            var scaleX = new DoubleAnimation(0.9, 1.0, new Duration(TimeSpan.FromMilliseconds(260))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            var scaleY = new DoubleAnimation(0.9, 1.0, new Duration(TimeSpan.FromMilliseconds(260))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(scaleX, VolumePopupBorder);
            Storyboard.SetTarget(scaleY, VolumePopupBorder);
            Storyboard.SetTargetProperty(scaleX, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
            Storyboard.SetTargetProperty(scaleY, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
            sb.Children.Add(scaleX);
            sb.Children.Add(scaleY);
            sb.Begin();
        }

        private void CloseVolumePopup()
        {
            // animate scale down then close
            var sb = new Storyboard();
            var scaleX = new DoubleAnimation(1.0, 0.9, new Duration(TimeSpan.FromMilliseconds(240))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            var scaleY = new DoubleAnimation(1.0, 0.9, new Duration(TimeSpan.FromMilliseconds(240))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            Storyboard.SetTarget(scaleX, VolumePopupBorder);
            Storyboard.SetTarget(scaleY, VolumePopupBorder);
            Storyboard.SetTargetProperty(scaleX, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
            Storyboard.SetTargetProperty(scaleY, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
            sb.Children.Add(scaleX);
            sb.Children.Add(scaleY);
            sb.Completed += (s, e) =>
            {
                VolumePopup.IsOpen = false;
                _popupPersistOpen = false;
            };
            sb.Begin();
        }

        private void CloseVolumePopupIfAppropriate()
        {
            // close only if not opened by button (persistent)
            if (_popupPersistOpen) return;
            CloseVolumePopup();
        }

        private int? LoadSavedVolume()
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WpfApp1");
                var file = Path.Combine(folder, "volume.txt");
                if (!File.Exists(file)) return null;
                var txt = File.ReadAllText(file).Trim();
                if (int.TryParse(txt, out var v)) return Math.Max(0, Math.Min(100, v));
            }
            catch { }
            return null;
        }

        private void SaveVolume(int v)
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WpfApp1");
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "volume.txt");
                File.WriteAllText(file, v.ToString());
            }
            catch { }
        }

        // XAML event handlers for volume container hover (must match names in XAML)
        private void VolumeContainer_MouseEnter(object sender, MouseEventArgs e)
        {
            OpenVolumePopup();
        }

        private void VolumeContainer_MouseLeave(object sender, MouseEventArgs e)
        {
            CloseVolumePopupIfAppropriate();
        }

        // Helper: get selected whisper model from UI if present, otherwise default to "small"
        private string GetSelectedWhisperModel()
        {
            try
            {
                var cmb = this.FindName("WhisperModelCombo") as System.Windows.Controls.ComboBox;
                if (cmb != null)
                {
                    if (cmb.SelectedItem != null) return cmb.SelectedItem.ToString() ?? "small";
                    if (cmb.Text != null && cmb.Text.Length > 0) return cmb.Text;
                }

                var tb = this.FindName("WhisperModelTextBox") as System.Windows.Controls.TextBox;
                if (tb != null && !string.IsNullOrWhiteSpace(tb.Text)) return tb.Text.Trim();
            }
            catch { }
            return "small";
        }

        // Helper: get selected device from UI if present, otherwise default to "cpu"
        private string GetSelectedDevice()
        {
            try
            {
                var cmb = this.FindName("DeviceCombo") as System.Windows.Controls.ComboBox;
                if (cmb != null)
                {
                    if (cmb.SelectedItem != null) return cmb.SelectedItem.ToString() ?? "cpu";
                    if (cmb.Text != null && cmb.Text.Length > 0) return cmb.Text;
                }

                var tb = this.FindName("DeviceTextBox") as System.Windows.Controls.TextBox;
                if (tb != null && !string.IsNullOrWhiteSpace(tb.Text)) return tb.Text.Trim();
            }
            catch { }
            return "cpu";
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Volume slider is 0..100; set VLC volume if available
            try
            {
                if (_vlcPlayer != null)
                {
                    _vlcPlayer.Volume = (int)e.NewValue; // VLC expects int 0..100
                }

                // persist value to file
                SaveVolume((int)e.NewValue);
            }
            catch
            {
                // ignore
            }
        }

        private async void InstallDepsButton_Click(object? sender, RoutedEventArgs e)
        {
            var msg = "Устанавливаются зависимости. Это может занять время. Продолжать?";
            if (MessageBox.Show(msg, "Подтвердите", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

            InstallDepsButton.IsEnabled = false;
            InstallDepsButton.Content = "Установка...";

            // show progress UI
            DepsProgressPanel.Visibility = Visibility.Visible;
            DepsProgressBar.Value = 0;
            DepsStatusText.Text = "Подготовка...";

            try
            {
                DepsStatusText.Text = "Проверка python...";
                await Task.Delay(200);
                var pythonCheck = await RunCommandAsync("python", "--version");
                DepsProgressBar.Value = 5;

                // upgrade pip and install common packages
                DepsStatusText.Text = "Обновление pip и установка базовых пакетов...";
                await RunCommandAsync("python", "-m pip install --upgrade pip setuptools wheel");
                DepsProgressBar.Value = 15;
                await RunCommandAsync("python", "-m pip install -U numpy scipy soundfile ffmpeg-python tqdm");
                DepsProgressBar.Value = 30;

                // install openai-whisper
                DepsStatusText.Text = "Установка openai-whisper...";
                await RunCommandAsync("python", "-m pip install -U openai-whisper");
                DepsProgressBar.Value = 55;

                // install whisperX (alignment) optionally
                DepsStatusText.Text = "Установка whisperX (опционально)...";
                await RunCommandAsync("python", "-m pip install -U git+https://github.com/m-bain/whisperX.git");
                DepsProgressBar.Value = 70;

                // install torch (CPU by default)
                DepsStatusText.Text = "Установка torch (CPU)...";
                try
                {
                    await RunCommandAsync("python", "-m pip install --index-url https://download.pytorch.org/whl/cpu torch");
                }
                catch
                {
                    // try generic torch as fallback
                    await RunCommandAsync("python", "-m pip install -U torch");
                }
                DepsProgressBar.Value = 80;

                // install demucs for vocal separation (improves ASR on songs)
                DepsStatusText.Text = "Установка demucs (vocal separation)...";
                try
                {
                    await RunCommandAsync("python", "-m pip install -U demucs");
                    DepsProgressBar.Value = 88;
                }
                catch
                {
                    // ignore failure, continue
                }

                // optional: install spleeter as alternative separator
                DepsStatusText.Text = "Установка spleeter (опционально)...";
                try
                {
                    await RunCommandAsync("python", "-m pip install -U spleeter");
                    DepsProgressBar.Value = 92;
                }
                catch { }

                // ensure ffmpeg available: check and download into app folder if missing
                bool ffmpegOk = true;
                try
                {
                    var ffout = await RunCommandAsync("ffmpeg", "-version");
                    ffmpegOk = !string.IsNullOrWhiteSpace(ffout);
                }
                catch { ffmpegOk = false; }

                if (!ffmpegOk)
                {
                    DepsStatusText.Text = "Загрузка ffmpeg (может занять время)...";
                    var target = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");
                    try
                    {
                        var ok = await DownloadAndExtractFfmpegAsync(target);
                        if (ok)
                        {
                            DepsProgressBar.Value = 95;
                            DepsStatusText.Text = "ffmpeg установлен в папке приложения.";
                        }
                        else
                        {
                            DepsStatusText.Text = "Не удалось установить ffmpeg (см. логи).";
                        }
                    }
                    catch (Exception ex)
                    {
                        // unexpected - report but don't crash
                        DepsStatusText.Text = "Не удалось установить ffmpeg: " + ex.Message;
                    }
                }

                DepsProgressBar.Value = 100;
                DepsStatusText.Text = "Установлено. Возможен перезапуск приложения.";
                MessageBox.Show("Зависимости установлены. Возможно потребуется перезапуск приложения.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                DepsStatusText.Text = "Ошибка: " + ex.Message.Replace("\n", " ");
                MessageBox.Show("Ошибка установки: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                InstallDepsButton.IsEnabled = true;
                InstallDepsButton.Content = "Установить зависимости (Python)";
                await Task.Delay(1200);
                DepsProgressPanel.Visibility = Visibility.Collapsed;
            }
        }

        private async Task<string> RunCommandAsync(string fileName, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) throw new Exception("Не удалось запустить " + fileName);
            var outStr = await p.StandardOutput.ReadToEndAsync();
            var errStr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) throw new Exception(errStr + "\n" + outStr);
            return outStr;
        }

        private async Task<bool> DownloadAndExtractFfmpegAsync(string targetFolder)
        {
            try
            {
                Directory.CreateDirectory(targetFolder);
                var url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
                var zipPath = Path.Combine(targetFolder, "ffmpeg.zip");
                using (var http = new HttpClient())
                using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    using (var fs = File.Create(zipPath))
                    {
                        await resp.Content.CopyToAsync(fs);
                    }
                }

                // extract
                ZipFile.ExtractToDirectory(zipPath, targetFolder, true);
                try { File.Delete(zipPath); } catch { }
                return true;
            }
            catch (OperationCanceledException)
            {
                // treat cancellation as failure but do not throw
                return false;
            }
            catch (Exception ex)
            {
                // log if possible and return failure
                try
                {
                    var tf = Path.Combine(Path.GetTempPath(), "musicauto_ffmpeg_error.txt");
                    File.AppendAllText(tf, DateTime.Now.ToString("o") + " - ffmpeg install error: " + ex.ToString() + Environment.NewLine);
                }
                catch { }
                return false;
            }
        }

        /// <summary>
        /// Maps aligned words with timestamps back to original lyrics lines.
        /// Each line gets the timestamp of its first word.
        /// </summary>
        private List<LyricsLine> MapWordsToLines(List<string> lines, List<WhisperClient.WordInfo> wordInfos)
        {
            var result = new List<LyricsLine>();
            if (lines == null || lines.Count == 0) return result;
            if (wordInfos == null || wordInfos.Count == 0)
            {
                // Fallback: create lines without timestamps
                foreach (var line in lines)
                {
                    result.Add(new LyricsLine { Timestamp = TimeSpan.Zero, Text = line });
                }
                return result;
            }

            // Build a flat list of words from all lines, keeping track of which line each word belongs to
            var allWordsWithLineIndex = new List<(string word, int lineIndex)>();
            for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
            {
                var lineWords = Regex.Split(lines[lineIdx], @"\s+")
                    .Where(w => !string.IsNullOrWhiteSpace(w))
                    .ToArray();
                foreach (var w in lineWords)
                {
                    allWordsWithLineIndex.Add((w, lineIdx));
                }
            }

            // Track first word timestamp for each line
            var lineFirstTimestamp = new double[lines.Count];
            var lineHasTimestamp = new bool[lines.Count];
            for (int i = 0; i < lines.Count; i++)
            {
                lineFirstTimestamp[i] = -1;
                lineHasTimestamp[i] = false;
            }

            // Match aligned words to lyrics words sequentially
            int alignedIdx = 0;
            for (int i = 0; i < allWordsWithLineIndex.Count && alignedIdx < wordInfos.Count; i++)
            {
                var (lyricsWord, lineIdx) = allWordsWithLineIndex[i];
                var lyricsWordNorm = NormalizeWord(lyricsWord);
                
                // Look for matching word in aligned words (within reasonable range)
                bool found = false;
                int searchLimit = Math.Min(alignedIdx + 15, wordInfos.Count);
                
                for (int j = alignedIdx; j < searchLimit; j++)
                {
                    var alignedWordNorm = NormalizeWord(wordInfos[j].Word);
                    
                    // Check for match (exact or substring)
                    if (lyricsWordNorm == alignedWordNorm || 
                        (lyricsWordNorm.Length > 2 && alignedWordNorm.Contains(lyricsWordNorm)) ||
                        (alignedWordNorm.Length > 2 && lyricsWordNorm.Contains(alignedWordNorm)))
                    {
                        // Found match - record timestamp for this line if it's the first word
                        if (!lineHasTimestamp[lineIdx])
                        {
                            lineFirstTimestamp[lineIdx] = wordInfos[j].Start;
                            lineHasTimestamp[lineIdx] = true;
                        }
                        alignedIdx = j + 1;
                        found = true;
                        break;
                    }
                }
                
                // If no match found, advance aligned index slightly to avoid getting stuck
                if (!found && alignedIdx < wordInfos.Count)
                {
                    alignedIdx++;
                }
            }

            // Create LyricsLine objects with timestamps
            double lastTimestamp = 0.0;
            for (int i = 0; i < lines.Count; i++)
            {
                double timestamp;
                if (lineHasTimestamp[i])
                {
                    timestamp = lineFirstTimestamp[i];
                    lastTimestamp = timestamp;
                }
                else
                {
                    // Estimate timestamp based on position
                    timestamp = lastTimestamp + 2.0; // Default gap
                    lastTimestamp = timestamp;
                }
                
                result.Add(new LyricsLine 
                { 
                    Timestamp = TimeSpan.FromSeconds(timestamp), 
                    Text = lines[i],
                    IsScream = false // Could detect from audio energy later
                });
            }

            // Ensure timestamps are monotonically increasing
            for (int i = 1; i < result.Count; i++)
            {
                if (result[i].Timestamp <= result[i - 1].Timestamp)
                {
                    result[i].Timestamp = result[i - 1].Timestamp.Add(TimeSpan.FromMilliseconds(500));
                }
            }

            return result;
        }

        /// <summary>
        /// Normalizes a word for comparison by removing punctuation and converting to lowercase.
        /// </summary>
        private static string NormalizeWord(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return string.Empty;
            return Regex.Replace(word.ToLowerInvariant(), @"[^\w]", "");
        }

        private async Task StartAutoResyncLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // only run while playing and file exists
                    if (!_isPlaying || string.IsNullOrWhiteSpace(_pendingFilePath) || !File.Exists(_pendingFilePath) || _currentLyrics == null || _currentLyrics.Count == 0)
                    {
                        await Task.Delay(1000, token);
                        continue;
                    }

                    // compute alignment offset in background
                    List<string> lines = _currentLyrics.Select(l => l.Text).ToList();
                    double offsetMs = 0.0;
                    try
                    {
                        offsetMs = await _lyricsService.ComputeAlignmentOffsetMs(_pendingFilePath!, lines);
                    }
                    catch { offsetMs = 0.0; }

                    // invert as before
                    double newAuto = -offsetMs;
                    // smooth update to avoid jumps
                    lock (this)
                    {
                        _autoSyncOffsetMs = _autoSyncOffsetMs * 0.85 + newAuto * 0.15;
                    }

                    // wait before next resync
                    await Task.Delay(5000, token);
                }
            }
            catch (TaskCanceledException) { }
            catch { }
        }
    }
}