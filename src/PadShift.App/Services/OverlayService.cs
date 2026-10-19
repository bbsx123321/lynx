using System.ComponentModel;
using PadShift.App.Infrastructure;
using PadShift.App.ViewModels;
using PadShift.App.Views;
using PadShift.Core.Host;

namespace PadShift.App.Services;

/// <summary>
/// Owns the single <see cref="OverlayWindow"/> and turns mapping-state changes into toasts:
/// enable/disable, layer change, slot change and config applied. Settings are read live from
/// <see cref="AppSettings.Overlay"/>, so saving the preferences window takes effect immediately.
/// </summary>
public sealed class OverlayService : IDisposable
{
    private readonly OverlayWindow _window;
    private readonly Views.MappingsOverlayWindow? _mappings;
    private bool _mappingsVisible;
    private readonly MainViewModel _viewModel;
    private readonly IRemapHost _host;
    private readonly AppSettings _settings;
    private bool _started;
    private bool _disposed;
    private string? _lastStateToast;

    public OverlayService(MainViewModel viewModel, IRemapHost host, AppSettings settings)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _window = new OverlayWindow();
        try
        {
            _mappings = new Views.MappingsOverlayWindow();
        }
        catch (Exception)
        {
            // 映射卡纯属辅助显示；创建失败不影响 toast/准星。
            _mappings = null;
        }
    }

    public OverlayToastQueue ToastQueue => _window.ToastQueue;

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _host.Event += OnHostEvent;
        _window.ApplySettings(_settings.Overlay);
    }

    /// <summary>Re-reads <see cref="AppSettings.Overlay"/> after the preferences window saved.</summary>
    public void ReloadSettings()
    {
        if (!_disposed)
        {
            _window.ApplySettings(_settings.Overlay);
        }
    }

    /// <summary>切换映射悬浮卡；text 为清单内容（无配置时传 null 显示占位）。返回切换后的可见性。</summary>
    public bool ToggleMappings(string? title, string? text)
    {
        if (_mappings is null)
        {
            return false;
        }

        _mappingsVisible = !_mappingsVisible;
        if (_mappingsVisible)
        {
            _mappings.SetText(title, text);
            _mappings.Show();
        }
        else
        {
            _mappings.Hide();
        }

        return _mappingsVisible;
    }

    public void ShowToast(string? message)
    {
        if (!_disposed)
        {
            _window.ShowToast(message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _host.Event -= OnHostEvent;
        
        try
        {
            _mappings?.Close();
        }
        catch (Exception)
        {
        }

        try

        {

            _window.Close();
        }
        catch (Exception)
        {
            // Shutdown path.
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PropertyName) || !_settings.Overlay.ShowToast)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(MainViewModel.RemapEnabled):
                ShowStateToast(Loc.T(_viewModel.RemapEnabled ? "toast.remapon" : "toast.remapoff"));
                break;

            case nameof(MainViewModel.SelectedSlot):
                if (_viewModel.SelectedSlot is { } slot)
                {
                    ShowStateToast(Loc.F("toast.slot", slot.Index + 1));
                }

                break;

            case nameof(MainViewModel.CurrentLayerText):
                ShowStateToast(_viewModel.CurrentLayerText);
                break;
        }
    }

    /// <summary>
    /// Toasts belong to state changes only: the live pad poll never reaches this service, and an
    /// event that repeats the same message must not create another toast.
    /// </summary>
    private void ShowStateToast(string message)
    {
        if (string.Equals(_lastStateToast, message, StringComparison.Ordinal))
        {
            return;
        }

        _lastStateToast = message;
        ShowToast(message);
    }

    private void OnHostEvent(object? sender, HostEvent hostEvent)
    {
        if (!_settings.Overlay.ShowToast)
        {
            return;
        }

        switch (hostEvent)
        {
            case ConfigAppliedEvent applied:
                ShowToast(Loc.F("toast.applied", System.IO.Path.GetFileNameWithoutExtension(applied.ConfigPath)));
                break;

            case SlotClearedEvent cleared:
                ShowToast(Loc.F("toast.cleared", cleared.Slot + 1));
                break;
        }
    }
}
