using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PadShift.App.Views;

/// <summary>
/// 映射表悬浮卡：把当前配置的绑定清单（<see cref="Infrastructure.BindingListExporter"/> 输出）
/// 以置顶、点击穿透的半透明卡片显示在屏幕右上角——游戏中瞄一眼即可确认按键。默认由全局热键
/// （偏好里可改）切换显隐；无配置加载时显示占位提示。
/// </summary>
public partial class MappingsOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    public MappingsOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyClickThrough();
        Loaded += (_, _) => PositionTopRight();
    }

    /// <summary>更新清单文本；空文本显示占位说明。</summary>
    public void SetText(string? title, string? text)
    {
        CaptionText.Text = string.IsNullOrWhiteSpace(title) ? LocProxy() : title;
        MappingsText.Text = string.IsNullOrWhiteSpace(text) ? NoProfileHint() : text!;
    }

    private static string LocProxy() => Infrastructure.Loc.T("mappings.title");

    private static string NoProfileHint() => Infrastructure.Loc.T("mappings.empty");

    private void ApplyClickThrough()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int style = GetWindowLong(handle, GwlExStyle);
        style |= WsExTransparent | WsExNoActivate | WsExToolWindow | WsExLayered;
        SetWindowLong(handle, GwlExStyle, style);
    }

    private void PositionTopRight()
    {
        try
        {
            double right = SystemParameters.WorkArea.Right;
            double top = SystemParameters.WorkArea.Top;
            Left = right - ActualWidth - 16;
            Top = top + 16;
        }
        catch (Exception)
        {
            // 多屏几何异常时留在默认位置。
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int style);
}
