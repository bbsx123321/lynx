namespace PadShift.App.Infrastructure;

/// <summary>One downloadable controller model the user can pick in the preferences.</summary>
/// <param name="Id">Stable identifier persisted in settings.json (<c>gamepadModelId</c>).</param>
/// <param name="DisplayName">Caption shown in the preferences combo box.</param>
/// <param name="Folder">Directory under <c>assets/3d</c>.</param>
/// <param name="ObjFileName">Import-ready OBJ inside <paramref name="Folder"/>.</param>
public readonly record struct GamepadModelInfo(string Id, string DisplayName, string Folder, string ObjFileName);

/// <summary>
/// The bundled controller models. Every entry keeps its provenance and licence in a
/// <c>LICENSE.txt</c> next to its OBJ; the first entry is the default for fresh installs.
/// </summary>
public static class GamepadModelCatalog
{
    /// <summary>Id used when the picked model is missing from the catalog or fails to load.</summary>
    public const string ProceduralId = "procedural";

    public static readonly GamepadModelInfo DualSense = new(
        "dualsense",
        "Sony DualSense（PS5）",
        GamepadModelLoader.AssetFolder,
        GamepadModelLoader.ObjFileName);

    public static readonly GamepadModelInfo Xbox360 = new(
        "xbox360",
        "Microsoft Xbox 360",
        "xbox360-mit",
        "xbox360.obj");

    public static readonly GamepadModelInfo SwitchPro = new(
        "switchpro",
        "Nintendo Switch Pro",
        "switchpro-mit",
        "switchpro.obj");

    /// <summary>Pickable models in display order.</summary>
    public static readonly IReadOnlyList<GamepadModelInfo> Models =
        [DualSense, Xbox360, SwitchPro];

    /// <summary>The model used when settings carry no valid id.</summary>
    public static string DefaultId => DualSense.Id;

    /// <summary>Resolve an id to its catalog entry; unknown or null ids fall back to the default.</summary>
    public static GamepadModelInfo Find(string? id)
    {
        foreach (GamepadModelInfo model in Models)
        {
            if (string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }
        }

        return DualSense;
    }
}
