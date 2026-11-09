using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace WindowsNotch
{
    public partial class MediaFocusWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;

        private const double CompactWidth = 344;
        private const double CompactHeight = 460;
        private const double CompactCoverSize = 200;
        private const double WideCoverSize = 150;
        private const double WheelPageThreshold = 60;
        private static readonly TimeSpan PageSwitchCooldown =
            TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan PageSlideDuration =
            TimeSpan.FromMilliseconds(240);

        private static readonly SolidColorBrush ActiveLyricBrush = Frozen(0xFF, 0xFF, 0xFF);
        private static readonly SolidColorBrush InactiveLyricBrush = Frozen(0x78, 0x78, 0x80);
        private static readonly SolidColorBrush IdleAccentBrush = Frozen(0x6C, 0x7B, 0xFF);
        private static readonly Color IdleAccent = Color.FromRgb(0x6C, 0x7B, 0xFF);

        private const uint MonitorDefaultToNearest = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FocusMonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref FocusMonitorInfo info);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        private readonly SmtcService _smtc;
        private readonly AppSettings _settings;
        private readonly LyricsService _lyrics = new();
        private readonly DispatcherTimer _progressTimer = new()
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        private readonly DispatcherTimer _visualizerTimer = new()
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        private readonly List<LyricRow> _lyricRows = new();
        private readonly List<TimeSpan> _lyricTimes = new();
        private readonly List<Border> _visualizerBars = new();
        private readonly double[] _barLevels = new double[32];
        private Border? _lyricsTopSpacer;
        private Border? _lyricsBottomSpacer;

        private CancellationTokenSource? _lyricsCts;
        private MediaInfo? _media;
        private string _lyricsKey = string.Empty;
        private int _activeLyricIndex = -1;
        private bool _isSeeking;
        private bool _isClosing;
        private bool _isLoaded;
        private bool _compactMode;
        private bool _layoutUpdating;
        private bool _headerActionsVisible = true;
        private bool _pointerOver;
        private int _compactPage;
        private double _wideWidth;
        private double _wideHeight;
        private double _wideLeft;
        private double _wideTop;
        private double _wheelAccum;
        private DateTime _lastPageSwitchUtc = DateTime.MinValue;
        private Color _accentColor = IdleAccent;
        private SolidColorBrush _accentBrush = IdleAccentBrush;
        private SolidColorBrush _accentSoftBrush = IdleAccentBrush;
        private LyricsPanelState _lyricsState = LyricsPanelState.Empty;

        private enum LyricsPanelState
        {
            Empty,
            Loading,
            Synced,
            Plain,
            Message
        }

        private sealed record LyricRow(Button Button, TextBlock Text);

        public MediaFocusWindow(SmtcService smtc, AppSettings settings)
        {
            _smtc = smtc;
            _settings = settings;

            InitializeComponent();

            Width = Math.Clamp(settings.FocusWidth, 620, 1400);
            Height = Math.Clamp(settings.FocusHeight, 320, 900);

            BuildVisualizer();
            WireControls();

            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            Closed += OnClosed;

            _progressTimer.Tick += OnProgressTick;
            _visualizerTimer.Tick += OnVisualizerTick;

            _smtc.MediaChanged += OnMediaChanged;
        }

        public event EventHandler? PositionSaved;

        public void RefreshSettings()
        {
            if (_media == null) return;

            if (!_settings.EnableLyrics)
            {
                CancelLyrics();
                _lyricsKey = string.Empty;
                ShowMessage(Localization.Get("LyricsDisabled"));
            }
            else if (_lyricsState == LyricsPanelState.Message)
            {
                _lyricsKey = string.Empty;
                BeginLyricsLoad(_media);
            }
        }

        public void CloseAnimated()
        {
            if (_isClosing) return;

            _isClosing = true;
            _progressTimer.Stop();
            _visualizerTimer.Stop();

            if (!IsLoaded)
            {
                Close();
                return;
            }

            var fade = new DoubleAnimation(1, 0,
                new Duration(TimeSpan.FromMilliseconds(140)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            var scale = new DoubleAnimation(1, 0.97,
                new Duration(TimeSpan.FromMilliseconds(160)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            fade.Completed += (_, _) =>
            {
                try
                {
                    Close();
                }
                catch (InvalidOperationException)
                {
                }
            };

            Root.BeginAnimation(OpacityProperty, fade);
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GwlExStyle);
            SetWindowLong(hwnd, GwlExStyle, exStyle | WsExNoActivate | WsExToolWindow);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            ApplyMedia(_smtc.CurrentMedia);
            PlayIntroAnimation();
            _progressTimer.Start();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _progressTimer.Stop();
            _visualizerTimer.Stop();
            _smtc.MediaChanged -= OnMediaChanged;
            CancelLyrics();
            _lyrics.Dispose();
        }

        private void PlayIntroAnimation()
        {
            Root.Opacity = 0;
            RootScale.ScaleX = 0.96;
            RootScale.ScaleY = 0.96;

            var fade = new DoubleAnimation(0, 1,
                new Duration(TimeSpan.FromMilliseconds(200)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            var scale = new DoubleAnimation(0.96, 1,
                new Duration(TimeSpan.FromMilliseconds(220)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            Root.BeginAnimation(OpacityProperty, fade);
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        }

        private void WireControls()
        {
            CloseButton.Click += (_, _) => CloseAnimated();
            CompactButton.Click += (_, _) => SetCompactMode(!_compactMode);

            Root.PreviewMouseWheel += OnRootMouseWheel;
            Viewport.SizeChanged += (_, _) =>
            {
                if (_compactMode) ApplyLayout();
            };

            PageDot0.MouseLeftButtonDown += (_, _) => SetCompactPage(0, true);
            PageDot1.MouseLeftButtonDown += (_, _) => SetCompactPage(1, true);

            Root.MouseEnter += (_, _) => OnPointerOverChanged(true);
            Root.MouseLeave += (_, _) => OnPointerOverChanged(false);

            PrevButton.Click += async (_, _) => await _smtc.PreviousAsync();
            PlayPauseButton.Click += async (_, _) => await _smtc.PlayPauseAsync();
            NextButton.Click += async (_, _) => await _smtc.NextAsync();

            ProgressSlider.PreviewMouseLeftButtonDown += (_, _) => _isSeeking = true;
            ProgressSlider.PreviewMouseLeftButtonUp += (_, _) => CommitSeek();
            ProgressSlider.LostMouseCapture += (_, _) => CommitSeek();
            ProgressSlider.ValueChanged += (_, _) =>
            {
                if (_isSeeking) UpdateTimeLabels(TimeSpan.FromSeconds(ProgressSlider.Value));
            };

            LyricsScroll.PreviewMouseWheel += (_, e) => e.Handled = true;
            PlainLyricsScroll.PreviewMouseWheel += (_, e) => e.Handled = true;
            LyricsScroll.SizeChanged += (_, _) =>
            {
                if (_lyricsState == LyricsPanelState.Synced &&
                    _activeLyricIndex >= 0)
                {
                    ScrollToLyric(_activeLyricIndex);
                }
            };

            var drag = new WindowDragHelper(this, DragHeader, IsInteractive);
            drag.DragEnded += (_, _) => PositionSaved?.Invoke(this, EventArgs.Empty);
        }

        private static bool IsInteractive(DependencyObject? obj)
        {
            while (obj != null)
            {
                if (obj is ButtonBase || obj is Slider || obj is Thumb || obj is ScrollBar)
                {
                    return true;
                }

                obj = VisualTreeHelper.GetParent(obj);
            }

            return false;
        }

        private void OnPointerOverChanged(bool over)
        {
            if (_pointerOver == over) return;

            _pointerOver = over;
            UpdateHeaderActions();
        }

        private void UpdateHeaderActions()
        {
            bool show = !_compactMode || _pointerOver;

            if (_headerActionsVisible == show) return;

            _headerActionsVisible = show;

            if (show)
            {
                HeaderActions.IsHitTestVisible = true;
            }

            var fade = new DoubleAnimation(
                show ? 1 : 0,
                new Duration(TimeSpan.FromMilliseconds(150)))
            {
                EasingFunction = new QuadraticEase
                {
                    EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn
                }
            };

            fade.Completed += (_, _) =>
            {
                if (!show)
                {
                    HeaderActions.IsHitTestVisible = false;
                }
            };

            HeaderActions.BeginAnimation(OpacityProperty, fade);
        }

        private void SetCompactMode(bool compact)
        {
            if (_compactMode == compact) return;

            if (compact)
            {
                _wideWidth = Width;
                _wideHeight = Height;
                _wideLeft = Left;
                _wideTop = Top;
            }

            _compactMode = compact;
            _compactPage = 0;
            _wheelAccum = 0;

            PageDots.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            LyricsTitleText.Visibility = compact
                ? Visibility.Collapsed
                : Visibility.Visible;
            LyricsPage.ClipToBounds = compact;
            UpdateHeaderLabel();
            UpdateHeaderActions();

            ApplyWindowBounds(compact);
            UpdateLayout();
            ApplyLayout();
            UpdatePageDots();
            RefreshLyricsLayout();
        }

        private Rect GetWorkArea()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return SystemParameters.WorkArea;

            IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return SystemParameters.WorkArea;

            var info = new FocusMonitorInfo
            {
                Size = Marshal.SizeOf<FocusMonitorInfo>()
            };

            if (!GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;

            double scale = 1.0;
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi > 0)
            {
                scale = dpi / 96.0;
            }
            else
            {
                var visual = VisualTreeHelper.GetDpi(this);
                if (visual.DpiScaleX > 0) scale = visual.DpiScaleX;
            }

            if (scale <= 0) scale = 1.0;

            return new Rect(
                info.Work.Left / scale,
                info.Work.Top / scale,
                (info.Work.Right - info.Work.Left) / scale,
                (info.Work.Bottom - info.Work.Top) / scale);
        }

        private void ApplyWindowBounds(bool compact)
        {
            var work = GetWorkArea();

            double targetWidth = compact ? CompactWidth : _wideWidth;
            double targetHeight = compact ? CompactHeight : _wideHeight;

            targetWidth = Math.Min(targetWidth, Math.Max(1, work.Width));
            targetHeight = Math.Min(targetHeight, Math.Max(1, work.Height));

            double targetLeft = compact
                ? _wideLeft + (_wideWidth - targetWidth) / 2
                : _wideLeft;
            double targetTop = compact
                ? _wideTop + (_wideHeight - targetHeight) / 2
                : _wideTop;

            Left = Math.Clamp(targetLeft, work.Left,
                Math.Max(work.Left, work.Right - targetWidth));
            Top = Math.Clamp(targetTop, work.Top,
                Math.Max(work.Top, work.Bottom - targetHeight));

            Width = targetWidth;
            Height = targetHeight;
        }

        private void ApplyLayout()
        {
            if (_layoutUpdating) return;
            _layoutUpdating = true;

            try
            {
                if (_compactMode)
                {
                    double pageWidth = Math.Max(1, Viewport.ActualWidth);
                    double pageHeight = Math.Max(1, Viewport.ActualHeight);

                    NowPlayingColumn.Width = new GridLength(pageWidth);
                    LyricsColumn.Width = new GridLength(0);

                    NowPlayingRow.Height = new GridLength(pageHeight);
                    LyricsRow.Height = new GridLength(pageHeight);

                    SlideHost.HorizontalAlignment = HorizontalAlignment.Stretch;
                    SlideHost.VerticalAlignment = VerticalAlignment.Top;
                    SlideHost.Width = pageWidth;
                    SlideHost.Height = pageHeight * 2;

                    Grid.SetRow(NowPlayingPage, 0);
                    Grid.SetColumn(NowPlayingPage, 0);
                    Grid.SetRow(LyricsPage, 1);
                    Grid.SetColumn(LyricsPage, 0);

                    NowPlayingPage.Margin = new Thickness(0);
                    LyricsPage.Margin = new Thickness(0);
                    LyricsPage.ClipToBounds = true;

                    CoverBorder.Width = CompactCoverSize;
                    CoverBorder.Height = CompactCoverSize;
                    CoverBorder.HorizontalAlignment = HorizontalAlignment.Left;

                    SetPageOffset(-_compactPage * pageHeight);
                }
                else
                {
                    NowPlayingColumn.Width = new GridLength(208);
                    LyricsColumn.Width = new GridLength(1, GridUnitType.Star);

                    NowPlayingRow.Height = new GridLength(1, GridUnitType.Star);
                    LyricsRow.Height = new GridLength(0);

                    SlideHost.HorizontalAlignment = HorizontalAlignment.Stretch;
                    SlideHost.VerticalAlignment = VerticalAlignment.Stretch;
                    SlideHost.Width = double.NaN;
                    SlideHost.Height = double.NaN;

                    Grid.SetRow(NowPlayingPage, 0);
                    Grid.SetColumn(NowPlayingPage, 0);
                    Grid.SetRow(LyricsPage, 0);
                    Grid.SetColumn(LyricsPage, 1);

                    NowPlayingPage.Margin = new Thickness(0);
                    LyricsPage.Margin = new Thickness(26, 0, 0, 0);
                    LyricsPage.ClipToBounds = false;

                    CoverBorder.Width = WideCoverSize;
                    CoverBorder.Height = WideCoverSize;
                    CoverBorder.HorizontalAlignment = HorizontalAlignment.Left;

                    SetPageOffset(0);
                }
            }
            finally
            {
                _layoutUpdating = false;
            }
        }

        private void SetCompactPage(int page, bool animate)
        {
            if (!_compactMode) return;

            page = Math.Clamp(page, 0, 1);
            if (page == _compactPage) return;

            _compactPage = page;
            UpdateHeaderLabel();
            UpdatePageDots();

            double pageHeight = Math.Max(1, Viewport.ActualHeight);
            double target = -page * pageHeight;

            if (animate)
            {
                AnimatePageOffset(target);
            }
            else
            {
                SetPageOffset(target);
            }

            if (page == 1) RefreshLyricsLayout();
        }

        private void SetPageOffset(double y)
        {
            NowPlayingTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null);

            NowPlayingTranslate.X = 0;
            LyricsTranslate.X = 0;
            NowPlayingTranslate.Y = y;
            LyricsTranslate.Y = y;
        }

        private void AnimatePageOffset(double target)
        {
            double current = NowPlayingTranslate.Y;

            NowPlayingTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null);

            NowPlayingTranslate.X = 0;
            LyricsTranslate.X = 0;
            NowPlayingTranslate.Y = current;
            LyricsTranslate.Y = current;

            var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

            NowPlayingTranslate.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(current, target, PageSlideDuration)
                {
                    EasingFunction = easing
                });

            LyricsTranslate.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(current, target, PageSlideDuration)
                {
                    EasingFunction = easing
                });
        }

        private void OnRootMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_compactMode) return;

            e.Handled = true;

            if (_compactPage == 0 && e.Delta > 0)
            {
                _wheelAccum = 0;
                return;
            }

            if (_compactPage == 1 && e.Delta < 0)
            {
                _wheelAccum = 0;
                return;
            }

            var now = DateTime.UtcNow;
            if (now - _lastPageSwitchUtc < PageSwitchCooldown)
            {
                _wheelAccum = 0;
                return;
            }

            _wheelAccum += e.Delta;

            if (_wheelAccum <= -WheelPageThreshold)
            {
                _wheelAccum = 0;
                _lastPageSwitchUtc = now;
                SetCompactPage(1, animate: true);
            }
            else if (_wheelAccum >= WheelPageThreshold)
            {
                _wheelAccum = 0;
                _lastPageSwitchUtc = now;
                SetCompactPage(0, animate: true);
            }
        }

        private void UpdateHeaderLabel()
        {
            HeaderText.SetResourceReference(TextBlock.TextProperty,
                _compactMode && _compactPage == 1
                    ? "LyricsTitle"
                    : "FocusNowPlaying");
        }

        private void UpdatePageDots()
        {
            PageDot0.Opacity = _compactPage == 0 ? 1 : 0.35;
            PageDot1.Opacity = _compactPage == 1 ? 1 : 0.35;
        }

        private void RefreshLyricsLayout()
        {
            if (!_isLoaded) return;

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!IsLoaded) return;

                UpdateLyricSpacers();

                if (_lyricsState == LyricsPanelState.Synced && _activeLyricIndex >= 0)
                {
                    ScrollToLyric(_activeLyricIndex);
                }
            }));
        }

        private void CommitSeek()
        {
            if (!_isSeeking) return;

            _isSeeking = false;
            _ = _smtc.SeekAsync(TimeSpan.FromSeconds(ProgressSlider.Value));
        }

        private void OnMediaChanged(MediaInfo? info)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

            Dispatcher.BeginInvoke(() => ApplyMedia(info));
        }

        private void ApplyMedia(MediaInfo? info)
        {
            _media = info;

            if (info == null)
            {
                ApplyNoMedia();
                return;
            }

            TitleText.Text = info.Title;
            ArtistText.Text = info.Artist;
            SetCover(info.AlbumArt);
            ApplySource(info.SourceAppId);
            UpdatePlayPause(info.IsPlaying);

            double duration = Math.Max(0, info.Duration.TotalSeconds);
            ProgressSlider.Maximum = Math.Max(1, duration);

            if (!_isSeeking)
            {
                ProgressSlider.Value = Math.Clamp(
                    info.Position.TotalSeconds, 0, ProgressSlider.Maximum);
                UpdateTimeLabels(info.Position);
            }

            ControlsPanel.IsEnabled = true;
            AttributionText.Visibility = Visibility.Collapsed;

            string key = LyricsService.BuildCacheKey(info);
            if (!string.Equals(key, _lyricsKey, StringComparison.Ordinal))
            {
                _lyricsKey = key;
                BeginLyricsLoad(info);
            }
            else if (VisualizerPanel.Visibility == Visibility.Visible)
            {
                UpdateVisualizerDetail(info);
            }
        }

        private void ApplyNoMedia()
        {
            CancelLyrics();
            _lyricsKey = string.Empty;
            _activeLyricIndex = -1;

            CoverImage.Source = null;
            CoverImage.Visibility = Visibility.Collapsed;
            CoverPlaceholderGlyph.Visibility = Visibility.Visible;
            SourceBadge.Visibility = Visibility.Collapsed;
            UpdateAmbientGlow();

            TitleText.Text = Localization.Get("NoMediaPlaying");
            ArtistText.Text = Localization.Get("NoMediaHint");

            ProgressSlider.Maximum = 1;
            ProgressSlider.Value = 0;
            PositionText.Text = "0:00";
            DurationText.Text = "--:--";
            ControlsPanel.IsEnabled = false;

            PlayPausePath.Data = Geometry.Parse("M0,0 L12,7 L0,14 Z");
            PlayPausePath.Margin = new Thickness(1.5, 0, 0, 0);

            ShowEmptyState(
                Localization.Get("NoMediaPlaying"),
                Localization.Get("NoMediaHint"));
        }

        private void SetCover(ImageSource? art)
        {
            if (art != null)
            {
                CoverImage.Source = art;
                CoverImage.Visibility = Visibility.Visible;
                CoverPlaceholderGlyph.Visibility = Visibility.Collapsed;
                return;
            }

            CoverImage.Source = null;
            CoverImage.Visibility = Visibility.Collapsed;
            CoverPlaceholderGlyph.Visibility = Visibility.Visible;
        }

        private void ApplySource(string appId)
        {
            Color? color = GetSourceColor(appId);
            string name = GetSourceName(appId);

            if (color is Color accent && !string.IsNullOrWhiteSpace(name))
            {
                _accentColor = accent;
                SourceBadge.Visibility = Visibility.Visible;
                SourceBadge.ToolTip = name;
                ApplySourceLogo(appId);
            }
            else
            {
                _accentColor = IdleAccent;
                SourceBadge.Visibility = Visibility.Collapsed;
                SourceBadge.ToolTip = null;
            }

            ApplyAccentBrushes();
            UpdateAmbientGlow();
        }

        private void ApplySourceLogo(string appId)
        {
            string id = (appId ?? string.Empty).ToLowerInvariant();

            bool isSpotify = id.Contains("spotify");
            bool isChrome = id.Contains("chrome");

            SpotifyLogo.Visibility = isSpotify
                ? Visibility.Visible
                : Visibility.Collapsed;
            ChromeLogo.Visibility = isChrome
                ? Visibility.Visible
                : Visibility.Collapsed;
            GenericSourceLogo.Visibility = isSpotify || isChrome
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void ApplyAccentBrushes()
        {
            _accentBrush = Frozen(_accentColor.R, _accentColor.G, _accentColor.B);
            _accentSoftBrush = new SolidColorBrush(
                Color.FromArgb(0xB4, _accentColor.R, _accentColor.G, _accentColor.B));
            _accentSoftBrush.Freeze();

            ProgressSlider.Foreground = _accentBrush;

            foreach (var bar in _visualizerBars)
            {
                bar.Background = _accentSoftBrush;
            }
        }

        private void UpdateAmbientGlow()
        {
            string id = (_media?.SourceAppId ?? string.Empty).ToLowerInvariant();

            string image = id switch
            {
                _ when id.Contains("spotify") => "layer%20blur%20green.png",
                _ when id.Contains("chrome") => "layer%20blur%20red.png",
                _ => "layer%20blur%20blue.png"
            };

            LyricsGlowImage.Source = new BitmapImage(
                new Uri($"pack://application:,,,/assets/{image}", UriKind.Absolute));

            double target = _media == null ? 0 : 0.55;

            if (Math.Abs(LyricsGlow.Opacity - target) > 0.01)
            {
                AnimateGlowOpacity(LyricsGlow, target);
            }
        }

        private static void AnimateGlowOpacity(UIElement element, double target)
        {
            var fade = new DoubleAnimation(target,
                new Duration(TimeSpan.FromMilliseconds(340)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            element.BeginAnimation(OpacityProperty, fade);
        }

        private static Color GetAmbientGlowColor(string? appId)
        {
            string id = (appId ?? string.Empty).ToLowerInvariant();

            if (id.Contains("spotify")) return Color.FromRgb(0x1E, 0xD7, 0x60);
            if (id.Contains("chrome")) return Color.FromRgb(0xEA, 0x43, 0x35);

            return Color.FromRgb(0x3D, 0x7E, 0xFF);
        }

        private static string GetSourceName(string appId)
        {
            string id = (appId ?? string.Empty).ToLowerInvariant();

            if (id.Contains("spotify")) return "Spotify";
            if (id.Contains("youtube")) return "YouTube";
            if (id.Contains("chrome")) return "Chrome";
            if (id.Contains("msedge") || id.Contains("edge")) return "Edge";
            if (id.Contains("firefox")) return "Firefox";
            if (id.Contains("foobar")) return "foobar2000";

            return string.Empty;
        }

        private static Color? GetSourceColor(string appId)
        {
            string id = (appId ?? string.Empty).ToLowerInvariant();

            if (id.Contains("spotify")) return Color.FromRgb(0x1D, 0xB9, 0x54);
            if (id.Contains("chrome")) return Color.FromRgb(0xEA, 0x43, 0x35);
            if (id.Contains("msedge") || id.Contains("edge")) return Color.FromRgb(0x00, 0x78, 0xD4);
            if (id.Contains("firefox")) return Color.FromRgb(0xFF, 0x71, 0x39);
            if (id.Contains("youtube")) return Color.FromRgb(0xFF, 0x00, 0x00);
            if (id.Contains("foobar")) return Color.FromRgb(0xC8, 0xC8, 0xD0);

            return null;
        }

        private void UpdatePlayPause(bool isPlaying)
        {
            PlayPausePath.Data = Geometry.Parse(isPlaying
                ? "M0,0 h4 v14 h-4 z M8,0 h4 v14 h-4 z"
                : "M0,0 L12,7 L0,14 Z");
            PlayPausePath.Margin = isPlaying ? new Thickness(0) : new Thickness(1.5, 0, 0, 0);
        }

        private void OnProgressTick(object? sender, EventArgs e)
        {
            if (_media == null) return;

            var position = _smtc.GetPosition();

            if (!_isSeeking)
            {
                ProgressSlider.Value = Math.Clamp(
                    position.TotalSeconds, 0, ProgressSlider.Maximum);
                UpdateTimeLabels(position);
            }

            UpdateActiveLyric(position);
        }

        private void UpdateTimeLabels(TimeSpan position)
        {
            PositionText.Text = FormatTime(position);

            double duration = Math.Max(0, _media?.Duration.TotalSeconds ?? 0);
            DurationText.Text = duration > 0
                ? "-" + FormatTime(TimeSpan.FromSeconds(
                    Math.Max(0, duration - position.TotalSeconds)))
                : "--:--";
        }

        private static string FormatTime(TimeSpan time)
        {
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;

            return time.TotalHours >= 1
                ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
                : $"{time.Minutes}:{time.Seconds:00}";
        }

        private void CancelLyrics()
        {
            var cts = _lyricsCts;
            _lyricsCts = null;

            if (cts == null) return;

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            cts.Dispose();
        }

        private void BeginLyricsLoad(MediaInfo media)
        {
            CancelLyrics();
            _activeLyricIndex = -1;
            _lyricRows.Clear();
            _lyricTimes.Clear();
            _lyricsTopSpacer = null;
            _lyricsBottomSpacer = null;
            LyricsStack.Children.Clear();

            if (!_settings.EnableLyrics)
            {
                ShowMessage(Localization.Get("LyricsDisabled"));
                return;
            }

            var cts = new CancellationTokenSource();
            _lyricsCts = cts;
            ShowLoading();
            _ = LoadLyricsAsync(media, cts.Token);
        }

        private async Task LoadLyricsAsync(MediaInfo media, CancellationToken cancellationToken)
        {
            LyricsResult result;

            try
            {
                result = await _lyrics.GetLyricsAsync(media, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                result = LyricsResult.Unavailable;
            }

            if (cancellationToken.IsCancellationRequested) return;

            _ = Dispatcher.BeginInvoke(() =>
            {
                if (cancellationToken.IsCancellationRequested || _media == null) return;
                if (!string.Equals(_lyricsKey, LyricsService.BuildCacheKey(_media),
                        StringComparison.Ordinal))
                {
                    return;
                }

                ApplyLyricsResult(result);
            });
        }

        private void ApplyLyricsResult(LyricsResult result)
        {
            switch (result.Status)
            {
                case LyricsStatus.Synced:
                    ShowSyncedLyrics(result.SyncedLines);
                    break;

                case LyricsStatus.Plain:
                    ShowPlainLyrics(result.PlainText);
                    break;

                case LyricsStatus.Instrumental:
                    ShowMessage(Localization.Get("Instrumental"));
                    break;

                case LyricsStatus.Unavailable:
                    ShowMessage(Localization.Get("LyricsOffline"));
                    break;

                default:
                    ShowMessage(Localization.Get("NoLyrics"));
                    break;
            }
        }

        private void ShowSyncedLyrics(IReadOnlyList<LyricLine> lines)
        {
            _lyricsState = LyricsPanelState.Synced;
            _lyricRows.Clear();
            _lyricTimes.Clear();
            _lyricsTopSpacer = null;
            _lyricsBottomSpacer = null;
            LyricsStack.Children.Clear();

            _lyricsTopSpacer = new Border
            {
                Height = 0,
                IsHitTestVisible = false
            };
            LyricsStack.Children.Add(_lyricsTopSpacer);

            foreach (var line in lines)
            {
                var text = new TextBlock
                {
                    Text = line.Text,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Left,
                    FontSize = 13.5,
                    LineHeight = 23,
                    Foreground = InactiveLyricBrush,
                    Opacity = 0.9,
                    Effect = new BlurEffect
                    {
                        Radius = 0,
                        KernelType = KernelType.Gaussian,
                        RenderingBias = RenderingBias.Performance
                    }
                };

                var button = new Button
                {
                    Content = text,
                    Style = (Style)FindResource("FocusLyricButton"),
                    Tag = line.Time,
                    MinHeight = 32,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                button.Click += OnLyricClick;

                _lyricRows.Add(new LyricRow(button, text));
                _lyricTimes.Add(line.Time);
                LyricsStack.Children.Add(button);
            }

            _lyricsBottomSpacer = new Border
            {
                Height = 0,
                IsHitTestVisible = false
            };
            LyricsStack.Children.Add(_lyricsBottomSpacer);

            SetPanelVisibility(
                synced: true, plain: false, loading: false,
                empty: false, visualizer: false);
            AttributionText.Visibility = Visibility.Visible;
            _activeLyricIndex = -1;
            UpdateActiveLyric(_smtc.GetPosition(), force: true);
        }

        private void ShowPlainLyrics(string text)
        {
            _lyricsState = LyricsPanelState.Plain;
            PlainLyricsText.Text = string.IsNullOrWhiteSpace(text)
                ? Localization.Get("NoLyrics")
                : text;

            SetPanelVisibility(
                synced: false, plain: true, loading: false,
                empty: false, visualizer: false);
            AttributionText.Visibility = Visibility.Visible;
        }

        private void ShowLoading()
        {
            _lyricsState = LyricsPanelState.Loading;
            LyricsLoadingText.Text = Localization.Get("LyricsLoading");

            SetPanelVisibility(
                synced: false, plain: false, loading: true,
                empty: false, visualizer: false);
            AttributionText.Visibility = Visibility.Collapsed;
        }

        private void ShowMessage(string message)
        {
            _lyricsState = LyricsPanelState.Message;

            SetPanelVisibility(
                synced: false, plain: false, loading: false,
                empty: false, visualizer: true);

            VisualizerMessageText.Text = message;
            UpdateVisualizerDetail(_media);
            StartVisualizer();
            AttributionText.Visibility = Visibility.Collapsed;
        }

        private void ShowEmptyState(string title, string hint)
        {
            _lyricsState = LyricsPanelState.Empty;
            LyricsEmptyText.Text = title;
            LyricsEmptyHint.Text = hint;

            SetPanelVisibility(
                synced: false, plain: false, loading: false,
                empty: true, visualizer: false);
            StopVisualizer();
            AttributionText.Visibility = Visibility.Collapsed;
        }

        private void SetPanelVisibility(
            bool synced, bool plain, bool loading, bool empty, bool visualizer)
        {
            LyricsSyncedPanel.Visibility = synced ? Visibility.Visible : Visibility.Collapsed;
            PlainLyricsScroll.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
            LyricsLoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            LyricsEmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            VisualizerPanel.Visibility = visualizer ? Visibility.Visible : Visibility.Collapsed;

            if (!visualizer) StopVisualizer();
        }

        private void OnLyricClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: TimeSpan time })
            {
                _ = _smtc.SeekAsync(time);
            }
        }

        private void UpdateActiveLyric(TimeSpan position, bool force = false)
        {
            if (_lyricRows.Count == 0) return;

            int index = -1;
            int low = 0;
            int high = _lyricTimes.Count - 1;

            while (low <= high)
            {
                int mid = low + (high - low) / 2;

                if (_lyricTimes[mid] <= position + TimeSpan.FromMilliseconds(120))
                {
                    index = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            SetActiveLyric(index, force);
        }

        private void SetActiveLyric(int index, bool force = false)
        {
            if (!force && index == _activeLyricIndex) return;

            _activeLyricIndex = index;
            UpdateLyricRowStyles();

            if (index >= 0 && index < _lyricRows.Count)
            {
                ScrollToLyric(index);
            }
        }

        private void UpdateLyricRowStyles()
        {
            for (int i = 0; i < _lyricRows.Count; i++)
            {
                ApplyLyricRowStyle(i, _activeLyricIndex);
            }
        }

        private void ApplyLyricRowStyle(int index, int activeIndex)
        {
            var row = _lyricRows[index];
            int distance = activeIndex < 0
                ? index + 1
                : Math.Abs(index - activeIndex);
            bool active = distance == 0;

            row.Text.Foreground = active ? ActiveLyricBrush : InactiveLyricBrush;
            row.Text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            row.Text.FontSize = active ? 17 : distance == 1 ? 14 : 13.5;
            row.Text.Opacity = active ? 1 : distance switch
            {
                1 => 0.80,
                2 => 0.58,
                3 => 0.40,
                4 => 0.28,
                5 => 0.20,
                _ => 0.14
            };

            if (row.Text.Effect is BlurEffect blur)
            {
                blur.Radius = distance switch
                {
                    0 => 0,
                    1 => 0.6,
                    2 => 1.4,
                    3 => 2.4,
                    4 => 3.4,
                    5 => 4.4,
                    6 => 5.4,
                    _ => Math.Min(8, 5.4 + (distance - 6) * 0.5)
                };
            }
        }

        private void ScrollToLyric(int index)
        {
            if (!_isLoaded || index < 0 || index >= _lyricRows.Count) return;

            try
            {
                var row = _lyricRows[index];
                LyricsScroll.UpdateLayout();
                UpdateLyricSpacers();
                row.Button.UpdateLayout();

                Point point = row.Button
                    .TransformToAncestor(LyricsStack)
                    .Transform(new Point(0, 0));

                double target = point.Y -
                    (LyricsScroll.ViewportHeight - row.Button.ActualHeight) / 2;

                target = Math.Clamp(
                    target, 0, Math.Max(0, LyricsScroll.ScrollableHeight));
                LyricsScroll.ScrollToVerticalOffset(target);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void UpdateLyricSpacers()
        {
            if (_lyricsTopSpacer == null || _lyricsBottomSpacer == null) return;

            double viewport = LyricsScroll.ViewportHeight;
            if (viewport <= 0) viewport = LyricsScroll.ActualHeight;
            if (viewport <= 0) return;

            double spacer = viewport / 2;

            if (Math.Abs(_lyricsTopSpacer.Height - spacer) > 0.5)
            {
                _lyricsTopSpacer.Height = spacer;
                _lyricsBottomSpacer.Height = spacer;
                LyricsScroll.UpdateLayout();
            }
        }

        private void BuildVisualizer()
        {
            for (int i = 0; i < _barLevels.Length; i++)
            {
                var bar = new Border
                {
                    Width = 4,
                    Height = 6,
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(2, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Background = _accentSoftBrush,
                    Opacity = 0.9
                };

                _barLevels[i] = 6;
                _visualizerBars.Add(bar);
                VisualizerBars.Children.Add(bar);
            }
        }

        private void StartVisualizer()
        {
            if (!_visualizerTimer.IsEnabled) _visualizerTimer.Start();
        }

        private void StopVisualizer()
        {
            if (_visualizerTimer.IsEnabled) _visualizerTimer.Stop();
        }

        private void OnVisualizerTick(object? sender, EventArgs e)
        {
            bool playing = _media?.IsPlaying == true;
            double now = Environment.TickCount64 / 1000.0;

            for (int i = 0; i < _visualizerBars.Count; i++)
            {
                double wave = 0.5 + 0.5 * Math.Sin(now * 3.6 + i * 0.55);
                double wave2 = 0.5 + 0.5 * Math.Sin(now * 1.9 + i * 1.27 + 1.0);
                double target = 6 + wave * wave2 * (playing ? 74 : 10);

                _barLevels[i] += (target - _barLevels[i]) * 0.24;
                _visualizerBars[i].Height = _barLevels[i];
            }
        }

        private void UpdateVisualizerDetail(MediaInfo? media)
        {
            if (media == null)
            {
                VisualizerDetailText.Text = Localization.Get("NoLyricsHint");
                return;
            }

            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(media.Artist))
            {
                parts.Add(media.Artist);
            }

            if (!string.IsNullOrWhiteSpace(media.Album))
            {
                parts.Add(media.Album);
            }

            string firstLine = string.Join(" · ", parts);

            string source = GetSourceName(media.SourceAppId);
            string duration = media.Duration > TimeSpan.Zero
                ? FormatTime(media.Duration)
                : string.Empty;

            string secondLine = string.Join(" · ",
                new[] { source, duration }.Where(value => !string.IsNullOrWhiteSpace(value)));

            VisualizerDetailText.Text = string.Join("\n",
                new[] { firstLine, secondLine }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
