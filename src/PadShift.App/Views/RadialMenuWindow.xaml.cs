using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PadShift.App.Infrastructure;
using PadShift.App.Services;
using WinForms = System.Windows.Forms;
using Path = System.Windows.Shapes.Path;

namespace PadShift.App.Views;

/// <summary>Result reported when the radial window closes.</summary>
public sealed class RadialMenuResult : EventArgs
{
    public RadialMenuResult(bool applied, int sectorIndex, RadialSectorSetting? sector)
    {
        Applied = applied;
        SectorIndex = sectorIndex;
        Sector = sector;
    }

    /// <summary>True when the user released the trigger/mouse (apply); false for Esc/cancel.</summary>
    public bool Applied { get; }

    public int SectorIndex { get; }

    public RadialSectorSetting? Sector { get; }
}

/// <summary>
/// Transparent, topmost, eight-sector radial menu drawn with <see cref="Path"/> +
/// <see cref="ArcSegment"/> (no image assets). It is deliberately a normal interactive window while
/// open — it must receive the mouse click and the Esc key — but it installs no global hooks, so it
/// never swallows input outside itself. The right stick is polled through a delegate; releasing the
/// controller trigger is handled by <see cref="RadialMenuService"/>.
/// </summary>
public partial class RadialMenuWindow : Window
{
    private const double CanvasSize = 440;
    private const double OuterRadius = 204;
    private const double InnerRadius = 72;

    private readonly IReadOnlyList<RadialSectorSetting> _sectors;
    private readonly Func<RadialStickState>? _stick;
    private readonly List<Path> _paths = new(8);
    private readonly List<Border> _labelPlates = new(8);
    private readonly DispatcherTimer? _stickTimer;

    private int _selected = -1;
    private bool _closing;

    public RadialMenuWindow(IReadOnlyList<RadialSectorSetting> sectors, Func<RadialStickState>? stick = null)
    {
        InitializeComponent();
        _sectors = sectors ?? throw new ArgumentNullException(nameof(sectors));
        _stick = stick;
        DrawSectors();
        UpdateSelection(-1);

        if (stick is not null)
        {
            _stickTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(33),
            };
            _stickTimer.Tick += OnStickTick;
        }
    }

    /// <summary>Raised exactly once when the window closes.</summary>
    public event EventHandler<RadialMenuResult>? ResultReady;

    public bool Applied { get; private set; }

    public int SelectedIndex => _selected;

    public RadialSectorSetting? SelectedSector =>
        _selected >= 0 && _selected < _sectors.Count ? _sectors[_selected] : null;

    /// <summary>Centres the window on the mouse cursor and clamps it to the cursor's screen.</summary>
    public void PositionAtCursor()
    {
        try
        {
            var cursor = WinForms.Cursor.Position;
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            double scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

            WinForms.Screen screen = WinForms.Screen.FromPoint(cursor);
            double left = (cursor.X / scaleX) - (Width / 2);
            double top = (cursor.Y / scaleY) - (Height / 2);
            double minLeft = screen.WorkingArea.Left / scaleX;
            double minTop = screen.WorkingArea.Top / scaleY;
            double maxLeft = (screen.WorkingArea.Right / scaleX) - Width;
            double maxTop = (screen.WorkingArea.Bottom / scaleY) - Height;

            Left = Math.Clamp(left, minLeft, Math.Max(minLeft, maxLeft));
            Top = Math.Clamp(top, minTop, Math.Max(minTop, maxTop));
        }
        catch (Exception)
        {
            // Cursor/screen queries can fail during session changes; the default position is fine.
        }
    }

    /// <summary>Starts right-stick polling; call after the window is shown.</summary>
    public void StartStickPolling()
    {
        _stickTimer?.Start();
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    /// <summary>Applies the highlighted sector (release/Enter).</summary>
    public void ApplyAndClose()
    {
        if (_closing)
        {
            return;
        }

        Applied = _selected >= 0;
        Close();
    }

    /// <summary>Cancels (Esc / menu disabled).</summary>
    public void CancelAndClose()
    {
        if (_closing)
        {
            return;
        }

        Applied = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _stickTimer?.Stop();
        if (_stickTimer is not null)
        {
            _stickTimer.Tick -= OnStickTick;
        }

        base.OnClosed(e);
        ResultReady?.Invoke(this, new RadialMenuResult(Applied, _selected, SelectedSector));
    }

    // ------------------------------------------------------------------ input

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Point point = e.GetPosition(RootCanvas);
        int hit = RadialMenuGeometry.HitTest(
            CanvasSize / 2,
            CanvasSize / 2,
            point.X,
            point.Y,
            _sectors.Count,
            InnerRadius,
            OuterRadius);
        if (hit >= 0)
        {
            UpdateSelection(hit);
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_selected >= 0)
        {
            ApplyAndClose();
        }
        else
        {
            CancelAndClose();
        }
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CancelAndClose();
                return;

            case Key.Enter:
                e.Handled = true;
                ApplyAndClose();
                return;

            case Key.Right:
            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                return;

            case Key.Left:
            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                return;
        }
    }

    private void OnStickTick(object? sender, EventArgs e)
    {
        if (_stick is null || _closing)
        {
            return;
        }

        RadialStickState state = _stick();
        if (!state.HasState)
        {
            return;
        }

        double magnitude = Math.Sqrt((state.X * state.X) + (state.Y * state.Y));
        if (magnitude < 0.45)
        {
            return;
        }

        int index = RadialMenuGeometry.SectorIndex(
            RadialMenuGeometry.AngleFromTopClockwise(state.X, state.Y),
            _sectors.Count);
        UpdateSelection(index);
    }

    private void MoveSelection(int delta)
    {
        int count = _sectors.Count;
        if (count == 0)
        {
            return;
        }

        int next = _selected < 0 ? 0 : ((_selected + delta) % count + count) % count;
        UpdateSelection(next);
    }

    // ------------------------------------------------------------------ drawing

    private void DrawSectors()
    {
        double center = CanvasSize / 2;
        double step = 360.0 / Math.Max(1, _sectors.Count);

        for (int index = 0; index < _sectors.Count; index++)
        {
            double centerAngle = RadialMenuGeometry.SectorCenterAngle(index, _sectors.Count);
            double startAngle = centerAngle - (step / 2);
            double endAngle = centerAngle + (step / 2);

            (double ox1, double oy1) = RadialMenuGeometry.PointOnCircle(center, center, OuterRadius, startAngle);
            (double ox2, double oy2) = RadialMenuGeometry.PointOnCircle(center, center, OuterRadius, endAngle);
            (double ix2, double iy2) = RadialMenuGeometry.PointOnCircle(center, center, InnerRadius, endAngle);
            (double ix1, double iy1) = RadialMenuGeometry.PointOnCircle(center, center, InnerRadius, startAngle);

            var figure = new PathFigure
            {
                StartPoint = new Point(ox1, oy1),
                IsClosed = true,
                IsFilled = true,
            };
            figure.Segments.Add(new ArcSegment(
                new Point(ox2, oy2),
                new Size(OuterRadius, OuterRadius),
                0,
                false,
                SweepDirection.Clockwise,
                true));
            figure.Segments.Add(new LineSegment(new Point(ix2, iy2), true));
            figure.Segments.Add(new ArcSegment(
                new Point(ix1, iy1),
                new Size(InnerRadius, InnerRadius),
                0,
                false,
                SweepDirection.Counterclockwise,
                true));

            var path = new Path
            {
                Data = new PathGeometry([figure]),
                Fill = SectorFill(index == _selected),
                Stroke = SectorStroke(index == _selected),
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
                Tag = index,
            };
            _paths.Add(path);
            SectorCanvas.Children.Add(path);

            RadialSectorSetting sector = _sectors[index];
            (double lx, double ly) = RadialMenuGeometry.PointOnCircle(
                center,
                center,
                (OuterRadius + InnerRadius) / 2,
                centerAngle);
            var plate = new Border
            {
                Width = 98,
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = sector.Label,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF6, 0xFF)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 96,
            });
            stack.Children.Add(new TextBlock
            {
                Text = DescribeAction(sector),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0x9C, 0xB8)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 96,
                Margin = new Thickness(0, 2, 0, 0),
            });
            plate.Child = stack;
            plate.Measure(new Size(98, double.PositiveInfinity));
            Canvas.SetLeft(plate, lx - 49);
            Canvas.SetTop(plate, ly - (plate.DesiredSize.Height / 2));
            _labelPlates.Add(plate);
            SectorCanvas.Children.Add(plate);
        }
    }

    private void UpdateSelection(int index)
    {
        if (index >= _sectors.Count)
        {
            index = -1;
        }

        _selected = index;
        for (int pathIndex = 0; pathIndex < _paths.Count; pathIndex++)
        {
            _paths[pathIndex].Fill = SectorFill(pathIndex == index);
            _paths[pathIndex].Stroke = SectorStroke(pathIndex == index);
            _paths[pathIndex].StrokeThickness = pathIndex == index ? 2.5 : 1.5;
        }

        if (SelectedSector is { } sector)
        {
            CenterLabel.Text = string.IsNullOrWhiteSpace(sector.Label) ? $"扇区 {index + 1}" : sector.Label;
            CenterHint.Text = DescribeAction(sector);
        }
        else
        {
            CenterLabel.Text = "径向菜单";
            CenterHint.Text = "移动选择 · 松开应用 · Esc 取消";
        }
    }

    private static Brush SectorFill(bool selected) => new SolidColorBrush(
        selected ? Color.FromArgb(0xF2, 0x2F, 0x6F, 0xB5) : Color.FromArgb(0xD8, 0x22, 0x22, 0x2C));

    private static Brush SectorStroke(bool selected) => new SolidColorBrush(
        selected ? Color.FromRgb(0x7C, 0xC0, 0xFF) : Color.FromRgb(0x45, 0x45, 0x58));

    private static string DescribeAction(RadialSectorSetting sector) => sector.Action switch
    {
        RadialSectorAction.SwitchLayer => $"切换层 {sector.Argument}",
        RadialSectorAction.SwitchSlot => $"槽位 {sector.Argument}",
        RadialSectorAction.ToggleRemap => sector.Argument.ToLowerInvariant() switch
        {
            "on" or "enable" or "启用" => "启用映射",
            "off" or "disable" or "停用" => "停用映射",
            _ => "启用/停用映射",
        },
        RadialSectorAction.RunMacro => "执行宏 " + sector.Argument,
        RadialSectorAction.OpenPreferences => "打开配置",
        _ => "空",
    };
}
