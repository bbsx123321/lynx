using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PadShift.App.ViewModels;
using PadShift.Core;
using PadShift.Core.Host;

namespace PadShift.App.Infrastructure;

/// <summary>
/// Offscreen render used by <c>--render-preview &lt;png&gt;</c>. It builds the real
/// <see cref="MainWindow"/> against a stub host, measures/arranges it at the design size and writes
/// a PNG through <see cref="RenderTargetBitmap"/>. No window is ever shown or screen-captured.
/// </summary>
internal static class RenderPreview
{
    private const string Switch = "--render-preview";

    /// <summary>Injects a full +X/+Y stick deflection so the thumbstick tilt is visible.</summary>
    private const string DemoSticksSwitch = "--demo-sticks";

    /// <summary>Keeps both sticks centred, for the before/after preview pair.</summary>
    private const string DemoSticksCenterSwitch = "--demo-sticks-center";

    /// <summary>Applies a synthetic orbit pose (<c>--demo-orbit &lt;yaw&gt;,&lt;pitch&gt;</c>) so flipped views render offscreen too.</summary>
    private const string DemoOrbitSwitch = "--demo-orbit";

    private const int RenderWidth = 1280;
    private const int RenderHeight = 780;

    /// <summary>Which synthetic stick pose the preview host should report.</summary>
    internal enum StickDemo
    {
        Default,
        Deflected,
        Centered,
    }

    /// <summary>True when the command line asks for a render pass (the app then exits).</summary>
    public static bool IsRequested(string[] args)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, Switch, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads <c>--demo-sticks</c> / <c>--demo-sticks-center</c>; default keeps the old pose.</summary>
    public static StickDemo GetStickDemo(string[] args)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, DemoSticksSwitch, StringComparison.OrdinalIgnoreCase))
            {
                return StickDemo.Deflected;
            }

            if (string.Equals(arg, DemoSticksCenterSwitch, StringComparison.OrdinalIgnoreCase))
            {
                return StickDemo.Centered;
            }
        }

        return StickDemo.Default;
    }

    /// <summary>Path following the switch, or null when it is missing.</summary>
    public static string? GetPath(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], Switch, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length ? args[i + 1] : null;
            }
        }

        return null;
    }

    /// <summary>Reads <c>--demo-orbit yaw,pitch</c> (degrees); null keeps the default pose.</summary>
    public static (double Yaw, double Pitch)? GetOrbit(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], DemoOrbitSwitch, StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length)
            {
                string[] parts = args[i + 1].Split(',');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out double yaw) &&
                    double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out double pitch))
                {
                    return (yaw, pitch);
                }
            }
        }

        return null;
    }

    /// <summary>Renders the client shell to <paramref name="path"/>; returns a process exit code.</summary>
    public static int Run(string path, StickDemo demo = StickDemo.Default, (double Yaw, double Pitch)? orbit = null)
    {
        try
        {
            var host = new PreviewHost(demo);
            var gamepad = new GamepadViewModel();
            var library = new ConfigLibraryViewModel(PreviewProfilesRoot());

            // 读真实设置（语言、3D 开关、手柄外形等），预览才能反映用户实际看到的界面；
            // 渲染路径不经过 StartClient，这里要手动应用语言字典。
            AppSettings previewSettings = AppSettings.Load();
            App.SwitchLanguage(previewSettings.Language);
            var viewModel = new MainViewModel(
                host,
                library,
                gamepad,
                new BindingEditorViewModel(),
                new PreferencesViewModel(previewSettings));

            // One poll so the PNG shows non-neutral sticks / buttons / trigger fill.
            gamepad.PollOnce();

            var window = new MainWindow
            {
                DataContext = viewModel,
                CloseToTray = false,
                MinimizeToTray = false,
            };

            if (window.Content is not FrameworkElement root)
            {
                throw new InvalidOperationException("MainWindow has no renderable content.");
            }

            // There is no HWND chrome offscreen; render the client area edge-to-edge.
            root.Margin = new Thickness(0);
            if (root is Panel panel)
            {
                panel.Background = window.Background;
            }

            if (SafeGraphics.Enabled)
            {
                // The window is never shown offscreen, so Loaded never runs; apply it here instead.
                SafeGraphics.DisableEffects(root);
            }

            if (orbit is { } pose)
            {
                // DataContextChanged 已经跑过 ApplyGamepadView（含模型加载），此时套上演示轨道角。
                window.Pad3DView.OrbitBy(pose.Yaw, pose.Pitch);
            }

            root.Measure(new Size(RenderWidth, RenderHeight));
            root.Arrange(new Rect(0, 0, RenderWidth, RenderHeight));
            root.UpdateLayout();

            var bitmap = new RenderTargetBitmap(RenderWidth, RenderHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            string fullPath = Path.GetFullPath(path);
            string? folder = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            using (FileStream stream = File.Create(fullPath))
            {
                encoder.Save(stream);
            }

            Console.WriteLine("render preview saved: " + fullPath);
            viewModel.Dispose();
            gamepad.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("render preview failed: " + ex);
            return 1;
        }
    }

    private static string PreviewProfilesRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "padshift-preview-profiles");
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception)
        {
            // A read-only temp folder only means the library panel shows its empty message.
        }

        return root;
    }

    /// <summary>Stub façade: one controller with a dramatic pose so the live bindings are visible.</summary>
    private sealed class PreviewHost : IRemapHost
    {
        private static readonly Guid PreviewControllerId = Guid.Parse("2f4c1c19-7c9a-4d3b-9e5f-0a1b2c3d4e5f");

        private readonly HostController _controller = new(
            PreviewControllerId,
            "Xbox 布局手柄（预览）",
            "Xbox",
            "USB",
            VendorId: 0,
            ProductId: 0,
            HasGyro: true,
            HasTouchpad: false,
            HasRumble: true,
            BatteryPercent: 82);

        private readonly ControllerStateSnapshot _snapshot;

        public PreviewHost() : this(StickDemo.Default)
        {
        }

        public PreviewHost(StickDemo demo)
        {
            var buttons = new bool[256];
            buttons[ButtonIds.A] = true;
            buttons[ButtonIds.LeftBumper] = true;
            buttons[ButtonIds.Guide] = true;
            buttons[ButtonIds.DpadUp] = true;

            var axes = new int[8];
            axes[Axes.LeftStickX] = -16000;
            axes[Axes.LeftStickY] = 24000;
            axes[Axes.RightStickX] = 12000;
            axes[Axes.RightStickY] = -8000;
            axes[Axes.RightTrigger] = 32767;

            if (demo == StickDemo.Deflected)
            {
                // Synthetic full deflection: +X is right, +Y is up on the physical stick.
                axes[Axes.LeftStickX] = 32767;
                axes[Axes.LeftStickY] = 32767;
                axes[Axes.RightStickX] = 32767;
                axes[Axes.RightStickY] = 32767;
            }
            else if (demo == StickDemo.Centered)
            {
                axes[Axes.LeftStickX] = 0;
                axes[Axes.LeftStickY] = 0;
                axes[Axes.RightStickX] = 0;
                axes[Axes.RightStickY] = 0;
            }

            _snapshot = new ControllerStateSnapshot(buttons, axes, Connected: true, Environment.TickCount64);
        }

        public event EventHandler<HostEvent>? Event
        {
            add { }
            remove { }
        }

        public bool RemapEnabled => true;

        public IReadOnlyList<HostController> Controllers => new[] { _controller };

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public IReadOnlyList<ShiftInfo> GetShifts(Guid controllerId) => Array.Empty<ShiftInfo>();

        public RemapState GetRemapState(Guid controllerId) => RemapState.RemapOn;

        public Task<ApplyConfigResult> ApplyConfigAsync(Guid controllerId, string profilePath, int slot) =>
            Task.FromResult(new ApplyConfigResult(true, null, slot));

        public EnableRemapResult EnableRemap(Guid? controllerId) => new(true, null);

        public void DisableRemap(Guid? controllerId)
        {
        }

        public void SelectSlot(Guid controllerId, int slot)
        {
        }

        public int GetCurrentSlot(Guid controllerId) => 0;

        public IReadOnlyList<string> GetAppliedConfigs(Guid controllerId, int slot) =>
            new[] { Path.Combine(Path.GetTempPath(), "preview", "Aim & Move.rewasd") };

        public bool IsKeyPressed(Guid controllerId, int scanCode) => false;

        public bool TryGetState(Guid controllerId, out ControllerStateSnapshot state)
        {
            if (controllerId != PreviewControllerId)
            {
                state = default;
                return false;
            }

            state = _snapshot;
            return true;
        }

        public void Vibrate(Guid controllerId, byte leftMotor, byte rightMotor, ushort durationMs)
        {
        }

        public void Dispose()
        {
        }
    }
}
