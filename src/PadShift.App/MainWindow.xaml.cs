using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PadShift.App.Infrastructure;
using PadShift.App.Services;
using PadShift.App.ViewModels;
using PadShift.App.Views;

namespace PadShift.App;

/// <summary>
/// Main window shell. It only contains view concerns (closing to tray, key capture, double-click to
/// apply, opening the macro/preferences dialogs); every decision lives in <see cref="MainViewModel"/>
/// so it stays testable.
/// </summary>
public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _model3DAvailable = true;

    public MainWindow()
    {
        InitializeComponent();
        Icon = AppIcon.LoadWindowIcon();
        IsVisibleChanged += OnWindowIsVisibleChanged;
        DataContextChanged += (_, _) => ApplyGamepadView();
        Loaded += (_, _) =>
        {
            if (SafeGraphics.Enabled)
            {
                // Local values beat the template/data-trigger effect setters, so one pass is enough.
                SafeGraphics.DisableEffects(this);
            }

            ApplyGamepadView();
        };
    }

    /// <summary>Set from <c>AppSettings.CloseToTray</c> before the window is shown.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Set from <c>AppSettings.MinimizeToTray</c>：最小化时隐藏到托盘而不是留在任务栏。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// Drives the 20 Hz gamepad poll: it only runs while the window is visible. Hiding to the tray
    /// stops the timer, showing it again resumes immediately.
    /// </summary>
    private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            ViewModel?.Gamepad.StartLive();
        }
        else
        {
            ViewModel?.Gamepad.StopLive();
        }
    }

    /// <summary>最小化 → 收进托盘（否则只是普通的最小化）。</summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized || !MinimizeToTray)
        {
            return;
        }

        // 先恢复成 Normal，否则下次从托盘点出来仍是 Minimized，窗口不会显示。
        WindowState = WindowState.Normal;
        Hide();
    }

    /// <summary>The live device session; used by the gyro calibration wizard. Null in tests.</summary>
    public DeviceSession? Session { get; set; }

    /// <summary>
    /// Shows the 3D controller when the preference is on, the scene was built and
    /// <c>--safe-graphics</c> is absent; otherwise the 2D vector pad stays visible. Called on startup
    /// and again whenever the preferences dialog is saved.
    /// </summary>
    public void ApplyGamepadView()
    {
        _model3DAvailable &= Pad3DView.SceneReady;

        bool prefer3D = ViewModel?.Preferences.Settings.Use3DGamepad ?? true;
        GamepadViewKind kind = GamepadViewMode.Resolve(prefer3D, SafeGraphics.Enabled, _model3DAvailable);

        // 「3D 手柄外形」：与当前场景不同的 id 触发整景重建（保存偏好后立即生效）。
        // 加载失败时 SwitchModel 回退程序化手柄并返回 false，下一次 Apply 再重试。
        string modelId = ViewModel?.Preferences.Settings.GamepadModelId ?? GamepadModelCatalog.DefaultId;
        if (kind == GamepadViewKind.Model3D && !Pad3DView.SwitchModel(modelId))
        {
            _model3DAvailable = false;
            kind = GamepadViewMode.Resolve(prefer3D, SafeGraphics.Enabled, _model3DAvailable);
        }

        Pad3DView.Visibility = kind == GamepadViewKind.Model3D ? Visibility.Visible : Visibility.Collapsed;
        Pad2DView.Visibility = kind == GamepadViewKind.Model3D ? Visibility.Collapsed : Visibility.Visible;
        PadModeTag.Text = kind == GamepadViewKind.Model3D
            ? "3D"
            : SafeGraphics.Enabled ? Loc.T("msg.padmode2dsafe") : "2D";
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>Allows the window to really close during application shutdown.</summary>
    public void CloseForExit()
    {
        ViewModel?.Gamepad.StopLive();
        _allowClose = true;
        Close();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        if (!CloseToTray)
        {
            // Closing the window means "exit" when the tray option is off. Ask first (mapping may be
            // running), then defer the shutdown so the close completes before teardown.
            if (Application.Current is App app)
            {
                if (!app.ConfirmExit())
                {
                    e.Cancel = true;
                    return;
                }

                e.Cancel = true;
                Dispatcher.BeginInvoke(new Action(app.ExitApplicationConfirmed));
                return;
            }

            _allowClose = true;
            return;
        }

        // Closing the window keeps the process (and remapping) alive in the tray.
        e.Cancel = true;
        Hide();
    }

    private void OnCaptureKey(object sender, RoutedEventArgs e)
    {
        MainViewModel? viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        var dialog = new KeyCaptureDialog { Owner = this };
        bool? result = dialog.ShowDialog();
        if (result != true || dialog.CapturedKey is not { } key)
        {
            return;
        }

        if (!DikMap.TryFromKey(key, out int dik, out string name))
        {
            viewModel.SetInlineMessage(Loc.T("msg.keynoscancode"));
            return;
        }

        viewModel.BindingEditor.SetKeyboardTarget(dik, name);
        viewModel.SetInlineMessage(Loc.F("msg.keycaptured", name, dik));
    }

    // ------------------------------------------------------------------ macro / preferences

    private void OnEditMacro(object sender, RoutedEventArgs e)
    {
        MainViewModel? viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        MacroEditorViewModel editor = viewModel.BindingEditor.BeginMacroEditor();
        var window = new MacroEditorWindow { Owner = this, DataContext = editor };
        if (window.ShowDialog() != true)
        {
            return;
        }

        viewModel.BindingEditor.ApplyMacroEditor();
        viewModel.SetInlineMessage(viewModel.BindingEditor.Message);
    }

    private void OnPreferences(object sender, RoutedEventArgs e) => ShowPreferences();

    /// <summary>Opens the preferences dialog (also invoked by the radial menu's "打开配置").</summary>
    public void ShowPreferences()
    {
        MainViewModel? viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        if (!IsVisible)
        {
            // The radial menu can request preferences while the shell is hidden in the tray.
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        // Drop edits that were cancelled the last time the dialog was open.
        viewModel.Preferences.Reload();
        var window = new PreferencesWindow { Owner = this, DataContext = viewModel.Preferences };
        bool? saved = window.ShowDialog();
        viewModel.SetInlineMessage(saved == true ? viewModel.Preferences.Message : Loc.T("msg.prefunsaved"));
        if (saved == true && Application.Current is App app)
        {
            app.OnPreferencesSaved();
        }
    }

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        ViewModel?.DeleteCurrentProfile();
    }

    private void OnGyroCalibration(object sender, RoutedEventArgs e)
    {
        MainViewModel? viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        var wizard = new GyroCalibrationViewModel(
            viewModel.Host,
            Session,
            viewModel.Preferences.Settings);
        var window = new GyroCalibrationWindow(wizard, viewModel.BindingEditor.ProfilePath) { Owner = this };
        window.ShowDialog();
    }

    // ------------------------------------------------------------------ import / export / presets

    private void OnImportConfig(object sender, RoutedEventArgs e)
    {
        MainViewModel? viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("dlg.importtitle"),
            Filter = "映射配置 (*.rewasd)|*.rewasd|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            viewModel.ImportConfig(dialog.FileName);
        }
    }

    private void OnExportConfig(object sender, RoutedEventArgs e)
    {
        MainViewModel? viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        string suggested = viewModel.BindingEditor.ProfilePath is null
            ? "Config.rewasd"
            : System.IO.Path.GetFileName(viewModel.BindingEditor.ProfilePath);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("dlg.exporttitle"),
            Filter = "映射配置 (*.rewasd)|*.rewasd",
            DefaultExt = ".rewasd",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = suggested,
        };

        if (dialog.ShowDialog(this) == true)
        {
            viewModel.ExportConfig(dialog.FileName);
        }
    }

    /// <summary>导出当前配置的人类可读映射清单（纯文本），打印或留档用。</summary>
    private void OnExportBindingList(object sender, RoutedEventArgs e)
    {
        MainViewModel? viewModel = ViewModel;
        string? profilePath = viewModel?.BindingEditor.ProfilePath;
        if (string.IsNullOrEmpty(profilePath) || !System.IO.File.Exists(profilePath))
        {
            viewModel?.SetInlineMessage(Loc.T("m.loadfirstExport2"));
            return;
        }

        string suggested = System.IO.Path.GetFileNameWithoutExtension(profilePath) + "-bindings.txt";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("menu.exportlist"),
            Filter = "Text (*.txt)|*.txt",
            DefaultExt = ".txt",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = suggested,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            string text = BindingListExporter.Build(
                System.IO.File.ReadAllText(profilePath),
                System.IO.Path.GetFileName(profilePath));
            System.IO.File.WriteAllText(dialog.FileName, text, System.Text.Encoding.UTF8);
            viewModel?.SetInlineMessage(Loc.F("m.exportedF", dialog.FileName));
        }
        catch (Exception ex)
        {
            viewModel?.SetInlineMessage(Loc.T("m.exportfail") + ex.Message);
        }
    }

    private void OnSaveAsPreset(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.SaveCurrentAsPreset(viewModel.BindingEditor.ProfileAppName);
    }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox listBox || listBox.SelectedItem is not ProfileItem item)
        {
            return;
        }

        // Single click previews the config in the binding panel; double click also applies it.
        ViewModel?.PreviewProfile(item);
    }

    private async void OnProfileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox || listBox.SelectedItem is not ProfileItem item)
        {
            return;
        }

        MainViewModel? viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        bool applied = await viewModel.ApplyProfileAsync(item);
        if (applied)
        {
            viewModel.SetInlineMessage(Loc.F("msg.applied", item.Name));
        }

        e.Handled = true;
    }
}
