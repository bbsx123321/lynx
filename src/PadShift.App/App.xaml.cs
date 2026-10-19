using System.IO;
using PadShift.Output;
using System.Threading;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using PadShift.Api;
using PadShift.App.Infrastructure;
using PadShift.App.Services;
using PadShift.App.ViewModels;
using PadShift.App.Views;
using PadShift.Core.Host;
using PadShift.Engine.Hosting;

namespace PadShift.App;

/// <summary>
/// Single desktop process: it discovers controllers, runs the remapping engine, hosts the HTTP API
/// in-process and keeps a tray icon. No Windows service, no second process.
/// </summary>
public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\PadShift.App.SingleInstance";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private DeviceSession? _session;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private OverlayService? _overlay;
    private RadialMenuService? _radial;
    private ConfirmationService? _confirmation;
    private WebApplication? _api;
    private GlobalHotkey? _stopMacroHotkey;
    private GlobalHotkey? _mappingsHotkey;
    private bool _exiting;

    /// <summary>支持的语言代码（设置文件里存储的值）。</summary>
    public static readonly string[] Languages = ["zh-CN", "en-US"];

    /// <summary>旧值兼容（"简体中文"）与非法值回退，一律归一为语言代码。</summary>
    public static string NormalizeLanguage(string? language) =>
        language is "en-US" or "en" or "English" ? "en-US" : "zh-CN";

    /// <summary>
    /// 热切换界面语言（"zh-CN" / "en-US"）：替换合并字典里的字符串资源，所有
    /// DynamicResource 引用立即更新；之后新产生的动态消息也会用新语言读取。
    /// </summary>
    public static void SwitchLanguage(string? language)
    {
        string code = NormalizeLanguage(language);
        var merged = Current.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            if (merged[i].Source?.OriginalString.Contains("Strings.", StringComparison.Ordinal) == true)
            {
                merged.RemoveAt(i);
            }
        }

        merged.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Localization/Strings.{code}.xaml"),
        });
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Troubleshooting mode: no effects anywhere in the shell and no click-through overlay.
        SafeGraphics.Enabled = SafeGraphics.IsRequested(e.Args);
        if (SafeGraphics.Enabled)
        {
            Console.Error.WriteLine("[PadShift] --safe-graphics: effects and overlay disabled.");
        }

        // Offscreen artwork/layout proof: render the shell to a PNG and exit. Never shows a window.
        if (RenderPreview.IsRequested(e.Args))
        {
            string? previewPath = RenderPreview.GetPath(e.Args);
            if (string.IsNullOrWhiteSpace(previewPath))
            {
                Console.Error.WriteLine("用法: PadShift.App --render-preview <png 路径> [--demo-sticks|--demo-sticks-center]");
                Shutdown(2);
                return;
            }

            Shutdown(RenderPreview.Run(previewPath, RenderPreview.GetStickDemo(e.Args), RenderPreview.GetOrbit(e.Args)));
            return;
        }

        if (!AcquireSingleInstance())
        {
            System.Windows.MessageBox.Show(
                Loc.T("app.running"),
                "PadShift",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            StartClient();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                Loc.T("app.startfail") + ex.Message,
                "PadShift",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Safety net for shutdown paths that bypass ExitApplication (logoff, unhandled exception).
        ReleaseRuntime();

        if (_ownsMutex && _instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (Exception)
            {
                // Not the owner any more.
            }
        }

        _instanceMutex?.Dispose();
        _instanceMutex = null;
        base.OnExit(e);

        // Shutdown() ends the dispatcher but only ends the process when every foreground thread is
        // gone; a leaked foreground worker (input poller, native driver pump) would otherwise keep
        // a windowless process alive forever. The exit code is already final here.
        Environment.Exit(e.ApplicationExitCode);
    }

    /// <summary>Called by the tray "退出" item; safe to call more than once.</summary>
    internal void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        if (!ConfirmExit())
        {
            return;
        }

        ExitApplicationConfirmed();
    }

    /// <summary>
    /// Asks before shutting down while remapping is active (releasing every key is visible to the
    /// game). Returns false when the user keeps PadShift running.
    /// </summary>
    internal bool ConfirmExit()
    {
        if (_exiting)
        {
            return true;
        }

        if (_session is not { RemapEnabled: true } || _confirmation is null)
        {
            return true;
        }

        return _confirmation.Confirm(
            ConfirmationKeys.ExitWhileRemapping,
            Loc.T("confirm.exit.title"),
            Loc.T("confirm.exit.msg"),
            Loc.T("confirm.exit.ok"));
    }

    /// <summary>Shutdown after the confirmation (or when there was nothing to confirm).</summary>
    internal void ExitApplicationConfirmed()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;

        // Close the window first so the user immediately sees the exit taking effect, then run the
        // runtime teardown on the pool with a hard budget. ReleaseRuntime waits on the API stop and
        // the session stop; an SSE client or a slow driver could park both forever, and doing that
        // on the UI thread after the tray icon is gone used to leave a windowless, trayless,
        // unkillable process behind (only the task manager could end it).
        _window?.CloseForExit();

        // NotifyIcon has creator-thread affinity: dispose it here, not from the pool task below.
        try
        {
            _stopMacroHotkey?.Dispose();
            _mappingsHotkey?.Dispose();
        }
        catch (Exception)
        {
        }

        _stopMacroHotkey = null;
        _mappingsHotkey = null;

        try
        {
            _tray?.Dispose();
        }
        catch (Exception)
        {
        }

        _tray = null;

        bool clean = false;
        try
        {
            clean = Task.Run(ReleaseRuntime).Wait(TimeSpan.FromSeconds(2.5));
        }
        catch (Exception)
        {
            // Teardown must never block the exit; the OS reclaims what is left.
        }

        if (!clean)
        {
            Console.Error.WriteLine("[PadShift] runtime teardown timed out; exiting anyway.");
        }

        Shutdown();
    }

    private void StartClient()
    {
        AppSettings settings = AppSettings.Load();
        SwitchLanguage(settings.Language);
        string profilesRoot = ResolveProfilesRoot(settings);

        // Keep the resolved root in the settings file so the preferences window shows the real
        // library location even when the user never picked one explicitly.
        settings.ProfilesRoot = profilesRoot;
        settings.Save();

        _confirmation = new ConfirmationService(
            settings,
            new DelegateConfirmationPrompt(request => ConfirmDialog.Prompt(_window, request)));

        var session = new DeviceSession(
            profilesRoot,
            settings.AutoSwitchEnabled,
            BuildSlotHotkeys(settings),
            settings.GyroBias,
            outputTuning: OutputTuning.FromPerformance(
                settings.VirtualPadPerformance,
                settings.UdpOutputEnabled,
                settings.UdpOutputPort) with { MaskPaddles = settings.MaskPaddleButtons });
        _session = session;

        var services = new ServiceContainer();
        services.RegisterInstance<IRemapHost>(session);
        services.RegisterInstance(session);
        services.RegisterInstance(settings);
        services.RegisterSingleton(_ => new ConfigLibraryViewModel(profilesRoot));
        services.RegisterSingleton(_ => new GamepadViewModel());
        services.RegisterSingleton(_ => new BindingEditorViewModel());
        services.RegisterSingleton(container => new PreferencesViewModel(container.Resolve<AppSettings>()));
        services.RegisterSingleton(container => new MainViewModel(
            container.Resolve<IRemapHost>(),
            container.Resolve<ConfigLibraryViewModel>(),
            container.Resolve<GamepadViewModel>(),
            container.Resolve<BindingEditorViewModel>(),
            container.Resolve<PreferencesViewModel>(),
            _confirmation));

        _viewModel = services.Resolve<MainViewModel>();

        session.Start();
        if (!string.IsNullOrWhiteSpace(session.Banner))
        {
            _viewModel.SetBanner(session.Banner);
        }

        _window = new MainWindow
        {
            DataContext = _viewModel,
            CloseToTray = settings.CloseToTray,
            MinimizeToTray = settings.MinimizeToTray,
            Session = session,
        };
        MainWindow = _window;

        _viewModel.PreferencesRequested += (_, _) => _window?.ShowPreferences();

        // "启动时最小化到托盘": keep the process (and remapping) alive without showing the shell.
        if (!settings.MinimizeToTrayOnStart)
        {
            _window.Show();
        }

        // Overlay first: the radial service reports its hotkey/trigger failures through a toast.
        // --safe-graphics also disables the overlay (it is a topmost layered window over the game).
        try
        {
            if (!SafeGraphics.Enabled)
            {
                _overlay = new OverlayService(_viewModel, session, settings);
                _overlay.Start();
            }
        }
        catch (Exception ex)
        {
            AppendBanner("叠加层初始化失败（映射不受影响）: " + ex.Message);
            _overlay = null;
        }

        try
        {
            var radialDispatcher = new RadialMenuDispatcher(new MainRadialHost(_viewModel));
            _radial = new RadialMenuService(session, settings, radialDispatcher);
            _radial.StatusChanged += message => _overlay?.ShowToast(message);
            _radial.PreferencesRequested += () => _window?.ShowPreferences();
            _radial.Start();
        }
        catch (Exception ex)
        {
            AppendBanner("径向菜单初始化失败（映射不受影响）: " + ex.Message);
            _radial = null;
        }

        StartApi();
        ApplyStopMacroHotkey(settings);
        ApplyMappingsHotkey(settings);
        _tray = new TrayIcon(_viewModel, ToggleWindow, ExitApplication);
    }

    /// <summary>注册/重挂停宏全局热键；空字符串禁用，注册失败只在横幅提示。</summary>
    private void ApplyStopMacroHotkey(AppSettings settings)
    {
        _stopMacroHotkey?.Dispose();
        _stopMacroHotkey = null;

        string hotkey = (settings.StopMacroHotkey ?? "").Trim();
        if (hotkey.Length == 0)
        {
            return;
        }

        var listener = new GlobalHotkey(0x4C58, () =>
        {
            Services.RadialMacroPlayer.RequestStop();
            _viewModel?.SetInlineMessage(Infrastructure.Loc.T("msg.macrostopped"));
        });
        if (listener.TryRegister(hotkey))
        {
            _stopMacroHotkey = listener;
        }
        else
        {
            listener.Dispose();
            AppendBanner("停宏热键注册失败（可能与其它程序冲突）: " + hotkey);
        }
    }

    /// <summary>注册/重挂映射悬浮卡热键；空字符串禁用。</summary>
    private void ApplyMappingsHotkey(AppSettings settings)
    {
        _mappingsHotkey?.Dispose();
        _mappingsHotkey = null;

        string hotkey = (settings.MappingsOverlayHotkey ?? "").Trim();
        if (hotkey.Length == 0 || _overlay is null)
        {
            return;
        }

        var listener = new GlobalHotkey(0x4C59, ToggleMappingsOverlay);
        if (listener.TryRegister(hotkey))
        {
            _mappingsHotkey = listener;
        }
        else
        {
            listener.Dispose();
            AppendBanner("映射悬浮卡热键注册失败（可能与其它程序冲突）: " + hotkey);
        }
    }

    /// <summary>热键回调：读当前配置生成绑定清单并切换悬浮卡显隐。</summary>
    private void ToggleMappingsOverlay()
    {
        if (_overlay is null)
        {
            return;
        }

        string? title = null;
        string? text = null;
        string? path = _viewModel?.BindingEditor.ProfilePath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                title = Path.GetFileName(path);
                text = Infrastructure.BindingListExporter.Build(File.ReadAllText(path), title);
            }
            catch (Exception)
            {
                // 读失败按"无内容"处理，卡内显示占位。
            }
        }

        if (!_overlay.ToggleMappings(title, text))
        {
            _viewModel?.SetInlineMessage(Infrastructure.Loc.T("msg.mappingsNoProfile"));
        }
    }

    /// <summary>Called when the preferences dialog was saved: live-apply overlay and hotkey changes.</summary>
    internal void OnPreferencesSaved()
    {
        SwitchLanguage(_viewModel?.Preferences.Settings.Language);
        if (_viewModel?.Preferences.Settings is { } saved)
        {
            ApplyStopMacroHotkey(saved);
            ApplyMappingsHotkey(saved);
        }

        _tray?.RefreshTexts();
        _viewModel?.Gamepad.RefreshLanguage();
        _overlay?.ReloadSettings();
        _radial?.ReloadHotkey();

        // 「使用 3D 手柄视图」 takes effect immediately, without restarting the app.
        _window?.ApplyGamepadView();
    }

    // ------------------------------------------------------------------ in-process API

    private void StartApi()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            WebApplication api = ApiHost.Create(_session, new ApiOptions());
            _api = api;

            _ = Task.Run(async () =>
            {
                try
                {
                    await api.StartAsync().ConfigureAwait(false);
                    string url = api.Urls.FirstOrDefault() ?? string.Empty;
                    OnUi(() => _viewModel?.SetApiStatus(
                        url.Length == 0 ? "本地 API: 已启动" : "本地 API: " + url));
                }
                catch (Exception ex)
                {
                    OnUi(() =>
                    {
                        _viewModel?.SetApiStatus("本地 API: 启动失败");
                        AppendBanner("本地 HTTP API 启动失败（端口可能被占用）: " + ex.Message);
                    });
                }
            });
        }
        catch (Exception ex)
        {
            AppendBanner("本地 HTTP API 创建失败: " + ex.Message);
        }
    }

    /// <summary>Marshals an update to the UI thread, ignoring the window that is already gone at exit.</summary>
    private void OnUi(Action action)
    {
        try
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.Invoke(action);
            }
        }
        catch (Exception)
        {
            // The dispatcher may be shutting down while the API finishes starting/stopping.
        }
    }

    private void AppendBanner(string text)
    {
        if (_viewModel is null)
        {
            return;
        }

        string? existing = _viewModel.Banner;
        _viewModel.SetBanner(string.IsNullOrWhiteSpace(existing) ? text : existing + "  " + text);
    }

    // ------------------------------------------------------------------ tray / window

    private void ToggleWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.IsVisible && _window.WindowState != WindowState.Minimized)
        {
            _window.Hide();
            return;
        }

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    // ------------------------------------------------------------------ shutdown

    private void ReleaseRuntime()
    {
        try
        {
            _tray?.Dispose();
        }
        catch (Exception)
        {
            // Tray teardown is cosmetic.
        }

        _tray = null;

        try
        {
            _radial?.Dispose();
        }
        catch (Exception)
        {
        }

        _radial = null;

        try
        {
            _overlay?.Dispose();
        }
        catch (Exception)
        {
        }

        _overlay = null;

        try
        {
            _viewModel?.Dispose();
        }
        catch (Exception)
        {
        }

        _viewModel = null;

        WebApplication? api = _api;
        _api = null;
        if (api is not null)
        {
            try
            {
                api.StopAsync().GetAwaiter().GetResult();
                api.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // The API socket may already be gone.
            }
        }

        DeviceSession? session = _session;
        _session = null;
        if (session is not null)
        {
            try
            {
                // Stop() releases every key/button the sinks believe are held.
                session.Stop();
            }
            catch (Exception)
            {
            }

            try
            {
                session.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }

    // ------------------------------------------------------------------ environment

    /// <summary>
    /// Maps the saved slot rows to the engine's public <see cref="SlotHotkey"/> DTO. Rows without a
    /// combination stay unbound (the engine steals no buttons), and an invalid row is skipped
    /// instead of failing startup.
    /// </summary>
    private static IReadOnlyList<SlotHotkey> BuildSlotHotkeys(AppSettings settings)
    {
        var hotkeys = new List<SlotHotkey>();
        foreach (SlotHotkeySetting slot in settings.Slots)
        {
            if (slot.Buttons is not { Length: > 0 })
            {
                continue;
            }

            try
            {
                hotkeys.Add(new SlotHotkey(slot.Slot, slot.Buttons, slot.HoldDurationMs));
            }
            catch (Exception)
            {
                // Out-of-range persisted values: run without that hotkey rather than refusing to start.
            }
        }

        return hotkeys;
    }

    /// <summary>
    /// The configured profiles folder when the user picked one, otherwise
    /// <c>%USERPROFILE%\Documents\Lynx\Profiles</c> (<c>..\Presets</c> is the preset folder).
    /// </summary>
    private static string ResolveProfilesRoot(AppSettings settings)
    {
        string configured = (settings.ProfilesRoot ?? "").Trim();
        if (configured.Length > 0)
        {
            try
            {
                Directory.CreateDirectory(configured);
                return configured;
            }
            catch (Exception)
            {
                // Unusable path (read-only drive, removed network share): fall back to the default.
            }
        }

        try
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents))
            {
                documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            string profiles = Path.Combine(documents, "Lynx", "Profiles");
            Directory.CreateDirectory(profiles);
            return profiles;
        }
        catch (Exception)
        {
            string fallback = Path.Combine(AppContext.BaseDirectory, "Profiles");
            try
            {
                Directory.CreateDirectory(fallback);
            }
            catch (Exception)
            {
                // Read-only install folder: the library will simply report that it is missing.
            }

            return fallback;
        }
    }

    private bool AcquireSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
            _ownsMutex = created;
            if (!created)
            {
                _instanceMutex.Dispose();
                _instanceMutex = null;
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            // A broken mutex must not prevent the app from starting.
            return true;
        }
    }
}
