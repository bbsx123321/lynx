using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using PadShift.App.ViewModels;

namespace PadShift.App.Infrastructure;

/// <summary>The control a named mesh in a downloaded controller model belongs to.</summary>
public readonly struct GamepadControlMap
{
    public GamepadControlMap(string name, long? buttonId, GamepadPartKind kind, string? label)
    {
        Name = name;
        ButtonId = buttonId;
        Kind = kind;
        Label = label;
    }

    /// <summary>Canonical part name used by the view ("A", "StickLeft", "DpadUp", ...).</summary>
    public string Name { get; }

    /// <summary>the v2.6 wire protocol button id, or null for body/decoration geometry.</summary>
    public long? ButtonId { get; }

    public GamepadPartKind Kind { get; }

    /// <summary>Caption drawn next to the part; null for body/decoration geometry.</summary>
    public string? Label { get; }

    public bool IsControl => ButtonId is not null;
}

/// <summary>
/// Maps the object/group names found in real controller models (Blender objects, Handheld
/// Companion exports, OBJ groups) to the canonical controls this app animates.
/// </summary>
public static class GamepadPartMap
{
    private static readonly Dictionary<string, GamepadControlMap> Exact = new(StringComparer.Ordinal)
    {
        ["a"] = Control("A", GamepadButtons.A, GamepadPartKind.Button, "A"),
        ["b"] = Control("B", GamepadButtons.B, GamepadPartKind.Button, "B"),
        ["x"] = Control("X", GamepadButtons.X, GamepadPartKind.Button, "X"),
        ["y"] = Control("Y", GamepadButtons.Y, GamepadPartKind.Button, "Y"),
        ["buttona"] = Control("A", GamepadButtons.A, GamepadPartKind.Button, "A"),
        ["buttonb"] = Control("B", GamepadButtons.B, GamepadPartKind.Button, "B"),
        ["buttonx"] = Control("X", GamepadButtons.X, GamepadPartKind.Button, "X"),
        ["buttony"] = Control("Y", GamepadButtons.Y, GamepadPartKind.Button, "Y"),
        ["facea"] = Control("A", GamepadButtons.A, GamepadPartKind.Button, "A"),
        ["faceb"] = Control("B", GamepadButtons.B, GamepadPartKind.Button, "B"),
        ["facex"] = Control("X", GamepadButtons.X, GamepadPartKind.Button, "X"),
        ["facey"] = Control("Y", GamepadButtons.Y, GamepadPartKind.Button, "Y"),
        ["leftstick"] = Control("StickLeft", GamepadButtons.LeftStickClick, GamepadPartKind.Stick, "L3"),
        ["rightstick"] = Control("StickRight", GamepadButtons.RightStickClick, GamepadPartKind.Stick, "R3"),
        ["ls"] = Control("StickLeft", GamepadButtons.LeftStickClick, GamepadPartKind.Stick, "L3"),
        ["rs"] = Control("StickRight", GamepadButtons.RightStickClick, GamepadPartKind.Stick, "R3"),
        ["l3"] = Control("StickLeft", GamepadButtons.LeftStickClick, GamepadPartKind.Stick, "L3"),
        ["r3"] = Control("StickRight", GamepadButtons.RightStickClick, GamepadPartKind.Stick, "R3"),
        ["lb"] = Control("LB", GamepadButtons.LeftBumper, GamepadPartKind.Bumper, "LB"),
        ["rb"] = Control("RB", GamepadButtons.RightBumper, GamepadPartKind.Bumper, "RB"),
        ["l1"] = Control("LB", GamepadButtons.LeftBumper, GamepadPartKind.Bumper, "LB"),
        ["r1"] = Control("RB", GamepadButtons.RightBumper, GamepadPartKind.Bumper, "RB"),
        ["leftbumper"] = Control("LB", GamepadButtons.LeftBumper, GamepadPartKind.Bumper, "LB"),
        ["rightbumper"] = Control("RB", GamepadButtons.RightBumper, GamepadPartKind.Bumper, "RB"),
        ["leftshoulder"] = Control("LB", GamepadButtons.LeftBumper, GamepadPartKind.Bumper, "LB"),
        ["rightshoulder"] = Control("RB", GamepadButtons.RightBumper, GamepadPartKind.Bumper, "RB"),
        ["lt"] = Control("LT", GamepadButtons.LeftTrigger, GamepadPartKind.Trigger, "LT"),
        ["rt"] = Control("RT", GamepadButtons.RightTrigger, GamepadPartKind.Trigger, "RT"),
        ["l2"] = Control("LT", GamepadButtons.LeftTrigger, GamepadPartKind.Trigger, "LT"),
        ["r2"] = Control("RT", GamepadButtons.RightTrigger, GamepadPartKind.Trigger, "RT"),
        ["lefttrigger"] = Control("LT", GamepadButtons.LeftTrigger, GamepadPartKind.Trigger, "LT"),
        ["righttrigger"] = Control("RT", GamepadButtons.RightTrigger, GamepadPartKind.Trigger, "RT"),
        ["dpadup"] = Control("DpadUp", GamepadButtons.DpadUp, GamepadPartKind.Dpad, "\u25B2"),
        ["dpaddown"] = Control("DpadDown", GamepadButtons.DpadDown, GamepadPartKind.Dpad, "\u25BC"),
        ["dpadleft"] = Control("DpadLeft", GamepadButtons.DpadLeft, GamepadPartKind.Dpad, "\u25C0"),
        ["dpadright"] = Control("DpadRight", GamepadButtons.DpadRight, GamepadPartKind.Dpad, "\u25B6"),
        ["up"] = Control("DpadUp", GamepadButtons.DpadUp, GamepadPartKind.Dpad, "\u25B2"),
        ["down"] = Control("DpadDown", GamepadButtons.DpadDown, GamepadPartKind.Dpad, "\u25BC"),
        ["left"] = Control("DpadLeft", GamepadButtons.DpadLeft, GamepadPartKind.Dpad, "\u25C0"),
        ["right"] = Control("DpadRight", GamepadButtons.DpadRight, GamepadPartKind.Dpad, "\u25B6"),
        ["start"] = Control("Menu", GamepadButtons.Start, GamepadPartKind.Button, "Menu"),
        ["menu"] = Control("Menu", GamepadButtons.Start, GamepadPartKind.Button, "Menu"),
        ["back"] = Control("View", GamepadButtons.Back, GamepadPartKind.Button, "View"),
        ["view"] = Control("View", GamepadButtons.Back, GamepadPartKind.Button, "View"),
        ["select"] = Control("View", GamepadButtons.Back, GamepadPartKind.Button, "View"),
        ["guide"] = Control("Guide", GamepadButtons.Guide, GamepadPartKind.Button, "Guide"),
        ["special"] = Control("Guide", GamepadButtons.Guide, GamepadPartKind.Button, "Guide"),
        ["home"] = Control("Guide", GamepadButtons.Guide, GamepadPartKind.Button, "Guide"),
        ["xbox"] = Control("Guide", GamepadButtons.Guide, GamepadPartKind.Button, "Guide"),
        ["paddle1"] = Control("P1", GamepadButtons.Paddle1, GamepadPartKind.Paddle, "P1"),
        ["paddle2"] = Control("P2", GamepadButtons.Paddle2, GamepadPartKind.Paddle, "P2"),
        ["paddle3"] = Control("P3", GamepadButtons.Paddle3, GamepadPartKind.Paddle, "P3"),
        ["paddle4"] = Control("P4", GamepadButtons.Paddle4, GamepadPartKind.Paddle, "P4"),
        ["p1"] = Control("P1", GamepadButtons.Paddle1, GamepadPartKind.Paddle, "P1"),
        ["p2"] = Control("P2", GamepadButtons.Paddle2, GamepadPartKind.Paddle, "P2"),
        ["p3"] = Control("P3", GamepadButtons.Paddle3, GamepadPartKind.Paddle, "P3"),
        ["p4"] = Control("P4", GamepadButtons.Paddle4, GamepadPartKind.Paddle, "P4"),
    };

    private static readonly string[] BodyTokens =
    [
        "body", "main", "shell", "case", "casing", "grip", "housing", "chassis", "frame",
        "cover", "logo", "screw", "port", "battery", "motor", "led", "ring", "well", "base",
        "socket", "hole", "insert", "label", "symbol", "light", "panel", "plate", "pcb",
    ];

    /// <summary>Resolves an OBJ group/object name (or a Blender object name) to a control.</summary>
    public static bool TryMap(string? sourceName, out GamepadControlMap map)
    {
        map = default;
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return false;
        }

        string name = Normalize(sourceName);
        if (name.Length == 0)
        {
            return false;
        }

        if (Exact.TryGetValue(name, out map))
        {
            return true;
        }

        // Shell names such as "Body" (which happens to start with B) must not fall through to the
        // face-button heuristic below.
        if (IsBodyName(sourceName))
        {
            return false;
        }

        // D-pad arms: "DPad-Up", "DirectionalPad_Left", "dpad up".
        if (name.Contains("dpad", StringComparison.Ordinal) ||
            name.Contains("directionpad", StringComparison.Ordinal) ||
            name.Contains("directionalpad", StringComparison.Ordinal))
        {
            if (name.Contains("up", StringComparison.Ordinal))
            {
                map = Exact["dpadup"];
                return true;
            }

            if (name.Contains("down", StringComparison.Ordinal))
            {
                map = Exact["dpaddown"];
                return true;
            }

            if (name.Contains("left", StringComparison.Ordinal))
            {
                map = Exact["dpadleft"];
                return true;
            }

            if (name.Contains("right", StringComparison.Ordinal))
            {
                map = Exact["dpadright"];
                return true;
            }
        }

        if (name.Contains("trigger", StringComparison.Ordinal))
        {
            if (name.Contains("left", StringComparison.Ordinal) || name.Contains("l2", StringComparison.Ordinal) || name.Contains("lt", StringComparison.Ordinal))
            {
                map = Exact["lefttrigger"];
                return true;
            }

            if (name.Contains("right", StringComparison.Ordinal) || name.Contains("r2", StringComparison.Ordinal) || name.Contains("rt", StringComparison.Ordinal))
            {
                map = Exact["righttrigger"];
                return true;
            }
        }

        if (name.Contains("bumper", StringComparison.Ordinal) || name.Contains("shoulder", StringComparison.Ordinal))
        {
            if (name.Contains("left", StringComparison.Ordinal) || name.Contains("l1", StringComparison.Ordinal) || name.Contains("lb", StringComparison.Ordinal))
            {
                // "Shoulder-Left-Middle" is the fixed part of the shoulder assembly, not the button.
                if (name.Contains("middle", StringComparison.Ordinal) || name.Contains("bottom", StringComparison.Ordinal))
                {
                    return false;
                }

                map = Exact["leftbumper"];
                return true;
            }

            if (name.Contains("right", StringComparison.Ordinal) || name.Contains("r1", StringComparison.Ordinal) || name.Contains("rb", StringComparison.Ordinal))
            {
                if (name.Contains("middle", StringComparison.Ordinal) || name.Contains("bottom", StringComparison.Ordinal))
                {
                    return false;
                }

                map = Exact["rightbumper"];
                return true;
            }
        }

        // Thumbsticks: rings and wells stay with the static shell.
        if (name.Contains("stick", StringComparison.Ordinal) || name.Contains("joystick", StringComparison.Ordinal))
        {
            if (name.Contains("ring", StringComparison.Ordinal) || name.Contains("well", StringComparison.Ordinal) ||
                name.Contains("socket", StringComparison.Ordinal) || name.Contains("hole", StringComparison.Ordinal))
            {
                return false;
            }

            if (name.Contains("left", StringComparison.Ordinal) || name.EndsWith('l'))
            {
                map = Exact["leftstick"];
                return true;
            }

            if (name.Contains("right", StringComparison.Ordinal) || name.EndsWith('r'))
            {
                map = Exact["rightstick"];
                return true;
            }
        }

        // Face buttons: "Button_A", "BtnA", "A_Button".
        if (name.Contains("button", StringComparison.Ordinal) || name.Contains("btn", StringComparison.Ordinal) ||
            name.StartsWith('b') || name.StartsWith('a') || name.StartsWith('x') || name.StartsWith('y'))
        {
            foreach (char letter in "abyx")
            {
                if (name.EndsWith(letter) || name.Contains(letter + "button", StringComparison.Ordinal) ||
                    name.Contains("button" + letter, StringComparison.Ordinal))
                {
                    map = Exact[letter.ToString()];
                    return true;
                }
            }
        }

        if (name.Contains("menu", StringComparison.Ordinal) || name.Contains("start", StringComparison.Ordinal))
        {
            map = Exact["menu"];
            return true;
        }

        if (name.Contains("view", StringComparison.Ordinal) || name.Contains("back", StringComparison.Ordinal) ||
            name.Contains("select", StringComparison.Ordinal))
        {
            map = Exact["view"];
            return true;
        }

        if (name.Contains("guide", StringComparison.Ordinal) || name.Contains("home", StringComparison.Ordinal) ||
            name.Contains("xbox", StringComparison.Ordinal))
        {
            map = Exact["guide"];
            return true;
        }

        return false;
    }

    /// <summary>True for names that clearly describe shell/decoration geometry.</summary>
    public static bool IsBodyName(string? sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return true;
        }

        string name = Normalize(sourceName);
        foreach (string token in BodyTokens)
        {
            if (name.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lowercases and removes separators so "Left Stick", "left-stick" and "leftstick" match.</summary>
    public static string Normalize(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        int length = 0;
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }

    private static GamepadControlMap Control(string name, long buttonId, GamepadPartKind kind, string label) =>
        new(name, buttonId, kind, label);
}

/// <summary>One mesh group from the downloaded model, already normalised into scene space.</summary>
public sealed class GamepadModelPart
{
    public GamepadModelPart(
        string sourceName,
        GamepadControlMap map,
        MeshGeometry3D mesh,
        Color color)
    {
        SourceName = sourceName;
        Map = map;
        Mesh = mesh;
        Color = color;
        Bounds = mesh.Bounds;
        Center = new Point3D(
            Bounds.X + (Bounds.SizeX / 2),
            Bounds.Y + (Bounds.SizeY / 2),
            Bounds.Z + (Bounds.SizeZ / 2));
    }

    /// <summary>Group/object name exactly as written in the OBJ file.</summary>
    public string SourceName { get; }

    public GamepadControlMap Map { get; }

    public MeshGeometry3D Mesh { get; }

    public Rect3D Bounds { get; }

    public Point3D Center { get; }

    public Color Color { get; }

    public int TriangleCount => Mesh.TriangleIndices.Count / 3;

    public bool IsControl => Map.IsControl;
}

/// <summary>A fully parsed controller model with its mapped controls and scene-space bounds.</summary>
public sealed class GamepadModel
{
    public GamepadModel(IReadOnlyList<GamepadModelPart> parts, string sourcePath)
    {
        Parts = parts;
        SourcePath = sourcePath;

        Rect3D bounds = Rect3D.Empty;
        int triangles = 0;
        foreach (GamepadModelPart part in parts)
        {
            bounds.Union(part.Bounds);
            triangles += part.TriangleCount;
        }

        Bounds = bounds;
        TriangleCount = triangles;
    }

    public IReadOnlyList<GamepadModelPart> Parts { get; }

    public string SourcePath { get; }

    public Rect3D Bounds { get; }

    public int TriangleCount { get; }

    /// <summary>Number of animated control groups (each becomes one ModelVisual3D).</summary>
    public int ControlCount
    {
        get
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (GamepadModelPart part in Parts)
            {
                if (part.IsControl)
                {
                    names.Add(part.Map.Name);
                }
            }

            return names.Count;
        }
    }

    /// <summary>
    /// True when the model has too few named controls to drive the UI; the view then keeps it as a
    /// static base and overlays the procedural stick caps/buttons using <see cref="ModelAnchors"/>.
    /// </summary>
    public bool NeedsControlOverlay => ControlCount < 6;
}

/// <summary>
/// Hand-written Wavefront OBJ + MTL importer for the bundled CC0 DualSense controller. It reads
/// <c>o</c>/<c>g</c> groups, <c>usemtl</c>/<c>Kd</c> colours, triangles/quads/ngons and normals,
/// normalises the model into PadShift scene units (X right, Y up, Z towards the camera) and freezes
/// every mesh. No NuGet package and no runtime allocation after the model is loaded.
/// </summary>
public static class GamepadModelLoader
{
    /// <summary>Folder under <c>assets/3d</c> that holds the bundled model.</summary>
    public const string AssetFolder = "dualsense-cc0";

    /// <summary>File name of the importer-ready OBJ conversion.</summary>
    public const string ObjFileName = "dualsense.obj";

    /// <summary>Target width in scene units; matches the procedural pad so the camera framing is stable.</summary>
    public const double TargetWidth = 29.0;

    /// <summary>Rejects assets above the per-frame triangle budget instead of stuttering.</summary>
    public const int MaxTriangles = 40_000;

    private static int _failureLogged;

    /// <summary>Locates <c>assets/3d/dualsense-cc0</c> next to the executable or above the repo root.</summary>
    public static string? FindAssetDirectory()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            for (int depth = 0; depth < 9 && directory is not null; depth++)
            {
                string candidate = Path.Combine(directory.FullName, "assets", "3d", AssetFolder);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    /// <summary>Locates the asset folder of any catalog model next to the executable or repo root.</summary>
    public static string? FindAssetDirectory(string folder)
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            for (int depth = 0; depth < 9 && directory is not null; depth++)
            {
                string candidate = Path.Combine(directory.FullName, "assets", "3d", folder);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    /// <summary>Full path of the bundled default OBJ, or null when the deploy is missing the asset.</summary>
    public static string? FindModelFile()
    {
        string? directory = FindAssetDirectory();
        if (directory is null)
        {
            return null;
        }

        string path = Path.Combine(directory, ObjFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Full path of a catalog model's OBJ, or null when the deploy is missing the asset.</summary>
    public static string? FindModelFile(GamepadModelInfo model)
    {
        string? directory = FindAssetDirectory(model.Folder);
        if (directory is null)
        {
            return null;
        }

        string path = Path.Combine(directory, model.ObjFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Loads the default bundled model, falling back to <c>false</c> (procedural scene) if it is
    /// absent or malformed. The diagnostic is logged once per process; a broken deploy must never
    /// take the window down.
    /// </summary>
    public static bool TryLoad(out GamepadModel? model, out string message) =>
        TryLoad(GamepadModelCatalog.DualSense, out model, out message);

    /// <summary>Loads a specific catalog model with the same fallback semantics as the default.</summary>
    public static bool TryLoad(GamepadModelInfo model, out GamepadModel? loaded, out string message)
    {
        loaded = null;
        string? path = FindModelFile(model);

        if (string.IsNullOrWhiteSpace(path))
        {
            message = "3D model not found under assets/3d/" + model.Folder + "; using the procedural controller.";
            LogOnce(message);
            return false;
        }

        try
        {
            var parsed = LoadObj(path);
            if (parsed.TriangleCount > MaxTriangles)
            {
                message = $"3D model has {parsed.TriangleCount} triangles (budget {MaxTriangles}); using the procedural controller.";
                LogOnce(message);
                return false;
            }

            loaded = parsed;
            message = $"3D model loaded: {Path.GetFileName(path)} ({parsed.TriangleCount} triangles, {parsed.ControlCount} controls).";
            return true;
        }
        catch (Exception ex)
        {
            loaded = null;
            message = "3D model failed to load (" + ex.GetType().Name + ": " + ex.Message + "); using the procedural controller.";
            LogOnce(message);
            return false;
        }
    }

    /// <summary>Loads a specific OBJ file path; used by tests and by the catalog path above.</summary>
    public static bool TryLoad(string? path, out GamepadModel? model, out string message)
    {
        model = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            message = "3D model not found; using the procedural controller.";
            LogOnce(message);
            return false;
        }

        try
        {
            model = LoadObj(path);
            if (model.TriangleCount > MaxTriangles)
            {
                message = $"3D model has {model.TriangleCount} triangles (budget {MaxTriangles}); using the procedural controller.";
                model = null;
                LogOnce(message);
                return false;
            }

            message = $"3D model loaded: {Path.GetFileName(path)} ({model.TriangleCount} triangles, {model.ControlCount} controls).";
            return true;
        }
        catch (Exception ex)
        {
            model = null;
            message = "3D model failed to load (" + ex.GetType().Name + ": " + ex.Message + "); using the procedural controller.";
            LogOnce(message);
            return false;
        }
    }

    /// <summary>Parses an OBJ file. Throws on unreadable/invalid geometry so callers can fall back.</summary>
    public static GamepadModel LoadObj(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("OBJ file not found.", path);
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var positions = new List<Point3D>();
        var normals = new List<Vector3D>();
        var materials = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<ObjGroup>();
        var byName = new Dictionary<string, ObjGroup>(StringComparer.Ordinal);
        ObjGroup? current = null;
        string defaultMaterial = string.Empty;

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int space = line.IndexOf(' ');
            string keyword = space < 0 ? line : line[..space];
            string rest = space < 0 ? string.Empty : line[(space + 1)..].Trim();

            switch (keyword)
            {
                case "v":
                    positions.Add(ParsePoint(rest, path));
                    break;

                case "vn":
                    normals.Add(ParseVector(rest, path));
                    break;

                case "o":
                case "g":
                    current = GetGroup(groups, byName, rest, defaultMaterial);
                    break;

                case "usemtl":
                    defaultMaterial = rest;
                    current ??= GetGroup(groups, byName, "Default", defaultMaterial);
                    current.Material = rest;
                    break;

                case "mtllib":
                    LoadMaterials(Path.Combine(directory, rest), materials);
                    break;

                case "f":
                    current ??= GetGroup(groups, byName, "Default", defaultMaterial);
                    ParseFace(rest, positions.Count, normals.Count, current, path);
                    break;
            }
        }

        if (positions.Count == 0)
        {
            throw new InvalidDataException("OBJ has no vertices.");
        }

        // Move the raw model into scene units. Blender exports Y-up; the rare Z-up export is rotated
        // so the widest axis still runs along X and the pad's face points at +Z.
        int axisSwap = Orientation(positions);
        var transformed = new Point3D[positions.Count];
        for (int i = 0; i < positions.Count; i++)
        {
            Point3D p = positions[i];
            p = axisSwap switch
            {
                1 => new Point3D(p.X, p.Z, -p.Y),
                2 => new Point3D(-p.Y, p.X, p.Z),
                _ => p,
            };
            transformed[i] = p;
        }

        Rect3D raw = Bounds(transformed);
        double scale = TargetWidth / Math.Max(raw.SizeX, 1e-6);
        double cx = raw.X + (raw.SizeX / 2);
        double cy = raw.Y + (raw.SizeY / 2);
        double cz = raw.Z + (raw.SizeZ / 2);

        for (int i = 0; i < transformed.Length; i++)
        {
            transformed[i] = new Point3D(
                (transformed[i].X - cx) * scale,
                (transformed[i].Y - cy) * scale,
                (transformed[i].Z - cz) * scale);
        }

        var parts = new List<GamepadModelPart>(groups.Count);
        foreach (ObjGroup group in groups)
        {
            if (group.Faces.Count == 0)
            {
                continue;
            }

            Color color = materials.TryGetValue(group.Material, out Color material)
                ? material
                : Color.FromRgb(0x55, 0x5D, 0x6B);

            MeshGeometry3D mesh = BuildMesh(group, transformed, normals, axisSwap);
            if (mesh.TriangleIndices.Count == 0)
            {
                continue;
            }

            if (GamepadPartMap.TryMap(group.Name, out GamepadControlMap map))
            {
                parts.Add(new GamepadModelPart(group.Name, map, mesh, color));
            }
            else
            {
                // Unnamed decoration still belongs to the shell; keep it so the silhouette is complete.
                var body = new GamepadControlMap("Body", null, GamepadPartKind.Body, null);
                parts.Add(new GamepadModelPart(group.Name, body, mesh, color));
            }
        }

        if (parts.Count == 0)
        {
            throw new InvalidDataException("OBJ has no drawable groups.");
        }

        return new GamepadModel(parts, Path.GetFullPath(path));
    }

    /// <summary>Picks the axis swap needed to put the widest model axis on X (returns 0, 1 or 2).</summary>
    private static int Orientation(IReadOnlyList<Point3D> points)
    {
        Rect3D bounds = Bounds(points);
        if (bounds.SizeX >= bounds.SizeY && bounds.SizeX >= bounds.SizeZ)
        {
            return 0;
        }

        return bounds.SizeZ >= bounds.SizeY ? 1 : 2;
    }

    private static Rect3D Bounds(IReadOnlyList<Point3D> points)
    {
        if (points.Count == 0)
        {
            return Rect3D.Empty;
        }

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double minZ = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        double maxZ = double.MinValue;

        foreach (Point3D p in points)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            minZ = Math.Min(minZ, p.Z);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
            maxZ = Math.Max(maxZ, p.Z);
        }

        return new Rect3D(minX, minY, minZ, maxX - minX, maxY - minY, maxZ - minZ);
    }

    private static MeshGeometry3D BuildMesh(
        ObjGroup group,
        IReadOnlyList<Point3D> positions,
        IReadOnlyList<Vector3D> normals,
        int axisSwap)
    {
        var meshPositions = new Point3DCollection();
        var meshNormals = new Vector3DCollection();
        var indices = new Int32Collection();
        var lookup = new Dictionary<(int Position, int Normal), int>();

        foreach (ObjFace face in group.Faces)
        {
            int a = face.Position[0];
            int b = face.Position[1];
            int c = face.Position[2];

            Vector3D faceNormal = Vector3D.CrossProduct(
                positions[b] - positions[a],
                positions[c] - positions[a]);
            if (faceNormal.LengthSquared < 1e-12)
            {
                continue;
            }

            faceNormal.Normalize();

            for (int corner = 0; corner < 3; corner++)
            {
                int positionIndex = face.Position[corner];
                int normalIndex = face.Normal.Length > corner ? face.Normal[corner] : -1;

                Vector3D normal = faceNormal;
                if (normalIndex >= 0 && normalIndex < normals.Count)
                {
                    normal = axisSwap switch
                    {
                        1 => new Vector3D(normals[normalIndex].X, normals[normalIndex].Z, -normals[normalIndex].Y),
                        2 => new Vector3D(-normals[normalIndex].Y, normals[normalIndex].X, normals[normalIndex].Z),
                        _ => normals[normalIndex],
                    };
                    normal.Normalize();
                }

                var key = (positionIndex, normalIndex);
                if (!lookup.TryGetValue(key, out int vertexIndex))
                {
                    vertexIndex = meshPositions.Count;
                    lookup[key] = vertexIndex;
                    meshPositions.Add(positions[positionIndex]);
                    meshNormals.Add(normal);
                }

                indices.Add(vertexIndex);
            }
        }

        var mesh = new MeshGeometry3D
        {
            Positions = meshPositions,
            Normals = meshNormals,
            TriangleIndices = indices,
        };

        mesh.Freeze();
        return mesh;
    }

    private static ObjGroup GetGroup(
        List<ObjGroup> groups,
        Dictionary<string, ObjGroup> byName,
        string name,
        string material)
    {
        string key = name.Length == 0 ? "Default" : name;
        if (!byName.TryGetValue(key, out ObjGroup? group))
        {
            group = new ObjGroup(key, material);
            byName[key] = group;
            groups.Add(group);
        }

        if (group.Material.Length == 0 && material.Length > 0)
        {
            group.Material = material;
        }

        return group;
    }

    private static void ParseFace(
        string text,
        int positionCount,
        int normalCount,
        ObjGroup group,
        string path)
    {
        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3)
        {
            return;
        }

        var position = new int[tokens.Length];
        var normal = new int[tokens.Length];

        for (int i = 0; i < tokens.Length; i++)
        {
            string[] parts = tokens[i].Split('/');
            position[i] = ResolveIndex(parts[0], positionCount, path);
            normal[i] = parts.Length >= 3 && parts[2].Length > 0
                ? ResolveIndex(parts[2], normalCount, path)
                : -1;
        }

        // Fan triangulation is correct for the convex quads/ngons produced by the Blender exporter.
        for (int i = 1; i < tokens.Length - 1; i++)
        {
            group.Faces.Add(new ObjFace(
                new[] { position[0], position[i], position[i + 1] },
                new[] { normal[0], normal[i], normal[i + 1] }));
        }
    }

    private static int ResolveIndex(string token, int count, string path)
    {
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value == 0)
        {
            throw new InvalidDataException("Invalid OBJ index '" + token + "' in " + path);
        }

        int index = value > 0 ? value - 1 : count + value;
        if (index < 0 || index >= count)
        {
            throw new InvalidDataException("OBJ index out of range in " + path);
        }

        return index;
    }

    private static Point3D ParsePoint(string text, string path)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            throw new InvalidDataException("Invalid OBJ vertex in " + path);
        }

        return new Point3D(ParseFloat(parts[0]), ParseFloat(parts[1]), ParseFloat(parts[2]));
    }

    private static Vector3D ParseVector(string text, string path)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            throw new InvalidDataException("Invalid OBJ normal in " + path);
        }

        return new Vector3D(ParseFloat(parts[0]), ParseFloat(parts[1]), ParseFloat(parts[2]));
    }

    private static double ParseFloat(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new InvalidDataException("Invalid OBJ number '" + text + "'.");

    private static void LoadMaterials(string path, Dictionary<string, Color> materials)
    {
        if (!File.Exists(path))
        {
            return;
        }

        string current = string.Empty;
        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0])
            {
                case "newmtl":
                    current = parts.Length > 1 ? parts[1] : string.Empty;
                    break;

                case "Kd":
                    if (current.Length > 0 && parts.Length >= 4)
                    {
                        materials[current] = Color.FromRgb(
                            ToByte(ParseFloat(parts[1])),
                            ToByte(ParseFloat(parts[2])),
                            ToByte(ParseFloat(parts[3])));
                    }

                    break;
            }
        }
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255.0), 0, 255);

    private static void LogOnce(string message)
    {
        if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
        {
            Debug.WriteLine("[PadShift] " + message);
        }
    }

    private sealed class ObjGroup
    {
        public ObjGroup(string name, string material)
        {
            Name = name;
            Material = material;
        }

        public string Name { get; }

        public string Material { get; set; }

        public List<ObjFace> Faces { get; } = new();
    }

    private readonly struct ObjFace
    {
        public ObjFace(int[] position, int[] normal)
        {
            Position = position;
            Normal = normal;
        }

        public int[] Position { get; }

        public int[] Normal { get; }
    }
}

/// <summary>
/// Proportional anchors used only when a downloaded model has no usable part names: the model stays
/// as the static base and the existing procedural caps/buttons are overlaid at these fractions of
/// its bounds. Values are tuned against a symmetric-stick asset layout.
/// </summary>
public static class ModelAnchors
{
    /// <summary>(x, y) fraction of the model bounds, from -0.5 (left/bottom) to +0.5 (right/top).</summary>
    public static readonly IReadOnlyDictionary<string, (double X, double Y)> Positions =
        new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal)
        {
            ["StickLeft"] = (-0.20, 0.15),
            ["StickRight"] = (0.11, -0.16),
            ["A"] = (0.31, -0.09),
            ["B"] = (0.40, 0.00),
            ["X"] = (0.22, 0.00),
            ["Y"] = (0.31, 0.09),
            ["DpadUp"] = (-0.27, -0.02),
            ["DpadDown"] = (-0.27, -0.12),
            ["DpadLeft"] = (-0.33, -0.07),
            ["DpadRight"] = (-0.21, -0.07),
            ["View"] = (-0.04, 0.07),
            ["Menu"] = (0.04, 0.07),
            ["Guide"] = (0.00, 0.16),
            ["LB"] = (-0.23, 0.30),
            ["RB"] = (0.23, 0.30),
            ["LT"] = (-0.23, 0.38),
            ["RT"] = (0.23, 0.38),
            ["P1"] = (-0.20, -0.40),
            ["P2"] = (-0.07, -0.42),
            ["P3"] = (0.07, -0.42),
            ["P4"] = (0.20, -0.40),
        };
}

/// <summary>
/// Positions for the two procedural thumbstick caps used when a downloaded model has <b>no separate
/// stick meshes</b> (only a single body mesh). Fractions are relative to the model bounding box:
/// X is 0 at the left edge and 1 at the right edge; Y is 0 at the top edge and 1 at the bottom edge
/// (screen convention, because that is how the artwork is laid out). The caps sit slightly above the
/// model's front face, and their tilt pivot is the centre of each generated cap mesh.
/// </summary>
/// <remarks>
/// Nudge these numbers when swapping in another controller model: the bundled DualSense model
/// reports
/// <c>StickLeft</c>/<c>StickRight</c> meshes and therefore does not use this table, but a single-mesh
/// model does. Starting guesses were left ≈ (0.30, 0.18) and right ≈ (0.62, -0.12) in the task; the
/// values below are calibrated against the bundled model's bounds for a sensible default.
/// </remarks>
public static class StickAnchors
{
    /// <summary>How far in front of the model's front face the caps sit, in scene units.</summary>
    public const double FrontOffset = 0.85;

    /// <summary>Cap position as (x fraction from left, y fraction from top), both 0..1.</summary>
    public static readonly IReadOnlyDictionary<string, (double X, double Y)> Positions =
        new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal)
        {
            ["StickLeft"] = (0.30, 0.28),
            ["StickRight"] = (0.62, 0.52),
        };

    /// <summary>Scene-space anchor for a stick cap, derived from the model bounds.</summary>
    public static Point3D Resolve(string partName, Rect3D bounds)
    {
        (double x, double y) = Positions.TryGetValue(partName, out (double X, double Y) position)
            ? position
            : partName == "StickRight" ? (0.62, 0.52) : (0.30, 0.28);

        return new Point3D(
            bounds.X + (x * bounds.SizeX),
            bounds.Y + ((1.0 - y) * bounds.SizeY),
            bounds.Z + bounds.SizeZ + FrontOffset);
    }
}
