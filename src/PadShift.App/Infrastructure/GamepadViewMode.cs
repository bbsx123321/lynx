using System.Windows;
using System.Windows.Media.Media3D;

namespace PadShift.App.Infrastructure;

/// <summary>Which renderer draws the main-window gamepad.</summary>
public enum GamepadViewKind
{
    /// <summary>The original 2D vector artwork. Always available, used as the fallback.</summary>
    Vector2D,

    /// <summary>The procedural 3D controller (<c>Viewport3D</c>).</summary>
    Model3D,
}

/// <summary>
/// Chooses between the 3D controller and the 2D vector pad. Kept free of WPF UI state so the
/// fallback rules (including <c>--safe-graphics</c>) are covered by the head-less test suite.
/// </summary>
public static class GamepadViewMode
{
    /// <summary>
    /// Returns <see cref="GamepadViewKind.Model3D"/> only when the user wants 3D, the process is not
    /// in safe-graphics mode and the scene was built successfully.
    /// </summary>
    public static GamepadViewKind Resolve(bool prefer3D, bool safeGraphics, bool modelAvailable = true)
    {
        if (!prefer3D || safeGraphics || !modelAvailable)
        {
            return GamepadViewKind.Vector2D;
        }

        return GamepadViewKind.Model3D;
    }
}
