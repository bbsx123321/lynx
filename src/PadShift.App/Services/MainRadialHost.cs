using PadShift.App.ViewModels;

namespace PadShift.App.Services;

/// <summary>
/// Bridges <see cref="IRadialMenuHost"/> to <see cref="MainViewModel"/>. Keeping the adapter here
/// means the ViewModel has no dependency on the radial window/service and stays head-less testable.
/// </summary>
public sealed class MainRadialHost : IRadialMenuHost
{
    private readonly MainViewModel _viewModel;

    public MainRadialHost(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    public bool SwitchLayer(int layer) => _viewModel.SwitchLayerFromRadial(layer);

    public bool SwitchSlot(int slot) => _viewModel.SwitchSlotFromRadial(slot);

    public bool SetRemapEnabled(bool enabled) => _viewModel.SetRemapEnabledFromRadial(enabled);

    public bool ToggleRemap() => _viewModel.ToggleRemapFromRadial();

    public bool RunMacro(string macro) => _viewModel.RunMacroFromRadial(macro);

    public bool OpenPreferences()
    {
        _viewModel.RequestPreferences();
        return true;
    }
}
