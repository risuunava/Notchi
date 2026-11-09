using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WindowsNotch.Conversion;

namespace WindowsNotch
{
    public partial class MainWindow : Window
    {
        private const double CollapsedWidth = 250;
        private const double CollapsedHeight = 40;
        private const double ExpandedWidth = 300;
        private const double ExpandedHeight = 108;
        private const double IdleExpandedHorizontalMargin = 8;
        private const int IdleCalendarPage = 0;
        private const int IdleMediaPage = 1;
        private const int IdleMetricsPage = 2;

        private const double SideCollapsedWidth = 40;
        private const double SideCollapsedHeight = 107;
        private const double SideExpandedWidth = 300;
        private const double SideExpandedHeight = 126;
        private const double SleeveRim = 6;
        private const double SleeveShoulderInset = 14;
        private const double SleeveSpanInset = SleeveShoulderInset + SleeveRim;
        private const double SleeveCornerRadius = 20;
        private const double FeatureBarHeight = 34;
        private const double FeatureBarGap = 4;
        private const double FeatureBarExtraHeight = FeatureBarGap + FeatureBarHeight + 10;

        private static readonly Duration PillPhaseDuration = new(TimeSpan.FromSeconds(0.16));
        private static readonly Duration IdlePageDuration = new(TimeSpan.FromMilliseconds(260));

        private const int GwlExStyle = -20;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;

        private const uint MonitorDefaultToNearest = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
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
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private static readonly SolidColorBrush AccentBrush = FrozenBrush(0x3D, 0x7E, 0xFF);
        private static readonly SolidColorBrush WarningBrush = FrozenBrush(0xFF, 0xB0, 0x20);
        private static readonly SolidColorBrush DangerBrush = FrozenBrush(0xFF, 0x45, 0x3A);
        private static readonly SolidColorBrush BatteryBrush = FrozenBrush(0x4C, 0xD9, 0x64);
        private static readonly SolidColorBrush DropZoneActiveBrush =
            FrozenBrush(Color.FromArgb(0x38, 0x3D, 0x7E, 0xFF));
        private static readonly SolidColorBrush CalendarDayBrush = FrozenBrush(0xF5, 0xF5, 0xF7);
        private static readonly SolidColorBrush CalendarMutedDayBrush = FrozenBrush(0x85, 0x85, 0x8D);
        private static readonly SolidColorBrush CalendarTodayTextBrush = FrozenBrush(0x1C, 0x1C, 0x1E);

        private static readonly Color IdleAccent = Color.FromRgb(0x6C, 0x7B, 0xFF);
        private static readonly Color DropAccent = Color.FromRgb(0x3D, 0x7E, 0xFF);
        private static readonly Color UnknownSourceColor = Color.FromRgb(0x5A, 0x5A, 0x5E);

        // Tepi yang menempel ke layar tetap hitam pekat; sisi luar memakai
        // gradasi violet-biru dengan opacity sedikit lebih rendah.
        private static readonly Color AttachedEdgeColor = Color.FromArgb(0xFF, 0x07, 0x07, 0x08);
        private static readonly Color BaseMidColor = Color.FromArgb(0xFF, 0x08, 0x08, 0x0B);
        private static readonly Color BaseTintColor = Color.FromArgb(0xF2, 0x0B, 0x09, 0x14);
        private static readonly Color BaseOuterColor = Color.FromArgb(0xE6, 0x11, 0x0E, 0x22);

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
        private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
        private readonly SmtcService _smtc = new();
        private readonly SystemStatusService _systemStatus = new();
        private MobileServerService? _mobileServer;
        private PairingRequest? _currentPairingRequest;
        private AppSettings _settings = new();
        private MediaInfo? _media;
        private MediaInfo? _lastMedia;
        private int _idlePage;
        private bool _isSeeking;
        private bool _isExpanded;
        private bool _domeMode;
        private DateTime _lastDomeClick;
        private bool _isDragging;
        private bool _dragMoved;
        private bool _isFileDragOver;
        private FileDropDestination _fileDropDestination = FileDropDestination.Path;
        private NoteWindow? _noteWindow;
        private SettingsWindow? _settingsWindow;
        private DropShelfWindow? _dropShelf;
        private MediaFocusWindow? _focusWindow;
        private bool _focusHidNotch;
        private bool _isMainClosing;
        private readonly QuickConversionService _quickConversion = new();
        private readonly List<string> _droppedPaths = new();
        private readonly GlobalHotkeyService _hotkey = new();
        private readonly TextBlock[] _calendarWeekdayTexts;
        private readonly Border[] _calendarDayCircles;
        private readonly TextBlock[] _calendarDayTexts;
        private readonly System.Windows.Shapes.Ellipse[] _calendarNoteDots;
        private readonly DateTime[] _calendarVisibleDates = new DateTime[7];
        private int _calendarOffsetDays = 0;
        private DateTime? _selectedCalendarDate = null;
        private Dictionary<string, string> _calendarNotes = new();
        private CalendarNoteWindow? _calendarNoteWindow;
        private static readonly SolidColorBrush CalendarSelectedBlueBrush = FrozenBrush(0x0A, 0x84, 0xFF);
        private readonly Grid[] _idlePages;
        private readonly TranslateTransform[] _idlePageTransforms;
        private bool _isHidden;
        private int _animationVersion;
        private bool _isConverting;
        private CancellationTokenSource? _quickConversionCts;
        private Point _dragStartCursor;
        private Point _dragGrab;
        private NotchAnchor _anchor = NotchAnchor.TopCenter;
        private Rect _monitor;

        private enum NotchAnchor
        {
            TopCenter,
            BottomCenter,
            LeftCenter,
            RightCenter
        }

        private enum FileDropDestination
        {
            Path,
            Convert
        }

        private bool IsSideAnchor => _anchor is NotchAnchor.LeftCenter or NotchAnchor.RightCenter;
        private bool IsNoteOpen => _noteWindow is { IsVisible: true };
        private bool IsSettingsOpen => _settingsWindow is { IsVisible: true };
        private bool IsShelfOpen => _dropShelf is { IsVisible: true };
        private bool IsCalendarNoteOpen => _calendarNoteWindow is { IsVisible: true };
        private bool IsAuxOpen => IsNoteOpen || IsSettingsOpen || IsShelfOpen || IsCalendarNoteOpen;

        private (double Width, double Height) CollapsedSize => IsSideAnchor
            ? (SideCollapsedWidth, SideCollapsedHeight)
            : (CollapsedWidth, CollapsedHeight);

        private (double Width, double Height) ExpandedSize => IsSideAnchor
            ? (SideExpandedWidth, SideExpandedHeight)
            : (ExpandedWidth, ExpandedHeight);

        public MainWindow()
        {
            InitializeComponent();

            _calendarWeekdayTexts = new[]
            {
                CalendarWeekday0, CalendarWeekday1, CalendarWeekday2,
                CalendarWeekday3, CalendarWeekday4, CalendarWeekday5,
                CalendarWeekday6
            };
            _calendarDayCircles = new[]
            {
                CalendarDayCircle0, CalendarDayCircle1, CalendarDayCircle2,
                CalendarDayCircle3, CalendarDayCircle4, CalendarDayCircle5,
                CalendarDayCircle6
            };
            _calendarDayTexts = new[]
            {
                CalendarDayText0, CalendarDayText1, CalendarDayText2,
                CalendarDayText3, CalendarDayText4, CalendarDayText5,
                CalendarDayText6
            };
            _calendarNoteDots = new[]
            {
                CalendarNoteDot0, CalendarNoteDot1, CalendarNoteDot2,
                CalendarNoteDot3, CalendarNoteDot4, CalendarNoteDot5,
                CalendarNoteDot6
            };
            _calendarNotes = CalendarStore.Load();
            InitCalendarInteractions();
            _idlePages = new[] { CalendarPage, MediaPage, MetricsPage };
            _idlePageTransforms = new[]
            {
                CalendarPageTransform, MediaPageTransform, MetricsPageTransform
            };

            _settings = SettingsStore.Load();

            bool autoStartEnabled = StartupService.IsEnabled();
            if (autoStartEnabled)
            {
                StartupService.RefreshPathIfEnabled();
            }

            if (_settings.AutoStart != autoStartEnabled)
            {
                _settings.AutoStart = autoStartEnabled;
                SettingsStore.Save(_settings);
            }

            _anchor = Enum.TryParse<NotchAnchor>(_settings.Anchor, out var anchor)
                ? anchor
                : NotchAnchor.TopCenter;
            _monitor = SystemParameters.WorkArea;
            SetAnchor(_anchor);

            Loaded += (s, e) =>
            {
                // DPI baru akurat setelah window ter-render
                UpdateMonitorFromWindow();
                var (w, h) = _isExpanded ? ExpandedSize : CollapsedSize;
                SetWindowSize(w, h);
            };
            StartClock();
            Localization.LanguageChanged += OnLanguageChanged;

            // Hover dengan debounce: cursor yang sempat keluar-masuk tidak memicu getaran
            PillHost.MouseEnter += (s, e) =>
            {
                if (_isDragging || IsAuxOpen || _isFileDragOver) return;
                _collapseTimer.Stop();
                ExpandNotch();
            };
            PillHost.MouseLeave += (s, e) =>
            {
                if (_isDragging || IsAuxOpen || _isFileDragOver) return;
                _collapseTimer.Stop();
                _collapseTimer.Start();
            };
            _collapseTimer.Tick += (s, e) =>
            {
                _collapseTimer.Stop();
                if (!_isDragging && !IsAuxOpen && !_isFileDragOver &&
                    !PillHost.IsMouseOver && !FeatureMenuBar.IsMouseOver)
                {
                    CollapseNotch();
                }
            };

            // Bar menu fitur di bawah notch
            FeatureMenuBar.MouseEnter += (s, e) =>
            {
                _collapseTimer.Stop();
            };
            FeatureMenuBar.MouseLeave += (s, e) =>
            {
                if (_isDragging || IsAuxOpen || _isFileDragOver) return;
                _collapseTimer.Stop();
                _collapseTimer.Start();
            };

            NotchiButton.Click += (s, e) =>
            {
                AnimateNotchiIcon();
                RestartApplication();
            };
            FileButton.Click += OnFileButtonClick;
            NoteButton.Click += OnNoteButtonClick;
            FocusButton.Click += OnFocusButtonClick;
            SettingsButton.Click += OnSettingsButtonClick;
            ExitButton.Click += (s, e) => Application.Current.Shutdown();

            PillHost.DragEnter += OnPillDragEnter;
            PillHost.DragOver += OnPillDragOver;
            PillHost.DragLeave += OnPillDragLeave;
            PillHost.Drop += OnPillDrop;

            // Drag & drop: geser notch ke salah satu titik snap
            PillHost.PreviewMouseLeftButtonDown += OnPillMouseDown;
            PillHost.PreviewMouseMove += OnPillMouseMove;
            PillHost.PreviewMouseLeftButtonUp += OnPillMouseUp;

            // Scroll wheel berpindah halaman saat idle, bukan saat pemutar media aktif
            PillHost.PreviewMouseWheel += OnPillMouseWheel;
            IdlePageViewport.SizeChanged += (s, e) => UpdateIdlePageRestPosition();

            PillHost.LostMouseCapture += (s, e) =>
            {
                // Jangan biarkan state drag menggantung bila capture hilang
                if (!_isDragging) return;
                _isDragging = false;
                PillHost.Cursor = Cursors.Hand;
                UpdateFeatureBarVisibility();
                UpdateNotchShape(PillHost.RenderSize);
            };

            // Bentuk pill dibangun ulang setiap ukuran berubah
            PillHost.SizeChanged += (s, e) =>
            {
                UpdateNotchShape(e.NewSize);
                UpdateFeatureBarPosition(e.NewSize.Width, e.NewSize.Height);

                if (_isFileDragOver) UpdateDropIndicatorLayout();

                // Setelah animasi collapse selesai: window snap ke ukuran pill
                // agar tidak ada area transparan yang menelan klik
                var (cw, ch) = CollapsedSize;
                if (!_isDragging &&
                    !_isExpanded &&
                    e.NewSize.Width <= cw + 0.5 &&
                    e.NewSize.Height <= ch + 0.5)
                {
                    SetWindowSize(cw, ch);
                    UpdateContentMargin();
                }
            };

            // Jangan pernah merebut fokus aplikasi lain saat tombol diklik,
            // dan sembunyikan dari Alt+Tab
            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int exStyle = GetWindowLong(hwnd, GwlExStyle);
                SetWindowLong(hwnd, GwlExStyle, exStyle | WsExNoActivate | WsExToolWindow);

                _hotkey.Initialize(this);
                _hotkey.HotkeyPressed += (_, _) => ToggleNotchiVisibility();
                _hotkey.FocusHotkeyPressed += (_, _) => ToggleMediaFocus();
                _hotkey.Register(_settings.HotkeyModifiers, _settings.HotkeyKey);
                _hotkey.RegisterFocus(
                    _settings.FocusHotkeyModifiers, _settings.FocusHotkeyKey);
            };

            _statusTimer.Tick += (s, e) =>
            {
                _statusTimer.Stop();
                StatusPopup.IsOpen = false;
            };

            WireMediaControls();

            // Media: tampilkan info lagu saat ada sesi, fallback ke idle dashboard bila tidak ada
            _smtc.MediaChanged += OnMediaChanged;
            _systemStatus.StatusChanged += OnSystemStatusChanged;
            _mediaTimer.Tick += (s, e) => UpdateProgress();
            _mediaTimer.Start();
            ApplyMedia(null);
            _ = _smtc.InitializeAsync();

            InitMobileServer();
        }

        private void WireMediaControls()
        {
            CollapsedPauseButton.Click += async (s, e) => await _smtc.PlayPauseAsync();
            SidePauseButton.Click += async (s, e) => await _smtc.PlayPauseAsync();
            PlayPauseButton.Click += async (s, e) => await _smtc.PlayPauseAsync();
            PrevButton.Click += async (s, e) => await _smtc.PreviousAsync();
            NextButton.Click += async (s, e) => await _smtc.NextAsync();

            LastMediaPlayPauseButton.Click += async (s, e) => await _smtc.PlayPauseAsync();
            LastMediaPrevButton.Click += async (s, e) => await _smtc.PreviousAsync();
            LastMediaNextButton.Click += async (s, e) => await _smtc.NextAsync();

            // Seek: drag/klik slider, terapkan saat tombol mouse dilepas
            ProgressSlider.PreviewMouseLeftButtonDown += (s, e) => _isSeeking = true;
            ProgressSlider.PreviewMouseLeftButtonUp += (s, e) => CommitSeek();
            ProgressSlider.LostMouseCapture += (s, e) => CommitSeek();
            ProgressSlider.ValueChanged += (s, e) =>
            {
                if (_isSeeking) UpdateTimeLabels(TimeSpan.FromSeconds(ProgressSlider.Value));
            };

            LastMediaProgressSlider.PreviewMouseLeftButtonDown += (s, e) => _isSeeking = true;
            LastMediaProgressSlider.PreviewMouseLeftButtonUp += (s, e) => CommitLastMediaSeek();
            LastMediaProgressSlider.LostMouseCapture += (s, e) => CommitLastMediaSeek();
            LastMediaProgressSlider.ValueChanged += (s, e) =>
            {
                if (!_isSeeking) return;

                double duration = Math.Max(0, _media?.Duration.TotalSeconds ?? 0);
                double position = LastMediaProgressSlider.Value;
                LastMediaPositionText.Text = FormatTime(TimeSpan.FromSeconds(position));
                LastMediaDurationText.Text = duration > 0
                    ? "-" + FormatTime(TimeSpan.FromSeconds(
                        Math.Max(0, duration - position)))
                    : "--:--";
            };
        }

        private void CommitSeek()
        {
            if (!_isSeeking) return;
            _isSeeking = false;
            _ = _smtc.SeekAsync(TimeSpan.FromSeconds(ProgressSlider.Value));
        }

        private void CommitLastMediaSeek()
        {
            if (!_isSeeking) return;
            _isSeeking = false;
            _ = _smtc.SeekAsync(TimeSpan.FromSeconds(LastMediaProgressSlider.Value));
        }

        // ===================== Bar Menu Fitur =====================

        private void RestartApplication()
        {
            try
            {
                ShowStatusPopup(Localization.Get("RestartingApp"), isError: false);

                string exePath = Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "Notchi.exe");

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory
                };

                // Beri sedikit jeda agar animasi putaran icon dan popup terbaca oleh user
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    try
                    {
                        System.Diagnostics.Process.Start(psi);
                    }
                    catch { }
                    Application.Current.Shutdown();
                };
                timer.Start();
            }
            catch
            {
                Application.Current.Shutdown();
            }
        }

        private void AnimateNotchiIcon()
        {
            var anim = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(400)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            NotchiIconRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
        }

        private const double FeatureBarCollapsedWidth = 34.0;
        private const double FeatureBarFullWidth = 194.0;

        private void ShowFeatureBar()
        {
            if (_isDragging) return;

            UpdateFeatureButtonStates();

            // Jika sudah ditampilkan penuh, jangan memulai ulang animasi
            // (mencegah animasi buka-tutup berkedip saat memanggil berulang)
            if (FeatureMenuBar.Visibility == Visibility.Visible
                && FeatureMenuBar.Width >= FeatureBarFullWidth)
                return;

            FeatureMenuBar.Visibility = Visibility.Visible;
            FeatureMenuBar.IsHitTestVisible = true;

            // Pastikan animasi sebelumnya dihentikan
            FeatureMenuBar.BeginAnimation(UIElement.OpacityProperty, null);
            FeatureMenuBar.BeginAnimation(FrameworkElement.WidthProperty, null);

            // Mulai dari icon Notchi bulat di tengah
            FeatureMenuBar.Opacity = 0;
            FeatureMenuBar.Width = FeatureBarCollapsedWidth;

            // Fade in bar secara halus
            var fadeAnim = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180)))
            {
                BeginTime = TimeSpan.FromMilliseconds(40),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            // Animasi memanjang melebar (expand) dari 34px ke 162px
            var expandAnim = new DoubleAnimation(FeatureBarCollapsedWidth, FeatureBarFullWidth, new Duration(TimeSpan.FromMilliseconds(320)))
            {
                BeginTime = TimeSpan.FromMilliseconds(90),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            FeatureMenuBar.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            FeatureMenuBar.BeginAnimation(FrameworkElement.WidthProperty, expandAnim);
        }

        private void HideFeatureBar(bool immediate = false)
        {
            // Jika sudah tersembunyi, jangan memulai ulang animasi
            if (FeatureMenuBar.Visibility == Visibility.Collapsed) return;

            if (immediate)
            {
                FeatureMenuBar.BeginAnimation(UIElement.OpacityProperty, null);
                FeatureMenuBar.BeginAnimation(FrameworkElement.WidthProperty, null);
                FeatureMenuBar.Opacity = 0;
                FeatureMenuBar.Width = FeatureBarCollapsedWidth;
                FeatureMenuBar.Visibility = Visibility.Collapsed;
                FeatureMenuBar.IsHitTestVisible = false;
                return;
            }

            FeatureMenuBar.IsHitTestVisible = false;

            // Animasi menyusut (collapse) kembali ke logo Notchi
            var shrinkAnim = new DoubleAnimation(FeatureMenuBar.ActualWidth > 0 ? FeatureMenuBar.ActualWidth : FeatureBarFullWidth, FeatureBarCollapsedWidth, new Duration(TimeSpan.FromMilliseconds(160)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            var fadeAnim = new DoubleAnimation(FeatureMenuBar.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(140)))
            {
                BeginTime = TimeSpan.FromMilliseconds(40),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            fadeAnim.Completed += (s, e) =>
            {
                if (!_isExpanded)
                {
                    FeatureMenuBar.Visibility = Visibility.Collapsed;
                }
            };

            FeatureMenuBar.BeginAnimation(FrameworkElement.WidthProperty, shrinkAnim);
            FeatureMenuBar.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        }

        private static readonly SolidColorBrush ActiveFeatureBgBrush = CreateFrozenBrush(0xFF, 0xFF, 0xFF);
        private static readonly SolidColorBrush ActiveFeatureDarkBrush = CreateFrozenBrush(0x12, 0x12, 0x14);
        private static readonly SolidColorBrush InactiveFeatureLightBrush = CreateFrozenBrush(0xF5, 0xF5, 0xF7);

        private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private void UpdateFeatureButtonStates()
        {
            bool isShelfActive = _dropShelf is { IsVisible: true };
            FileIconBorder.Background = isShelfActive ? ActiveFeatureBgBrush : Brushes.Transparent;
            FileIconPath.Stroke = isShelfActive ? ActiveFeatureDarkBrush : InactiveFeatureLightBrush;

            bool isNoteActive = _noteWindow is { IsVisible: true };
            NoteIconBorder.Background = isNoteActive ? ActiveFeatureBgBrush : Brushes.Transparent;
            NoteIconPath.Stroke = isNoteActive ? ActiveFeatureDarkBrush : InactiveFeatureLightBrush;

            bool isFocusActive = _focusWindow is { IsVisible: true };
            FocusIconBorder.Background = isFocusActive ? ActiveFeatureBgBrush : Brushes.Transparent;
            FocusIconPath.Stroke = isFocusActive ? ActiveFeatureDarkBrush : InactiveFeatureLightBrush;

            bool isSettingsActive = _settingsWindow is { IsVisible: true };
            SettingsIconBorder.Background = isSettingsActive ? ActiveFeatureBgBrush : Brushes.Transparent;
            SettingsIconPath.Fill = isSettingsActive ? ActiveFeatureDarkBrush : InactiveFeatureLightBrush;
        }

        private void OnFileButtonClick(object sender, RoutedEventArgs e)
        {
            if (_dropShelf is { IsVisible: true })
            {
                _dropShelf.Close();
                return;
            }

            ShowFileShelf();
        }

        private void OnNoteButtonClick(object sender, RoutedEventArgs e)
        {
            if (_noteWindow is { IsVisible: true })
            {
                _noteWindow.Close();
                return;
            }

            ShowNoteWindow();
        }

        private void OnFocusButtonClick(object sender, RoutedEventArgs e)
        {
            if (_focusWindow is { IsVisible: true })
            {
                CloseMediaFocus();
                return;
            }

            OpenMediaFocus();
        }

        private void ToggleMediaFocus()
        {
            if (_focusWindow is { IsVisible: true })
            {
                CloseMediaFocus();
                return;
            }

            OpenMediaFocus();
        }

        private void OpenMediaFocus()
        {
            if (_focusWindow is { IsVisible: true }) return;

            var window = new MediaFocusWindow(_smtc, _settings);
            PositionFocusWindow(window);

            window.PositionSaved += (_, _) =>
            {
                _settings.FocusLeft = window.Left;
                _settings.FocusTop = window.Top;
                SettingsStore.Save(_settings);
            };

            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_focusWindow, window))
                {
                    _focusWindow = null;
                }

                RestoreNotchAfterFocus();

                if (!_isMainClosing)
                {
                    UpdateFeatureButtonStates();
                }
            };

            _focusWindow = window;

            if (_settings.HideNotchInFocus)
            {
                _focusHidNotch = Visibility == Visibility.Visible && !_isHidden;

                if (_focusHidNotch)
                {
                    BeginAnimation(OpacityProperty, null);
                    Opacity = 1;
                    Visibility = Visibility.Hidden;
                }
            }

            UpdateFeatureButtonStates();
            window.Show();
        }

        private void CloseMediaFocus()
        {
            _focusWindow?.CloseAnimated();
        }

        private void RestoreNotchAfterFocus()
        {
            if (_isMainClosing ||
                Dispatcher.HasShutdownStarted ||
                Dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (!_focusHidNotch) return;

            _focusHidNotch = false;
            if (_isHidden) return;

            Visibility = Visibility.Visible;
            Opacity = 0;

            var fadeIn = new DoubleAnimation(0, 1,
                new Duration(TimeSpan.FromMilliseconds(180)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            BeginAnimation(OpacityProperty, fadeIn);
        }

        private void PositionFocusWindow(MediaFocusWindow window)
        {
            var work = _monitor;
            if (work.Width < 1 || work.Height < 1)
            {
                work = SystemParameters.WorkArea;
            }

            const double margin = 8;

            double left = _settings.FocusLeft ??
                work.Left + (work.Width - window.Width) / 2;

            double top = _settings.FocusTop ??
                work.Top + (work.Height - window.Height) / 2;

            left = Math.Clamp(left,
                work.Left + margin,
                Math.Max(work.Left + margin, work.Right - window.Width - margin));

            top = Math.Clamp(top,
                work.Top + margin,
                Math.Max(work.Top + margin, work.Bottom - window.Height - margin));

            window.Left = left;
            window.Top = top;
        }

        private void OnSettingsButtonClick(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow is { IsVisible: true })
            {
                _settingsWindow.Close();
                return;
            }

            ShowSettingsWindow();
        }

        private void CloseOtherAuxWindows(Window? keepWindow = null)
        {
            if (_dropShelf != null && _dropShelf != keepWindow)
            {
                var shelf = _dropShelf;
                _dropShelf = null;
                try
                {
                    if (shelf.IsVisible)
                    {
                        shelf.Close();
                    }
                }
                catch (InvalidOperationException) { }
            }

            if (_noteWindow != null && _noteWindow != keepWindow)
            {
                var note = _noteWindow;
                _noteWindow = null;
                try
                {
                    if (note.IsVisible)
                    {
                        note.Close();
                    }
                }
                catch (InvalidOperationException) { }
            }

            if (_settingsWindow != null && _settingsWindow != keepWindow)
            {
                var settings = _settingsWindow;
                _settingsWindow = null;
                try
                {
                    if (settings.IsVisible)
                    {
                        settings.Close();
                    }
                }
                catch (InvalidOperationException) { }
            }

            UpdateFeatureButtonStates();
        }

        private void ShowNoteWindow()
        {
            CloseOtherAuxWindows(_noteWindow);

            if (_noteWindow is { IsVisible: true })
            {
                _noteWindow.Activate();
                return;
            }

            var note = new NoteWindow();
            PositionAuxWindow(note);

            note.PositionSaved += (s, e) =>
            {
                _settings.NoteLeft = note.Left;
                _settings.NoteTop = note.Top;
                SettingsStore.Save(_settings);
            };

            note.Closed += (s, e) =>
            {
                _noteWindow = null;
                UpdateFeatureButtonStates();

                if (PillHost.IsMouseOver || FeatureMenuBar.IsMouseOver || IsAuxOpen) return;

                _collapseTimer.Stop();
                CollapseNotch();
            };

            _noteWindow = note;
            note.Show();
            note.Activate();
            UpdateFeatureButtonStates();
        }

        private void ShowSettingsWindow()
        {
            CloseOtherAuxWindows(_settingsWindow);

            if (_settingsWindow is { IsVisible: true })
            {
                _settingsWindow.Activate();
                return;
            }

            var settings = new SettingsWindow(_settings);
            PositionAuxWindow(settings);

            settings.SettingsChanged += (s, e) =>
            {
                _hotkey.Register(_settings.HotkeyModifiers, _settings.HotkeyKey);
                _hotkey.RegisterFocus(
                    _settings.FocusHotkeyModifiers, _settings.FocusHotkeyKey);
                _focusWindow?.RefreshSettings();
                ApplyMobileServerSettings();
            };

            settings.Closed += (s, e) =>
            {
                _settingsWindow = null;
                UpdateFeatureButtonStates();

                if (PillHost.IsMouseOver || FeatureMenuBar.IsMouseOver || IsAuxOpen) return;

                _collapseTimer.Stop();
                CollapseNotch();
            };

            _settingsWindow = settings;
            settings.Show();
            settings.Activate();
            UpdateFeatureButtonStates();
        }

        private void PositionAuxWindow(Window window)
        {
            const double gap = 8;
            var work = _monitor;
            if (work.Width < 1 || work.Height < 1)
            {
                work = new Rect(0, 0,
                    SystemParameters.PrimaryScreenWidth,
                    SystemParameters.PrimaryScreenHeight);
            }

            double? savedLeft = null;
            double? savedTop = null;

            if (window is NoteWindow)
            {
                savedLeft = _settings.NoteLeft;
                savedTop = _settings.NoteTop;
            }
            else if (window is SettingsWindow)
            {
                savedLeft = _settings.SettingsLeft;
                savedTop = _settings.SettingsTop;
            }
            else if (window is DropShelfWindow)
            {
                savedLeft = _settings.DropShelfLeft;
                savedTop = _settings.DropShelfTop;
            }

            double left;
            double top;

            if (savedLeft.HasValue && savedTop.HasValue)
            {
                left = savedLeft.Value;
                top = savedTop.Value;
            }
            else
            {
                if (_anchor == NotchAnchor.LeftCenter)
                {
                    left = Left + Width + gap;
                    top = Top;
                }
                else if (_anchor == NotchAnchor.RightCenter)
                {
                    left = Left - window.Width - gap;
                    top = Top;
                }
                else if (_anchor == NotchAnchor.BottomCenter)
                {
                    left = Left + (Width - window.Width) / 2;
                    top = Top - window.Height - gap;
                }
                else
                {
                    double notchBottom = Top + (_isExpanded ? ExpandedHeight + FeatureBarExtraHeight : CollapsedHeight);
                    top = notchBottom + gap;
                    left = Left + (Width - window.Width) / 2;
                }
            }

            left = Math.Clamp(left,
                work.Left + gap,
                Math.Max(work.Left + gap, work.Right - window.Width - gap));
            top = Math.Clamp(top,
                work.Top + gap,
                Math.Max(work.Top + gap, work.Bottom - window.Height - gap));

            window.Left = left;
            window.Top = top;
        }

        private void OnMediaChanged(MediaInfo? info)
        {
            // Event SMTC datang dari thread lain
            Dispatcher.BeginInvoke(() => ApplyMedia(info));
        }

        private void OnSystemStatusChanged(SystemStatusSnapshot status)
        {
            // Sampling berjalan di thread pool; update UI di dispatcher
            Dispatcher.BeginInvoke(() => ApplySystemStatus(status));
        }

        private void ApplySystemStatus(SystemStatusSnapshot status)
        {
            SetMetric(ExpandedCpuBar, ExpandedCpuText, status.CpuPercent);
            SetMetric(ExpandedRamBar, ExpandedRamText, status.RamPercent);
            SetMetric(ExpandedDiskBar, ExpandedDiskText, status.DiskPercent);

            ExpandedDiskLabel.Text = status.DiskLabel;

            ApplyBattery(status.BatteryPercent, status.IsCharging,
                ExpandedBatteryPanel, ExpandedBatteryFill, ExpandedBatteryText, 16);
        }

        private void ApplyMedia(MediaInfo? info)
        {
            if (info == null && _media != null)
            {
                _lastMedia = SnapshotMedia(_media);
            }

            _media = info;

            if (info == null)
            {
                UpdateLastMediaPage();
                _systemStatus.Start();
                UpdateVisualState();
                UpdateAccent();
                return;
            }

            _lastMedia = info;
            UpdateLastMediaPage();

            TrackTitle.Text = info.Title;
            TrackArtist.Text = info.Artist;
            SetAlbumArt(info.AlbumArt);

            ApplySourceApp(info.SourceAppId);
            UpdatePlayPauseGlyph(info.IsPlaying);

            UpdateVisualState();
            UpdateAccent();

            ProgressSlider.Maximum = Math.Max(1, info.Duration.TotalSeconds);
            if (!_isSeeking)
            {
                ProgressSlider.Value = Math.Clamp(info.Position.TotalSeconds, 0, ProgressSlider.Maximum);
                UpdateTimeLabels(info.Position);
            }
        }

        private MediaInfo SnapshotMedia(MediaInfo source)
        {
            double position = Math.Clamp(
                ProgressSlider.Value, 0, Math.Max(0, source.Duration.TotalSeconds));

            return new MediaInfo
            {
                Title = source.Title,
                Artist = source.Artist,
                Album = source.Album,
                AlbumArt = source.AlbumArt,
                SourceAppId = source.SourceAppId,
                IsPlaying = false,
                Position = TimeSpan.FromSeconds(position),
                Duration = source.Duration
            };
        }

        private void UpdateLastMediaPage()
        {
            bool hasMedia = _lastMedia != null;

            LastMediaContent.Visibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;
            LastMediaEmptyPanel.Visibility = hasMedia ? Visibility.Collapsed : Visibility.Visible;

            if (_lastMedia is not MediaInfo media) return;

            LastMediaTitle.Text = media.Title;
            LastMediaArtist.Text = media.Artist;
            SetLastMediaArt(media.AlbumArt);
            ApplySourceApp(
                media.SourceAppId, LastMediaSourceIcon, LastMediaSourceLogo, LastMediaSourceGlyph);

            double duration = Math.Max(0, media.Duration.TotalSeconds);
            double position = Math.Clamp(
                media.Position.TotalSeconds, 0, duration > 0 ? duration : 0);

            LastMediaProgressSlider.IsHitTestVisible = _media != null;
            LastMediaControlsRow.IsEnabled = _media != null;
            LastMediaControlsRow.Opacity = _media != null ? 1 : 0.4;
            LastMediaProgressSlider.Maximum = Math.Max(1, duration);
            LastMediaProgressSlider.Value = position;
            LastMediaPositionText.Text = FormatTime(TimeSpan.FromSeconds(position));
            LastMediaDurationText.Text = duration > 0
                ? "-" + FormatTime(TimeSpan.FromSeconds(Math.Max(0, duration - position)))
                : "--:--";

            LastMediaPlayPausePath.Data = Geometry.Parse(media.IsPlaying
                ? "M0,0 h4 v14 h-4 z M8,0 h4 v14 h-4 z"
                : "M0,0 L12,7 L0,14 z");
        }

        private void SetLastMediaArt(ImageSource? art)
        {
            if (art != null)
            {
                LastMediaArt.Source = art;
                LastMediaArt.Visibility = Visibility.Visible;
                LastMediaArtPlaceholder.Visibility = Visibility.Collapsed;
                return;
            }

            LastMediaArt.Source = null;
            LastMediaArt.Visibility = Visibility.Collapsed;
            LastMediaArtPlaceholder.Visibility = Visibility.Visible;
        }

        private void SetAlbumArt(ImageSource? art)
        {
            if (art != null)
            {
                AlbumArt.Source = art;
                AlbumArt.Visibility = Visibility.Visible;
                AlbumArtPlaceholder.Visibility = Visibility.Collapsed;

                SideAlbumArt.Source = art;
                SideAlbumArt.Visibility = Visibility.Visible;
                SideAlbumArtPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                AlbumArt.Source = null;
                AlbumArt.Visibility = Visibility.Collapsed;
                AlbumArtPlaceholder.Visibility = Visibility.Visible;

                SideAlbumArt.Source = null;
                SideAlbumArt.Visibility = Visibility.Collapsed;
                SideAlbumArtPlaceholder.Visibility = Visibility.Visible;
            }
        }

        // Ikon aplikasi sumber: logo platform bila dikenal, fallback ke glyph musik.
        private void ApplySourceApp(string aumid) =>
            ApplySourceApp(aumid, SourceAppIcon, SourceAppLogo, SourceAppGlyph);

        private void ApplySourceApp(string aumid, Border icon, Image logo, TextBlock glyph)
        {
            string? logoKey = TryGetSourceLogoKey(aumid);

            if (logoKey != null && TryFindResource(logoKey) is ImageSource logoSource)
            {
                logo.Source = logoSource;
                logo.Visibility = Visibility.Visible;
                glyph.Visibility = Visibility.Collapsed;
                icon.Background = Brushes.Transparent;
                return;
            }

            Color color = GetSourceColor(aumid) ?? UnknownSourceColor;

            logo.Source = null;
            logo.Visibility = Visibility.Collapsed;
            glyph.Visibility = Visibility.Visible;
            icon.Background = FrozenBrush(color);
        }

        private static string? TryGetSourceLogoKey(string aumid)
        {
            var id = (aumid ?? string.Empty).ToLowerInvariant();

            if (id.Contains("spotify")) return "SpotifyLogoImage";
            if (id.Contains("chrome")) return "ChromeLogoImage";
            if (id.Contains("youtube")) return "YouTubeLogoImage";

            return null;
        }

        private static Color? GetSourceColor(string aumid)
        {
            var id = (aumid ?? string.Empty).ToLowerInvariant();

            if (id.Contains("spotify")) return Color.FromRgb(0x1D, 0xB9, 0x54);
            if (id.Contains("chrome")) return Color.FromRgb(0xEA, 0x43, 0x35);
            if (id.Contains("msedge") || id.Contains("edge")) return Color.FromRgb(0x00, 0x78, 0xD4);
            if (id.Contains("firefox")) return Color.FromRgb(0xFF, 0x71, 0x39);
            if (id.Contains("youtube")) return Color.FromRgb(0xFF, 0x00, 0x00);
            if (id.Contains("foobar")) return Color.FromRgb(0x2B, 0x2B, 0x2B);

            return null;
        }

        // Warna aksen mengikuti state: drop > media > idle.
        private void UpdateAccent()
        {
            Color accent = _isFileDragOver
                ? DropAccent
                : _media == null
                    ? IdleAccent
                    : GetSourceColor(_media.SourceAppId) ?? IdleAccent;

            NotchAccent.Fill = CreateAccentBrush(accent);
            UpdateProgressFill(_media);
        }

        // Progress bar mengikuti warna sumber media dengan gradasi terang -> warna.
        private void UpdateProgressFill(MediaInfo? media)
        {
            Color? sourceColor = media == null ? null : GetSourceColor(media.SourceAppId);
            Brush brush = sourceColor is Color color
                ? CreateProgressBrush(color)
                : (TryFindResource("MediaProgressBrush") as Brush ?? Brushes.White);

            ProgressSlider.Foreground = brush;
            LastMediaProgressSlider.Foreground = brush;
        }

        private static Brush CreateProgressBrush(Color accent)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };
            brush.GradientStops.Add(new GradientStop(Blend(accent, Colors.White, 0.65), 0));
            brush.GradientStops.Add(new GradientStop(Blend(accent, Colors.White, 0.3), 0.5));
            brush.GradientStops.Add(new GradientStop(accent, 1));
            brush.Freeze();
            return brush;
        }

        private static Color Blend(Color a, Color b, double t)
        {
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        // Arah gradasi mengikuti sisi yang menempel ke layar.
        private void UpdateSurface()
        {
            NotchShape.Fill = CreateBaseBrush();
            NotchSheen.Fill = CreateSheenBrush();

            Brush attachedEdgeMask = CreateAttachedEdgeMask();
            NotchOuterBorder.OpacityMask = attachedEdgeMask;
            NotchBorder.OpacityMask = attachedEdgeMask;
        }

        private (Point Start, Point End) EdgeGradientPoints => _anchor switch
        {
            NotchAnchor.LeftCenter => (new Point(0, 0.5), new Point(1, 0.5)),
            NotchAnchor.RightCenter => (new Point(1, 0.5), new Point(0, 0.5)),
            NotchAnchor.BottomCenter => (new Point(0, 1), new Point(0, 0)),
            _ => (new Point(0, 0), new Point(0, 1))
        };

        private Brush CreateBaseBrush()
        {
            (Point start, Point end) = EdgeGradientPoints;

            var brush = new LinearGradientBrush
            {
                StartPoint = start,
                EndPoint = end
            };

            brush.GradientStops.Add(new GradientStop(AttachedEdgeColor, 0));
            brush.GradientStops.Add(new GradientStop(BaseMidColor, 0.45));
            brush.GradientStops.Add(new GradientStop(BaseTintColor, 0.75));
            brush.GradientStops.Add(new GradientStop(BaseOuterColor, 1));

            brush.Freeze();
            return brush;
        }

        private Brush CreateSheenBrush()
        {
            (Point start, Point end) = EdgeGradientPoints;

            var brush = new LinearGradientBrush
            {
                StartPoint = start,
                EndPoint = end
            };

            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x06, 255, 255, 255), 1));

            brush.Freeze();
            return brush;
        }

        // Menghilangkan stroke tepat di sisi yang menempel ke layar.
        private Brush CreateAttachedEdgeMask()
        {
            (Point start, Point end) = EdgeGradientPoints;

            var brush = new LinearGradientBrush
            {
                StartPoint = start,
                EndPoint = end
            };

            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.03));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 255, 255, 255), 0.12));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 255, 255, 255), 1));

            brush.Freeze();
            return brush;
        }

        private Brush CreateAccentBrush(Color accent)
        {
            Point center = _anchor switch
            {
                NotchAnchor.LeftCenter => new Point(1, 0.5),
                NotchAnchor.RightCenter => new Point(0, 0.5),
                _ => new Point(0.82, 0.5)
            };

            var brush = new RadialGradientBrush
            {
                Center = center,
                GradientOrigin = center,
                RadiusX = IsSideAnchor ? 0.95 : 0.72,
                RadiusY = 0.95,
                MappingMode = BrushMappingMode.RelativeToBoundingBox
            };

            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb(0x38, accent.R, accent.G, accent.B), 0));
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb(0x1C, accent.R, accent.G, accent.B), 0.45));
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb(0x08, accent.R, accent.G, accent.B), 0.75));
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1));

            brush.Freeze();
            return brush;
        }

        private void UpdatePlayPauseGlyph(bool isPlaying)
        {
            // Pause = dua bar, play = segitiga
            string collapsed = isPlaying
                ? "M0,0 h4 v13 h-4 z M6,0 h4 v13 h-4 z"
                : "M0,0 L10,6.5 L0,13 z";
            string expanded = isPlaying
                ? "M0,0 h4 v14 h-4 z M8,0 h4 v14 h-4 z"
                : "M0,0 L12,7 L0,14 z";

            CollapsedPausePath.Data = Geometry.Parse(collapsed);
            SidePausePath.Data = Geometry.Parse(collapsed);
            PlayPausePath.Data = Geometry.Parse(expanded);
            LastMediaPlayPausePath.Data = Geometry.Parse(expanded);
        }

        private void UpdateProgress()
        {
            if (_media == null || _isSeeking) return;

            var position = _smtc.GetPosition();
            ProgressSlider.Value = Math.Clamp(position.TotalSeconds, 0, ProgressSlider.Maximum);
            UpdateTimeLabels(position);

            double duration = Math.Max(0, _media.Duration.TotalSeconds);
            LastMediaProgressSlider.Maximum = Math.Max(1, duration);
            LastMediaProgressSlider.Value = Math.Clamp(
                position.TotalSeconds, 0, LastMediaProgressSlider.Maximum);
            LastMediaPositionText.Text = FormatTime(position);
            LastMediaDurationText.Text = duration > 0
                ? "-" + FormatTime(TimeSpan.FromSeconds(
                    Math.Max(0, duration - position.TotalSeconds)))
                : "--:--";
        }

        // Label kiri "0:11", kanan sisa waktu "-3:22" (ala mockup)
        private void UpdateTimeLabels(TimeSpan position)
        {
            PositionText.Text = FormatTime(position);

            if (_media == null || _media.Duration <= TimeSpan.Zero)
            {
                DurationText.Text = "--:--";
                return;
            }

            var remaining = _media.Duration - position;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            DurationText.Text = "-" + FormatTime(remaining);
        }

        private static string FormatTime(TimeSpan time)
        {
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;
            return time.TotalHours >= 1
                ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
                : $"{time.Minutes}:{time.Seconds:00}";
        }

        // ===================== Posisi & Bentuk =====================

        // Membentuk dua lapis pill: selimut hitam dengan gelombang di dekat
        // tepi layar, lalu capsule gradasi yang dibungkus di dalamnya.
        // Saat digeser: keduanya memakai kapsul penuh agar netral dan rapi.
        private void UpdateNotchShape(Size size)
        {
            double w = size.Width;
            double h = size.Height;
            if (w < 1 || h < 1) return;

            if (_isDragging)
            {
                double radius = Math.Min(w, h) / 2;
                var capsule = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
                capsule.Freeze();
                NotchSleeve.Data = capsule;
                NotchShape.Data = capsule;
                return;
            }

            if (_domeMode)
            {
                // Mode bukit: lengkungan hitam kecil, tanpa isi.
                NotchSleeve.Data = CreateDomeGeometry(w, h);
                NotchShape.Visibility = Visibility.Collapsed;
                NotchAccent.Visibility = Visibility.Collapsed;
                NotchSheen.Visibility = Visibility.Collapsed;
                NotchOuterBorder.Visibility = Visibility.Collapsed;
                NotchBorder.Visibility = Visibility.Collapsed;
                return;
            }

            NotchShape.Visibility = Visibility.Visible;
            NotchAccent.Visibility = Visibility.Visible;
            NotchSheen.Visibility = Visibility.Visible;
            NotchOuterBorder.Visibility = Visibility.Visible;
            NotchBorder.Visibility = Visibility.Visible;
            NotchSleeve.Data = CreateSleeveGeometry(w, h);
            NotchShape.Data = CreateInnerCapsuleGeometry(w, h);
        }

        private Geometry CreateDomeGeometry(double w, double h)
        {
            // Kedalaman bukit dibuat kecil/tipis dan mengikuti sisi yang menempel.
            double depth = Math.Min(16, Math.Min(w, h) * 0.45);
            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                switch (_anchor)
                {
                    case NotchAnchor.BottomCenter:
                        ctx.BeginFigure(new Point(0, h), isFilled: true, isClosed: true);
                        ctx.LineTo(new Point(w, h), true, false);
                        ctx.BezierTo(new Point(w * 0.66, h - depth),
                                     new Point(w * 0.34, h - depth),
                                     new Point(0, h), true, false);
                        break;

                    case NotchAnchor.LeftCenter:
                        ctx.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
                        ctx.LineTo(new Point(0, h), true, false);
                        ctx.BezierTo(new Point(depth, h * 0.66),
                                     new Point(depth, h * 0.34),
                                     new Point(0, 0), true, false);
                        break;

                    case NotchAnchor.RightCenter:
                        ctx.BeginFigure(new Point(w, 0), isFilled: true, isClosed: true);
                        ctx.LineTo(new Point(w, h), true, false);
                        ctx.BezierTo(new Point(w - depth, h * 0.66),
                                     new Point(w - depth, h * 0.34),
                                     new Point(w, 0), true, false);
                        break;

                    default: // TopCenter
                        ctx.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
                        ctx.LineTo(new Point(w, 0), true, false);
                        ctx.BezierTo(new Point(w * 0.66, depth),
                                     new Point(w * 0.34, depth),
                                     new Point(0, 0), true, false);
                        break;
                }
            }
            geometry.Freeze();
            return geometry;
        }

        private void UpdateDomeVisibility()
        {
            ContentRoot.Visibility = _domeMode ? Visibility.Collapsed : Visibility.Visible;
        }

        private Geometry CreateSleeveGeometry(double w, double h)
        {
            double shoulder = Math.Min(SleeveShoulderInset,
                Math.Max(0, Math.Min(w, h) / 4));
            double innerRadius = CreateInnerRadius(w, h);
            double radius = Math.Min(Math.Min(w, h) / 2,
                innerRadius + SleeveRim);

            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                if (IsSideAnchor)
                {
                    // Tepi tempel di kiri; sudut atas/bawah melebar keluar.
                    ctx.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
                    ctx.ArcTo(new Point(shoulder, shoulder), new Size(shoulder, shoulder),
                              0, false, SweepDirection.Counterclockwise, true, false);
                    ctx.LineTo(new Point(w - radius, shoulder), true, false);
                    ctx.ArcTo(new Point(w, shoulder + radius), new Size(radius, radius),
                              0, false, SweepDirection.Clockwise, true, false);
                    ctx.LineTo(new Point(w, h - shoulder - radius), true, false);
                    ctx.ArcTo(new Point(w - radius, h - shoulder), new Size(radius, radius),
                              0, false, SweepDirection.Clockwise, true, false);
                    ctx.LineTo(new Point(shoulder, h - shoulder), true, false);
                    ctx.ArcTo(new Point(0, h), new Size(shoulder, shoulder),
                              0, false, SweepDirection.Counterclockwise, true, false);
                }
                else
                {
                    // Tepi tempel di atas; sudut kiri/kanan melebar keluar.
                    ctx.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
                    ctx.LineTo(new Point(w, 0), true, false);
                    ctx.ArcTo(new Point(w - shoulder, shoulder), new Size(shoulder, shoulder),
                              0, false, SweepDirection.Counterclockwise, true, false);
                    ctx.LineTo(new Point(w - shoulder, h - radius), true, false);
                    ctx.ArcTo(new Point(w - shoulder - radius, h), new Size(radius, radius),
                              0, false, SweepDirection.Clockwise, true, false);
                    ctx.LineTo(new Point(shoulder + radius, h), true, false);
                    ctx.ArcTo(new Point(shoulder, h - radius), new Size(radius, radius),
                              0, false, SweepDirection.Clockwise, true, false);
                    ctx.LineTo(new Point(shoulder, shoulder), true, false);
                    ctx.ArcTo(new Point(0, 0), new Size(shoulder, shoulder),
                              0, false, SweepDirection.Counterclockwise, true, false);
                }
            }

            if (_anchor == NotchAnchor.BottomCenter)
            {
                geometry.Transform = new ScaleTransform(1, -1, 0, h / 2);
            }
            else if (_anchor == NotchAnchor.RightCenter)
            {
                geometry.Transform = new ScaleTransform(-1, 1, w / 2, 0);
            }

            geometry.Freeze();
            return geometry;
        }

        private double CreateInnerRadius(double w, double h)
        {
            (double insetX, double insetY) = IsSideAnchor
                ? (SleeveRim, SleeveSpanInset)
                : (SleeveSpanInset, SleeveRim);
            double innerWidth = Math.Max(1, w - insetX * 2);
            double innerHeight = Math.Max(1, h - insetY * 2);

            return Math.Min(SleeveCornerRadius,
                Math.Min(innerWidth, innerHeight) / 2);
        }

        private Geometry CreateInnerCapsuleGeometry(double w, double h)
        {
            (double insetX, double insetY) = IsSideAnchor
                ? (SleeveRim, SleeveSpanInset)
                : (SleeveSpanInset, SleeveRim);
            double innerWidth = Math.Max(1, w - insetX * 2);
            double innerHeight = Math.Max(1, h - insetY * 2);
            double radius = Math.Min(SleeveCornerRadius,
                Math.Min(innerWidth, innerHeight) / 2);

            var capsule = new RectangleGeometry(
                new Rect(insetX, insetY, innerWidth, innerHeight), radius, radius);
            capsule.Freeze();
            return capsule;
        }

        private void SetAnchor(NotchAnchor anchor)
        {
            _anchor = anchor;

            // Hentikan animasi yang mungkin masih berjalan sebelum mengubah ukuran dasar
            _animationVersion++;
            PillHost.BeginAnimation(FrameworkElement.WidthProperty, null);
            PillHost.BeginAnimation(FrameworkElement.HeightProperty, null);

            var (w, h) = _isExpanded ? ExpandedSize : CollapsedSize;

            PillHost.HorizontalAlignment = anchor switch
            {
                NotchAnchor.LeftCenter => HorizontalAlignment.Left,
                NotchAnchor.RightCenter => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Center
            };
            PillHost.VerticalAlignment = anchor == NotchAnchor.BottomCenter
                ? VerticalAlignment.Bottom
                : VerticalAlignment.Top;

            PillHost.Width = w;
            PillHost.Height = h;

            UpdateContentMargin();
            SetWindowSize(w, h);
            UpdateFeatureBarPosition(w, h);
            UpdateNotchShape(new Size(w, h));
            UpdateSurface();
            UpdateVisualState();
            UpdateAccent();
            UpdatePopupPlacement();
        }

        private void UpdatePopupPlacement()
        {
            bool bottom = _anchor == NotchAnchor.BottomCenter;
            PlacementMode placement = bottom ? PlacementMode.Top : PlacementMode.Bottom;

            StatusPopup.Placement = placement;
            PairingPromptPopup.Placement = placement;

            double statusOffset = _isExpanded
                ? FeatureBarGap + FeatureBarHeight + 14
                : 8;
            StatusPopup.VerticalOffset = bottom ? -statusOffset : statusOffset;

            double pairingOffset = _isExpanded
                ? FeatureBarGap + FeatureBarHeight + 10
                : 10;
            PairingPromptPopup.VerticalOffset = bottom ? -pairingOffset : pairingOffset;
        }

        private void UpdateContentMargin()
        {
            // Tepi capsule dalam menjorok ke dalam (SleeveSpanInset di sisi yang
            // tidak menempel layar), jadi konten diberi margin agar tidak keluar.
            double sideMargin = IsSideAnchor && !_isExpanded ? 0 : SleeveSpanInset + 2;
            ContentRoot.Margin = new Thickness(sideMargin, 0, sideMargin, 0);
        }

        private void UpdateFeatureBarVisibility()
        {
            if (_isDragging || !_isExpanded)
            {
                HideFeatureBar(immediate: true);
            }
            else
            {
                ShowFeatureBar();
            }
        }

        private void UpdateFeatureBarPosition(double pillWidth, double pillHeight)
        {
            double gap = pillHeight + FeatureBarGap;
            bool bottom = _anchor == NotchAnchor.BottomCenter;
            FeatureMenuBar.VerticalAlignment = bottom
                ? VerticalAlignment.Bottom
                : VerticalAlignment.Top;

            if (_anchor == NotchAnchor.RightCenter)
            {
                FeatureMenuBar.HorizontalAlignment = HorizontalAlignment.Right;
                FeatureMenuBar.Margin = new Thickness(0, gap, 14, 0);
            }
            else if (_anchor == NotchAnchor.LeftCenter)
            {
                FeatureMenuBar.HorizontalAlignment = HorizontalAlignment.Left;
                FeatureMenuBar.Margin = new Thickness(14, gap, 0, 0);
            }
            else if (bottom)
            {
                FeatureMenuBar.HorizontalAlignment = HorizontalAlignment.Center;
                FeatureMenuBar.Margin = new Thickness(0, 0, 0, gap);
            }
            else
            {
                FeatureMenuBar.HorizontalAlignment = HorizontalAlignment.Center;
                FeatureMenuBar.Margin = new Thickness(0, gap, 0, 0);
            }
        }

        private void SetWindowSize(double targetWidth, double targetHeight)
        {
            double windowWidth = targetWidth;
            double windowHeight = _isExpanded
                ? targetHeight + FeatureBarExtraHeight
                : targetHeight;

            Width = windowWidth;
            Height = windowHeight;

            var work = _monitor;
            if (work.Width < 1 || work.Height < 1)
            {
                work = new Rect(0, 0,
                    SystemParameters.PrimaryScreenWidth,
                    SystemParameters.PrimaryScreenHeight);
            }

            switch (_anchor)
            {
                case NotchAnchor.LeftCenter:
                    Left = work.Left;
                    Top = work.Top + (work.Height - targetHeight) / 2;
                    break;
                case NotchAnchor.RightCenter:
                    Left = work.Right - windowWidth;
                    Top = work.Top + (work.Height - targetHeight) / 2;
                    break;
                case NotchAnchor.BottomCenter:
                    Left = work.Left + (work.Width - windowWidth) / 2;
                    Top = work.Bottom - windowHeight;
                    break;
                default:
                    Left = work.Left + (work.Width - windowWidth) / 2;
                    Top = work.Top;
                    break;
            }

            UpdateFeatureBarPosition(targetWidth, targetHeight);
        }

        // Area kerja monitor (dalam DIP) tempat notch berada
        private void UpdateMonitorFromWindow()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
                UpdateMonitor(monitor);
            }
            catch
            {
                // Pakai monitor utama bila query gagal
            }
        }

        private void UpdateMonitorFromCursor()
        {
            try
            {
                if (GetCursorPos(out var point))
                {
                    UpdateMonitor(MonitorFromPoint(point, MonitorDefaultToNearest));
                }
            }
            catch
            {
                // Pakai monitor utama bila query gagal
            }
        }

        private void UpdateMonitor(IntPtr monitor)
        {
            if (monitor == IntPtr.Zero) return;

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return;

            // GetMonitorInfo mengembalikan piksel fisik; WPF memakai DIP.
            // DPI diambil dari window; fallback ke VisualTree setelah render.
            double scaleX = 1, scaleY = 1;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi > 0)
                {
                    scaleX = dpi / 96.0;
                    scaleY = scaleX;
                }
            }

            if (scaleX <= 0 || scaleX == 1)
            {
                var visual = VisualTreeHelper.GetDpi(this);
                if (visual.DpiScaleX > 0) scaleX = visual.DpiScaleX;
                if (visual.DpiScaleY > 0) scaleY = visual.DpiScaleY;
            }

            if (scaleX <= 0) scaleX = 1;
            if (scaleY <= 0) scaleY = 1;

            _monitor = new Rect(
                info.Work.Left / scaleX,
                info.Work.Top / scaleY,
                (info.Work.Right - info.Work.Left) / scaleX,
                (info.Work.Bottom - info.Work.Top) / scaleY);
        }

        // ===================== Drag & Drop =====================

        // Anchor bawah menempel di tepi bawah window, jadi saat expanded ada
        // ruang tambahan (bar fitur) di atas pill.
        private double GetPillVerticalOffset()
        {
            if (_anchor != NotchAnchor.BottomCenter) return 0;
            return Math.Max(0, ActualHeight - PillHost.ActualHeight);
        }

        private void OnPillMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;
            if (!GetCursorPos(out var cursor)) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            double scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

            _dragStartCursor = new Point(cursor.X, cursor.Y);
            // Offset kursor terhadap tepi kiri pill (bukan window) agar notch tidak
            // melompat saat window menyusut ke ukuran drag tanpa ruang ikon.
            double pillOffsetX = Math.Max(0, (ActualWidth - PillHost.ActualWidth) / 2);
            _dragGrab = new Point(
                cursor.X / scaleX - Left - pillOffsetX,
                cursor.Y / scaleY - Top - GetPillVerticalOffset());
            _dragMoved = false;
            _isDragging = true;
            _collapseTimer.Stop();
            PillHost.CaptureMouse();
        }

        private void OnPillMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;
            if (!GetCursorPos(out var cursor)) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            double scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

            double dx = (cursor.X - _dragStartCursor.X) / scaleX;
            double dy = (cursor.Y - _dragStartCursor.Y) / scaleY;

            if (!_dragMoved && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;

            if (!_dragMoved)
            {
                _dragMoved = true;
                UpdateMonitorFromCursor();
                BeginDragVisual(cursor, scaleX, scaleY);
            }
            else
            {
                // Ikuti monitor tempat kursor berada (multi-monitor)
                UpdateMonitorFromCursor();
            }

            // Bebas mengikuti kursor; snap ke anchor baru terjadi saat mouse dilepas
            Left = cursor.X / scaleX - _dragGrab.X;
            Top = cursor.Y / scaleY - _dragGrab.Y;
            PillHost.Cursor = Cursors.SizeAll;
        }

        // Saat mulai digeser: ciutkan notch ke ukuran collapsed dan bentuk kapsul rapi
        private void BeginDragVisual(NativePoint cursor, double scaleX, double scaleY)
        {
            if (_isExpanded)
            {
                _isExpanded = false;
                ApplyExpandedState(false);
            }

            _animationVersion++;
            PillHost.BeginAnimation(FrameworkElement.WidthProperty, null);
            PillHost.BeginAnimation(FrameworkElement.HeightProperty, null);

            var (cw, ch) = CollapsedSize;
            double grabX = cursor.X / scaleX - Left;
            double grabY = cursor.Y / scaleY - Top - GetPillVerticalOffset();

            PillHost.Width = cw;
            PillHost.Height = ch;
            Width = cw;
            Height = ch;
            UpdateFeatureBarVisibility();

            // Jaga posisi notch tetap di bawah kursor setelah ukuran berubah
            grabX = Math.Clamp(grabX, 0, cw);
            grabY = Math.Clamp(grabY, 0, ch);
            _dragGrab = new Point(grabX, grabY);
            Left = cursor.X / scaleX - grabX;
            Top = cursor.Y / scaleY - grabY;

            UpdateNotchShape(new Size(cw, ch));
        }

        private void OnPillMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;

            _isDragging = false;
            PillHost.ReleaseMouseCapture();
            PillHost.Cursor = Cursors.Hand;

            if (!_dragMoved)
            {
                // Klik dua kali cepat: toggle mode bukit (dome hitam, tanpa isi)
                var now = DateTime.Now;
                bool isDouble = (now - _lastDomeClick).TotalMilliseconds <= 400;
                _lastDomeClick = now;
                if (isDouble)
                {
                    if (!_domeMode)
                    {
                        // Masuk mode bukit: sembunyikan isi, lalu mengecil halus ke dome.
                        _domeMode = true;
                        UpdateDomeVisibility();
                        if (_isExpanded)
                            CollapseNotch();
                        else
                            UpdateNotchShape(PillHost.RenderSize);
                    }
                    else
                    {
                        // Keluar mode bukit: tampilkan notch normal lagi.
                        _domeMode = false;
                        UpdateDomeVisibility();
                        if (PillHost.IsMouseOver)
                            ExpandNotch();
                        else
                            UpdateNotchShape(PillHost.RenderSize);
                    }
                    return;
                }
                UpdateNotchShape(PillHost.RenderSize);
                return;
            }

            // Simpan pusat pill saat ini sebelum ukuran diubah
            double centerX = Left + Width / 2;
            double centerY = Top + Height / 2;

            // Setelah drag, kembali ke ukuran collapsed lalu snap ke titik terdekat
            if (_isExpanded)
            {
                _isExpanded = false;
                ApplyExpandedState(false);
            }

            _animationVersion++;
            PillHost.BeginAnimation(FrameworkElement.WidthProperty, null);
            PillHost.BeginAnimation(FrameworkElement.HeightProperty, null);

            var (cw, ch) = CollapsedSize;
            PillHost.Width = cw;
            PillHost.Height = ch;
            Width = cw;
            Height = ch;

            SnapToNearestAnchor(centerX, centerY);
        }

        private void SnapToNearestAnchor(double centerX, double centerY)
        {
            var work = _monitor;
            if (work.Width < 1 || work.Height < 1)
            {
                work = new Rect(0, 0,
                    SystemParameters.PrimaryScreenWidth,
                    SystemParameters.PrimaryScreenHeight);
            }

            // Empat anchor: tengah atas, tengah bawah, kiri tengah, kanan tengah
            var candidates = new (NotchAnchor Anchor, double Left, double Top, double Width, double Height)[]
            {
                (NotchAnchor.TopCenter, work.Left + (work.Width - CollapsedWidth) / 2, work.Top,
                    CollapsedWidth, CollapsedHeight),
                (NotchAnchor.BottomCenter, work.Left + (work.Width - CollapsedWidth) / 2,
                    work.Bottom - CollapsedHeight, CollapsedWidth, CollapsedHeight),
                (NotchAnchor.LeftCenter, work.Left, work.Top + (work.Height - SideCollapsedHeight) / 2,
                    SideCollapsedWidth, SideCollapsedHeight),
                (NotchAnchor.RightCenter, work.Right - SideCollapsedWidth,
                    work.Top + (work.Height - SideCollapsedHeight) / 2,
                    SideCollapsedWidth, SideCollapsedHeight)
            };

            // Bandingkan pusat pill dengan pusat tiap titik snap
            var best = candidates
                .OrderBy(c =>
                {
                    double cx = c.Left + c.Width / 2;
                    double cy = c.Top + c.Height / 2;
                    return (cx - centerX) * (cx - centerX) + (cy - centerY) * (cy - centerY);
                })
                .First();

            SetAnchor(best.Anchor);
            SaveAnchor();
        }

        private bool IsInteractiveElement(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is ButtonBase || source is Slider || source is Thumb || source is TextBox)
                {
                    return true;
                }

                if (_calendarDayCircles != null)
                {
                    for (int i = 0; i < _calendarDayCircles.Length; i++)
                    {
                        if (source == _calendarDayCircles[i]) return true;
                    }
                }

                if (source == CalendarMonthText) return true;

                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private void OnPillDragEnter(object sender, DragEventArgs e)
        {
            if (!HasFileDrop(e.Data))
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            _isFileDragOver = true;
            _collapseTimer.Stop();
            UpdateFileDropDestination(e);
            UpdateDropIndicatorLayout();
            DropIndicator.Visibility = Visibility.Visible;
            ExpandNotch();
            UpdateAccent();

            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void OnPillDragOver(object sender, DragEventArgs e)
        {
            if (!HasFileDrop(e.Data))
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            if (!_isFileDragOver)
            {
                _isFileDragOver = true;
                _collapseTimer.Stop();
                DropIndicator.Visibility = Visibility.Visible;
                UpdateAccent();
            }

            ExpandNotch();
            UpdateFileDropDestination(e);
            UpdateDropIndicatorLayout();

            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void OnPillDragLeave(object sender, DragEventArgs e)
        {
            if (!_isFileDragOver) return;

            _isFileDragOver = false;
            _fileDropDestination = FileDropDestination.Path;
            ApplyFileDropDestinationVisual();
            DropIndicator.Visibility = Visibility.Collapsed;
            UpdateAccent();

            if (IsAuxOpen || _isDragging) return;
            if (PillHost.IsMouseOver || FeatureMenuBar.IsMouseOver) return;

            _collapseTimer.Stop();
            _collapseTimer.Start();
        }

        private async void OnPillDrop(object sender, DragEventArgs e)
        {
            if (!TryGetDroppedPaths(e.Data, out var paths))
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            bool toConversion = IsDropToConversion(e);

            _isFileDragOver = false;
            _fileDropDestination = toConversion
                ? FileDropDestination.Convert
                : FileDropDestination.Path;
            ApplyFileDropDestinationVisual();
            DropIndicator.Visibility = Visibility.Collapsed;
            UpdateAccent();
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;

            if (toConversion)
            {
                StartQuickConversion(paths);
                return;
            }

            // "Share to Mobile" drop zone: auto-send files directly to connected phone
            var server = MobileServerService.Instance;
            if (server == null || !server.HasConnectedClients)
            {
                ShowStatusPopup(Localization.Get("NoMobileConnected"), isError: true);
                return;
            }

            string[] filePaths = paths
                .Where(File.Exists)
                .ToArray();

            if (filePaths.Length == 0)
            {
                ShowStatusPopup(Localization.Get("SendFolderNotSupported"), isError: true);
                return;
            }

            if (filePaths.Length == 1)
            {
                string name = System.IO.Path.GetFileName(filePaths[0]);
                ShowStatusPopup(Localization.Format("SendingToMobile", name), isError: false);
                bool ok = await server.SendShelfFileAsync(filePaths[0]);
                ShowStatusPopup(ok
                    ? Localization.Format("SentToMobile", name)
                    : Localization.Get("SendFailed"), isError: !ok);
            }
            else
            {
                ShowStatusPopup(Localization.Format("SendingToMobile", filePaths.Length + " files"), isError: false);
                int sent = 0;
                foreach (string path in filePaths)
                {
                    if (await server.SendShelfFileAsync(path)) sent++;
                }
                ShowStatusPopup(Localization.Format("SentManyToMobile", sent), isError: false);
            }
        }

        private bool IsDropToConversion(DragEventArgs e)
        {
            double width = PillHost.ActualWidth > 0
                ? PillHost.ActualWidth
                : PillHost.Width;

            return width > 0 && e.GetPosition(PillHost).X < width / 2;
        }

        private async void StartQuickConversion(IEnumerable<string> paths)
        {
            if (_isConverting)
            {
                ShowStatusPopup(Localization.Get("ConvertBusy"), isError: true);
                return;
            }

            string[] supportedPaths = paths
                .Where(path => File.Exists(path) &&
                    ConversionFormats.TryGetTarget(path, out _))
                .ToArray();

            if (supportedPaths.Length == 0)
            {
                ShowStatusPopup(
                    Localization.Get("ConvertNoSupportedFiles"),
                    isError: true);
                return;
            }

            _isConverting = true;
            var cts = new CancellationTokenSource();
            _quickConversionCts = cts;

            try
            {
                var progress = new Progress<QuickConversionProgress>(item =>
                {
                    ShowStatusPopup(
                        Localization.Format(
                            "ConvertDownloadProgress",
                            item.FileName,
                            item.Index,
                            item.Total),
                        isError: false);
                });

                var outcomes = await _quickConversion.ConvertAsync(
                    supportedPaths,
                    progress,
                    cts.Token);

                int succeeded = outcomes.Count(item => item.Success);
                int failed = outcomes.Count - succeeded;

                if (succeeded == 0)
                {
                    string? error = outcomes
                        .FirstOrDefault(item => !item.Success)?.Error;

                    ShowStatusPopup(
                        string.IsNullOrWhiteSpace(error)
                            ? Localization.Get("ConvertFailed")
                            : error!,
                        isError: true);
                }
                else if (failed == 0)
                {
                    ShowStatusPopup(
                        Localization.Format("ConvertSavedToDownloads", succeeded),
                        isError: false);
                }
                else
                {
                    ShowStatusPopup(
                        Localization.Format(
                            "ConvertSavedPartial",
                            succeeded,
                            failed),
                        isError: true);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                CrashLogger.Log("MainWindow.QuickConversion", exception);
                ShowStatusPopup(Localization.Get("ConvertFailed"), isError: true);
            }
            finally
            {
                if (ReferenceEquals(_quickConversionCts, cts))
                {
                    _quickConversionCts = null;
                }

                cts.Dispose();
                _isConverting = false;
            }
        }

        private void ShowStatusPopup(string text, bool isError)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            _statusTimer.Stop();
            StatusPopupText.Text = text;
            StatusPopupCard.BorderBrush = isError ? DangerBrush : AccentBrush;
            StatusPopupText.Foreground = isError
                ? DangerBrush
                : FrozenBrush(0xF5, 0xF5, 0xF7);
            UpdatePopupPlacement();
            StatusPopup.IsOpen = true;
            _statusTimer.Start();
        }

        private void UpdateFileDropDestination(DragEventArgs e)
        {
            _fileDropDestination = IsDropToConversion(e)
                ? FileDropDestination.Convert
                : FileDropDestination.Path;
            ApplyFileDropDestinationVisual();
        }

        private void ApplyFileDropDestinationVisual()
        {
            DropPathZone.Background = _fileDropDestination == FileDropDestination.Path
                ? DropZoneActiveBrush
                : Brushes.Transparent;
            DropConvertZone.Background = _fileDropDestination == FileDropDestination.Convert
                ? DropZoneActiveBrush
                : Brushes.Transparent;
        }

        private void UpdateDropIndicatorLayout()
        {
            bool showLabels = PillHost.ActualWidth >= 160;

            DropPathLabel.Visibility = showLabels
                ? Visibility.Visible
                : Visibility.Collapsed;
            DropConvertLabel.Visibility = showLabels
                ? Visibility.Visible
                : Visibility.Collapsed;

            DropPathIcon.Margin = showLabels
                ? new Thickness(0, 0, 6, 0)
                : new Thickness(0);
            DropConvertIcon.Margin = showLabels
                ? new Thickness(0, 0, 6, 0)
                : new Thickness(0);
        }

        private static bool HasFileDrop(IDataObject data)
        {
            return data.GetDataPresent(DataFormats.FileDrop);
        }

        private static bool TryGetDroppedPaths(IDataObject data, out string[] paths)
        {
            paths = Array.Empty<string>();

            if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
            if (data.GetData(DataFormats.FileDrop) is not string[] dropped ||
                dropped.Length == 0)
            {
                return false;
            }

            paths = dropped;
            return true;
        }

        private void ShowFileShelf()
        {
            CloseOtherAuxWindows(_dropShelf);

            if (_dropShelf is { IsVisible: true })
            {
                _dropShelf.Activate();
                return;
            }

            CreateDropShelf(_droppedPaths);
        }

        private void ShowDropShelf(string[] paths)
        {
            string[] validPaths = paths
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .ToArray();

            if (validPaths.Length == 0) return;

            foreach (string path in validPaths)
            {
                if (_droppedPaths.Count >= DropShelfWindow.MaxItems) break;
                if (_droppedPaths.Any(existing =>
                        string.Equals(existing, path,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _droppedPaths.Add(path);
            }

            if (_dropShelf is { IsVisible: true })
            {
                CloseOtherAuxWindows(_dropShelf);
                _dropShelf.AddPaths(validPaths);
                SyncDroppedPathsFromShelf(_dropShelf);
                PositionAuxWindow(_dropShelf);
                _dropShelf.Activate();
                UpdateFeatureButtonStates();
                return;
            }

            CreateDropShelf(_droppedPaths);
        }

        private void CreateDropShelf(IEnumerable<string> paths)
        {
            CloseOtherAuxWindows();

            var shelf = new DropShelfWindow(paths);
            PositionAuxWindow(shelf);

            shelf.PositionSaved += (s, e) =>
            {
                _settings.DropShelfLeft = shelf.Left;
                _settings.DropShelfTop = shelf.Top;
                SettingsStore.Save(_settings);
            };

            shelf.ItemsChanged += (s, e) => SyncDroppedPathsFromShelf(shelf);
            shelf.Closed += (s, e) =>
            {
                _dropShelf = null;
                UpdateFeatureButtonStates();

                if (PillHost.IsMouseOver || FeatureMenuBar.IsMouseOver || IsAuxOpen) return;

                _collapseTimer.Stop();
                CollapseNotch();
            };

            _dropShelf = shelf;
            shelf.Show();
            shelf.Activate();
            SyncDroppedPathsFromShelf(shelf);
            UpdateFeatureButtonStates();
        }

        private void SyncDroppedPathsFromShelf(DropShelfWindow shelf)
        {
            _droppedPaths.Clear();
            _droppedPaths.AddRange(shelf.Paths);
            UpdateFileMenuLabel();
        }

        private void UpdateFileMenuLabel()
        {
            FileButton.ToolTip = _droppedPaths.Count > 0
                ? Localization.Format("FileCountLabel", _droppedPaths.Count)
                : Localization.Get("FileTitle");
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            UpdateFileMenuLabel();
            UpdateClock();
        }

        private void SaveAnchor()
        {
            _settings.Anchor = _anchor.ToString();
            SettingsStore.Save(_settings);
        }

        // ===================== State =====================

        // Menampilkan/menyembunyikan elemen sesuai state (expanded/collapsed)
        private void ApplyExpandedState(bool expanded)
        {
            ProgressRow.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            ControlsRow.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            CollapsedPauseButton.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            SourceAppIcon.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            UpdateContentMargin();
            UpdateVisualState();
        }

        // Menentukan layout mana yang tampil: idle dashboard, media horizontal, atau mini vertikal di sisi
        private void UpdateVisualState()
        {
            UpdateFeatureBarVisibility();

            bool hasMedia = _media != null;
            bool sideCollapsed = IsSideAnchor && !_isExpanded;
            bool bottomAnchor = _anchor == NotchAnchor.BottomCenter;

            SideCollapsedRoot.Visibility = hasMedia && sideCollapsed ? Visibility.Visible : Visibility.Collapsed;
            SideIdleRoot.Visibility = !hasMedia && sideCollapsed ? Visibility.Visible : Visibility.Collapsed;
            MediaRoot.Visibility = hasMedia && !_isExpanded && !sideCollapsed
                ? Visibility.Visible
                : Visibility.Collapsed;
            IdleRoot.Visibility = _isExpanded || (!hasMedia && !sideCollapsed)
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Mode sisi yang mekar tetap horizontal, tapi konten ditengahkan vertikal
            MediaRoot.VerticalAlignment = IsSideAnchor
                ? VerticalAlignment.Center
                : bottomAnchor ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            MediaRoot.Margin = IsSideAnchor
                ? new Thickness(0)
                : bottomAnchor ? new Thickness(0, 0, 0, 7.5) : new Thickness(0, 7.5, 0, 0);

            VerticalAlignment pageAlignment = VerticalAlignment.Center;
            CalendarPage.VerticalAlignment = pageAlignment;
            MediaPage.VerticalAlignment = pageAlignment;
            MetricsPage.VerticalAlignment = pageAlignment;

            if (IdleRoot.Visibility == Visibility.Visible)
            {
                IdleCompactRoot.Visibility = _isExpanded ? Visibility.Collapsed : Visibility.Visible;
                IdleExpandedRoot.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
                // Sejajarkan area konten dengan tinggi capsule dalam (inset sleeve).
                double expandedInset = IsSideAnchor ? SleeveSpanInset : SleeveRim;
                IdleExpandedRoot.Margin = new Thickness(IdleExpandedHorizontalMargin, expandedInset,
                    IdleExpandedHorizontalMargin, expandedInset);
            }

            if (!sideCollapsed) return;

            SideAlbumArtBorder.Visibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;
            SidePauseButton.Visibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;
        }

        // Menjalankan jam dan tanggal real-time (untuk state tanpa media)
        private void StartClock()
        {
            UpdateClock();
            _timer.Tick += (s, e) => UpdateClock();
            _timer.Start();
        }

        private void UpdateClock()
        {
            var now = DateTime.Now;
            string time = now.ToString("HH:mm", CultureInfo.CurrentCulture);

            CompactClockText.Text = time;

            SideHourText.Text = now.ToString("HH", CultureInfo.CurrentCulture);
            SideMinuteText.Text = now.ToString("mm", CultureInfo.CurrentCulture);

            UpdateCalendar(now);
        }

        private void InitCalendarInteractions()
        {
            for (int i = 0; i < _calendarDayCircles.Length; i++)
            {
                int index = i;
                _calendarDayCircles[index].PreviewMouseLeftButtonDown += (s, e) =>
                {
                    OnCalendarDayClicked(index);
                    e.Handled = true;
                };
            }

            CalendarMonthText.Cursor = Cursors.Hand;
            CalendarMonthText.ToolTip = Localization.Get("CalendarShiftScrollHint");
            CalendarMonthText.MouseLeftButtonDown += (s, e) =>
            {
                // Reset tanggal ke hari ini
                if (_calendarOffsetDays != 0)
                {
                    _calendarOffsetDays = 0;
                    UpdateCalendar(DateTime.Now);
                    ShowStatusPopup(DateTime.Now.ToString("dd MMMM yyyy", CultureInfo.CurrentCulture), isError: false);
                    e.Handled = true;
                }
            };
        }

        private void OnCalendarDayClicked(int index)
        {
            if (index < 0 || index >= _calendarVisibleDates.Length) return;

            DateTime clickedDate = _calendarVisibleDates[index];
            _selectedCalendarDate = clickedDate;
            UpdateCalendar(DateTime.Now);

            ShowCalendarNoteWindow(clickedDate);
        }

        private void ShowCalendarNoteWindow(DateTime date)
        {
            if (_calendarNoteWindow is { IsVisible: true })
            {
                _calendarNoteWindow.Close();
                _calendarNoteWindow = null;
            }

            string dateKey = date.ToString("yyyy-MM-dd");
            _calendarNotes.TryGetValue(dateKey, out var currentNote);

            var noteWin = new CalendarNoteWindow(date, currentNote);
            PositionAuxWindow(noteWin);

            noteWin.Saved += (s, noteText) =>
            {
                if (string.IsNullOrEmpty(noteText))
                {
                    _calendarNotes.Remove(dateKey);
                }
                else
                {
                    _calendarNotes[dateKey] = noteText;
                }
                CalendarStore.Save(_calendarNotes);
                UpdateCalendar(DateTime.Now);
                if (!string.IsNullOrEmpty(noteText))
                {
                    ShowStatusPopup($"{date:dd MMM}: {noteText}", isError: false);
                }
            };

            noteWin.Deleted += (s, e) =>
            {
                _calendarNotes.Remove(dateKey);
                CalendarStore.Save(_calendarNotes);
                UpdateCalendar(DateTime.Now);
            };

            noteWin.Closed += (s, e) =>
            {
                _calendarNoteWindow = null;
                _selectedCalendarDate = null;
                UpdateCalendar(DateTime.Now);
                if (!PillHost.IsMouseOver && !FeatureMenuBar.IsMouseOver && !IsAuxOpen)
                {
                    _collapseTimer.Stop();
                    CollapseNotch();
                }
            };

            _calendarNoteWindow = noteWin;
            noteWin.Show();
            noteWin.Activate();
        }

        // Kalender ringkas: 7 hari berurutan dengan tanggal referensi di kolom tengah.
        private void UpdateCalendar(DateTime now)
        {
            CultureInfo culture = CultureInfo.CurrentCulture;
            DateTime reference = now.Date.AddDays(_calendarOffsetDays);
            DateTime start = reference.AddDays(-3);

            CalendarMonthText.Text = reference.ToString("MMMM yyyy", culture);
            CalendarClockText.Text = now.ToString("HH:mm", culture);

            for (int index = 0; index < _calendarDayTexts.Length; index++)
            {
                DateTime day = start.AddDays(index);
                _calendarVisibleDates[index] = day;

                bool isToday = day.Date == now.Date;
                bool isSelected = _selectedCalendarDate.HasValue && day.Date == _selectedCalendarDate.Value.Date;
                bool isCurrentMonth = day.Month == reference.Month && day.Year == reference.Year;

                string weekday = day.ToString("dddd", culture);
                _calendarWeekdayTexts[index].Text = weekday.Length > 0
                    ? culture.TextInfo.ToUpper(weekday[0]).ToString()
                    : string.Empty;

                double opacity = Math.Max(0.25, 1 - Math.Abs(index - 3) * 0.25);
                _calendarWeekdayTexts[index].Opacity = opacity;
                _calendarDayCircles[index].Opacity = opacity;

                _calendarDayTexts[index].Text = day.Day.ToString(culture);

                // Cek apakah ada catatan untuk tanggal ini
                string dateKey = day.ToString("yyyy-MM-dd");
                bool hasNote = _calendarNotes.TryGetValue(dateKey, out var note) && !string.IsNullOrEmpty(note);

                _calendarDayCircles[index].BorderBrush = Brushes.Transparent;
                if (isToday)
                {
                    // Jika hari ini memiliki catatan: lingkaran berubah menjadi biru solid dengan teks putih
                    _calendarDayCircles[index].Background = hasNote ? CalendarSelectedBlueBrush : Brushes.White;
                    _calendarDayTexts[index].Foreground = hasNote ? Brushes.White : CalendarTodayTextBrush;
                    _calendarDayTexts[index].FontWeight = FontWeights.Bold;
                    // Sembunyikan titik bawah untuk hari ini karena lingkarannya sudah berwarna biru
                    _calendarNoteDots[index].Visibility = Visibility.Collapsed;
                }
                else
                {
                    _calendarDayCircles[index].Background = Brushes.Transparent;
                    _calendarDayTexts[index].Foreground = isCurrentMonth ? CalendarDayBrush : CalendarMutedDayBrush;
                    _calendarDayTexts[index].FontWeight = FontWeights.SemiBold;
                    _calendarNoteDots[index].Visibility = hasNote ? Visibility.Visible : Visibility.Collapsed;
                }

                if (hasNote)
                {
                    _calendarDayCircles[index].ToolTip = $"{day:d MMMM yyyy}\n{note}";
                }
                else
                {
                    _calendarDayCircles[index].ToolTip = day.ToString("d MMMM yyyy", culture);
                }
            }
        }

        private void OnPillMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_isExpanded || e.Delta == 0) return;

            // Opsi 4: Shift + Scroll Wheel untuk geser tanggal kalender
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _idlePage == IdleCalendarPage)
            {
                e.Handled = true;
                int daysShift = e.Delta < 0 ? 7 : -7; // Geser seminggu ke depan / belakang
                _calendarOffsetDays += daysShift;
                UpdateCalendar(DateTime.Now);

                DateTime currentRef = DateTime.Now.Date.AddDays(_calendarOffsetDays);
                ShowStatusPopup(currentRef.ToString("MMMM yyyy", CultureInfo.CurrentCulture), isError: false);
                return;
            }

            int targetPage = e.Delta < 0
                ? Math.Min(IdleMetricsPage, _idlePage + 1)
                : Math.Max(IdleCalendarPage, _idlePage - 1);

            if (targetPage == _idlePage) return;

            e.Handled = true;
            AnimateIdlePage(targetPage);
        }

        private void AnimateIdlePage(int targetPage)
        {
            if (targetPage == _idlePage) return;

            double offset = GetIdlePageOffset();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            for (int index = 0; index < _idlePages.Length; index++)
            {
                double startX = _idlePageTransforms[index].X;
                double startOpacity = _idlePages[index].Opacity;
                double endX = (index - targetPage) * offset;
                double endOpacity = index == targetPage ? 1 : 0;

                // Nilai dasar diset ke target agar animasi bisa dipotong kapan saja
                // tanpa menunggu callback Completed.
                _idlePageTransforms[index].X = endX;
                _idlePages[index].Opacity = endOpacity;

                var slide = new DoubleAnimation(startX, endX, IdlePageDuration)
                {
                    EasingFunction = ease,
                    FillBehavior = FillBehavior.Stop
                };
                var fade = new DoubleAnimation(startOpacity, endOpacity, IdlePageDuration)
                {
                    FillBehavior = FillBehavior.Stop
                };

                _idlePageTransforms[index].BeginAnimation(
                    TranslateTransform.XProperty, slide);
                _idlePages[index].BeginAnimation(UIElement.OpacityProperty, fade);
            }

            _idlePage = targetPage;
        }

        private void ResetIdlePage(int page = IdleCalendarPage)
        {
            _idlePage = Math.Clamp(page, IdleCalendarPage, _idlePages.Length - 1);
            ClearIdlePageAnimations();
            UpdateIdlePageRestPosition();
        }

        private void UpdateIdlePageRestPosition()
        {
            double offset = GetIdlePageOffset();

            for (int index = 0; index < _idlePages.Length; index++)
            {
                _idlePageTransforms[index].X = (index - _idlePage) * offset;
                _idlePages[index].Opacity = index == _idlePage ? 1 : 0;
            }
        }

        private double GetIdlePageOffset()
        {
            double width = IdlePageViewport.ActualWidth;
            if (width > 0) return width;

            return Math.Max(1, ExpandedWidth - IdleExpandedHorizontalMargin * 2 - 18);
        }

        private void ClearIdlePageAnimations()
        {
            for (int index = 0; index < _idlePages.Length; index++)
            {
                _idlePageTransforms[index].BeginAnimation(
                    TranslateTransform.XProperty, null);
                _idlePages[index].BeginAnimation(UIElement.OpacityProperty, null);
            }
        }

        private static SolidColorBrush FrozenBrush(byte r, byte g, byte b)
        {
            return FrozenBrush(Color.FromRgb(r, g, b));
        }

        private static SolidColorBrush FrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static void SetMetric(ProgressBar bar, TextBlock text, double? value)
        {
            if (value is not double v)
            {
                bar.Value = 0;
                text.Text = "--%";
                return;
            }

            v = Math.Clamp(v, 0, 100);
            bar.Value = v;
            text.Text = $"{Math.Round(v):0}%";
            bar.Foreground = v >= 90 ? DangerBrush : v >= 75 ? WarningBrush : AccentBrush;
        }

        private void ApplyBattery(
            int? percent,
            bool isCharging,
            FrameworkElement panel,
            Border fill,
            TextBlock text,
            double innerWidth)
        {
            if (percent is not int p)
            {
                panel.Visibility = Visibility.Collapsed;
                return;
            }

            p = Math.Clamp(p, 0, 100);
            panel.Visibility = Visibility.Visible;
            fill.Width = innerWidth * p / 100.0;
            fill.Background = isCharging
                ? AccentBrush
                : p <= 20 ? DangerBrush : p <= 40 ? WarningBrush : BatteryBrush;
            text.Text = isCharging ? $"{p}% AC" : $"{p}%";
        }

        // Saat kursor mouse masuk ke area Notch (Mekar)
        private void ExpandNotch()
        {
            if (_isExpanded || _domeMode) return;
            _isExpanded = true;
            _animationVersion++;

            ApplyExpandedState(true);
            ResetIdlePage(_media != null ? IdleMediaPage : IdleCalendarPage);

            // Window langsung snap ke ukuran akhir (transparan, tak terlihat),
            // lalu pill-lah yang dianimasikan -> tanpa resize OS per frame
            var (w, h) = ExpandedSize;
            SetWindowSize(w, h);
            AnimatePillTo(w, h, widthFirst: true);
            ShowFeatureBar();
        }

        // Saat kursor mouse keluar dari area Notch (Mengecil)
        private void CollapseNotch()
        {
            if (!_isExpanded) return;
            _isExpanded = false;
            _animationVersion++;

            ApplyExpandedState(false);
            HideFeatureBar();

            // Window baru menyusul setelah animasi pill selesai (lihat handler SizeChanged)
            var (w, h) = CollapsedSize;
            AnimatePillTo(w, h, widthFirst: false);
        }

        // Animasi dua tahap agar capsule tidak bergetar:
        // buka = lebar dulu lalu tinggi, tutup = tinggi dulu lalu lebar.
        private void AnimatePillTo(double targetWidth, double targetHeight, bool widthFirst)
        {
            int version = _animationVersion;

            double startWidth = PillHost.Width;
            double startHeight = PillHost.Height;
            if (double.IsNaN(startWidth) || startWidth <= 0) startWidth = PillHost.ActualWidth;
            if (double.IsNaN(startHeight) || startHeight <= 0) startHeight = PillHost.ActualHeight;

            // Bekukan posisi saat ini supaya hover cepat bolak-balik tetap mulus
            PillHost.BeginAnimation(FrameworkElement.WidthProperty, null);
            PillHost.BeginAnimation(FrameworkElement.HeightProperty, null);
            PillHost.Width = startWidth;
            PillHost.Height = startHeight;

            // Tutup: tinggi menyusut lebih dulu, lebar menyusul dengan sedikit
            // overlap. Keduanya dijadwalkan sekaligus agar tidak ada jeda frame
            // di antara tahap (penyebab animasi terasa patah).
            if (!widthFirst)
            {
                var heightAnim = new DoubleAnimation(
                    startHeight, targetHeight, new Duration(TimeSpan.FromSeconds(0.2)))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                var widthAnim = new DoubleAnimation(
                    startWidth, targetWidth, new Duration(TimeSpan.FromSeconds(0.2)))
                {
                    BeginTime = TimeSpan.FromSeconds(0.11),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };

                PillHost.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
                PillHost.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);
                return;
            }

            var firstProperty = widthFirst
                ? FrameworkElement.WidthProperty
                : FrameworkElement.HeightProperty;
            double firstStart = widthFirst ? startWidth : startHeight;
            double firstTarget = widthFirst ? targetWidth : targetHeight;

            if (Math.Abs(firstTarget - firstStart) < 0.5)
            {
                AnimatePillSecondPhase(targetWidth, targetHeight, widthFirst, version);
                return;
            }

            var firstAnim = new DoubleAnimation(firstStart, firstTarget, PillPhaseDuration)
            {
                EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
            };
            firstAnim.Completed += (s, e) =>
                AnimatePillSecondPhase(targetWidth, targetHeight, widthFirst, version);

            PillHost.BeginAnimation(firstProperty, firstAnim);
        }

        private void AnimatePillSecondPhase(
            double targetWidth, double targetHeight, bool widthFirst, int version)
        {
            if (version != _animationVersion) return;

            var property = widthFirst
                ? FrameworkElement.HeightProperty
                : FrameworkElement.WidthProperty;
            double target = widthFirst ? targetHeight : targetWidth;
            double current = widthFirst ? PillHost.Height : PillHost.Width;

            if (Math.Abs(target - current) < 0.5) return;

            var anim = new DoubleAnimation(current, target, PillPhaseDuration)
            {
                EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
            };

            PillHost.BeginAnimation(property, anim);
        }

        private void ToggleNotchiVisibility()
        {
            if (_isHidden)
            {
                // Tampilkan kembali dengan fade-in
                _isHidden = false;
                Visibility = Visibility.Visible;
                var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                BeginAnimation(OpacityProperty, fadeIn);
            }
            else
            {
                // Sembunyikan dengan fade-out
                var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(150)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                fadeOut.Completed += (_, _) =>
                {
                    Visibility = Visibility.Hidden;
                    _isHidden = true;
                };
                BeginAnimation(OpacityProperty, fadeOut);
            }
        }

        private void InitMobileServer()
        {
            _mobileServer = new MobileServerService(_smtc, _systemStatus);

            _mobileServer.OnFileReceived += (path) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ShowDropShelf(new[] { path });
                    ShowStatusPopup($"File diterima: {Path.GetFileName(path)}", isError: false);
                });
            };

            _mobileServer.OnTextReceived += (text) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        string downloadsFolder = Path.Combine(userProfile, "Downloads");
                        Directory.CreateDirectory(downloadsFolder);
                        string noteFile = Path.Combine(downloadsFolder, $"Notchi_Mobile_Note_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                        File.WriteAllText(noteFile, text);
                        ShowDropShelf(new[] { noteFile });
                        ShowStatusPopup("Catatan teks diterima dari HP", isError: false);
                    }
                    catch { }
                });
            };

            PairingService.OnPairingRequested += OnPairingRequestedFromDevice;

            DenyPairButton.Click += (s, e) =>
            {
                PairingPromptPopup.IsOpen = false;
                if (_currentPairingRequest != null)
                {
                    _currentPairingRequest.ApprovalSource.TrySetResult(false);
                    _currentPairingRequest = null;
                }
            };

            AllowPairButton.Click += (s, e) =>
            {
                PairingPromptPopup.IsOpen = false;
                if (_currentPairingRequest != null)
                {
                    _currentPairingRequest.ApprovalSource.TrySetResult(true);
                    _currentPairingRequest = null;
                }
            };

            ApplyMobileServerSettings();
        }

        private async void ApplyMobileServerSettings()
        {
            if (_mobileServer == null) return;

            try
            {
                if (_settings.IsMobileServerEnabled)
                {
                    if (!_mobileServer.IsRunning)
                    {
                        await _mobileServer.StartAsync(_settings.MobileServerPort);
                    }
                }
                else
                {
                    if (_mobileServer.IsRunning)
                    {
                        await _mobileServer.StopAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("MobileServer.ApplySettings", ex);
                ShowStatusPopup("Gagal memulai server Notchi Mobile", isError: true);
            }
        }

        private Task OnPairingRequestedFromDevice(PairingRequest req)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource();

            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    _currentPairingRequest = req;
                    PairingPromptDeviceName.Text = $"Izinkan perangkat \"{req.DeviceName}\" terhubung?";
                    UpdatePopupPlacement();
                    PairingPromptPopup.IsOpen = true;

                    await req.ApprovalSource.Task;
                }
                finally
                {
                    PairingPromptPopup.IsOpen = false;
                    tcs.TrySetResult();
                }
            });

            return tcs.Task;
        }

        protected override void OnClosed(EventArgs e)
        {
            _isMainClosing = true;
            PairingService.OnPairingRequested -= OnPairingRequestedFromDevice;
            _mobileServer?.Dispose();
            _quickConversionCts?.Cancel();
            _statusTimer.Stop();
            StatusPopup.IsOpen = false;
            _focusWindow?.Close();
            _focusWindow = null;
            _hotkey.Dispose();
            Localization.LanguageChanged -= OnLanguageChanged;
            _mediaTimer.Stop();
            _timer.Stop();
            _systemStatus.StatusChanged -= OnSystemStatusChanged;
            _systemStatus.Dispose();
            _smtc.Dispose();
            base.OnClosed(e);
        }
    }
}
