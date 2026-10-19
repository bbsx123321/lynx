using System.IO;
using System.Windows;
using PadShift.App.ViewModels;

namespace PadShift.App.Views;

/// <summary>
/// Six-tab preferences window (常规 / 手柄 Slots / 配置 / 径向菜单 / 叠加层 / 确认). Everything it
/// edits lives in <see cref="PreferencesViewModel"/>; the window only adds the folder picker and the
/// dialog result.
/// </summary>
public partial class PreferencesWindow : Window
{
    public PreferencesWindow() => InitializeComponent();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is PreferencesViewModel viewModel && !viewModel.Save())
        {
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnBrowseProfilesRoot(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PreferencesViewModel viewModel)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 Profile 根目录",
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(viewModel.ProfilesRoot) && Directory.Exists(viewModel.ProfilesRoot))
        {
            dialog.InitialDirectory = viewModel.ProfilesRoot;
        }

        if (dialog.ShowDialog(this) == true)
        {
            viewModel.ProfilesRoot = dialog.FolderName;
        }
    }
}
