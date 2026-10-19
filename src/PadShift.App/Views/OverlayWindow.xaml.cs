using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using PadShift.App.Infrastructure;
using PadShift.App.Services;
using Drawing = System.Drawing;

namespace PadShift.App.Views;

/// <summary>
/// Click-through, topmost, no-activate overlay. It never takes focus from a game:
/// <c>ShowActivated=false</c>, <c>IsHitTestVisible=false</c> and the native
/// <c>WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c> extended styles (set on
/// <c>SourceInitialized</c>) keep it out of hit testing, the taskbar and Alt+Tab.
/// </summary>
/// <remarks>
/// The magnifier ring samples the screen with GDI (<c>Graphics.CopyFromScreen</c>) and draws a
/// scaled copy; <c>VisualBrush</c> is deliberately not used and the ring captures no input.
/// </remarks>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;

    private readonly DispatcherTimer _frameTimer;
    private readonly OverlayToastQueue _toastQueue = new();
    private OverlaySettings _settings = new();
    private DateTimeOffset _lastMagnifierUpdate = DateTimeOffset.MinValue;
    private Drawing.Bitmap? _captureBuffer;
    private Drawing.Bitmap? _scaledBuffer;
    private int _bufferPixels;

    // Monitor geometry in DIPs (the same space as SystemParameters.VirtualScreen*).
    private MonitorBounds[] _monitorDips = Array.Empty<MonitorBounds>();
    private int _selectedMonitorIndex;
    private MonitorPoint _crosshairAbsolute;
    private Rect _toastArea = new(0, 0, 1, 1);
    private double _canvasScaleX = 1;
    private double _canvasScaleY = 1;
    private double _virtualLeft;
    private double _virtualTop;
    private double _virtualWidth = 1;
    private double _virtualHeight = 1;

    public OverlayWindow()
    {
        InitializeComponent();
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _frameTimer.Tick += OnFrame;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Render();
        Closed += (_, _) => _frameTimer.Stop();
    }

    /// <summary>The single-slot toast queue; mapping state changes enqueue here.</summary>
    public OverlayToastQueue ToastQueue => _toastQueue;

    /// <summary>Applies new settings, shows/hides the overlay and redraws it.</summary>
    public void ApplySettings(OverlaySettings settings)
    {
        _settings = settings ?? new OverlaySettings();
        _settings.Normalize();

        if (!_settings.Enabled)
        {
            _toastQueue.Clear();
            ToastHost.Visibility = Visibility.Collapsed;
            Hide();
            _frameTimer.Stop();
            return;
        }

        PositionWindow();
        RefreshMonitorGeometry();
        if (!IsVisible)
        {
            Show();
        }

        Render();
        _frameTimer.Start();
    }

    /// <summary>Enqueues a mapping-status toast (only the newest message is shown).</summary>
    public void ShowToast(string? message) => _toastQueue.Enqueue(message, DateTimeOffset.UtcNow);

    // ------------------------------------------------------------------ window plumbing

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            int style = GetWindowLong(handle, GwlExStyle);
            style |= WsExTransparent | WsExNoActivate | WsExToolWindow | WsExLayered;
            SetWindowLong(handle, GwlExStyle, style);
        }

        PositionWindow();
        RefreshMonitorGeometry();
        Render();
    }

    /// <summary>
    /// Covers the whole virtual screen (all monitors), not just the primary one. SystemParameters
    /// values are already device-independent pixels for a PerMonitorV2 process.
    /// </summary>
    private void PositionWindow()
    {
        try
        {
            double left = SystemParameters.VirtualScreenLeft;
            double top = SystemParameters.VirtualScreenTop;
            double width = SystemParameters.VirtualScreenWidth;
            double height = SystemParameters.VirtualScreenHeight;
            if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height)
                || width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("invalid virtual screen metrics");
            }

            _virtualLeft = left;
            _virtualTop = top;
            _virtualWidth = width;
            _virtualHeight = height;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }
        catch (Exception)
        {
            // Session changes can fail; the primary monitor is a sane fallback.
            _virtualLeft = 0;
            _virtualTop = 0;
            _virtualWidth = Math.Max(1, SystemParameters.PrimaryScreenWidth);
            _virtualHeight = Math.Max(1, SystemParameters.PrimaryScreenHeight);
            Left = 0;
            Top = 0;
            Width = _virtualWidth;
            Height = _virtualHeight;
        }
    }

    /// <summary>
    /// Reads the monitor list and converts every physical rectangle to the overlay's DIP canvas.
    /// The crosshair uses the selected monitor; the toast rectangle stays on the primary monitor.
    /// </summary>
    private void RefreshMonitorGeometry()
    {
        IReadOnlyList<MonitorDescriptor> monitors = MonitorLayout.Enumerate();
        if (monitors.Count == 0)
        {
            _monitorDips = Array.Empty<MonitorBounds>();
            _selectedMonitorIndex = 0;
            _canvasScaleX = 1;
            _canvasScaleY = 1;
            _toastArea = new Rect(0, 0, _virtualWidth, _virtualHeight);
            return;
        }

        int primary = 0;
        for (int i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].IsPrimary)
            {
                primary = i;
                break;
            }
        }

        // SystemParameters.VirtualScreen* is expressed in one DIP space (the primary monitor's
        // scale), so every physical monitor rectangle must use the same scale to stay aligned.
        _canvasScaleX = monitors[primary].ScaleX > 0 ? monitors[primary].ScaleX : 1;
        _canvasScaleY = monitors[primary].ScaleY > 0 ? monitors[primary].ScaleY : 1;

        var dips = new MonitorBounds[monitors.Count];
        for (int i = 0; i < monitors.Count; i++)
        {
            dips[i] = MonitorLayout.ToDip(monitors[i].PhysicalBounds, _canvasScaleX, _canvasScaleY);
        }

        _monitorDips = dips;
        _selectedMonitorIndex = Math.Max(0, MonitorLayout.IndexOf(monitors, _settings.MonitorDeviceName));

        MonitorBounds toast = dips[primary];
        _toastArea = new Rect(
            toast.Left - _virtualLeft,
            toast.Top - _virtualTop,
            Math.Max(1, toast.Width),
            Math.Max(1, toast.Height));
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        UpdateToast(now);

        if (_settings.UseMagnifier && now - _lastMagnifierUpdate >= TimeSpan.FromMilliseconds(100))
        {
            _lastMagnifierUpdate = now;
            UpdateMagnifier();
        }
    }

    // ------------------------------------------------------------------ rendering

    private void Render()
    {
        double width = OverlayCanvas.ActualWidth > 0 ? OverlayCanvas.ActualWidth : Width;
        double height = OverlayCanvas.ActualHeight > 0 ? OverlayCanvas.ActualHeight : Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return;
        }

        OverlayCanvas.Width = width;
        OverlayCanvas.Height = height;

        CrosshairCanvas.Children.Clear();
        if (!_settings.ShowCrosshair)
        {
            MagnifierHost.Visibility = Visibility.Collapsed;
            return;
        }

        double centerX;
        double centerY;
        if (_monitorDips.Length > 0)
        {
            MonitorPoint absolute = MonitorLayout.Resolve(
                _monitorDips,
                _selectedMonitorIndex,
                _settings.CenterOnScreen,
                _settings.OffsetX,
                _settings.OffsetY);
            _crosshairAbsolute = absolute;
            centerX = absolute.X - _virtualLeft;
            centerY = absolute.Y - _virtualTop;
        }
        else
        {
            // No display enumeration: fall back to the virtual-screen centre.
            double absoluteX = _virtualLeft + (width / 2) + (_settings.CenterOnScreen ? 0 : _settings.OffsetX);
            double absoluteY = _virtualTop + (height / 2) + (_settings.CenterOnScreen ? 0 : _settings.OffsetY);
            _crosshairAbsolute = new MonitorPoint((int)Math.Round(absoluteX), (int)Math.Round(absoluteY));
            centerX = width / 2 + (_settings.CenterOnScreen ? 0 : _settings.OffsetX);
            centerY = height / 2 + (_settings.CenterOnScreen ? 0 : _settings.OffsetY);
        }

        Brush brush = CreateBrush(_settings.Color);

        switch (_settings.Shape)
        {
            case CrosshairShape.Dot:
            {
                double diameter = Math.Max(4, _settings.Size / 4);
                var dot = new Ellipse
                {
                    Width = diameter,
                    Height = diameter,
                    Fill = brush,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(dot, centerX - (diameter / 2));
                Canvas.SetTop(dot, centerY - (diameter / 2));
                CrosshairCanvas.Children.Add(dot);
                break;
            }

            case CrosshairShape.Circle:
            {
                var circle = new Ellipse
                {
                    Width = _settings.Size,
                    Height = _settings.Size,
                    Stroke = brush,
                    StrokeThickness = _settings.Thickness,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(circle, centerX - (_settings.Size / 2));
                Canvas.SetTop(circle, centerY - (_settings.Size / 2));
                CrosshairCanvas.Children.Add(circle);
                break;
            }

            default:
            {
                var horizontal = new Rectangle
                {
                    Width = _settings.Size,
                    Height = _settings.Thickness,
                    Fill = brush,
                    RadiusX = _settings.Thickness / 2,
                    RadiusY = _settings.Thickness / 2,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(horizontal, centerX - (_settings.Size / 2));
                Canvas.SetTop(horizontal, centerY - (_settings.Thickness / 2));
                CrosshairCanvas.Children.Add(horizontal);

                var vertical = new Rectangle
                {
                    Width = _settings.Thickness,
                    Height = _settings.Size,
                    Fill = brush,
                    RadiusX = _settings.Thickness / 2,
                    RadiusY = _settings.Thickness / 2,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(vertical, centerX - (_settings.Thickness / 2));
                Canvas.SetTop(vertical, centerY - (_settings.Size / 2));
                CrosshairCanvas.Children.Add(vertical);
                break;
            }
        }

        LayoutMagnifier(centerX, centerY);
    }

    private void LayoutMagnifier(double centerX, double centerY)
    {
        if (!_settings.UseMagnifier)
        {
            MagnifierHost.Visibility = Visibility.Collapsed;
            return;
        }

        double diameter = Math.Max(12, _settings.Size * _settings.MagnifierSizePercent / 100.0);
        MagnifierHost.Width = diameter;
        MagnifierHost.Height = diameter;
        MagnifierHost.Visibility = Visibility.Visible;
        MagnifierRing.Width = diameter;
        MagnifierRing.Height = diameter;
        MagnifierImage.Width = diameter;
        MagnifierImage.Height = diameter;
        MagnifierImage.Clip = new EllipseGeometry(new Point(diameter / 2, diameter / 2), diameter / 2, diameter / 2);

        Canvas.SetLeft(MagnifierHost, centerX + (_settings.Size / 2) + (diameter / 2) + 8);
        Canvas.SetTop(MagnifierHost, centerY - (diameter / 2));
    }

    private void UpdateToast(DateTimeOffset now)
    {
        string? message = _toastQueue.Current(now);
        double opacity = _toastQueue.Opacity(now);
        if (!_settings.ShowToast || message is null || opacity <= 0.01)
        {
            ToastHost.Visibility = Visibility.Collapsed;
            return;
        }

        if (ToastText.Text != message)
        {
            ToastText.Text = message;
            ToastHost.Measure(new Size(Math.Max(1, _toastArea.Width * 0.8), double.PositiveInfinity));
        }

        ToastHost.Opacity = opacity;
        ToastHost.Visibility = Visibility.Visible;

        // The toast always stays on the primary monitor, even when the crosshair is elsewhere.
        double width = Math.Max(1, _toastArea.Width);
        double height = Math.Max(1, _toastArea.Height);
        Canvas.SetLeft(ToastHost, _toastArea.Left + Math.Max(0, (width - ToastHost.DesiredSize.Width) / 2));
        Canvas.SetTop(ToastHost, _toastArea.Top + (height * 0.78));
    }

    // ------------------------------------------------------------------ magnifier (GDI sampling)

    private void UpdateMagnifier()
    {
        if (!_settings.UseMagnifier || MagnifierHost.Visibility != Visibility.Visible)
        {
            return;
        }

        try
        {
            double ringSize = Math.Max(12, _settings.Size * _settings.MagnifierSizePercent / 100.0);
            int pixels = Math.Max(8, (int)Math.Round(ringSize * _canvasScaleX));
            int zoom = Math.Clamp(_settings.Magnification, OverlaySettings.MinMagnification, OverlaySettings.MaxMagnification);
            int sourceSize = Math.Max(2, pixels / zoom);

            int screenX = (int)Math.Round(_crosshairAbsolute.X * _canvasScaleX);
            int screenY = (int)Math.Round(_crosshairAbsolute.Y * _canvasScaleY);
            int sourceLeft = screenX - (sourceSize / 2);
            int sourceTop = screenY - (sourceSize / 2);

            EnsureBuffers(pixels, sourceSize);
            if (_captureBuffer is null || _scaledBuffer is null)
            {
                return;
            }

            using (Drawing.Graphics capture = Drawing.Graphics.FromImage(_captureBuffer))
            {
                capture.CopyFromScreen(
                    sourceLeft,
                    sourceTop,
                    0,
                    0,
                    new Drawing.Size(sourceSize, sourceSize),
                    Drawing.CopyPixelOperation.SourceCopy);
            }

            using (Drawing.Graphics scale = Drawing.Graphics.FromImage(_scaledBuffer))
            {
                scale.InterpolationMode = Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                scale.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.Half;
                scale.DrawImage(_captureBuffer, 0, 0, pixels, pixels);
            }

            IntPtr hBitmap = _scaledBuffer.GetHbitmap();
            try
            {
                MagnifierImage.Source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch (Exception)
        {
            // Screen capture can fail on secure desktops/session changes. Hide the image but keep the
            // crosshair itself visible: the magnifier is documented as best-effort.
            MagnifierImage.Source = null;
            MagnifierRing.Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x10, 0x18));
        }
    }

    private void EnsureBuffers(int pixels, int sourceSize)
    {
        if (_bufferPixels == pixels && _captureBuffer is not null && _scaledBuffer is not null)
        {
            if (_captureBuffer.Width == sourceSize && _scaledBuffer.Width == pixels)
            {
                return;
            }
        }

        _captureBuffer?.Dispose();
        _scaledBuffer?.Dispose();
        _captureBuffer = new Drawing.Bitmap(sourceSize, sourceSize, Drawing.Imaging.PixelFormat.Format32bppPArgb);
        _scaledBuffer = new Drawing.Bitmap(pixels, pixels, Drawing.Imaging.PixelFormat.Format32bppPArgb);
        _bufferPixels = pixels;
    }

    private static Brush CreateBrush(string color)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);
        }
        catch (Exception)
        {
            return new SolidColorBrush(Color.FromRgb(0x5C, 0xFF, 0x8A));
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
