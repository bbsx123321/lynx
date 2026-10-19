using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using PadShift.App.ViewModels;

namespace PadShift.App.Infrastructure;

/// <summary>What a procedural mesh represents; the view uses it to pick press/animation rules.</summary>
public enum GamepadPartKind
{
    Body,
    Grip,
    StickWell,
    Stick,
    Trigger,
    Bumper,
    Button,
    Dpad,
    Paddle,
    Shadow,
}

/// <summary>
/// One independently transformable piece of the 3D controller: a frozen mesh, its placement in the
/// scene, the gamepad button it selects (if any) and the caption drawn next to it.
/// </summary>
public sealed class GamepadPart
{
    public GamepadPart(
        string name,
        long? buttonId,
        GamepadPartKind kind,
        MeshGeometry3D mesh,
        Point3D position,
        Vector3D rotationDegrees,
        Color color,
        string? label = null,
        Vector3D labelOffset = default)
    {
        Name = name;
        ButtonId = buttonId;
        Kind = kind;
        Mesh = mesh;
        Position = position;
        RotationDegrees = rotationDegrees;
        Color = color;
        Label = label;
        LabelOffset = labelOffset;
        LabelAnchor = position + labelOffset;
    }

    public string Name { get; }

    /// <summary><see cref="GamepadButtons"/> id this part selects; null for decoration.</summary>
    public long? ButtonId { get; }

    public GamepadPartKind Kind { get; }

    public MeshGeometry3D Mesh { get; }

    /// <summary>Placement of the mesh origin in scene space (X right, Y up, Z towards the viewer).</summary>
    public Point3D Position { get; }

    /// <summary>Static Euler rotation (degrees, applied X then Y then Z) before the translation.</summary>
    public Vector3D RotationDegrees { get; }

    /// <summary>Base diffuse colour; the view derives frozen materials from it.</summary>
    public Color Color { get; }

    /// <summary>Caption drawn over the part in the 2D label overlay ("A", "LT", "P1", ...).</summary>
    public string? Label { get; }

    public Vector3D LabelOffset { get; }

    /// <summary>Scene-space anchor used to place <see cref="Label"/>.</summary>
    public Point3D LabelAnchor { get; }

    public int TriangleCount => Mesh.TriangleIndices.Count / 3;
}

/// <summary>
/// Builds the 3D gamepad procedurally: a bevelled body, two grip cylinders, stick wells and caps,
/// triggers, bumpers, face buttons, d-pad arms and back paddles. No third-party model is used;
/// see <c>assets/3d/LICENSE.txt</c> for the provenance note.
/// </summary>
/// <remarks>
/// Scene units: the pad is about 29 units wide, the front face sits at <c>z = +1.3</c>, X is right,
/// Y is up and Z points at the camera. Every returned mesh is frozen, so building the scene allocates
/// nothing per frame; the view only swaps frozen materials and updates transform values.
/// </remarks>
public static class GamepadMeshBuilder
{
    public const double BodyHalfWidth = 14.7;

    public const double BodyHalfHeight = 6.8;

    public const double BodyCenterY = 2.4;

    public const double BodyHalfDepth = 1.3;

    /// <summary>Z of the front face; buttons sit on it and press towards the body.</summary>
    public const double FaceZ = BodyHalfDepth;

    private static readonly Color BodyColor = Color.FromRgb(0x4A, 0x52, 0x64);
    private static readonly Color GripColor = Color.FromRgb(0x3A, 0x41, 0x50);
    private static readonly Color WellColor = Color.FromRgb(0x0A, 0x0D, 0x14);
    private static readonly Color StickColor = Color.FromRgb(0x74, 0x80, 0x8F);
    private static readonly Color DpadColor = Color.FromRgb(0x56, 0x5F, 0x70);
    private static readonly Color TriggerColor = Color.FromRgb(0x66, 0x70, 0x7F);
    private static readonly Color GuideColor = Color.FromRgb(0x74, 0x7E, 0x90);
    private static readonly Color PaddleColor = Color.FromRgb(0x40, 0x48, 0x56);
    private static readonly Color FaceAColor = Color.FromRgb(0x3F, 0xA1, 0x3F);
    private static readonly Color FaceBColor = Color.FromRgb(0xC0, 0x39, 0x2B);
    private static readonly Color FaceXColor = Color.FromRgb(0x2E, 0x6F, 0xD8);
    private static readonly Color FaceYColor = Color.FromRgb(0xE0, 0xAC, 0x1F);

    /// <summary>Builds every part of the controller; the list order is stable.</summary>
    public static IReadOnlyList<GamepadPart> Build()
    {
        var parts = new List<GamepadPart>(28);

        // ---- body and grips ----------------------------------------------------------------
        parts.Add(new GamepadPart(
            "Body",
            null,
            GamepadPartKind.Body,
            Extrusion(inset => RoundedRect(BodyHalfWidth, BodyHalfHeight, 3.2, inset), BodyHalfDepth, 0.35),
            new Point3D(0, BodyCenterY, 0),
            default,
            BodyColor));

        parts.Add(new GamepadPart(
            "GripLeft",
            null,
            GamepadPartKind.Grip,
            Extrusion(inset => Ellipse(2.9 - inset, 1.15 - (inset * 0.3), 12), 4.2, 0.5),
            new Point3D(-10.2, -5.2, 0),
            new Vector3D(90, 0, -16),
            GripColor));

        parts.Add(new GamepadPart(
            "GripRight",
            null,
            GamepadPartKind.Grip,
            Extrusion(inset => Ellipse(2.9 - inset, 1.15 - (inset * 0.3), 12), 4.2, 0.5),
            new Point3D(10.2, -5.2, 0),
            new Vector3D(90, 0, 16),
            GripColor));

        // ---- recessed centre plate behind the guide cluster ---------------------------------
        parts.Add(new GamepadPart(
            "FacePlate",
            null,
            GamepadPartKind.Body,
            Extrusion(inset => RoundedRect(3.5, 2.7, 1.2, inset), 0.08, 0.06),
            new Point3D(0, 2.2, FaceZ + 0.02),
            default,
            Color.FromRgb(0x3D, 0x44, 0x54)));

        // ---- thumbsticks: a dark well plus a tilting shaft/cap ------------------------------
        (double X, double Y)[] sticks = [(-6.9, 3.2), (6.0, -2.8)];
        string[] stickNames = ["StickLeft", "StickRight"];
        string[] stickLabels = ["L3", "R3"];
        long[] stickIds = [GamepadButtons.LeftStickClick, GamepadButtons.RightStickClick];

        for (int i = 0; i < sticks.Length; i++)
        {
            parts.Add(new GamepadPart(
                "Well" + (i == 0 ? "Left" : "Right"),
                null,
                GamepadPartKind.StickWell,
                Disc(3.35, 0.22, 18),
                new Point3D(sticks[i].X, sticks[i].Y, FaceZ),
                default,
                WellColor));

            MeshBuilder stick = new();
            stick.AddDisc(1.5, 2.1, 12, backCap: false, z: 0);
            stick.AddDisc(2.3, 1.0, 16, backCap: true, z: 1.8);

            parts.Add(new GamepadPart(
                stickNames[i],
                stickIds[i],
                GamepadPartKind.Stick,
                stick.Freeze(),
                new Point3D(sticks[i].X, sticks[i].Y, FaceZ + 0.15),
                default,
                StickColor,
                stickLabels[i],
                new Vector3D(0, 0, 3.1)));
        }

        // ---- face buttons -------------------------------------------------------------------
        AddButton(parts, "Y", GamepadButtons.Y, 9.6, 5.4, FaceYColor, "Y");
        AddButton(parts, "A", GamepadButtons.A, 9.6, 0.2, FaceAColor, "A");
        AddButton(parts, "X", GamepadButtons.X, 7.0, 2.8, FaceXColor, "X");
        AddButton(parts, "B", GamepadButtons.B, 12.2, 2.8, FaceBColor, "B");

        // ---- d-pad: four independent arms ---------------------------------------------------
        AddDpad(parts, "DpadUp", GamepadButtons.DpadUp, "\u25B2", -10.0, -1.0, 1.1, 2.4);
        AddDpad(parts, "DpadDown", GamepadButtons.DpadDown, "\u25BC", -10.0, -3.4, 1.1, 2.4);
        AddDpad(parts, "DpadLeft", GamepadButtons.DpadLeft, "\u25C0", -11.2, -2.2, 2.4, 1.1);
        AddDpad(parts, "DpadRight", GamepadButtons.DpadRight, "\u25B6", -8.8, -2.2, 2.4, 1.1);

        // ---- view / menu / guide -------------------------------------------------------------
        AddDiscButton(parts, "View", GamepadButtons.Back, -2.6, 0.4, 1.0, 0.45, 12, DpadColor, "View");
        AddDiscButton(parts, "Menu", GamepadButtons.Start, 2.6, 0.4, 1.0, 0.45, 12, DpadColor, "Menu");
        AddDiscButton(parts, "Guide", GamepadButtons.Guide, 0, 3.6, 2.0, 0.6, 18, GuideColor, "Guide");

        // ---- bumpers and triggers (top edge) -------------------------------------------------
        AddBoxPart(parts, "LB", GamepadButtons.LeftBumper, "LB", GamepadPartKind.Bumper, -8.2, 7.5, FaceZ + 0.75, 8.6, 1.9, 1.5, TriggerColor);
        AddBoxPart(parts, "RB", GamepadButtons.RightBumper, "RB", GamepadPartKind.Bumper, 8.2, 7.5, FaceZ + 0.75, 8.6, 1.9, 1.5, TriggerColor);

        // The trigger caps sit behind the front face so a pull sinks them into the body; the static
        // housings below them keep the silhouette readable when the analog value reaches 1.
        AddBoxPart(parts, "LTHousing", null, null, GamepadPartKind.Body, -8.2, 9.9, -0.7, 6.8, 1.8, 2.4, Color.FromRgb(0x3C, 0x44, 0x53));
        AddBoxPart(parts, "RTHousing", null, null, GamepadPartKind.Body, 8.2, 9.9, -0.7, 6.8, 1.8, 2.4, Color.FromRgb(0x3C, 0x44, 0x53));
        AddBoxPart(parts, "LT", GamepadButtons.LeftTrigger, "LT", GamepadPartKind.Trigger, -8.2, 11.6, -0.6, 6.4, 5.6, 2.0, TriggerColor,
            new Vector3D(0, -2.0, 1.05));
        AddBoxPart(parts, "RT", GamepadButtons.RightTrigger, "RT", GamepadPartKind.Trigger, 8.2, 11.6, -0.6, 6.4, 5.6, 2.0, TriggerColor,
            new Vector3D(0, -2.0, 1.05));

        // ---- back paddles (front-facing strip between the grips) -----------------------------
        string[] paddleNames = ["P1", "P2", "P3", "P4"];
        long[] paddleIds =
        [
            GamepadButtons.Paddle1, GamepadButtons.Paddle2, GamepadButtons.Paddle3, GamepadButtons.Paddle4,
        ];
        for (int i = 0; i < paddleNames.Length; i++)
        {
            AddBoxPart(
                parts,
                paddleNames[i],
                paddleIds[i],
                paddleNames[i],
                GamepadPartKind.Paddle,
                -6.6 + (i * 4.4),
                -8.8,
                0.5,
                2.9,
                1.5,
                1.0,
                PaddleColor);
        }

        // ---- soft contact shadow ------------------------------------------------------------
        MeshBuilder shadow = new();
        shadow.AddEllipse(17.5, 11.5, 28);
        parts.Add(new GamepadPart(
            "Shadow",
            null,
            GamepadPartKind.Shadow,
            shadow.Freeze(),
            new Point3D(0, -0.6, -3.0),
            default,
            Color.FromArgb(64, 0, 0, 0)));

        return parts;
    }

    /// <summary>
    /// A standalone thumbstick cap (dark cylinder + concave top, ~200 triangles) used when the
    /// downloaded model has no separate stick meshes. The mesh is centred on its own bounding-box
    /// centre in X/Y and has its concave top facing +Z; <see cref="Gamepad3DView"/> rotates it about
    /// that centre so it tilts in place. Position it with <see cref="StickAnchors"/>.
    /// </summary>
    public static MeshGeometry3D BuildStickCapMesh()
    {
        MeshBuilder builder = new();
        builder.AddStickCap(radius: 2.25, height: 2.0, dip: 0.42, segments: 24);
        return builder.Freeze();
    }

    /// <summary>Total triangle count of the whole scene (asserted by the regression suite).</summary>
    public static int TotalTriangles(IEnumerable<GamepadPart> parts)
    {
        int total = 0;
        foreach (GamepadPart part in parts)
        {
            total += part.TriangleCount;
        }

        return total;
    }

    private static void AddButton(
        List<GamepadPart> parts,
        string name,
        long buttonId,
        double x,
        double y,
        Color color,
        string label)
    {
        AddDiscButton(parts, name, buttonId, x, y, 1.25, 0.55, 14, color, label);
    }

    private static void AddDiscButton(
        List<GamepadPart> parts,
        string name,
        long buttonId,
        double x,
        double y,
        double radius,
        double depth,
        int segments,
        Color color,
        string label)
    {
        parts.Add(new GamepadPart(
            name,
            buttonId,
            GamepadPartKind.Button,
            Disc(radius, depth, segments),
            new Point3D(x, y, FaceZ),
            default,
            color,
            label,
            new Vector3D(0, 0, depth + 0.05)));
    }

    private static void AddDpad(
        List<GamepadPart> parts,
        string name,
        long buttonId,
        string label,
        double x,
        double y,
        double width,
        double height)
    {
        const double depth = 0.55;
        parts.Add(new GamepadPart(
            name,
            buttonId,
            GamepadPartKind.Dpad,
            Box(width, height, depth),
            new Point3D(x, y, FaceZ + (depth / 2)),
            default,
            DpadColor,
            label,
            new Vector3D(0, 0, depth + 0.05)));
    }

    private static void AddBoxPart(
        List<GamepadPart> parts,
        string name,
        long? buttonId,
        string? label,
        GamepadPartKind kind,
        double x,
        double y,
        double z,
        double width,
        double height,
        double depth,
        Color color,
        Vector3D labelOffset = default)
    {
        parts.Add(new GamepadPart(
            name,
            buttonId,
            kind,
            Box(width, height, depth),
            new Point3D(x, y, z),
            default,
            color,
            label,
            labelOffset == default ? new Vector3D(0, 0, (depth / 2) + 0.05) : labelOffset));
    }

    // ------------------------------------------------------------------ primitives

    /// <summary>A closed cylinder along +Z, base at <paramref name="z"/>.</summary>
    private static MeshGeometry3D Disc(double radius, double depth, int segments)
    {
        MeshBuilder builder = new();
        builder.AddDisc(radius, depth, segments, backCap: true, z: 0);
        return builder.Freeze();
    }

    private static MeshGeometry3D Box(double width, double height, double depth)
    {
        MeshBuilder builder = new();
        builder.AddBox(width, height, depth);
        return builder.Freeze();
    }

    /// <summary>Bevelled extrusion of a closed 2D outline: two caps and three side bands.</summary>
    private static MeshGeometry3D Extrusion(
        Func<double, IReadOnlyList<Point>> outlineAt,
        double halfDepth,
        double bevel)
    {
        MeshBuilder builder = new();
        double[] insets = [bevel, 0, 0, bevel];
        double[] zs =
        [
            -halfDepth,
            -halfDepth + bevel,
            halfDepth - bevel,
            halfDepth,
        ];

        List<IReadOnlyList<Point>> rings = new(4);
        foreach (double inset in insets)
        {
            rings.Add(outlineAt(inset));
        }

        builder.AddCap(rings[0], zs[0], front: false);
        builder.AddCap(rings[3], zs[3], front: true);

        for (int band = 0; band < 3; band++)
        {
            IReadOnlyList<Point> lower = rings[band];
            IReadOnlyList<Point> upper = rings[band + 1];
            for (int i = 0; i < lower.Count; i++)
            {
                int next = (i + 1) % lower.Count;
                Point3D a = new(lower[i].X, lower[i].Y, zs[band]);
                Point3D b = new(lower[next].X, lower[next].Y, zs[band]);
                Point3D c = new(upper[next].X, upper[next].Y, zs[band + 1]);
                Point3D d = new(upper[i].X, upper[i].Y, zs[band + 1]);
                Vector3D outward = new(a.X + b.X, a.Y + b.Y, 0);
                builder.AddQuad(a, b, c, d, outward);
            }
        }

        return builder.Freeze();
    }

    /// <summary>Rounded rectangle centred on the origin, counter-clockwise, 4 segments per corner.</summary>
    private static IReadOnlyList<Point> RoundedRect(double halfWidth, double halfHeight, double radius, double inset)
    {
        double hx = Math.Max(0.2, halfWidth - inset);
        double hy = Math.Max(0.2, halfHeight - inset);
        double r = Math.Clamp(radius - inset, 0.08, Math.Min(hx, hy));

        var points = new List<Point>(16);
        (double Cx, double Cy, double Start)[] corners =
        [
            (hx - r, hy - r, 0),
            (-(hx - r), hy - r, 90),
            (-(hx - r), -(hy - r), 180),
            (hx - r, -(hy - r), 270),
        ];

        const int Steps = 4;
        foreach ((double cx, double cy, double start) in corners)
        {
            for (int step = 0; step < Steps; step++)
            {
                double angle = (start + (step * (90.0 / Steps))) * Math.PI / 180.0;
                points.Add(new Point(cx + (r * Math.Cos(angle)), cy + (r * Math.Sin(angle))));
            }
        }

        return points;
    }

    private static IReadOnlyList<Point> Circle(double radius, int segments)
    {
        var points = new List<Point>(segments);
        for (int i = 0; i < segments; i++)
        {
            double angle = i * 2 * Math.PI / segments;
            points.Add(new Point(radius * Math.Cos(angle), radius * Math.Sin(angle)));
        }

        return points;
    }

    /// <summary>Ellipse outline; the grips use it so they stay flat instead of bulging forward.</summary>
    private static IReadOnlyList<Point> Ellipse(double radiusX, double radiusY, int segments)
    {
        var points = new List<Point>(segments);
        for (int i = 0; i < segments; i++)
        {
            double angle = i * 2 * Math.PI / segments;
            points.Add(new Point(radiusX * Math.Cos(angle), radiusY * Math.Sin(angle)));
        }

        return points;
    }

    // ------------------------------------------------------------------ mesh accumulator

    private sealed class MeshBuilder
    {
        private readonly Point3DCollection _positions = new();
        private readonly Vector3DCollection _normals = new();
        private readonly Int32Collection _indices = new();

        public void AddTriangle(Point3D a, Point3D b, Point3D c, Vector3D expected)
        {
            Vector3D normal = Vector3D.CrossProduct(b - a, c - a);
            if (normal.LengthSquared < 1e-9)
            {
                return;
            }

            if (Vector3D.DotProduct(normal, expected) < 0)
            {
                (b, c) = (c, b);
                normal = -normal;
            }

            normal.Normalize();
            int start = _positions.Count;
            _positions.Add(a);
            _positions.Add(b);
            _positions.Add(c);
            _normals.Add(normal);
            _normals.Add(normal);
            _normals.Add(normal);
            _indices.Add(start);
            _indices.Add(start + 1);
            _indices.Add(start + 2);
        }

        public void AddQuad(Point3D a, Point3D b, Point3D c, Point3D d, Vector3D expected)
        {
            Vector3D normal = Vector3D.CrossProduct(b - a, c - a);
            if (normal.LengthSquared < 1e-9)
            {
                return;
            }

            if (Vector3D.DotProduct(normal, expected) < 0)
            {
                (b, d) = (d, b);
                normal = -normal;
            }

            normal.Normalize();
            int start = _positions.Count;
            _positions.Add(a);
            _positions.Add(b);
            _positions.Add(c);
            _positions.Add(d);
            for (int i = 0; i < 4; i++)
            {
                _normals.Add(normal);
            }

            _indices.Add(start);
            _indices.Add(start + 1);
            _indices.Add(start + 2);
            _indices.Add(start);
            _indices.Add(start + 2);
            _indices.Add(start + 3);
        }

        public void AddCap(IReadOnlyList<Point> ring, double z, bool front)
        {
            Vector3D expected = new(0, 0, front ? 1 : -1);
            Point3D centre = new(0, 0, z);
            for (int i = 0; i < ring.Count; i++)
            {
                int next = (i + 1) % ring.Count;
                Point3D a = new(ring[i].X, ring[i].Y, z);
                Point3D b = new(ring[next].X, ring[next].Y, z);
                if (front)
                {
                    AddTriangle(centre, a, b, expected);
                }
                else
                {
                    AddTriangle(centre, b, a, expected);
                }
            }
        }

        public void AddDisc(double radius, double depth, int segments, bool backCap, double z)
        {
            IReadOnlyList<Point> ring = Circle(radius, segments);
            AddCap(ring, z + depth, front: true);
            if (backCap)
            {
                AddCap(ring, z, front: false);
            }

            for (int i = 0; i < ring.Count; i++)
            {
                int next = (i + 1) % ring.Count;
                Point3D a = new(ring[i].X, ring[i].Y, z);
                Point3D b = new(ring[next].X, ring[next].Y, z);
                Point3D c = new(ring[next].X, ring[next].Y, z + depth);
                Point3D d = new(ring[i].X, ring[i].Y, z + depth);
                AddQuad(a, b, c, d, new Vector3D(a.X + b.X, a.Y + b.Y, 0));
            }
        }

        public void AddBox(double width, double height, double depth)
        {
            double x = width / 2;
            double y = height / 2;
            double z = depth / 2;

            AddQuad(new(-x, -y, z), new(x, -y, z), new(x, y, z), new(-x, y, z), new Vector3D(0, 0, 1));
            AddQuad(new(x, -y, -z), new(-x, -y, -z), new(-x, y, -z), new(x, y, -z), new Vector3D(0, 0, -1));
            AddQuad(new(x, -y, z), new(x, -y, -z), new(x, y, -z), new(x, y, z), new Vector3D(1, 0, 0));
            AddQuad(new(-x, -y, -z), new(-x, -y, z), new(-x, y, z), new(-x, y, -z), new Vector3D(-1, 0, 0));
            AddQuad(new(-x, y, z), new(x, y, z), new(x, y, -z), new(-x, y, -z), new Vector3D(0, 1, 0));
            AddQuad(new(-x, -y, -z), new(x, -y, -z), new(x, -y, z), new(-x, -y, z), new Vector3D(0, -1, 0));
        }

        public void AddEllipse(double radiusX, double radiusY, int segments)
        {
            Point3D centre = new(0, 0, 0);
            for (int i = 0; i < segments; i++)
            {
                double angle = i * 2 * Math.PI / segments;
                double nextAngle = (i + 1) * 2 * Math.PI / segments;
                Point3D a = new(radiusX * Math.Cos(angle), radiusY * Math.Sin(angle), 0);
                Point3D b = new(radiusX * Math.Cos(nextAngle), radiusY * Math.Sin(nextAngle), 0);
                AddTriangle(centre, a, b, new Vector3D(0, 0, 1));
            }
        }

        /// <summary>
        /// Closed thumbstick cap: a cylindrical wall from z = 0 to <paramref name="height"/>, a flat
        /// bottom and a concave (dished) top. Every ring is centred on (0, 0), so the mesh's bounding
        /// box centre is also the tilt pivot.
        /// </summary>
        public void AddStickCap(double radius, double height, double dip, int segments)
        {
            // Outer wall.
            for (int i = 0; i < segments; i++)
            {
                double angle = i * 2 * Math.PI / segments;
                double nextAngle = (i + 1) * 2 * Math.PI / segments;
                double cos = Math.Cos(angle);
                double sin = Math.Sin(angle);
                double cosNext = Math.Cos(nextAngle);
                double sinNext = Math.Sin(nextAngle);

                Point3D a = new(radius * cos, radius * sin, 0);
                Point3D b = new(radius * cosNext, radius * sinNext, 0);
                Point3D c = new(radius * cosNext, radius * sinNext, height);
                Point3D d = new(radius * cos, radius * sin, height);
                AddQuad(a, b, c, d, new Vector3D(cos + cosNext, sin + sinNext, 0));
            }

            // Flat bottom (faces -Z).
            Point3D bottom = new(0, 0, 0);
            for (int i = 0; i < segments; i++)
            {
                double angle = i * 2 * Math.PI / segments;
                double nextAngle = (i + 1) * 2 * Math.PI / segments;
                Point3D a = new(radius * Math.Cos(nextAngle), radius * Math.Sin(nextAngle), 0);
                Point3D b = new(radius * Math.Cos(angle), radius * Math.Sin(angle), 0);
                AddTriangle(bottom, a, b, new Vector3D(0, 0, -1));
            }

            // Concave top: rim → mid ring → inner ring → centre dish.
            double[] ringRadii = [radius, radius * 0.68, radius * 0.34];
            double[] ringZ = [height, height - (dip * 0.55), height - (dip * 0.92)];
            for (int ring = 0; ring < ringRadii.Length - 1; ring++)
            {
                for (int i = 0; i < segments; i++)
                {
                    double angle = i * 2 * Math.PI / segments;
                    double nextAngle = (i + 1) * 2 * Math.PI / segments;
                    Point3D a = new(ringRadii[ring] * Math.Cos(angle), ringRadii[ring] * Math.Sin(angle), ringZ[ring]);
                    Point3D b = new(ringRadii[ring] * Math.Cos(nextAngle), ringRadii[ring] * Math.Sin(nextAngle), ringZ[ring]);
                    Point3D c = new(ringRadii[ring + 1] * Math.Cos(nextAngle), ringRadii[ring + 1] * Math.Sin(nextAngle), ringZ[ring + 1]);
                    Point3D d = new(ringRadii[ring + 1] * Math.Cos(angle), ringRadii[ring + 1] * Math.Sin(angle), ringZ[ring + 1]);
                    AddQuad(a, b, c, d, new Vector3D(a.X + b.X, a.Y + b.Y, 1));
                }
            }

            Point3D centre = new(0, 0, height - dip);
            for (int i = 0; i < segments; i++)
            {
                double angle = i * 2 * Math.PI / segments;
                double nextAngle = (i + 1) * 2 * Math.PI / segments;
                Point3D a = new(ringRadii[^1] * Math.Cos(angle), ringRadii[^1] * Math.Sin(angle), ringZ[^1]);
                Point3D b = new(ringRadii[^1] * Math.Cos(nextAngle), ringRadii[^1] * Math.Sin(nextAngle), ringZ[^1]);
                AddTriangle(centre, a, b, new Vector3D(0, 0, 1));
            }
        }

        public MeshGeometry3D Freeze()
        {
            _positions.Freeze();
            _normals.Freeze();
            _indices.Freeze();

            var mesh = new MeshGeometry3D
            {
                Positions = _positions,
                Normals = _normals,
                TriangleIndices = _indices,
            };

            mesh.Freeze();
            return mesh;
        }
    }
}
