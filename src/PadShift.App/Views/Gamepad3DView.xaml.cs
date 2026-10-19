using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using PadShift.App.Infrastructure;
using PadShift.App.ViewModels;

namespace PadShift.App.Views;

/// <summary>
/// The 3D controller: a downloaded, CC0 Xbox-style model loaded through
/// <see cref="GamepadModelLoader"/> (OBJ + MTL, no third-party renderer). If the asset is missing or
/// malformed the original <see cref="GamepadMeshBuilder"/> scene is used instead. Sticks tilt,
/// triggers pull in, buttons depress and glow cyan, and clicking a part selects it on
/// <see cref="GamepadViewModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// The view owns no poll: it repaints only when the gamepad view model raises
/// <see cref="INotifyPropertyChanged.PropertyChanged"/> from its existing 20 Hz poll, so hiding the
/// window also freezes the scene. The only extra timer is the optional 20 Hz idle sway, which runs
/// solely while no controller is connected and the control is visible.
/// </para>
/// <para>
/// Materials are frozen and pre-built per control (four variants), so a frame only swaps references
/// and updates transform values: no per-frame allocations. Static shell geometry is merged into a
/// single <see cref="ModelVisual3D"/>; only mapped controls get their own visual.
/// </para>
/// </remarks>
public partial class Gamepad3DView : UserControl
{
    /// <summary>Maximum stick tilt for a fully deflected axis (within the 20–25° tuned range).</summary>
    public const double StickTiltDegrees = 24.0;

    /// <summary>How far a fully pulled trigger travels into the body, in scene units.</summary>
    public const double TriggerPullDepth = 1.4;

    /// <summary>How far a pressed digital control sinks into the body, in scene units.</summary>
    public const double ButtonPressDepth = 0.28;

    /// <summary>Vertical field of view; a tight lens keeps the perspective product-shot look.</summary>
    public const double FieldOfView = 35.0;

    /// <summary>Idle sway amplitude (total sweep is twice this) while no controller is connected.</summary>
    public const double IdleSwayDegrees = 1.0;

    /// <summary>Orbit yaw per dragged pixel (drag ~520 px to flip to the back).</summary>
    public const double OrbitYawPerPixel = 0.35;

    /// <summary>Orbit pitch per dragged pixel, clamped to <see cref="OrbitMaxPitchDegrees"/>.</summary>
    public const double OrbitPitchPerPixel = 0.25;

    /// <summary>Vertical orbit limit; beyond this the pad would flip over and confuse the axes.</summary>
    public const double OrbitMaxPitchDegrees = 75.0;

    private static readonly Vector3D ViewDirection = Normalize(new Vector3D(0.30, 0.46, 1.0));

    private static readonly Color PressedGlow = Color.FromRgb(0x18, 0x8F, 0xA8);
    private static readonly Color Accent = Color.FromRgb(0x2A, 0x5F, 0x9A);

    private readonly Dictionary<string, List<PartVisual>> _parts = new(StringComparer.Ordinal);
    private readonly Dictionary<GeometryModel3D, long> _buttonByModel = new();
    private readonly List<LabelChip> _labels = new();

    private readonly PerspectiveCamera _camera;
    private readonly ModelVisual3D _root = new();
    private readonly SolidColorBrush _labelBackground;
    private readonly AxisAngleRotation3D _orbitYawAxis = new(new Vector3D(0, 1, 0), 0);
    private readonly AxisAngleRotation3D _orbitPitchAxis = new(new Vector3D(1, 0, 0), 0);
    private readonly RotateTransform3D _orbitYaw;
    private readonly RotateTransform3D _orbitPitch;
    private readonly DispatcherTimer _idleTimer;

    // 用户拖动设定的轨道角（度）；idle sway 叠加在显示角上，不污染这两个值。
    private double _userYaw;
    private double _userPitch;
    private bool _orbitDragging;
    private Point _orbitOrigin;

    private GamepadViewModel? _gamepad;
    private bool _attached;
    private bool _idleRunning;
    private bool _lightsAdded;
    private double _idlePhase;
    private int _staticTriangles;
    private Rect3D _bounds = new(-15, -8, -5, 30, 16, 10);
    private Rect3D _dpadBounds = Rect3D.Empty;
    private Rect3D _guideBounds = Rect3D.Empty;

    // Active mouse-drag on a stick cap: the part id being dragged and the press origin.
    private string? _dragStickName;
    private Point _dragOrigin;
    private bool _draggingStick;

    public Gamepad3DView()
    {
        InitializeComponent();

        _camera = new PerspectiveCamera(new Point3D(0, 8, 52), new Vector3D(0, -1, -1), new Vector3D(0, 1, 0), FieldOfView);
        Viewport.Camera = _camera;

        // WPF's 3D pipeline antialiases hardware-rendered Viewport3D content (4x MSAA). EdgeMode is
        // the only knob exposed on a FrameworkElement; Unspecified keeps the antialiased default, and
        // the bitmap cache lets the compositor reuse the static shell between 20 Hz updates.
        RenderOptions.SetEdgeMode(Viewport, EdgeMode.Unspecified);
        RenderOptions.SetBitmapScalingMode(Viewport, BitmapScalingMode.HighQuality);
        Viewport.CacheMode = new BitmapCache(1.0) { EnableClearType = false, SnapsToDevicePixels = false };

        _orbitYaw = new RotateTransform3D(_orbitYawAxis, 0, 0, 0);
        _orbitPitch = new RotateTransform3D(_orbitPitchAxis, 0, 0, 0);
        var orbit = new Transform3DGroup();
        orbit.Children.Add(_orbitPitch);
        orbit.Children.Add(_orbitYaw);
        _root.Transform = orbit;

        _labelBackground = new SolidColorBrush(Color.FromArgb(0xC0, 0x08, 0x0B, 0x12));
        _labelBackground.Freeze();

        _idleTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _idleTimer.Tick += OnIdleTick;

        try
        {
            BuildScene();
            SceneReady = _parts.Count > 0;
            if (!SceneReady)
            {
                throw new InvalidOperationException("3D scene has no parts.");
            }
        }
        catch (Exception ex)
        {
            // A scene that cannot be built must not take the window down: MainWindow then keeps the
            // 2D vector pad visible (see GamepadViewMode.Resolve).
            SceneReady = false;
            Debug.WriteLine("[PadShift] 3D scene failed: " + ex.Message);
        }

        Viewport.MouseLeftButtonDown += OnViewportMouseLeftButtonDown;
        Viewport.MouseMove += OnViewportMouseMove;
        Viewport.MouseLeftButtonUp += OnViewportMouseLeftButtonUp;
        Viewport.LostMouseCapture += OnViewportLostMouseCapture;
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
        Unloaded += (_, _) => Detach();
    }

    /// <summary>
    /// Test-only constructor: builds the scene from an explicit model so the unit suite can exercise
    /// a single-mesh model without touching the bundled asset. The normal asset load runs first and
    /// is then discarded by <see cref="ResetScene"/>.
    /// </summary>
    internal Gamepad3DView(GamepadModel model) : this()
    {
        ResetScene();
        BuildDownloadedScene(model);
        UsingDownloadedModel = true;
        ModelSource = model.SourcePath;
        SceneReady = _parts.Count > 0;
    }

    /// <summary>False when the scene could not be built; the caller falls back to 2D.</summary>
    public bool SceneReady { get; private set; }

    /// <summary>True when the bundled downloaded model (not the procedural fallback) is on screen.</summary>
    public bool UsingDownloadedModel { get; private set; }

    /// <summary>Source path of the loaded OBJ; null for the procedural fallback.</summary>
    public string? ModelSource { get; private set; }

    /// <summary>Catalog id of the model on screen; <c>procedural</c> for the fallback pad.</summary>
    public string CurrentModelId { get; private set; } = GamepadModelCatalog.DefaultId;

    /// <summary>
    /// Rebuilds the scene with another catalog model (unknown ids fall back to the default). The
    /// same id is a no-op. Returns false when even the fallback could not build a scene.
    /// </summary>
    public bool SwitchModel(string modelId)
    {
        string resolved = GamepadModelCatalog.Find(modelId).Id;
        if (resolved == CurrentModelId)
        {
            return SceneReady;
        }

        ResetScene();
        BuildScene(resolved);
        SceneReady = _parts.Count > 0;

        if (SceneReady)
        {
            SyncAll();
            FitCamera();
            UpdateLabels();
        }

        return SceneReady;
    }

    /// <summary>Number of independently animated <see cref="ModelVisual3D"/> parts in the scene.</summary>
    public int PartCount => _parts.Count;

    /// <summary>
    /// Testable snapshot of the animated thumbsticks: part id, current axis angles and the pivot the
    /// rotations use. The live poll updates these from <see cref="GamepadViewModel.LeftStickX"/> etc.;
    /// an empty list means the scene has no stick visual at all.
    /// </summary>
    public IReadOnlyList<StickTransform> StickTransforms
    {
        get
        {
            var result = new List<StickTransform>(2);
            foreach (string name in new[] { "StickLeft", "StickRight" })
            {
                if (!_parts.TryGetValue(name, out List<PartVisual>? parts))
                {
                    continue;
                }

                foreach (PartVisual part in parts)
                {
                    result.Add(new StickTransform(
                        name,
                        part.AxisX.Angle,
                        part.AxisY.Angle,
                        part.Pivot,
                        part.Placement,
                        part.IsModelPart ? "model" : "procedural"));
                }
            }

            return result;
        }
    }

    /// <summary>Total ModelVisual3D children under the scene root (shell + controls + shadow).</summary>
    public int VisualCount => _root.Children.Count;

    /// <summary>Total triangles in the scene (checked by the perf-budget tests).</summary>
    public int TriangleCount
    {
        get
        {
            int total = 0;
            foreach (List<PartVisual> list in _parts.Values)
            {
                foreach (PartVisual part in list)
                {
                    total += part.TriangleCount;
                }
            }

            return total + _staticTriangles;
        }
    }

    // ------------------------------------------------------------------ scene construction

    private void BuildScene()
    {
        EnsureLights();
        BuildScene(GamepadModelCatalog.DefaultId);
    }

    /// <summary>Adds the studio lights once; scene rebuilds must not stack duplicate copies.</summary>
    private void EnsureLights()
    {
        if (_lightsAdded)
        {
            return;
        }

        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x1A, 0x1C, 0x22)));
        lights.Children.Add(new DirectionalLight(
            Color.FromRgb(0xFF, 0xEF, 0xD8), Normalize(new Vector3D(0.35, -0.80, -0.50))));
        lights.Children.Add(new DirectionalLight(
            Color.FromRgb(0x2E, 0x4E, 0x7A), Normalize(new Vector3D(-0.72, 0.20, -0.60))));
        lights.Children.Add(new DirectionalLight(
            Color.FromRgb(0x26, 0x2B, 0x33), Normalize(new Vector3D(0.50, 0.30, 0.40))));
        lights.Freeze();

        Viewport.Children.Add(new ModelVisual3D { Content = lights });
        Viewport.Children.Add(_root);
        _lightsAdded = true;
    }

    /// <summary>Builds the scene for a catalog model id, falling back to the procedural pad.</summary>
    private void BuildScene(string modelId)
    {
        GamepadModelInfo info = GamepadModelCatalog.Find(modelId);
        if (GamepadModelLoader.TryLoad(info, out GamepadModel? model, out string message) && model is not null)
        {
            try
            {
                BuildDownloadedScene(model);
                UsingDownloadedModel = true;
                ModelSource = model.SourcePath;
                CurrentModelId = info.Id;
                Debug.WriteLine("[PadShift] " + message);
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[PadShift] 3D model scene failed: " + ex.Message + "; using procedural fallback.");
                ResetScene();
            }
        }
        else
        {
            Debug.WriteLine("[PadShift] " + message);
        }

        BuildProceduralScene();
        CurrentModelId = GamepadModelCatalog.ProceduralId;
    }

    private void BuildDownloadedScene(GamepadModel model)
    {
        _bounds = model.Bounds;
        var staticGroup = new Model3DGroup();
        var controlBounds = new Dictionary<string, Rect3D>(StringComparer.Ordinal);

        foreach (GamepadModelPart part in model.Parts)
        {
            if (!part.IsControl)
            {
                // The d-pad base is one shell mesh in the source model but still glows when a
                // direction is pressed, so it gets its own (non-clickable) visual.
                if (string.Equals(part.SourceName, "DpadBase", StringComparison.Ordinal))
                {
                    _dpadBounds = part.Bounds;
                    AddPartVisual(
                        new GamepadControlMap("Dpad", null, GamepadPartKind.Dpad, null),
                        part.Mesh,
                        part.Color,
                        part.Center,
                        isModelPart: true);
                    continue;
                }

                var geometry = new GeometryModel3D(part.Mesh, CreateMaterial(Lift(part.Color)));
                geometry.Freeze();
                staticGroup.Children.Add(geometry);
                continue;
            }

            if (string.Equals(part.Map.Name, "Guide", StringComparison.Ordinal))
            {
                _guideBounds = _guideBounds.IsEmpty ? part.Bounds : Union(_guideBounds, part.Bounds);
            }

            AddPartVisual(part.Map, part.Mesh, part.Color, part.Center, isModelPart: true, ModelControlScale(part.Map.Name));
            controlBounds[part.Map.Name] = controlBounds.TryGetValue(part.Map.Name, out Rect3D existing)
                ? Union(existing, part.Bounds)
                : part.Bounds;
        }

        if (staticGroup.Children.Count > 0)
        {
            staticGroup.Freeze();
            _root.Children.Add(new ModelVisual3D { Content = staticGroup });

            foreach (Model3D child in staticGroup.Children)
            {
                if (child is GeometryModel3D geometry && geometry.Geometry is MeshGeometry3D mesh)
                {
                    _staticTriangles += mesh.TriangleIndices.Count / 3;
                }
            }
        }

        AddShadow(_bounds);

        // A model with no named controls keeps its beautiful shell but gets the procedural caps on
        // top, positioned with the tuned anchor table.
        bool overlayOnly = model.NeedsControlOverlay;
        AddProceduralOverlays(overlayOnly ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(_parts.Keys, StringComparer.Ordinal));
        AddStickOverlays();

        foreach ((string name, Rect3D bounds) in controlBounds)
        {
            if (!overlayOnly && _parts.ContainsKey(name))
            {
                AddLabelForControl(name, bounds);
            }
        }

        FitCamera();
    }

    private void BuildProceduralScene()
    {
        _bounds = new Rect3D(-16, -9, -6, 32, 21, 12);
        _staticTriangles = 0;
        AddShadow(_bounds);

        foreach (GamepadPart part in GamepadMeshBuilder.Build())
        {
            if (part.Kind == GamepadPartKind.Shadow)
            {
                continue;
            }

            AddProceduralPart(part);
            if (part.Label is { Length: > 0 } text)
            {
                AddLabel(text, part.LabelAnchor);
            }
        }

        FitCamera();
    }

    private void AddProceduralOverlays(HashSet<string> present)
    {
        IReadOnlyList<GamepadPart> procedural = GamepadMeshBuilder.Build();
        bool all = present.Count == 0;

        foreach (GamepadPart part in procedural)
        {
            // Sticks get their own cap mesh and anchor table (see AddStickOverlays).
            if (part.Name is "StickLeft" or "StickRight")
            {
                continue;
            }

            if (part.ButtonId is null || (!all && present.Contains(part.Name)))
            {
                continue;
            }

            Point3D position;
            double scale;
            Vector3D rotation;
            Vector3D pressAxis;

            if (all)
            {
                position = AnchorFor(part);
                scale = 0.85;
                rotation = default;
                pressAxis = default;
            }
            else if (part.Name.StartsWith("Dpad", StringComparison.Ordinal))
            {
                position = DpadAnchor(part.Name);
                scale = 0.62;
                rotation = new Vector3D(90, 0, 0);
                pressAxis = new Vector3D(0, -1, 0);
            }
            else if (part.Name is "View" or "Menu")
            {
                position = ViewMenuAnchor(part.Name);
                scale = 0.70;
                rotation = new Vector3D(-90, 0, 0);
                pressAxis = new Vector3D(0, -1, 0);
            }
            else if (part.Name is "LB" or "RB" or "LT" or "RT")
            {
                position = ShoulderAnchor(part.Name);
                scale = 0.55;
                rotation = default;
                pressAxis = default;
            }
            else if (part.Name.StartsWith('P'))
            {
                position = PaddleAnchor(part.Name);
                scale = 0.42;
                rotation = default;
                pressAxis = default;
            }
            else
            {
                continue;
            }

            AddOverlay(part, position, scale, rotation, pressAxis);
        }
    }

    /// <summary>
    /// Adds the two procedural thumbstick caps when the loaded model has no separate stick meshes.
    /// The caps are placed with <see cref="StickAnchors"/> (nudge those fractions to fit another
    /// model) and are registered under the same <c>StickLeft</c>/<c>StickRight</c> part ids as model
    /// meshes, so the exact same live animation path drives them.
    /// </summary>
    private void AddStickOverlays()
    {
        string[] names = ["StickLeft", "StickRight"];
        string[] labels = ["L3", "R3"];
        long[] buttonIds = [GamepadButtons.LeftStickClick, GamepadButtons.RightStickClick];

        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            if (_parts.ContainsKey(name))
            {
                // The model already provides a dedicated stick mesh; leave it alone.
                continue;
            }

            Point3D anchor = StickAnchors.Resolve(name, _bounds);
            var map = new GamepadControlMap(name, buttonIds[i], GamepadPartKind.Stick, labels[i]);
            AddPartVisual(
                map,
                GamepadMeshBuilder.BuildStickCapMesh(),
                Color.FromRgb(0x12, 0x16, 0x1E),
                anchor,
                isModelPart: false);

            AddLabel(labels[i], anchor + new Vector3D(0, 2.3, 1.0));
        }
    }

    /// <summary>Maps a procedural control onto the loaded shell using the tuned proportional anchors.</summary>
    private Point3D AnchorFor(GamepadPart part)
    {
        if (ModelAnchors.Positions.TryGetValue(part.Name, out (double X, double Y) anchor))
        {
            return new Point3D(
                _bounds.X + ((0.5 + anchor.X) * _bounds.SizeX),
                _bounds.Y + ((0.5 + anchor.Y) * _bounds.SizeY),
                _bounds.Z + _bounds.SizeZ + 0.35);
        }

        return new Point3D(
            _bounds.X + (_bounds.SizeX / 2),
            _bounds.Y + (_bounds.SizeY / 2),
            _bounds.Z + _bounds.SizeZ + 0.35);
    }

    private Point3D DpadAnchor(string name)
    {
        Point3D center = Center(_dpadBounds);
        double dx = _dpadBounds.SizeX * 0.28;
        double dz = _dpadBounds.SizeZ * 0.28;
        double y = _dpadBounds.Y + (_dpadBounds.SizeY / 2) + 0.30;

        return name switch
        {
            "DpadUp" => new Point3D(center.X, y, center.Z + dz),
            "DpadDown" => new Point3D(center.X, y, center.Z - dz),
            "DpadLeft" => new Point3D(center.X - dx, y, center.Z),
            _ => new Point3D(center.X + dx, y, center.Z),
        };
    }

    private Point3D ViewMenuAnchor(string name)
    {
        Point3D center = Center(_guideBounds);
        double y = _guideBounds.Y + (_guideBounds.SizeY / 2) + 0.35;
        return new Point3D(center.X + (name == "View" ? -2.0 : 2.0), y, center.Z);
    }

    private Point3D PaddleAnchor(string name)
    {
        int index = name switch
        {
            "P1" => 0,
            "P2" => 1,
            "P3" => 2,
            _ => 3,
        };

        double centerX = _bounds.X + (_bounds.SizeX / 2);
        return new Point3D(
            centerX + (-4.8 + (index * 3.2)),
            _bounds.Y + 1.2,
            _bounds.Z + _bounds.SizeZ - 1.3);
    }

    private Point3D ShoulderAnchor(string name)
    {
        double centerX = _bounds.X + (_bounds.SizeX / 2);
        double centerZ = _bounds.Z + (_bounds.SizeZ / 2);
        bool left = name is "LB" or "LT";
        bool trigger = name is "LT" or "RT";

        return new Point3D(
            centerX + (left ? -1 : 1) * _bounds.SizeX * 0.27,
            _bounds.Y + _bounds.SizeY + 0.15 - (trigger ? 0.45 : 0),
            centerZ - (_bounds.SizeZ * (trigger ? 0.22 : 0.10)));
    }

    private static double ModelControlScale(string name) => name switch
    {
        "A" or "B" or "X" or "Y" => 1.45,
        "Guide" => 1.20,
        _ => 1.0,
    };

    private static Point3D Center(Rect3D bounds) => new(
        bounds.X + (bounds.SizeX / 2),
        bounds.Y + (bounds.SizeY / 2),
        bounds.Z + (bounds.SizeZ / 2));

    private void AddOverlay(GamepadPart part, Point3D position, double scale, Vector3D rotation, Vector3D pressAxis)
    {
        MeshGeometry3D mesh = OffsetMesh(part.Mesh, position);
        var map = new GamepadControlMap(part.Name, part.ButtonId, part.Kind, part.Label);
        bool backPaddle = part.Name.StartsWith('P');
        Color color = backPaddle
            ? Color.FromArgb(0, part.Color.R, part.Color.G, part.Color.B)
            : part.Color;

        AddPartVisual(map, mesh, color, position, isModelPart: true, scale, rotation, pressAxis);

        if (part.Label is { Length: > 0 } text)
        {
            Vector3D offset = backPaddle
                ? new Vector3D(0, -1.1, 0)
                : new Vector3D(0, 0.9, 0);
            AddLabel(text, position + offset);
        }
    }

    /// <summary>Bakes the placement offset into a frozen copy so overlay meshes animate like model parts.</summary>
    private static MeshGeometry3D OffsetMesh(MeshGeometry3D source, Point3D offset)
    {
        var positions = new Point3DCollection(source.Positions.Count);
        foreach (Point3D point in source.Positions)
        {
            positions.Add(new Point3D(point.X + offset.X, point.Y + offset.Y, point.Z + offset.Z));
        }

        var normals = new Vector3DCollection(source.Normals.Count);
        foreach (Vector3D normal in source.Normals)
        {
            normals.Add(normal);
        }

        var indices = new Int32Collection(source.TriangleIndices.Count);
        foreach (int index in source.TriangleIndices)
        {
            indices.Add(index);
        }

        positions.Freeze();
        normals.Freeze();
        indices.Freeze();

        var mesh = new MeshGeometry3D
        {
            Positions = positions,
            Normals = normals,
            TriangleIndices = indices,
        };
        mesh.Freeze();
        return mesh;
    }

    private void AddProceduralPart(GamepadPart part)
    {
        AddPartVisual(
            new GamepadControlMap(part.Name, part.ButtonId, part.Kind, part.Label),
            part.Mesh,
            part.Color,
            part.Position,
            isModelPart: false);
    }

    private void AddPartVisual(
        GamepadControlMap map,
        MeshGeometry3D mesh,
        Color color,
        Point3D placement,
        bool isModelPart,
        double scale = 1.0,
        Vector3D rotationDegrees = default,
        Vector3D pressAxis = default)
    {
        // Every moving part tilts about the centre of its own bounding box, never the model origin
        // (which would make a stick orbit the controller). Sticks are the exception: a real stick
        // pivots on the ball joint at the bottom of its shaft, so tilting about the box centre
        // swings the shaft tip the opposite way into the well (visible clipping) — pivoting on the
        // lowest point of the mesh keeps the shaft planted and only the cap sweeps.
        Point3D pivot = Center(mesh.Bounds);
        if (isModelPart && map.Kind == GamepadPartKind.Stick)
        {
            pivot = new Point3D(pivot.X, pivot.Y, mesh.Bounds.Z);
        }

        Color baseColor = Lift(color);
        bool transparent = baseColor.A == 0;

        Material normal = CreateMaterial(baseColor, glow: null, specular: !transparent);
        Material pressed = transparent
            ? normal
            : CreateMaterial(Mix(baseColor, PressedGlow, 0.12), Scale(PressedGlow, 0.46), specular: true);
        Color selectedTint = Mix(baseColor, Accent, 0.40);
        Material selected = transparent
            ? normal
            : CreateMaterial(selectedTint, Scale(Accent, 0.40), specular: true);
        Material selectedPressed = transparent
            ? normal
            : CreateMaterial(Mix(selectedTint, PressedGlow, 0.18), Scale(PressedGlow, 0.58), specular: true);

        var axisX = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
        var axisY = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        var rotateX = new RotateTransform3D(axisX, pivot);
        var rotateY = new RotateTransform3D(axisY, pivot);
        var translate = new TranslateTransform3D(
            isModelPart ? 0 : placement.X,
            isModelPart ? 0 : placement.Y,
            isModelPart ? 0 : placement.Z);

        var transform = new Transform3DGroup();
        if (scale != 1.0)
        {
            transform.Children.Add(new ScaleTransform3D(scale, scale, scale, pivot.X, pivot.Y, pivot.Z));
        }

        if (rotationDegrees != default)
        {
            if (rotationDegrees.X != 0)
            {
                transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), rotationDegrees.X), pivot));
            }

            if (rotationDegrees.Y != 0)
            {
                transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), rotationDegrees.Y), pivot));
            }

            if (rotationDegrees.Z != 0)
            {
                transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), rotationDegrees.Z), pivot));
            }
        }

        transform.Children.Add(rotateX);
        transform.Children.Add(rotateY);
        transform.Children.Add(translate);

        var part = new PartVisual(
            map,
            new GeometryModel3D(mesh, normal),
            normal,
            pressed,
            selected,
            selectedPressed,
            axisX,
            axisY,
            translate,
            pivot,
            placement,
            isModelPart,
            pressAxis);

        // The model itself must stay unfrozen: its Material is swapped on press/selection. Its mesh
        // and all four materials are frozen, so a swap only changes one reference.
        _root.Children.Add(new ModelVisual3D { Content = part.Model, Transform = transform });

        if (!_parts.TryGetValue(map.Name, out List<PartVisual>? list))
        {
            list = new List<PartVisual>(2);
            _parts[map.Name] = list;
        }

        list.Add(part);

        if (map.ButtonId is { } buttonId)
        {
            _buttonByModel[part.Model] = buttonId;
        }
    }

    private void AddLabelForControl(string name, Rect3D bounds)
    {
        Point3D center = new(
            bounds.X + (bounds.SizeX / 2),
            bounds.Y + (bounds.SizeY / 2),
            bounds.Z + (bounds.SizeZ / 2));

        // The d-pad already has moulded arrows; one "D-pad" caption is clearer than four chips.
        if (name.StartsWith("Dpad", StringComparison.Ordinal))
        {
            if (name == "DpadUp")
            {
                AddLabel("D-pad", center + new Vector3D(0, -1.4, 0));
            }

            return;
        }

        Vector3D offset = new(0, (bounds.SizeY * 0.5) + 0.5, (bounds.SizeZ * 0.5) + 0.5);
        switch (name)
        {
            case "StickLeft":
            case "StickRight":
                offset = new Vector3D(0, (bounds.SizeY * 0.5) + 1.0, (bounds.SizeZ * 0.5) + 0.6);
                break;

            case "LB":
                offset = new Vector3D(-1.4, 0.8, 0);
                break;

            case "RB":
                offset = new Vector3D(1.4, 0.8, 0);
                break;

            case "LT":
                offset = new Vector3D(-2.4, 1.2, 0);
                break;

            case "RT":
                offset = new Vector3D(2.4, 1.2, 0);
                break;

            case "P1":
            case "P2":
            case "P3":
            case "P4":
                offset = new Vector3D(0, -((bounds.SizeY * 0.5) + 0.8), (bounds.SizeZ * 0.5) + 0.5);
                break;
        }

        AddLabel(name switch
        {
            "StickLeft" => "L3",
            "StickRight" => "R3",
            "View" => "View",
            "Menu" => "Menu",
            _ => name,
        }, center + offset);
    }

    private void AddShadow(Rect3D bounds)
    {
        const int Segments = 40;
        double centerX = bounds.X + (bounds.SizeX / 2);
        double centerZ = bounds.Z + (bounds.SizeZ / 2);
        double radiusX = bounds.SizeX * 0.46;
        double radiusZ = Math.Max(bounds.SizeZ * 0.95, bounds.SizeY * 0.55);
        double y = bounds.Y - 0.25;

        var positions = new Point3DCollection(Segments + 1);
        var normals = new Vector3DCollection(Segments + 1);
        var texCoords = new PointCollection(Segments + 1);
        var indices = new Int32Collection(Segments * 3);
        positions.Add(new Point3D(centerX, y, centerZ));
        normals.Add(new Vector3D(0, 1, 0));
        texCoords.Add(new Point(0.5, 0.5));

        for (int i = 0; i < Segments; i++)
        {
            double angle = i * 2 * Math.PI / Segments;
            positions.Add(new Point3D(
                centerX + (radiusX * Math.Cos(angle)),
                y,
                centerZ + (radiusZ * Math.Sin(angle))));
            normals.Add(new Vector3D(0, 1, 0));
            texCoords.Add(new Point(0.5 + (0.5 * Math.Cos(angle)), 0.5 + (0.5 * Math.Sin(angle))));
        }

        for (int i = 0; i < Segments; i++)
        {
            indices.Add(0);
            indices.Add(1 + ((i + 1) % Segments));
            indices.Add(1 + i);
        }

        positions.Freeze();
        normals.Freeze();
        texCoords.Freeze();
        indices.Freeze();

        var mesh = new MeshGeometry3D
        {
            Positions = positions,
            Normals = normals,
            TextureCoordinates = texCoords,
            TriangleIndices = indices,
        };
        mesh.Freeze();

        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xB4, 0x00, 0x00, 0x00), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, 0x00, 0x00, 0x00), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 1.0));
        brush.Freeze();

        var material = new DiffuseMaterial(brush);
        material.Freeze();
        var shadow = new GeometryModel3D(mesh, material);
        shadow.Freeze();

        _root.Children.Add(new ModelVisual3D { Content = shadow });
    }

    private void ResetScene()
    {
        _root.Children.Clear();
        _parts.Clear();
        _buttonByModel.Clear();
        _labels.Clear();
        LabelLayer.Children.Clear();
        _staticTriangles = 0;
        _dpadBounds = Rect3D.Empty;
        _guideBounds = Rect3D.Empty;
        UsingDownloadedModel = false;
        ModelSource = null;
    }

    private void AddLabel(string text, Point3D anchor)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            IsHitTestVisible = false,
        };

        var chip = new Border
        {
            Background = _labelBackground,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(3, 0, 3, 1),
            Child = block,
            IsHitTestVisible = false,
        };

        LabelLayer.Children.Add(chip);
        _labels.Add(new LabelChip(chip, anchor));
    }

    private static Material CreateMaterial(Color color, Color? glow = null, bool specular = true)
    {
        var group = new MaterialGroup();

        var diffuseBrush = new SolidColorBrush(color);
        diffuseBrush.Freeze();
        group.Children.Add(new DiffuseMaterial(diffuseBrush));

        if (specular)
        {
            var specularBrush = new SolidColorBrush(Color.FromArgb(0x3E, 0xFF, 0xFF, 0xFF));
            specularBrush.Freeze();
            var specularMaterial = new SpecularMaterial(specularBrush, 38);
            specularMaterial.Freeze();
            group.Children.Add(specularMaterial);
        }

        if (glow is { } glowColor)
        {
            var glowBrush = new SolidColorBrush(glowColor);
            glowBrush.Freeze();
            var emissive = new EmissiveMaterial(glowBrush);
            emissive.Freeze();
            group.Children.Add(emissive);
        }

        group.Freeze();
        return group;
    }

    private static Color Lift(Color color) => color;

    private static Color Scale(Color color, double factor) => Color.FromArgb(
        color.A,
        (byte)Math.Clamp(color.R * factor, 0, 255),
        (byte)Math.Clamp(color.G * factor, 0, 255),
        (byte)Math.Clamp(color.B * factor, 0, 255));

    private static Color Mix(Color from, Color to, double amount) => Color.FromArgb(
        from.A,
        (byte)Math.Clamp(from.R + ((to.R - from.R) * amount), 0, 255),
        (byte)Math.Clamp(from.G + ((to.G - from.G) * amount), 0, 255),
        (byte)Math.Clamp(from.B + ((to.B - from.B) * amount), 0, 255));

    private static Vector3D Normalize(Vector3D value)
    {
        value.Normalize();
        return value;
    }

    private static Rect3D Union(Rect3D a, Rect3D b)
    {
        Rect3D result = a;
        result.Union(b);
        return result;
    }

    // ------------------------------------------------------------------ data binding

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        _gamepad = DataContext as GamepadViewModel;
        Attach();
        SyncAll();
        UpdateIdle();
    }

    private void Attach()
    {
        if (_attached || _gamepad is not { } gamepad)
        {
            return;
        }

        _attached = true;
        gamepad.PropertyChanged += OnGamepadPropertyChanged;
        gamepad.ControlSelected += OnControlSelected;
    }

    private void Detach()
    {
        _idleTimer.Stop();
        _idleRunning = false;

        if (!_attached || _gamepad is not { } gamepad)
        {
            return;
        }

        _attached = false;
        gamepad.PropertyChanged -= OnGamepadPropertyChanged;
        gamepad.ControlSelected -= OnControlSelected;
    }

    private void OnControlSelected(object? sender, GamepadControlViewModel e) => UpdateSelection();

    private void OnGamepadPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        GamepadViewModel? gamepad = _gamepad;
        if (gamepad is null || e.PropertyName is not { } name)
        {
            return;
        }

        // The poll can hand us a property change from the thread pool (when the view model could not
        // capture a UI SynchronizationContext). Freezables such as AxisAngleRotation3D and
        // TranslateTransform3D have dispatcher affinity, so writing them off-thread throws and the
        // 3D view silently stops animating. Marshal every change onto the UI dispatcher.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                new Action(() => OnGamepadPropertyChanged(sender, e)));
            return;
        }

        switch (name)
        {
            case nameof(GamepadViewModel.LeftStickX):
            case nameof(GamepadViewModel.LeftStickY):
                UpdateStick("StickLeft", gamepad.LeftStickX, gamepad.LeftStickY);
                return;

            case nameof(GamepadViewModel.RightStickX):
            case nameof(GamepadViewModel.RightStickY):
                UpdateStick("StickRight", gamepad.RightStickX, gamepad.RightStickY);
                return;

            case nameof(GamepadViewModel.LT):
                UpdateTrigger("LT", gamepad.LT);
                return;

            case nameof(GamepadViewModel.RT):
                UpdateTrigger("RT", gamepad.RT);
                return;

            case nameof(GamepadViewModel.HasController):
                UpdateIdle();
                return;

            case nameof(GamepadViewModel.SelectedControl):
                UpdateSelection();
                return;

            default:
                if (PropertyToPart.TryGetValue(name, out string? partName))
                {
                    UpdatePress(partName);
                }

                return;
        }
    }

    /// <summary>Live property name to scene part name.</summary>
    private static readonly Dictionary<string, string> PropertyToPart = new(StringComparer.Ordinal)
    {
        ["A"] = "A",
        ["B"] = "B",
        ["X"] = "X",
        ["Y"] = "Y",
        ["Up"] = "DpadUp",
        ["Down"] = "DpadDown",
        ["Left"] = "DpadLeft",
        ["Right"] = "DpadRight",
        ["LB"] = "LB",
        ["RB"] = "RB",
        ["LS"] = "StickLeft",
        ["RS"] = "StickRight",
        ["View"] = "View",
        ["Menu"] = "Menu",
        ["Guide"] = "Guide",
        ["P1"] = "P1",
        ["P2"] = "P2",
        ["P3"] = "P3",
        ["P4"] = "P4",
    };

    private static bool IsPressed(GamepadViewModel gamepad, long buttonId) => buttonId switch
    {
        GamepadButtons.A => gamepad.A,
        GamepadButtons.B => gamepad.B,
        GamepadButtons.X => gamepad.X,
        GamepadButtons.Y => gamepad.Y,
        GamepadButtons.LeftBumper => gamepad.LB,
        GamepadButtons.RightBumper => gamepad.RB,
        GamepadButtons.Back => gamepad.View,
        GamepadButtons.Start => gamepad.Menu,
        GamepadButtons.Guide => gamepad.Guide,
        GamepadButtons.LeftStickClick => gamepad.LS,
        GamepadButtons.RightStickClick => gamepad.RS,
        GamepadButtons.DpadUp => gamepad.Up,
        GamepadButtons.DpadDown => gamepad.Down,
        GamepadButtons.DpadLeft => gamepad.Left,
        GamepadButtons.DpadRight => gamepad.Right,
        GamepadButtons.LeftTrigger => gamepad.LT > 0.02,
        GamepadButtons.RightTrigger => gamepad.RT > 0.02,
        GamepadButtons.Paddle1 => gamepad.P1,
        GamepadButtons.Paddle2 => gamepad.P2,
        GamepadButtons.Paddle3 => gamepad.P3,
        GamepadButtons.Paddle4 => gamepad.P4,
        _ => false,
    };

    // ------------------------------------------------------------------ per-property updates

    private void UpdateStick(string partName, double x, double y)
    {
        if (!_parts.TryGetValue(partName, out List<PartVisual>? parts))
        {
            return;
        }

        foreach (PartVisual part in parts)
        {
            // Screen-oriented input: negative Y is up, so the cap tilts towards +Y when pushed up.
            part.AxisX.Angle = y * StickTiltDegrees;
            part.AxisY.Angle = x * StickTiltDegrees;
        }

        UpdatePress(partName);
    }

    private void UpdateTrigger(string partName, double value)
    {
        if (!_parts.TryGetValue(partName, out List<PartVisual>? parts))
        {
            return;
        }

        foreach (PartVisual part in parts)
        {
            part.Translate.OffsetZ = part.IsModelPart
                ? -value * TriggerPullDepth
                : part.Placement.Z - (value * TriggerPullDepth);
        }

        UpdatePress(partName);
    }

    private void UpdatePress(string partName)
    {
        if (!_parts.TryGetValue(partName, out List<PartVisual>? parts) || _gamepad is not { } gamepad)
        {
            return;
        }

        foreach (PartVisual part in parts)
        {
            bool pressed = part.ButtonId is { } id && IsPressed(gamepad, id);
            part.ApplyPress(pressed, part.Kind == GamepadPartKind.Stick ? 0.12 : ButtonPressDepth);
        }

        if (partName.StartsWith("Dpad", StringComparison.Ordinal) &&
            _parts.TryGetValue("Dpad", out List<PartVisual>? dpadBase))
        {
            bool any = gamepad.Up || gamepad.Down || gamepad.Left || gamepad.Right;
            foreach (PartVisual part in dpadBase)
            {
                part.ApplyPress(any, 0);
            }
        }
    }

    private void UpdateSelection()
    {
        long? selected = _gamepad?.SelectedControl?.ButtonId;
        bool dpadSelected = selected == GamepadButtons.DpadUp || selected == GamepadButtons.DpadDown ||
            selected == GamepadButtons.DpadLeft || selected == GamepadButtons.DpadRight;

        foreach (List<PartVisual> list in _parts.Values)
        {
            foreach (PartVisual part in list)
            {
                part.Selected = part.Name == "Dpad"
                    ? dpadSelected
                    : part.ButtonId is { } id && id == selected;
                part.RefreshMaterial();
            }
        }
    }

    private void SyncAll()
    {
        if (_gamepad is not { } gamepad)
        {
            return;
        }

        UpdateStick("StickLeft", gamepad.LeftStickX, gamepad.LeftStickY);
        UpdateStick("StickRight", gamepad.RightStickX, gamepad.RightStickY);
        UpdateTrigger("LT", gamepad.LT);
        UpdateTrigger("RT", gamepad.RT);

        foreach (string partName in PropertyToPart.Values)
        {
            UpdatePress(partName);
        }

        UpdateSelection();
    }

    // ------------------------------------------------------------------ idle motion

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateIdle();

    private void UpdateIdle()
    {
        if (IsVisible && _gamepad is { HasController: false } && !_idleRunning && !_orbitDragging)
        {
            _idleRunning = true;
            _idleTimer.Start();
        }
        else if ((!IsVisible || _gamepad is null || _gamepad.HasController || _orbitDragging) && _idleRunning)
        {
            _idleRunning = false;
            _idleTimer.Stop();
            UpdateOrbitDisplay();
        }
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        if (!IsVisible || _gamepad is null || _gamepad.HasController || _orbitDragging)
        {
            _idleRunning = false;
            _idleTimer.Stop();
            return;
        }

        _idlePhase += 0.05;
        UpdateOrbitDisplay();
    }

    /// <summary>用户轨道角 + idle sway → 实际显示角（sway 不写入用户角度）。</summary>
    private void UpdateOrbitDisplay()
    {
        double sway = _idleRunning ? Math.Sin(_idlePhase) * IdleSwayDegrees : 0;
        _orbitYawAxis.Angle = _userYaw + sway;
        _orbitPitchAxis.Angle = _userPitch;
        UpdateLabels();
    }

    // ------------------------------------------------------------------ camera, labels, input

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        FitCamera();
        UpdateLabels();
    }

    /// <summary>Fits the camera to the loaded model bounds with a 3/4 product-shot angle.</summary>
    private void FitCamera()
    {
        double width = Viewport.ActualWidth;
        double height = Viewport.ActualHeight;
        if (width < 1 || height < 1 || _bounds.IsEmpty)
        {
            return;
        }

        double aspect = width / height;
        double halfVertical = FieldOfView * Math.PI / 360.0;
        double halfHorizontal = Math.Atan(Math.Tan(halfVertical) * aspect);

        Point3D center = new(
            _bounds.X + (_bounds.SizeX / 2),
            _bounds.Y + (_bounds.SizeY / 2),
            _bounds.Z + (_bounds.SizeZ / 2));

        // Fit by projecting the eight bounding-box corners onto the camera's right/up axes; using
        // the bounding sphere here would zoom out by ~2x on a pad that is much deeper than it is tall.
        Vector3D forward = -ViewDirection;
        Vector3D right = Vector3D.CrossProduct(forward, new Vector3D(0, 1, 0));
        right.Normalize();
        Vector3D up = Vector3D.CrossProduct(right, forward);
        up.Normalize();

        double halfWidth = 0;
        double halfHeight = 0;
        for (int corner = 0; corner < 8; corner++)
        {
            var point = new Point3D(
                center.X + (((corner & 1) == 0 ? -0.5 : 0.5) * _bounds.SizeX),
                center.Y + (((corner & 2) == 0 ? -0.5 : 0.5) * _bounds.SizeY),
                center.Z + (((corner & 4) == 0 ? -0.5 : 0.5) * _bounds.SizeZ));

            Vector3D relative = point - center;
            halfWidth = Math.Max(halfWidth, Math.Abs(Vector3D.DotProduct(relative, right)));
            halfHeight = Math.Max(halfHeight, Math.Abs(Vector3D.DotProduct(relative, up)));
        }

        double distance = Math.Max(
            halfWidth / Math.Tan(Math.Max(halfHorizontal, 0.05)),
            halfHeight / Math.Tan(Math.Max(halfVertical, 0.05))) * 1.02;

        _camera.FieldOfView = FieldOfView;
        _camera.Position = center + (ViewDirection * distance);
        _camera.LookDirection = -ViewDirection * distance;
        _camera.UpDirection = new Vector3D(0, 1, 0);

        _orbitYaw.CenterX = center.X;
        _orbitYaw.CenterY = center.Y;
        _orbitYaw.CenterZ = center.Z;
        _orbitPitch.CenterX = center.X;
        _orbitPitch.CenterY = center.Y;
        _orbitPitch.CenterZ = center.Z;
    }

    private void UpdateLabels()
    {
        if (Viewport.ActualWidth < 1 || Viewport.ActualHeight < 1)
        {
            return;
        }

        foreach (LabelChip label in _labels)
        {
            if (Project(label.Anchor) is not { } point)
            {
                label.Chip.Visibility = Visibility.Collapsed;
                continue;
            }

            label.Chip.Visibility = Visibility.Visible;
            label.Chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size size = label.Chip.DesiredSize;
            Canvas.SetLeft(label.Chip, point.X - (size.Width / 2));
            Canvas.SetTop(label.Chip, point.Y - (size.Height / 2));
        }
    }

    /// <summary>Projects a scene point to control coordinates (WPF exposes no camera matrix).</summary>
    private Point? Project(Point3D world)
    {
        Vector3D forward = _camera.LookDirection;
        forward.Normalize();
        Vector3D right = Vector3D.CrossProduct(forward, _camera.UpDirection);
        right.Normalize();
        Vector3D up = Vector3D.CrossProduct(right, forward);

        Vector3D relative = world - _camera.Position;
        double depth = Vector3D.DotProduct(relative, forward);
        if (depth <= 0.1)
        {
            return null;
        }

        double width = Viewport.ActualWidth;
        double height = Viewport.ActualHeight;
        double tanHalf = Math.Tan(_camera.FieldOfView * Math.PI / 360.0);
        double aspect = width / height;

        double ndcX = Vector3D.DotProduct(relative, right) / (depth * tanHalf);
        double ndcY = Vector3D.DotProduct(relative, up) / (depth * tanHalf / aspect);
        return new Point((ndcX + 1) * 0.5 * width, (1 - ndcY) * 0.5 * height);
    }

    private void OnViewportMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_gamepad is null || HitTestButton(e.GetPosition(Viewport)) is not { } id)
        {
            // 空白处（或没有绑定视图模型时）按下 = 轨道旋转：左右翻转看背面、上下俯仰，
            // 与控件点击/摇杆拖拽互不干扰。
            _orbitDragging = Viewport.CaptureMouse();
            if (_orbitDragging)
            {
                _orbitOrigin = e.GetPosition(Viewport);
                UpdateIdle();
                e.Handled = true;
            }

            return;
        }

        // Pressing a stick cap starts a drag so the stick visibly follows the pointer (the same
        // animation path as a physical deflection); pressing it also selects it like any control.
        if (id is GamepadButtons.LeftStickClick or GamepadButtons.RightStickClick)
        {
            _dragStickName = id == GamepadButtons.LeftStickClick ? "StickLeft" : "StickRight";
            _dragOrigin = e.GetPosition(Viewport);
            _draggingStick = Viewport.CaptureMouse();
            if (_draggingStick)
            {
                e.Handled = true;
            }
        }

        if (_gamepad.SelectByButtonId(id))
        {
            e.Handled = true;
        }
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingStick && _dragStickName is { } stickName)
        {
            (double x, double y) = DragDeflection(e.GetPosition(Viewport) - _dragOrigin, Viewport.RenderSize);
            UpdateStick(stickName, x, y);
            return;
        }

        if (_orbitDragging)
        {
            Vector delta = e.GetPosition(Viewport) - _orbitOrigin;
            _orbitOrigin = e.GetPosition(Viewport);
            OrbitBy(delta.X * OrbitYawPerPixel, -delta.Y * OrbitPitchPerPixel);
        }
    }

    private void OnViewportMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingStick)
        {
            Viewport.ReleaseMouseCapture();
            EndStickDrag();
            return;
        }

        if (_orbitDragging)
        {
            Viewport.ReleaseMouseCapture();
            EndOrbitDrag();
        }
    }

    private void OnViewportLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_draggingStick)
        {
            EndStickDrag();
        }

        if (_orbitDragging)
        {
            EndOrbitDrag();
        }
    }

    /// <summary>复位视角按钮：清零轨道角并回到默认 3/4 产品视角。</summary>
    private void OnResetOrbit(object sender, RoutedEventArgs e) => ResetOrbit();

    private void EndOrbitDrag()
    {
        _orbitDragging = false;
        UpdateIdle();
    }

    /// <summary>当前用户轨道角（度）；测试与复位按钮用。</summary>
    internal double OrbitYawDegrees => _userYaw;

    /// <summary>当前用户俯仰角（度），始终在 ±<see cref="OrbitMaxPitchDegrees"/> 内。</summary>
    internal double OrbitPitchDegrees => _userPitch;

    /// <summary>按增量旋转轨道角并立即应用（俯仰钳制在 ±75°）。</summary>
    internal void OrbitBy(double yawDegrees, double pitchDegrees)
    {
        _userYaw += yawDegrees;
        _userPitch = Math.Clamp(_userPitch + pitchDegrees, -OrbitMaxPitchDegrees, OrbitMaxPitchDegrees);
        UpdateOrbitDisplay();
    }

    /// <summary>复位视角：清零轨道角，回到默认 3/4 产品视角。</summary>
    internal void ResetOrbit()
    {
        _userYaw = 0;
        _userPitch = 0;
        _idlePhase = 0;
        UpdateOrbitDisplay();
    }

    /// <summary>Stops the drag and re-centres the cap; the next live poll restores the real stick.</summary>
    private void EndStickDrag()
    {
        _draggingStick = false;
        string? stickName = _dragStickName;
        _dragStickName = null;
        if (stickName is not null)
        {
            UpdateStick(stickName, 0, 0);
        }
    }

    /// <summary>
    /// Maps a pointer drag (viewport pixels, Y down) to a normalized stick deflection (-1..1,
    /// screen convention Y up). Dragging across 45% of the viewport's shorter side is full
    /// deflection, matching the 24° tilt of a physical full push.
    /// </summary>
    internal static (double X, double Y) DragDeflection(Vector dragDelta, Size viewportSize)
    {
        double scale = Math.Max(1.0, Math.Min(viewportSize.Width, viewportSize.Height) * 0.45);
        double x = Math.Clamp(dragDelta.X / scale, -1.0, 1.0);
        double y = Math.Clamp(-dragDelta.Y / scale, -1.0, 1.0);
        return (x, y);
    }

    /// <summary>
    /// Ray-casts the scene at <paramref name="point"/> (control coordinates) and returns the button
    /// id of the nearest mapped mesh under it, or null.
    /// </summary>
    internal long? HitTestButton(Point point)
    {
        long? hit = null;

        VisualTreeHelper.HitTest(
            Viewport,
            null,
            result =>
            {
                if (result is RayMeshGeometry3DHitTestResult ray
                    && ray.ModelHit is GeometryModel3D model
                    && _buttonByModel.TryGetValue(model, out long buttonId))
                {
                    hit = buttonId;
                    return HitTestResultBehavior.Stop;
                }

                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(point));

        return hit;
    }

    private sealed class LabelChip
    {
        public LabelChip(Border chip, Point3D anchor)
        {
            Chip = chip;
            Anchor = anchor;
        }

        public Border Chip { get; }

        public Point3D Anchor { get; }
    }

    /// <summary>One part: its 3D model, the frozen material variants and its animation state.</summary>
    private sealed class PartVisual
    {
        public PartVisual(
            GamepadControlMap map,
            GeometryModel3D model,
            Material normal,
            Material? pressed,
            Material? selected,
            Material? selectedPressed,
            AxisAngleRotation3D axisX,
            AxisAngleRotation3D axisY,
            TranslateTransform3D translate,
            Point3D pivot,
            Point3D placement,
            bool isModelPart,
            Vector3D pressAxis)
        {
            Map = map;
            Model = model;
            Normal = normal;
            PressedMaterial = pressed;
            SelectedMaterial = selected;
            SelectedPressedMaterial = selectedPressed;
            AxisX = axisX;
            AxisY = axisY;
            Translate = translate;
            Pivot = pivot;
            Placement = placement;
            IsModelPart = isModelPart;
            PressAxis = pressAxis == default ? new Vector3D(0, 0, -1) : pressAxis;
        }

        public GamepadControlMap Map { get; }

        public GeometryModel3D Model { get; }

        public AxisAngleRotation3D AxisX { get; }

        public AxisAngleRotation3D AxisY { get; }

        public TranslateTransform3D Translate { get; }

        public Point3D Pivot { get; }

        /// <summary>Scene-space anchor of a procedural mesh; unused (zero) for model meshes.</summary>
        public Point3D Placement { get; }

        public bool IsModelPart { get; }

        public Vector3D PressAxis { get; }

        public Rect3D Bounds => Model.Geometry.Bounds;

        public int TriangleCount => Model.Geometry is MeshGeometry3D mesh ? mesh.TriangleIndices.Count / 3 : 0;

        public long? ButtonId => Map.ButtonId;

        public GamepadPartKind Kind => Map.Kind;

        public string Name => Map.Name;

        public Material Normal { get; }

        public Material? PressedMaterial { get; }

        public Material? SelectedMaterial { get; }

        public Material? SelectedPressedMaterial { get; }

        public bool Pressed { get; private set; }

        public bool Selected { get; set; }

        public void ApplyPress(bool pressed, double depth)
        {
            if (Pressed != pressed)
            {
                Pressed = pressed;
                RefreshMaterial();
            }

            // The trigger pull is applied by the analog path and must not be overwritten here.
            if (Kind == GamepadPartKind.Trigger)
            {
                return;
            }

            if (IsModelPart)
            {
                double travel = pressed ? depth : 0;
                Translate.OffsetX = PressAxis.X * travel;
                Translate.OffsetY = PressAxis.Y * travel;
                Translate.OffsetZ = PressAxis.Z * travel;
            }
            else
            {
                Translate.OffsetZ = Placement.Z - (pressed ? depth : 0);
            }
        }

        public void RefreshMaterial()
        {
            if (PressedMaterial is null)
            {
                return;
            }

            Model.Material = (Selected, Pressed) switch
            {
                (true, true) => SelectedPressedMaterial!,
                (true, false) => SelectedMaterial!,
                (false, true) => PressedMaterial,
                _ => Normal,
            };
        }
    }
}

/// <summary>
/// Snapshot of one animated thumbstick: its canonical part id, the current X/Y tilt angles (degrees)
/// and the pivot those rotations use. Exposed so tests can prove that live analog input reaches the
/// 3D scene and that every stick tilts about its own bounding-box centre.
/// </summary>
public readonly record struct StickTransform(
    string PartId,
    double AxisXDegrees,
    double AxisYDegrees,
    Point3D Pivot,
    Point3D Placement,
    string Source);

