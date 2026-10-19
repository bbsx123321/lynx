using System.Drawing;
using System.Windows.Forms;
using PadShift.App.Infrastructure;
using PadShift.App.ViewModels;

namespace PadShift.App.Services;

/// <summary>
/// Tray presence: show/hide the window, toggle remapping, and exit (releasing every held key).
/// Uses <see cref="NotifyIcon"/> because a WPF tray icon would need an extra package.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly MainViewModel _viewModel;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly Icon? _ownedIcon;
    private readonly TaskbarCreatedListener _taskbarListener;
    private bool _disposed;
    private string? _lastToggleText;
    private string? _lastIconText;

    public TrayIcon(MainViewModel viewModel, Action toggleWindow, Action exit)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(toggleWindow);
        ArgumentNullException.ThrowIfNull(exit);

        _viewModel = viewModel;

        var menu = new ContextMenuStrip();
        _showItem = new ToolStripMenuItem(Loc.T("tray.show"), null, (_, _) => toggleWindow());
        menu.Items.Add(_showItem);
        _toggleItem = new ToolStripMenuItem(Loc.T("tray.enable"), null, (_, _) => _viewModel.ToggleRemapCommand.Execute(null));
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        _exitItem = new ToolStripMenuItem(Loc.T("tray.exit"), null, (_, _) => exit());
        menu.Items.Add(_exitItem);

        Icon? loaded = AppIcon.LoadTrayIcon();
        _ownedIcon = loaded;
        _icon = new NotifyIcon
        {
            Icon = loaded ?? SystemIcons.Application,
            Text = "PadShift",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => toggleWindow();

        // explorer.exe restarts (crash, logoff scripts) wipe every tray icon and NotifyIcon does not
        // listen for the shell's TaskbarCreated broadcast; without this the window can be hidden in
        // the tray with no icon left to bring it back or exit the app.
        _taskbarListener = new TaskbarCreatedListener();
        _taskbarListener.TaskbarCreated += OnTaskbarCreated;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateText();
    }

    private void OnTaskbarCreated()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // Re-adding requires one invisible pass; setting Visible while already true is a no-op.
            _icon.Visible = false;
            _icon.Visible = true;
        }
        catch (Exception)
        {
            // The shell may not be ready yet; the next restart gets another chance.
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.RemapEnabled) or nameof(MainViewModel.StatusText))
        {
            UpdateText();
        }
    }

    /// <summary>语言切换后由 App 调用：重读菜单与提示文本。</summary>
    public void RefreshTexts()
    {
        _showItem.Text = Loc.T("tray.show");
        _exitItem.Text = Loc.T("tray.exit");
        _lastToggleText = null;
        _lastIconText = null;
        UpdateText();
    }

    private void UpdateText()
    {
        // NotifyIcon.Text is a Shell_NotifyIcon modify message; never touch it when nothing changed.
        string toggle = Loc.T(_viewModel.RemapEnabled ? "tray.disable" : "tray.enable");
        if (!string.Equals(_lastToggleText, toggle, StringComparison.Ordinal))
        {
            _lastToggleText = toggle;
            _toggleItem.Text = toggle;
        }

        string text = Loc.T(_viewModel.RemapEnabled ? "tray.on" : "tray.off");
        // NotifyIcon.Text is length-limited by the shell; keep it short.
        text = text.Length <= 63 ? text : text[..63];
        if (!string.Equals(_lastIconText, text, StringComparison.Ordinal))
        {
            _lastIconText = text;
            _icon.Text = text;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _taskbarListener.TaskbarCreated -= OnTaskbarCreated;
        _taskbarListener.Dispose();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch (Exception)
        {
            // The shell may already be gone during logoff.
        }

        try
        {
            _ownedIcon?.Dispose();
        }
        catch (Exception)
        {
            // The icon handle may already be released with the notify icon.
        }
    }

    /// <summary>
    /// Hidden top-level window that listens for the shell's registered <c>TaskbarCreated</c>
    /// broadcast (sent when explorer.exe recreates the taskbar) so the icon can be re-added.
    /// Message-only windows do not receive broadcasts, so this must be a real hidden window.
    /// </summary>
    private sealed class TaskbarCreatedListener : NativeWindow, IDisposable
    {
        private static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        public event Action? TaskbarCreated;

        public TaskbarCreatedListener()
        {
            var parameters = new CreateParams
            {
                Caption = nameof(TaskbarCreatedListener),
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
                Style = unchecked((int)0x80000000), // WS_POPUP: never becomes visible
            };

            CreateHandle(parameters);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == TaskbarCreatedMessage)
            {
                TaskbarCreated?.Invoke();
            }

            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string name);
    }
}
