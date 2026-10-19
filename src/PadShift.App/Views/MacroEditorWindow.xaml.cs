using System.Windows;
using PadShift.App.Infrastructure;
using PadShift.App.ViewModels;

namespace PadShift.App.Views;

/// <summary>
/// Modal macro sequence editor. Only the "capture key" button needs view code: it reuses
/// <see cref="KeyCaptureDialog"/> and hands the DIK scan code to the step, so the actual editing
/// logic stays in <see cref="MacroEditorViewModel"/>.
/// </summary>
public partial class MacroEditorWindow : Window
{
    public MacroEditorWindow() => InitializeComponent();

    private void OnCaptureStepKey(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MacroStepViewModel step })
        {
            return;
        }

        var dialog = new KeyCaptureDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.CapturedKey is not { } key)
        {
            return;
        }

        if (!DikMap.TryFromKey(key, out int dik, out string name))
        {
            MessageBox.Show(this, "该按键没有可用的扫描码，请换一个键。", "宏编辑器",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        step.SetKeyboardKey(dik, name);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (DataContext is MacroEditorViewModel editor && !editor.CanSave)
        {
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
