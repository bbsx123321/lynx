using System.Windows;
using System.Windows.Media;

namespace PadShift.App.Infrastructure;

/// <summary>
/// The <c>--safe-graphics</c> escape hatch. It turns the shell into a flat, effect-free window for
/// troubleshooting machines where a DropShadowEffect/BlurEffect forces software rendering (RDP,
/// virtual machines, broken GPU drivers). The mapping engine is not affected, and the click-through
/// overlay is not created at all.
/// </summary>
public static class SafeGraphics
{
    /// <summary>Command-line switch that enables the mode.</summary>
    public const string Switch = "--safe-graphics";

    /// <summary>True when the process was started with <see cref="Switch"/>.</summary>
    public static bool Enabled { get; set; }

    /// <summary>True when <paramref name="args"/> contains the switch (case-insensitive).</summary>
    public static bool IsRequested(string[]? args)
    {
        if (args is null)
        {
            return false;
        }

        foreach (string arg in args)
        {
            if (string.Equals(arg, Switch, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Clears <see cref="UIElement.Effect"/> on every realized visual. A local value wins over the
    /// style/template data-trigger setters, so a later press cannot re-introduce a glow effect.
    /// </summary>
    public static void DisableEffects(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (root is UIElement element && element.Effect is not null)
        {
            element.Effect = null;
        }

        if (root is not Visual && root is not System.Windows.Media.Media3D.Visual3D)
        {
            return;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DisableEffects(VisualTreeHelper.GetChild(root, index));
        }
    }
}
