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
        private bool _isPlaying = false;

        private LibVLC? _libVLC;
        private VlcPlayerType? _vlcPlayer;
        private DispatcherTimer _progressTimer;

        // keep media reference so we can delay play
        private Media? _pendingMedia = null;
        private string? _pendingFilePath = null;

        // seek state
        private bool _isSeeking = false;

        // button-open state
        private bool _wasOpenedByButton = false;

        // startup latency for playback
        private double _playbackStartupLatencyMs = 0.0;
        // automatic alignment offset (positive means move text earlier when applied as subtraction)
        private double _autoSyncOffsetMs = 0.0;

        // resync variable
        private CancellationTokenSource? _resyncCts;
        private bool _autoResyncEnabled = true; // enabled by default

        private WhisperClient? _whisperClient = null;

        // Neural aligner optional
        private NeuralAligner? _neuralAligner = null;
        private bool _useNeuralAligner = false; // disabled by default

        public MainWindow()
        {
            InitializeComponent();

            _whisperClient = new WhisperClient();

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
            _vlcPlayer = new VlcPlayerType(_libVLC);
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
                ApplyIntensityToLyricsStatic(current);

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
                    SongTitleText.Text = System.IO.Path.GetFileNameWithoutExtension(path);
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

                    // Show lyrics input overlay
                    LyricsInputOverlay.Visibility = Visibility.Visible;
                    LyricsNotFoundPanel.Visibility = Visibility.Collapsed;

                    // prefill artist/title from filename if possible
                    var (artist, title) = ParseFilenameForArtistTitle(path);
                    ArtistTextBox.Text = artist;
                    TitleTextBox.Text = title;
                });

                try
                {
                    // stop previous
                    _vlcPlayer?.Stop();
                    _pendingMedia?.Dispose();

                    _pendingMedia = new Media(_libVLC!, new Uri(path));

                    // do NOT start playing immediately; wait until user loads lyrics or chooses to continue
                }
                catch
                {
                    MessageBox.Show("Failed to open media.");
                }

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

        private async void LoadLyricsButton_Click(object? sender, RoutedEventArgs e)
        {
            var artist = ArtistTextBox.Text?.Trim() ?? string.Empty;
            var title = TitleTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            {
                MessageBox.Show("Введите артиста и название трека.");
                return;
            }

            // attempt to fetch from lyrics.ovh
            LyricsInputOverlay.IsEnabled = false;
            var raw = await _lyricsService.FetchLyricsRawByArtistTitleAsync(artist, title);
            LyricsInputOverlay.IsEnabled = true;

            if (!string.IsNullOrWhiteSpace(raw))
            {
                // use original lyric lines instead of fixed 5-word chunks
                var lines = raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(l => l.Trim())
                               .Where(l => !string.IsNullOrWhiteSpace(l))
                               .ToList();

                _currentLyrics.Clear();

                List<LyricsLine> aligned = null;
                // try whisper forced-align flow
                try
                {
                    var modelName = GetSelectedWhisperModel();
                    var device = GetSelectedDevice();
                    var segments = await _whisperClient.TranscribeAsync(_pendingFilePath ?? string.Empty, modelName, device);
                    if (segments != null && segments.Count > 0)
                    {
                        // build a word-level list from segments
                        var wordTimes = new List<(string word, double start, double end)>();
                        foreach (var seg in segments)
                        {
                            var words = Regex.Split(seg.Text.Trim(), "\\s+") .Where(w => !string.IsNullOrWhiteSpace(w)).ToArray();
                            if (words.Length == 0) continue;
                            double segStart = seg.Start; double segEnd = seg.End;
                            double dur = segEnd - segStart;
                            for (int wi = 0; wi < words.Length; wi++)
                            {
                                double wstart = segStart + (dur * wi) / words.Length;
                                double wend = segStart + (dur * (wi + 1)) / words.Length;
                                wordTimes.Add((words[wi], wstart, wend));
                            }
                        }

                        // map lines (which may be phrases) to earliest word time that contains matching words
                        for (int i = 0; i < lines.Count; i++)
                        {
                            var ln = lines[i];
                            var lnWords = Regex.Split(ln, "\\s+").Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim(new char[]{',','.', '"','\'','!','?'}).ToLower()).ToList();
                            double chosen = 0.0; bool found = false; bool scream = false;
                            for (int wi = 0; wi < wordTimes.Count; wi++)
                            {
                                var wt = wordTimes[wi];
                                var wclean = wt.word.Trim(new char[]{',','.', '"','\'','!','?'}).ToLower();
                                if (lnWords.Contains(wclean))
                                {
                                    chosen = wt.start; found = true;
                                    // detect scream by checking if word period overlaps a high-energy window (use earlier RMS features)
                                    // simple heuristic: if word chunk duration < 0.5s and uppercase letters exist assume shout
                                    if (wt.word.Any(c => char.IsUpper(c)) || wt.end - wt.start < 0.35) scream = true;
                                    break;
                                }
                            }
                            if (!found)
                            {
                                // fallback to previous alignment methods
                            }
                            _currentLyrics.Add(new LyricsLine { Timestamp = TimeSpan.FromSeconds(chosen), Text = ln, IsScream = scream });
                        }
                    }
                }
                catch { aligned = null; }

                // If whisper didn't produce useful alignment, optionally try neural aligner if enabled
                if ((_currentLyrics == null || _currentLyrics.Count == 0) && _useNeuralAligner && !string.IsNullOrWhiteSpace(_pendingFilePath) && File.Exists(_pendingFilePath))
                {
                    try
                    {
                        _neuralAligner ??= new NeuralAligner();
                        var model = _neuralAligner.LoadModel();
                        if (model != null)
                        {
                            var neural = await Task.Run(() => _neuralAligner.AlignTextToAudioWithModel(_pendingFilePath!, lines));
                            if (neural != null && neural.Count > 0)
                            {
                                _currentLyrics = neural;
                            }
                        }
                        else
                        {
                            // model not present - inform user
                            MessageBox.Show("Модель нейронной сети не найдена. Выключите использование нейрона или обучите модель.", "Инфо", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                    catch { }
                }

                if (aligned != null && aligned.Count == lines.Count)
                {
                    _currentLyrics = aligned;
                }

                // if no alignment was produced, fall back to using the raw lines or estimated timestamps
                if (_currentLyrics == null || _currentLyrics.Count == 0)
                {
                    // try to estimate timestamps for each line from audio
                    List<TimeSpan>? estimates = null;
                    if (!string.IsNullOrWhiteSpace(_pendingFilePath) && File.Exists(_pendingFilePath))
                    {
                        try
                        {
                            estimates = await _lyricsService.EstimateTimestampsFromAudioAsync(_pendingFilePath, lines.Count);
                        }
                        catch { estimates = null; }
                    }

                    if (estimates != null && estimates.Count == lines.Count)
                    {
                        for (int i = 0; i < lines.Count; i++)
                        {
                            _currentLyrics.Add(new LyricsLine { Timestamp = estimates[i], Text = lines[i] });
                        }
                    }
                    else
                    {
                        // fallback: place lines uniformly across the track (or short fixed spacing)
                        double chunkSeconds = 2.0;
                        if (_vlcPlayer != null && _vlcPlayer.Length > 0 && lines.Count > 0)
                        {
                            var totalSec = _vlcPlayer.Length / 1000.0;
                            chunkSeconds = Math.Max(0.25, totalSec / lines.Count);
                        }

                        var t = TimeSpan.Zero;
                        foreach (var ln in lines)
                        {
                            _currentLyrics.Add(new LyricsLine { Timestamp = t, Text = ln });
                            t = t.Add(TimeSpan.FromSeconds(chunkSeconds));
                        }
                    }
                }

                // ensure sorted
                _currentLyrics = _currentLyrics.OrderBy(x => x.Timestamp).ToList();

                // render and hide overlay
                RenderLyrics(_currentLyrics);
                LyricsInputOverlay.Visibility = Visibility.Collapsed;

                // start playback flow (delayed so animations complete)
                await StartPlaybackAfterLyricsLoadedAsync();
            }
            else
            {
                // show not found options
                LyricsNotFoundPanel.Visibility = Visibility.Visible;
            }
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
            // treat cancel as continue without lyrics
            ContinueNoLyricsButton_Click(sender, e);
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

        private void BackButton_Click(object sender, RoutedEventArgs e)
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

            // restore left column and show main buttons
            if (RootGrid.ColumnDefinitions.Count > 0)
            {
                RootGrid.ColumnDefinitions[0].Width = new GridLength(280);
            }
            LeftButtonsPanel.Visibility = Visibility.Visible;
            LyricsPanel.Children.Clear();

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
                var tb = new TextBlock { Text = line.Text, Foreground = line.IsScream ? Brushes.Red : Brushes.White, FontSize = 24, Margin = new Thickness(4), Opacity = 1.0, Tag = line, TextWrapping = TextWrapping.Wrap };
                if (line.IsScream) { tb.FontWeight = FontWeights.Bold; }
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

            // highlight nearest lyrics line without dynamic intensity styling
            LyricsLine? nearest = null;
            foreach (var l in _currentLyrics)
            {
                if (l.Timestamp <= adjusted) nearest = l;
                else break;
            }
            if (nearest == null) return;

            foreach (var child in LyricsPanel.Children)
            {
                if (child is TextBlock tb)
                {
                    if (tb.Tag is LyricsLine ll && ll == nearest)
                    {
                        tb.FontWeight = FontWeights.Bold;
                        tb.Foreground = Brushes.White;
                        tb.Opacity = 1.0;
                    }
                    else
                    {
                        tb.FontWeight = FontWeights.Normal;
                        tb.Foreground = Brushes.White;
                        tb.Opacity = 0.6;
                    }
                }
            }
        }

        private async void LoadMp3Button_Click_old(object sender, RoutedEventArgs e)
        {
            // kept for reference
        }

        private void RealTimeButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Real Time clicked", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
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
                _wasOpenedByButton = false;
                CloseVolumePopup();
            }
            else
            {
                _wasOpenedByButton = true;
                OpenVolumePopup();
            }
        }

        private void OpenVolumePopup()
        {
            VolumePopup.IsOpen = true;
            // animate scale from 0.9 to 1.0 and fade in
            var sb = new Storyboard();
            var scaleX = new DoubleAnimation(0.9, 1.0, new Duration(TimeSpan.FromMilliseconds(180))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            var scaleY = new DoubleAnimation(0.9, 1.0, new Duration(TimeSpan.FromMilliseconds(180))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
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
            var scaleX = new DoubleAnimation(1.0, 0.9, new Duration(TimeSpan.FromMilliseconds(140))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            var scaleY = new DoubleAnimation(1.0, 0.9, new Duration(TimeSpan.FromMilliseconds(140))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            Storyboard.SetTarget(scaleX, VolumePopupBorder);
            Storyboard.SetTarget(scaleY, VolumePopupBorder);
            Storyboard.SetTargetProperty(scaleX, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
            Storyboard.SetTargetProperty(scaleY, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
            sb.Children.Add(scaleX);
            sb.Children.Add(scaleY);
            sb.Completed += (s, e) => VolumePopup.IsOpen = false;
            sb.Begin();
        }

        private void CloseVolumePopupIfAppropriate()
        {
            // close only if not opened by button (persistent)
            if (_wasOpenedByButton) return;
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
                await RunCommandAsync("python", "-m pip install --index-url https://download.pytorch.org/whl/cpu torch");
                DepsProgressBar.Value = 85;

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
                        await DownloadAndExtractFfmpegAsync(target);
                        DepsProgressBar.Value = 95;
                        DepsStatusText.Text = "ffmpeg установлен в папке приложения.";
                    }
                    catch (Exception ex)
                    {
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

        private Task DownloadAndExtractFfmpegAsync(string targetFolder)
        {
            return Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(targetFolder);
                    var url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
                    var zipPath = Path.Combine(targetFolder, "ffmpeg.zip");
                    using (var http = new HttpClient())
                    using (var resp = http.GetAsync(url).Result)
                    {
                        resp.EnsureSuccessStatusCode();
                        using (var fs = File.Create(zipPath))
                        {
                            resp.Content.CopyToAsync(fs).Wait();
                            fs.Close();
                        }
                    }

                    ZipFile.ExtractToDirectory(zipPath, targetFolder, true);
                    File.Delete(zipPath);
                }
                catch (Exception ex)
                {
                    throw new Exception("Не удалось скачать или распаковать ffmpeg: " + ex.Message);
                }
            });
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