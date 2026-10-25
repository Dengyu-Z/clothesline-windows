using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Security;
using Microsoft.VisualBasic.FileIO;
using WpfButton = System.Windows.Controls.Button;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDataObject = System.Windows.DataObject;
using WpfDragDrop = System.Windows.DragDrop;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfImage = System.Windows.Controls.Image;
using MediaColor = System.Windows.Media.Color;
using MediaBrushes = System.Windows.Media.Brushes;
using InputCursors = System.Windows.Input.Cursors;

namespace SnapshotRail;

public sealed class CaptureItem(string path, BitmapSource thumbnail, string? imageFingerprint = null)
{
    public string Path { get; } = path;
    public string FileName => System.IO.Path.GetFileName(Path);
    public BitmapSource Thumbnail { get; } = thumbnail;
    public CaptureVersion? Version { get; } = CaptureVersion.Read(path);
    public string? ContentFingerprint { get; } = imageFingerprint;
    public bool IsVideo => MainWindow.VideoExtensions.Contains(System.IO.Path.GetExtension(Path));
}

public partial class MainWindow : Window
{
    internal static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mov", ".mp4", ".m4v", ".avi", ".wmv" };
    private const string PreferencesKey = @"Software\Clothesline";
    private const int HotkeyId = 0xC10;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint WmHotkey = 0x0312;
    private const uint VkT = 0x54;
    private const int DefaultMaximumItems = 12;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".mov", ".mp4", ".m4v", ".avi", ".wmv" };
    private readonly ObservableCollection<CaptureItem> _items = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CaptureItem, FrameworkElement> _cardViews = new();
    private readonly Dictionary<CaptureItem, double> _cardAngles = new();
    private readonly HashSet<CaptureItem> _flyingItems = new();
    private readonly HashSet<CaptureFlightWindow> _flights = new();
    private readonly List<string> _extraFolders = new();
    private readonly DismissedCaptureStore _dismissedCaptures = new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clothesline", "dismissed-captures.json"));
    private readonly RailSessionStore _railSession = new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clothesline", "rail-session.json"));
    private DateTime _scanStartedUtc;
    private bool _sessionReady;
    private System.Windows.Point _dragStart;
    private CaptureItem? _pressedItem;
    private bool _dragStarted;
    private readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _pointerTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _longPressTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer _swayTimer = new() { Interval = TimeSpan.FromMilliseconds(2800) };
    private readonly Random _random = new();
    private HwndSource? _source;
    private uint _lastClipboardSequence;
    private uint _ignoreClipboardSequence;
    private bool _autoHideEnabled = true;
    private DateTime _shownAt;
    private bool _longPressTriggered;
    private bool _loadingInitialItems;
    private double _cardScale = ReadDoublePreference("CardScale", 1.0);
    private int _maximumItems = ReadIntPreference("MaximumItems", DefaultMaximumItems);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private bool _clipboardMonitoring = ReadPreference("ClipboardMonitoring", defaultValue: true);
    public bool ClipboardMonitoring
    {
        get => _clipboardMonitoring;
        set
        {
            _clipboardMonitoring = value;
            WritePreference("ClipboardMonitoring", value);
        }
    }
    public bool AutoHideEnabled
    {
        get => _autoHideEnabled;
        set { _autoHideEnabled = value; if (!value) ShowRail(); }
    }

    public double CardScale
    {
        get => _cardScale;
        set
        {
            _cardScale = Math.Clamp(value, 0.75, 1.35);
            WriteDoublePreference("CardScale", _cardScale);
            RebuildCards();
        }
    }

    public int MaximumItems
    {
        get => _maximumItems;
        set
        {
            _maximumItems = Math.Clamp(value, 4, 30);
            WriteIntPreference("MaximumItems", _maximumItems);
            TrimItems();
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        _clipboardTimer.Tick += ClipboardTimer_Tick;
        _pointerTimer.Tick += PointerTimer_Tick;
        _longPressTimer.Tick += LongPressTimer_Tick;
        _swayTimer.Tick += SwayTimer_Tick;
        SizeChanged += (_, _) => LayoutCards();
        Deactivated += (_, _) =>
        {
            // Windows has no macOS menu bar. Losing focus is the closest
            // equivalent to clicking elsewhere in that bar: tuck the rail
            // away while preserving the Ctrl+Alt+T toggle.
            if (_autoHideEnabled && !_dragStarted && _flights.Count == 0
                && (DateTime.UtcNow - _shownAt).TotalSeconds >= 2) HideRail();
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var area = CurrentWorkArea();
        Width = Math.Min(1600, area.Width);
        Height = Math.Min(310, area.Height);
        Left = Math.Max(area.Left, area.Left + (area.Width - Width) / 2);
        Top = area.Top + 8;
        _shownAt = DateTime.UtcNow;

        var inboxFolder = InboxFolder();
        try { Directory.CreateDirectory(inboxFolder); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        _extraFolders.AddRange(ReadFolderPreferences());
        var folders = new[]
        {
            inboxFolder,
            ScreenshotFolder(),
            VideosFolder(),
            VideoCapturesFolder(),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        }.Concat(_extraFolders).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        _scanStartedUtc = DateTime.UtcNow;
        var recent = RailSessionStore.SelectStartupPaths(_railSession.Load(),
            folders.SelectMany(SafeEnumerateImages), MaximumItems, _dismissedCaptures.Contains);
        _loadingInitialItems = true;
        foreach (var path in recent) AddImage(path);
        _loadingInitialItems = false;
        _sessionReady = true;
        SaveRailSession();
        foreach (var folder in folders) Watch(folder);
        _lastClipboardSequence = GetClipboardSequenceNumber();
        _clipboardTimer.Start();
        _pointerTimer.Start();
        _swayTimer.Start();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessageFilter);
        RegisterHotKey(handle, HotkeyId, ModControl | ModAlt, VkT);
    }

    private IntPtr WindowMessageFilter(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            ToggleRail();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void ToggleRail()
    {
        if (Visibility == Visibility.Visible) HideRail();
        else ShowRail();
    }

    private void ShowRail(bool activate = true)
    {
        var area = CurrentWorkArea();
        Width = Math.Min(1600, area.Width);
        Height = Math.Min(310, area.Height);
        Left = Math.Max(area.Left, area.Left + (area.Width - Width) / 2);
        Top = area.Top + 8;
        Show();
        if (activate) Activate();
        _shownAt = DateTime.UtcNow;
    }

    private void HideRail() => Hide();

    private static System.Windows.Rect CurrentWorkArea()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        return new System.Windows.Rect(screen.WorkingArea.X, screen.WorkingArea.Y,
            screen.WorkingArea.Width, screen.WorkingArea.Height);
    }

    private void PointerTimer_Tick(object? sender, EventArgs e)
    {
        var point = System.Windows.Forms.Cursor.Position;
        var area = CurrentWorkArea();
        var nearTop = point.Y <= area.Top + 12 && point.X >= area.Left && point.X <= area.Right;
        if (Visibility != Visibility.Visible)
        {
            if (nearTop) ShowRail();
            return;
        }
        if (!_autoHideEnabled || _flights.Count > 0 || (DateTime.UtcNow - _shownAt).TotalSeconds < 2) return;
        var local = PointFromScreen(new System.Windows.Point(point.X, point.Y));
        var insideCard = _cardViews.Values.Any(card =>
        {
            var left = Canvas.GetLeft(card);
            var top = Canvas.GetTop(card);
            return local.X >= left && local.X <= left + card.Width && local.Y >= top && local.Y <= top + card.Height;
        });
        var insideHeader = local.Y >= 0 && local.Y <= 38;
        var inside = insideCard || insideHeader;
        if (!nearTop && !inside) HideRail();
    }

    private void LongPressTimer_Tick(object? sender, EventArgs e)
    {
        _longPressTimer.Stop();
        if (_pressedItem is null) return;
        _longPressTriggered = true;
        try
        {
            if (_pressedItem.IsVideo)
                Process.Start(new ProcessStartInfo(_pressedItem.Path) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo
                {
                    FileName = "mspaint.exe",
                    Arguments = $"\"{_pressedItem.Path}\"",
                    UseShellExecute = true
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            try { Process.Start(new ProcessStartInfo(_pressedItem.Path) { UseShellExecute = true }); }
            catch (Exception fallback) when (fallback is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
    }

    private void SwayTimer_Tick(object? sender, EventArgs e)
    {
        foreach (var (item, card) in _cardViews)
        {
            if (_flyingItems.Contains(item)) continue;
            if (card.RenderTransform is not TransformGroup group || group.Children[0] is not RotateTransform rotate)
                continue;
            var baseAngle = _cardAngles.TryGetValue(item, out var angle) ? angle : 0;
            var gust = 1.1 + _random.NextDouble() * 1.5;
            var direction = _random.Next(0, 2) == 0 ? -1 : 1;
            var sway = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1150) };
            sway.KeyFrames.Add(new SplineDoubleKeyFrame(baseAngle + direction * gust, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), new KeySpline(0.2, 0.8, 0.2, 1)));
            sway.KeyFrames.Add(new SplineDoubleKeyFrame(baseAngle - direction * gust * 0.55, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650)), new KeySpline(0.2, 0.8, 0.2, 1)));
            sway.KeyFrames.Add(new SplineDoubleKeyFrame(baseAngle, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1150)), new KeySpline(0.2, 0.8, 0.2, 1)));
            rotate.BeginAnimation(RotateTransform.AngleProperty, sway);
        }
    }

    private void ClipboardTimer_Tick(object? sender, EventArgs e)
    {
        var sequence = GetClipboardSequenceNumber();
        if (sequence == 0 || sequence == _lastClipboardSequence) return;
        _lastClipboardSequence = sequence;
        if (sequence == _ignoreClipboardSequence) return;
        if (!ClipboardMonitoring) return;
        try
        {
            if (WpfClipboard.ContainsImage() && WpfClipboard.GetImage() is BitmapSource image)
                SaveClipboardImage(image, automatic: true);
        }
        catch (ExternalException) { }
        catch (InvalidOperationException) { }
    }

    private static string ScreenshotFolder() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    private static string VideosFolder() =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    private static string VideoCapturesFolder() =>
        System.IO.Path.Combine(VideosFolder(), "Captures");

    private static string InboxFolder() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clothesline", "Screenshots");

    private void Watch(string folder)
    {
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            watcher.Created += (_, args) => QueueImage(args.FullPath);
            watcher.Renamed += (_, args) => QueueImage(args.FullPath);
            watcher.Error += (_, _) => Dispatcher.BeginInvoke(() => RestartWatcher(folder));
            _watchers.Add(watcher);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void RestartWatcher(string folder)
    {
        foreach (var watcher in _watchers.Where(w => string.Equals(w.Path, folder, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            watcher.Dispose();
            _watchers.Remove(watcher);
        }
        Watch(folder);
    }

    private void QueueImage(string path)
    {
        if (!ImageExtensions.Contains(System.IO.Path.GetExtension(path))) return;
        _ = Task.Run(async () =>
        {
            // Recorders keep the file open while they finalize it. Wait until
            // its size stops changing before decoding a thumbnail.
            long previous = -1;
            for (var attempt = 0; attempt < 12; attempt++)
            {
                await Task.Delay(attempt == 0 ? 650 : 500);
                try
                {
                    var current = new FileInfo(path).Length;
                    if (current > 0 && current == previous) break;
                    previous = current;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            await Dispatcher.InvokeAsync(() => AddImage(path));
        });
    }

    private void AddImage(string path)
    {
        if (!File.Exists(path) || !ImageExtensions.Contains(System.IO.Path.GetExtension(path))) return;
        var fullPath = System.IO.Path.GetFullPath(path);
        if (_dismissedCaptures.Contains(fullPath) || _knownPaths.Contains(fullPath)) return;
        // A capture can arrive while the rail is auto-hidden. Reveal it before
        // creating the card so the user actually sees the flight animation.
        if (!_knownPaths.Add(fullPath)) return;
        try
        {
            var fingerprint = VideoExtensions.Contains(System.IO.Path.GetExtension(fullPath))
                ? null : ImageFingerprint.FromFile(fullPath);
            if (fingerprint is not null && _items.Any(item => item.ContentFingerprint == fingerprint))
            {
                // Remember the second path too so it does not resurrect the
                // duplicate after restart. Never delete either source file.
                _dismissedCaptures.Remember(fullPath, CaptureVersion.Read(fullPath));
                _knownPaths.Remove(fullPath);
                return;
            }
            var thumbnail = LoadThumbnail(fullPath);
            if (thumbnail is null) { _knownPaths.Remove(fullPath); return; }
            // New captures should reveal the line so the flight is visible.
            if (!_loadingInitialItems) ShowRail(activate: false);
            _items.Add(new CaptureItem(fullPath, thumbnail, fingerprint));
            while (_items.Count > MaximumItems)
            {
                var old = _items[0];
                RememberDismissed(old);
                _items.RemoveAt(0);
                RemoveCard(old);
            }
            AddCard(_items[^1]);
            LayoutCards();
            if (!_loadingInitialItems) BeginCaptureFlight(_items[^1]);
            SaveRailSession();
            SetStatus("截图飞入中…");
            _ = Task.Delay(1100).ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                if (StatusText.Text == "截图飞入中…") StatusText.Text = string.Empty;
            }));
        }
        catch (IOException) { _knownPaths.Remove(fullPath); }
        catch (UnauthorizedAccessException) { _knownPaths.Remove(fullPath); }
        catch (NotSupportedException) { _knownPaths.Remove(fullPath); }
        catch (System.IO.FileFormatException) { _knownPaths.Remove(fullPath); }
    }

    private void AddCard(CaptureItem item)
    {
        // Match the macOS composition: a metal peg, a short overlap, then an
        // aspect-fitted glass photo frame. The card size follows the image.
        var maxWidth = 136.0 * CardScale;
        var maxHeight = 104.0 * CardScale;
        var scale = Math.Min(maxWidth / Math.Max(1, item.Thumbnail.PixelWidth),
                             maxHeight / Math.Max(1, item.Thumbnail.PixelHeight));
        var photoWidth = Math.Max(30, Math.Round(item.Thumbnail.PixelWidth * scale));
        var photoHeight = Math.Max(24, Math.Round(item.Thumbnail.PixelHeight * scale));
        var frameWidth = photoWidth + 10;
        var frameHeight = photoHeight + 10;
        var card = new Grid
        {
            Width = frameWidth,
            Height = frameHeight + 20,
            Tag = item,
            ToolTip = "单击复制 · 长按标注 · 拖动分享"
        };
        var peg = new Border
        {
            Width = 11,
            Height = 27,
            CornerRadius = new CornerRadius(4),
            Background = new LinearGradientBrush(MediaColor.FromRgb(225, 230, 235), MediaColor.FromRgb(116, 127, 141), 0),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(255, 255, 255)),
            BorderThickness = new Thickness(0.7),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1.5, Opacity = 0.35 }
        };
        peg.Child = new Border
        {
            Width = 6,
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(MediaColor.FromArgb(145, 35, 42, 52)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Margin = new Thickness(0, 9, 0, 0)
        };
        card.Children.Add(peg);
        var frame = new Border
        {
            Width = frameWidth,
            Height = frameHeight,
            Padding = new Thickness(5),
            CornerRadius = new CornerRadius(15),
            Background = new LinearGradientBrush(
                MediaColor.FromArgb(218, 255, 255, 255),
                MediaColor.FromArgb(150, 218, 226, 241), 90),
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(230, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Margin = new Thickness(0, 17, 0, 0),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 5, Opacity = 0.26 }
        };
        var grid = new Grid();
        // Keep the source aspect ratio. Stretch.Fill makes copied screenshots
        // look like a fixed template instead of a reduced photograph.
        var image = new WpfImage { Source = item.Thumbnail, Width = photoWidth, Height = photoHeight, Stretch = System.Windows.Media.Stretch.Uniform, Cursor = InputCursors.Hand, Tag = item };
        image.MouseLeftButtonDown += Thumbnail_MouseLeftButtonDown;
        image.MouseMove += Thumbnail_MouseMove;
        image.MouseLeftButtonUp += Thumbnail_MouseLeftButtonUp;
        grid.Children.Add(image);
        if (item.IsVideo)
        {
            var play = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
                Background = new SolidColorBrush(MediaColor.FromArgb(205, 15, 23, 42)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            play.Child = new TextBlock
            { Text = "▶", Foreground = MediaBrushes.White, FontSize = 14,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0) };
            grid.Children.Add(play);
        }
        var close = new WpfButton { Content = "×", Width = 20, Height = 20, Padding = new Thickness(0, -2, 0, 0), Background = new SolidColorBrush(MediaColor.FromArgb(210, 15, 23, 42)), Foreground = MediaBrushes.White, BorderThickness = new Thickness(0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = System.Windows.VerticalAlignment.Top, Tag = item, ToolTip = "移入回收站", Focusable = false, IsHitTestVisible = true };
        System.Windows.Controls.Panel.SetZIndex(close, 10);
        // Wire the item directly as well as through Tag. This keeps deletion reliable even if a theme wraps the Button in another visual.
        close.Click += (_, args) =>
        {
            DismissItem(item);
            args.Handled = true;
        };
        close.PreviewMouseLeftButtonDown += (_, args) =>
        {
            DismissItem(item);
            args.Handled = true;
        };
        grid.Children.Add(close);
        card.ContextMenu = BuildCardMenu(item);
        frame.Child = grid;
        card.Children.Add(frame);
        var angle = (item.Path.GetHashCode(StringComparison.OrdinalIgnoreCase) % 2 == 0 ? -1 : 1)
                    * (2.0 + Math.Abs(item.Path.GetHashCode(StringComparison.OrdinalIgnoreCase) % 3));
        var rotate = new RotateTransform(angle);
        card.RenderTransformOrigin = new System.Windows.Point(0.5, 0);
        card.RenderTransform = new TransformGroup { Children = new TransformCollection { rotate } };
        CardsCanvas.Children.Add(card);
        _cardViews[item] = card;
        _cardAngles[item] = angle;
    }

    private void BeginCaptureFlight(CaptureItem item)
    {
        if (!_cardViews.TryGetValue(item, out var card)) return;
        card.Opacity = 0;
        _flyingItems.Add(item);
        // Measure only after WPF has laid out the newly revealed rail. The real
        // card stays hidden until the full-screen copy arrives at its exact peg.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_cardViews.ContainsKey(item)) { _flyingItems.Remove(item); return; }
            CaptureFlightWindow? flight = null;
            void Land()
            {
                if (flight is not null) _flights.Remove(flight);
                _flyingItems.Remove(item);
                if (_cardViews.TryGetValue(item, out var current))
                {
                    current.Opacity = 1;
                    if (current.RenderTransform is TransformGroup group && group.Children[0] is RotateTransform rotate)
                    {
                        var angle = _cardAngles[item];
                        var settle = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(650) };
                        settle.KeyFrames.Add(new LinearDoubleKeyFrame(angle, KeyTime.FromPercent(0)));
                        settle.KeyFrames.Add(new SplineDoubleKeyFrame(angle - 5, KeyTime.FromPercent(0.3), new KeySpline(0.2, 0.8, 0.2, 1)));
                        settle.KeyFrames.Add(new SplineDoubleKeyFrame(angle + 2, KeyTime.FromPercent(0.65), new KeySpline(0.2, 0.8, 0.2, 1)));
                        settle.KeyFrames.Add(new SplineDoubleKeyFrame(angle, KeyTime.FromPercent(1), new KeySpline(0.2, 0.8, 0.2, 1)));
                        rotate.BeginAnimation(RotateTransform.AngleProperty, settle);
                    }
                }
                _shownAt = DateTime.UtcNow;
            }
            try
            {
                UpdateLayout();
                // Use unrotated layout coordinates; the overlay applies tilt.
                var targetTop = CardsCanvas.PointToScreen(new System.Windows.Point(Canvas.GetLeft(card), Canvas.GetTop(card)));
                var targetBottom = CardsCanvas.PointToScreen(new System.Windows.Point(Canvas.GetLeft(card) + card.Width, Canvas.GetTop(card) + card.Height));
                flight = new CaptureFlightWindow(item.Thumbnail, new Rect(targetTop, targetBottom), _cardAngles[item], Land) { Owner = this };
                _flights.Add(flight);
                flight.Show();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ExternalException or System.ComponentModel.Win32Exception)
            {
                if (flight is not null) flight.Close();
                Land();
                SetStatus("飞入动画不可用，截图已挂上");
                Trace.TraceError($"Capture flight: {ex}");
            }
        }));
    }

    private void RemoveCard(CaptureItem item)
    {
        if (_cardViews.Remove(item, out var card)) CardsCanvas.Children.Remove(card);
        _cardAngles.Remove(item);
    }

    private ContextMenu BuildCardMenu(CaptureItem item)
    {
        var menu = new ContextMenu();
        menu.Items.Add(ContextItem("复制图片", (_, _) => CopyItem(item)));
        menu.Items.Add(ContextItem("打开", (_, _) => OpenItem(item)));
        menu.Items.Add(ContextItem("在资源管理器中显示", (_, _) => RevealItem(item)));
        menu.Items.Add(new Separator());
        menu.Items.Add(ContextItem("从绳子上取下", (_, _) => RemoveOnly(item)));
        menu.Items.Add(ContextItem("移入回收站", (_, _) => TrashItem(item)));
        menu.Items.Add(new Separator());
        var index = _items.IndexOf(item);
        menu.Items.Add(ContextItem("取下左侧全部图片", (_, _) => RemoveSide(item, true), index > 0));
        menu.Items.Add(ContextItem("取下右侧全部图片", (_, _) => RemoveSide(item, false), index >= 0 && index < _items.Count - 1));
        menu.Items.Add(ContextItem("取下全部图片", (_, _) => ClearLine()));
        return menu;
    }

    private static MenuItem ContextItem(string header, RoutedEventHandler handler, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += handler;
        return item;
    }

    private void CopyItem(CaptureItem item)
    {
        try
        {
            CopyItemToClipboard(item);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException) { }
    }

    private void CopyItemToClipboard(CaptureItem item)
    {
        if (item.IsVideo)
        {
            var data = new WpfDataObject(WpfDataFormats.FileDrop, new[] { item.Path });
            WpfClipboard.SetDataObject(data, true);
        }
        else if (LoadThumbnail(item.Path) is { } image)
        {
            WpfClipboard.SetImage(image);
        }
        _ignoreClipboardSequence = GetClipboardSequenceNumber();
    }

    private static void OpenItem(CaptureItem item)
    {
        try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    private static void RevealItem(CaptureItem item)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
    }

    private void RemoveOnly(CaptureItem item)
    {
        if (!_items.Remove(item)) return;
        var remembered = RememberDismissed(item);
        RemoveCard(item);
        _knownPaths.Remove(item.Path);
        LayoutCards();
        var saved = SaveRailSession();
        SetStatus(remembered && saved ? "已取下，下次启动不会重新挂载" : "已取下，但无法保存取下记录");
    }

    private bool SaveRailSession()
    {
        if (!_sessionReady) return true;
        try
        {
            _railSession.Save(_items.Select(item => item.Path), _scanStartedUtc);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Unable to persist rail session: {ex.Message}");
            return false;
        }
    }

    private bool RememberDismissed(CaptureItem item)
    {
        try
        {
            // A recording may finalize after its card was created. Remember
            // the file as it exists at dismissal, not the early thumbnail time.
            _dismissedCaptures.Remember(item.Path, CaptureVersion.Read(item.Path) ?? item.Version);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Unable to persist dismissed capture: {ex.Message}");
            return false;
        }
    }

    private void TrashItem(CaptureItem item)
    {
        if (!_items.Contains(item)) return;
        RemoveOnly(item);
        try
        {
            if (File.Exists(item.Path)) FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            SetStatus("已移入回收站");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException)
        {
            SetStatus("已取下，但移入回收站失败");
        }
    }

    private void RemoveSide(CaptureItem item, bool left)
    {
        var index = _items.IndexOf(item);
        if (index < 0) return;
        var targets = left ? _items.Take(index).ToArray() : _items.Skip(index + 1).ToArray();
        foreach (var target in targets) RemoveOnly(target);
    }

    private void ClearLine()
    {
        foreach (var item in _items.ToArray()) RemoveOnly(item);
    }

    private void LayoutCards()
    {
        if (RopeCanvas is null) return;
        var width = Math.Max(1, LineSurface.ActualWidth);
        var sag = Math.Min(30, width * 0.018);
        var ropeTop = 58.0;
        var ropePath = new PathGeometry();
        var figure = new PathFigure { StartPoint = new System.Windows.Point(-20, ropeTop), IsClosed = false };
        figure.Segments.Add(new QuadraticBezierSegment(new System.Windows.Point(width / 2, ropeTop + 2 * sag), new System.Windows.Point(width + 20, ropeTop), true));
        ropePath.Figures.Add(figure);
        RopeLine.Data = ropePath;
        RopeShadow.Data = ropePath;
        RopeHighlight.Data = ropePath;
        var count = _items.Count;
        EmptyHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var gap = Math.Min(174, Math.Max(112, (width - 180) / Math.Max(1, count - 1)));
        var start = count <= 1 ? width / 2 : width / 2 - gap * (count - 1) / 2;
        for (var i = 0; i < count; i++)
        {
            var item = _items[i];
            if (!_cardViews.TryGetValue(item, out var card)) continue;
            var x = count <= 1 ? width / 2 : start + gap * i;
            var f = Math.Clamp(x / width, 0, 1);
            var y = ropeTop + 4 * sag * f * (1 - f);
            Canvas.SetLeft(card, x - card.Width / 2);
            Canvas.SetTop(card, y - 11);
        }
    }

    private static BitmapSource? LoadThumbnail(string path)
    {
        if (VideoExtensions.Contains(System.IO.Path.GetExtension(path)))
            return LoadVideoFrame(path) ?? CreateVideoPlaceholder();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 240;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource? LoadVideoFrame(string path)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var item = SHCreateItemFromParsingName(path, IntPtr.Zero, iid);
            var size = new SIZE { cx = 240, cy = 135 };
            item.GetImage(size, SIIGBF.SIIGBF_BIGGERSIZEOK | SIIGBF.SIIGBF_RESIZETOFIT, out var bitmap);
            if (bitmap == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DeleteObject(bitmap); }
        }
        catch (COMException) { return null; }
        catch (ExternalException) { return null; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern IShellItemImageFactory SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, [In] Guid riid);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [Flags]
    private enum SIIGBF : uint
    {
        SIIGBF_BIGGERSIZEOK = 0x00000001,
        SIIGBF_RESIZETOFIT = 0x00000000
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    private static BitmapSource CreateVideoPlaceholder()
    {
        var pixels = new byte[240 * 135 * 4];
        for (var y = 0; y < 135; y++)
        for (var x = 0; x < 240; x++)
        {
            var i = (y * 240 + x) * 4;
            pixels[i] = (byte)(35 + y * 20 / 135);
            pixels[i + 1] = (byte)(45 + x * 24 / 240);
            pixels[i + 2] = (byte)(75 + (x + y) * 30 / 375);
            pixels[i + 3] = 255;
        }
        var result = BitmapSource.Create(240, 135, 96, 96, PixelFormats.Bgra32, null, pixels, 240 * 4);
        result.Freeze();
        return result;
    }

    private static IEnumerable<string> SafeEnumerateImages(string folder)
    {
        try { return Directory.EnumerateFiles(folder).Where(path => ImageExtensions.Contains(System.IO.Path.GetExtension(path))).ToArray(); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static DateTime SafeLastWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private void Thumbnail_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Cards are created in code, so use Tag rather than DataContext.
        if (sender is not WpfImage image || image.Tag is not CaptureItem item) return;
        _pressedItem = item;
        _dragStarted = false;
        _longPressTriggered = false;
        _dragStart = e.GetPosition(this);
        _longPressTimer.Start();
        image.CaptureMouse();
        e.Handled = true;
    }

    private void Thumbnail_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_pressedItem is null || _dragStarted || _longPressTriggered || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragStarted = true;
        if (sender is WpfImage image && image.IsMouseCaptured) image.ReleaseMouseCapture();
        try
        {
            var dragged = _pressedItem;
            var data = new WpfDataObject(WpfDataFormats.FileDrop, new[] { dragged.Path });
            var result = WpfDragDrop.DoDragDrop((DependencyObject)sender, data,
                WpfDragDropEffects.Copy | WpfDragDropEffects.Move | WpfDragDropEffects.Link);
            // A folder drop is a move in Explorer; apps normally request a
            // copy. Remove the line card only after the move is accepted.
            if (result == WpfDragDropEffects.Move)
            {
                RemoveOnly(dragged);
            }
        }
        finally { _pressedItem = null; }
    }

    private void Thumbnail_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is WpfImage image && image.IsMouseCaptured) image.ReleaseMouseCapture();
        _longPressTimer.Stop();
        var item = _pressedItem;
        _pressedItem = null;
        if (_dragStarted || _longPressTriggered || item is null) { _dragStarted = false; _longPressTriggered = false; return; }
        _dragStarted = false;

        if (e.ClickCount > 1)
        {
            try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        else
        {
            try { CopyItemToClipboard(item); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException) { }
        }
        e.Handled = true;
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: CaptureItem item }) DismissItem(item);
    }

    private void DismissItem(CaptureItem item)
    {
        if (IsInboxPath(item.Path)) TrashItem(item);
        else RemoveOnly(item);
    }

    private static bool IsInboxPath(string path)
    {
        var inbox = System.IO.Path.GetFullPath(InboxFolder()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var full = System.IO.Path.GetFullPath(path);
        return full.StartsWith(inbox, StringComparison.OrdinalIgnoreCase);
    }

    private void SaveClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!WpfClipboard.ContainsImage())
            {
                SetStatus("剪贴板里没有图片");
                return;
            }

            var image = WpfClipboard.GetImage();
            if (image is null)
            {
                SetStatus("剪贴板里没有图片");
                return;
            }

            SaveClipboardImage(image, automatic: false);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            SetStatus("剪贴板正忙，请重试");
        }
        catch (IOException)
        {
            SetStatus("无法写入 PNG 文件");
        }
        catch (UnauthorizedAccessException)
        {
            SetStatus("没有权限写入截图目录");
        }
        catch (InvalidOperationException)
        {
            SetStatus("剪贴板中没有可保存的图片");
        }
    }

    private void SaveClipboardImage(BitmapSource image, bool automatic)
    {
        image = MakeOpaque(image);
        var fingerprint = ImageFingerprint.FromBitmap(image);
        if (_items.Any(item => item.ContentFingerprint == fingerprint))
        {
            if (!automatic) SetStatus("这张图片已经挂在绳子上");
            return;
        }
        var folder = InboxFolder();
        Directory.CreateDirectory(folder);
        var path = NextScreenshotPath(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            encoder.Save(stream);
        AddImage(path);
        if (!automatic) SetStatus($"已保存：{System.IO.Path.GetFileName(path)}");
    }

    private static BitmapSource MakeOpaque(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        // Some Windows screenshot producers expose RGB data with an all-zero
        // alpha channel. PngBitmapEncoder preserves that channel, producing
        // an apparently empty card. Make the clipboard snapshot fully opaque.
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight,
            converted.DpiX, converted.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static string NextScreenshotPath(string folder)
    {
        var stem = $"Snapshot-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
        var path = System.IO.Path.Combine(folder, stem + ".png");
        for (var suffix = 2; File.Exists(path); suffix++)
            path = System.IO.Path.Combine(folder, $"{stem}-{suffix}.png");
        return path;
    }

    private void SetStatus(string message) => StatusText.Text = message;

    public void AddWatchedFolder(string folder)
    {
        if (!Directory.Exists(folder) || _extraFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
        _extraFolders.Add(folder);
        WriteFolderPreferences(_extraFolders);
        Watch(folder);
        foreach (var path in SafeEnumerateImages(folder)) AddImage(path);
    }

    public void RemoveWatchedFolder(string folder)
    {
        var normalized = System.IO.Path.GetFullPath(folder);
        _extraFolders.RemoveAll(path => string.Equals(System.IO.Path.GetFullPath(path), normalized, StringComparison.OrdinalIgnoreCase));
        WriteFolderPreferences(_extraFolders);
        foreach (var watcher in _watchers.Where(w => string.Equals(System.IO.Path.GetFullPath(w.Path), normalized, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            watcher.Dispose();
            _watchers.Remove(watcher);
        }
    }

    public void SetCardScale(double scale) => CardScale = scale;

    private void RebuildCards()
    {
        if (CardsCanvas is null) return;
        CardsCanvas.Children.Clear();
        _cardViews.Clear();
        _cardAngles.Clear();
        foreach (var item in _items.ToArray()) AddCard(item);
        LayoutCards();
    }

    private void TrimItems()
    {
        while (_items.Count > MaximumItems) RemoveOnly(_items[0]);
    }

    private static bool ReadPreference(string name, bool defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PreferencesKey);
            return key?.GetValue(name) is int value ? value != 0 : defaultValue;
        }
        catch (SecurityException) { return defaultValue; }
        catch (UnauthorizedAccessException) { return defaultValue; }
    }

    private static double ReadDoublePreference(string name, double defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PreferencesKey);
            var raw = key?.GetValue(name)?.ToString();
            return double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : defaultValue;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException) { return defaultValue; }
    }

    private static int ReadIntPreference(string name, int defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PreferencesKey);
            return key?.GetValue(name) is int value ? Math.Clamp(value, 4, 30) : defaultValue;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException) { return defaultValue; }
    }

    private static void WriteIntPreference(string name, int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PreferencesKey);
            key?.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException) { }
    }

    private static string[] ReadFolderPreferences()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PreferencesKey);
            return key?.GetValue("ExtraFolders") is string value
                ? value.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static void WriteFolderPreferences(IEnumerable<string> folders)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PreferencesKey);
            key?.SetValue("ExtraFolders", string.Join('\0', folders), RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException) { }
    }

    private static void WriteDoublePreference(string name, double value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PreferencesKey);
            key?.SetValue(name, value, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException) { }
    }

    private static void WritePreference(string name, bool value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PreferencesKey);
            key?.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (SecurityException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_source is not null)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _source.RemoveHook(WindowMessageFilter);
        }
        _clipboardTimer.Stop();
        _pointerTimer.Stop();
        _longPressTimer.Stop();
        _swayTimer.Stop();
        foreach (var flight in _flights.ToArray()) flight.Close();
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
    }
}
