using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using WpfImage = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace SnapshotRail;

/// <summary>Screen-sized, nonactivating overlay: a flight must not be clipped by the rail strip.</summary>
internal sealed class CaptureFlightWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly Grid _card = new();
    private readonly Border _glass;
    private readonly Border _peg;
    private readonly Border _photo;
    private readonly RotateTransform _rotation = new();
    private readonly Stopwatch _clock = new();
    private readonly Rect _targetPixels;
    private readonly Rect _screenPixels;
    private readonly double _tilt;
    private Action? _land;
    private Point _from;
    private Point _to;
    private Size _initialSize;
    private Size _targetSize;
    private bool _subscribed;

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    public CaptureFlightWindow(BitmapSource image, Rect targetPixels, double tilt, Action land)
    {
        _targetPixels = targetPixels;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
            (int)(targetPixels.X + targetPixels.Width / 2), (int)targetPixels.Y));
        _screenPixels = new Rect(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
        _tilt = tilt;
        _land = land;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        IsHitTestVisible = false;
        Left = _screenPixels.X;
        Top = _screenPixels.Y;
        Width = _screenPixels.Width;
        Height = _screenPixels.Height;
        Content = _canvas;
        _card.RenderTransformOrigin = new Point(0.5, 0);
        _card.RenderTransform = _rotation;
        _card.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 5, Opacity = 0.25 };
        _glass = new Border
        {
            Background = new LinearGradientBrush(Color.FromArgb(218, 255, 255, 255), Color.FromArgb(150, 218, 226, 241), 90),
            BorderBrush = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
            BorderThickness = new Thickness(1)
        };
        _photo = new Border
        {
            Child = new WpfImage { Source = image, Stretch = Stretch.UniformToFill },
            ClipToBounds = true
        };
        _peg = new Border
        {
            Width = 11, Height = 27, CornerRadius = new CornerRadius(4),
            Background = new LinearGradientBrush(Color.FromRgb(225, 230, 235), Color.FromRgb(116, 127, 141), 0),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(0.7),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Top
        };
        _card.Children.Add(_glass);
        _card.Children.Add(_photo);
        _card.Children.Add(_peg);
        _canvas.Children.Add(_card);
        SourceInitialized += InitializeOverlay;
        ContentRendered += StartFlight;
        Closed += (_, _) => Finish();
    }

    private void InitializeOverlay(object? sender, EventArgs args)
    {
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExTransparent | WsExToolWindow | WsExNoActivate);
        // Screen.Bounds and PointToScreen are physical pixels; set native bounds
        // first, then convert both endpoints through this overlay's DPI matrix.
        SetWindowPos(handle, new IntPtr(-1), (int)_screenPixels.X, (int)_screenPixels.Y,
            (int)_screenPixels.Width, (int)_screenPixels.Height, 0x0010);
    }

    private void StartFlight(object? sender, EventArgs args)
    {
        ContentRendered -= StartFlight;
        var topLeft = PointFromScreen(_targetPixels.TopLeft);
        var bottomRight = PointFromScreen(_targetPixels.BottomRight);
        _targetSize = new Size(bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
        _to = new Point(topLeft.X + _targetSize.Width / 2, topLeft.Y);
        // Windows screenshots do not carry macOS's capture rectangle metadata.
        // Use an explicit screen-centre fallback, not a fabricated capture area.
        var centre = PointFromScreen(new Point(_screenPixels.X + _screenPixels.Width * 0.5,
            _screenPixels.Y + _screenPixels.Height * 0.55));
        _from = centre;
        var enlarge = Math.Min(3.0, Math.Min(Math.Max(1, ActualWidth) * 0.38 / _targetSize.Width,
            Math.Max(1, ActualHeight) * 0.35 / _targetSize.Height));
        enlarge = Math.Max(1, enlarge);
        _initialSize = new Size(_targetSize.Width * enlarge, _targetSize.Height * enlarge);
        _from.Y -= _initialSize.Height / 2;
        Update(0);
        _clock.Start();
        CompositionTarget.Rendering += RenderFrame;
        _subscribed = true;
    }

    private void RenderFrame(object? sender, EventArgs args)
    {
        var t = Math.Min(1, _clock.Elapsed.TotalMilliseconds / 900);
        Update(t);
        if (t >= 1) { Finish(); Close(); }
    }

    private void Update(double raw)
    {
        var t = raw < 0.5 ? 4 * raw * raw * raw : 1 - Math.Pow(-2 * raw + 2, 3) / 2;
        static double Mix(double a, double b, double t) => a + (b - a) * t;
        var chrome = Math.Clamp((t - 0.22) / 0.65, 0, 1);
        var width = Mix(_initialSize.Width, _targetSize.Width, t);
        var height = Mix(_initialSize.Height, _targetSize.Height, t);
        _card.Width = width;
        _card.Height = height;
        Canvas.SetLeft(_card, Mix(_from.X, _to.X, t) - width / 2);
        Canvas.SetTop(_card, Mix(_from.Y, _to.Y, t) - Math.Sin(Math.PI * t) * 45);
        _rotation.Angle = _tilt * t;
        _glass.Margin = new Thickness(0, 17 * t, 0, 3 * t);
        _glass.CornerRadius = new CornerRadius(15 * t);
        _glass.Opacity = chrome;
        _photo.Margin = new Thickness(6 * t, 23 * t, 6 * t, 9 * t);
        _photo.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(1, width - 12 * t),
            Math.Max(1, height - 32 * t)), 10 * t, 10 * t);
        _peg.Opacity = chrome;
        _card.Opacity = Math.Min(1, raw / 0.08);
    }

    private void Finish()
    {
        if (_subscribed) CompositionTarget.Rendering -= RenderFrame;
        _subscribed = false;
        _clock.Stop();
        var land = _land;
        _land = null;
        land?.Invoke();
    }
}
