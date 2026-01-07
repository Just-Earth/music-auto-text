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

        // seek state
        private bool _isSeeking = false;

        // button-open state
        private bool _wasOpenedByButton = false;

        public MainWindow()
        {
            InitializeComponent();

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

        private void VlcPlayer_EndReached(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _isPlaying = false;
                PlayPauseBottomButton.Content = "▶";
                _progressTimer.Stop();
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
            }
            else
            {
                _vlcPlayer.Play();
                _isPlaying = true;
                PlayPauseBottomButton.Content = "⏸";
                _progressTimer.Start();
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
                });

                try
                {
                    // stop previous
                    _vlcPlayer?.Stop();
                    _pendingMedia?.Dispose();

                    _pendingMedia = new Media(_libVLC!, new Uri(path));

                    // do NOT start playing immediately; we'll start after animations finish
                }
                catch
                {
                    MessageBox.Show("Failed to open media.");
                }

                // fetch lyrics
                var rawLines = await _lyricsService.FetchLyricsByFilenameAsync(path);

                // detect fallback: if service returned words from the filename (common fallback), do not render duplicate title
                var filename = System.IO.Path.GetFileNameWithoutExtension(path);
                var filenameWords = filename.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToLowerInvariant()).ToList();

                bool isFallback = rawLines != null && rawLines.Count > 0 && rawLines.All(l =>
                    l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => filenameWords.Contains(w.ToLowerInvariant())));

                _currentLyrics.Clear();
                if (!isFallback)
                {
                    // flatten rawLines into words and group into chunks of ~6 words
                    var words = new List<string>();
                    foreach (var l in rawLines)
                    {
                        var parts = l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        words.AddRange(parts);
                    }
                    var chunks = new List<string>();
                    for (int i = 0; i < words.Count; i += 6)
                    {
                        var take = Math.Min(6, words.Count - i);
                        chunks.Add(string.Join(' ', words.GetRange(i, take)));
                    }
                    var t = TimeSpan.Zero;
                    foreach (var c in chunks)
                    {
                        _currentLyrics.Add(new LyricsLine { Timestamp = t, Text = c });
                        t = t.Add(TimeSpan.FromSeconds(3));
                    }
                }

                // filter out any lyric chunks that are identical to the title to avoid overlap
                var titleLower = filename.ToLowerInvariant();
                _currentLyrics = _currentLyrics.Where(ll => !string.Equals(ll.Text.Trim(), titleLower, StringComparison.OrdinalIgnoreCase) && !ll.Text.ToLowerInvariant().Contains(titleLower)).ToList();

                // render only if we have real lyrics
                RenderLyrics(_currentLyrics);

                // start timer later when media begins
                _isPlaying = false;

                // Wait for a short time to allow animations to run, then fade in player and start playback
                await Task.Delay(800);

                Dispatcher.Invoke(() =>
                {
                    // ensure progress area visible and animate opacity if not already
                    ProgressAreaGrid.Visibility = Visibility.Visible;
                    PlayPauseBottomButton.Visibility = Visibility.Visible;
                    // make fades slower
                    var titleFade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(700))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                    SongTitleText.BeginAnimation(UIElement.OpacityProperty, titleFade);

                    var progressFade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(700))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, BeginTime = TimeSpan.FromMilliseconds(200) };
                    ProgressAreaGrid.BeginAnimation(UIElement.OpacityProperty, progressFade);

                    var playFade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(700))) { BeginTime = TimeSpan.FromMilliseconds(200) };
                    PlayPauseBottomButton.BeginAnimation(UIElement.OpacityProperty, playFade);
                });

                // short delay before play so user sees fade-ins
                await Task.Delay(450);

                // finally start playback
                try
                {
                    if (_pendingMedia != null && _vlcPlayer != null)
                    {
                        _vlcPlayer.Play(_pendingMedia);
                        _isPlaying = true;
                        PlayPauseBottomButton.Content = "⏸";
                        _progressTimer.Start();
                    }
                }
                catch
                {
                    // ignore
                }
            }
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
            foreach (var line in lyrics)
            {
                var tb = new TextBlock { Text = line.Text, Foreground = Brushes.White, FontSize = 24, Margin = new Thickness(4), Opacity = 0.6, Tag = line };
                LyricsPanel.Children.Add(tb);
            }
        }

        private void ApplyIntensityToLyricsStatic(TimeSpan time)
        {
            // highlight nearest lyrics line without dynamic intensity styling
            LyricsLine? nearest = null;
            foreach (var l in _currentLyrics)
            {
                if (l.Timestamp <= time) nearest = l;
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

        private void VolumeContainer_MouseEnter(object sender, MouseEventArgs e)
        {
            OpenVolumePopup();
        }

        private void VolumeContainer_MouseLeave(object sender, MouseEventArgs e)
        {
            CloseVolumePopupIfAppropriate();
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
    }
}