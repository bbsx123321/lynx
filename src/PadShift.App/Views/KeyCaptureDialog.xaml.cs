using System.Windows;
using System.Windows.Input;
using PadShift.App.Infrastructure;

namespace PadShift.App.Views;

/// <summary>
/// Modal "press any key" dialog. Uses plain WPF keyboard input (no hooks, no driver, no elevation),
/// ignores modifier-only presses and treats Esc as cancel.
/// </summary>
public partial class KeyCaptureDialog : Window
{
    public KeyCaptureDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            Keyboard.Focus(this);
        };
    }

    /// <summary>The captured key, or null when the dialog was cancelled.</summary>
    public Key? CapturedKey { get; private set; }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;

        if (key == Key.Escape)
        {
            CapturedKey = null;
            DialogResult = false;
            return;
        }

        // Modifier-only presses never become a binding: the user still has to press the real key.
        if (key == Key.None || DikMap.IsModifierOnly(key))
        {
            return;
        }

        CapturedKey = key;
        DialogResult = true;
    }
}
