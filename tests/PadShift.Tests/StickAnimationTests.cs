using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using PadShift.App.Infrastructure;
using PadShift.App.ViewModels;
using PadShift.App.Views;
using PadShift.Core;
using PadShift.Core.Host;
using Xunit;

namespace PadShift.Tests;

/// <summary>
/// Regression suite for the 3D thumbsticks: the bundled model must expose dedicated stick meshes,
/// every stick must tilt about its own bounding-box centre, live analog input must reach the scene
/// (including when it is raised from a background thread), pointer drags map to deflection, and a
/// model without stick meshes must get the procedural caps at <see cref="StickAnchors"/>.
/// </summary>
public class StickAnimationTests
{
    private sealed class FakeHost : IRemapHost
    {
        private readonly HostController _controller = new(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            "Stick Test Pad",
            "Xbox",
            "USB",
            VendorId: 0,
            ProductId: 0,
            HasGyro: false,
            HasTouchpad: false,
            HasRumble: false,
            BatteryPercent: 100);

        public Guid Id => _controller.Id;

        public bool[] Buttons { get; } = new bool[256];

        public int[] Axes { get; } = new int[8];

        public event EventHandler<HostEvent>? Event
        {
            add { }
            remove { }
        }

        public bool RemapEnabled => false;

        public IReadOnlyList<HostController> Controllers => new[] { _controller };

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public IReadOnlyList<ShiftInfo> GetShifts(Guid controllerId) => Array.Empty<ShiftInfo>();

        public RemapState GetRemapState(Guid controllerId) => RemapState.NothingApplied;

        public System.Threading.Tasks.Task<ApplyConfigResult> ApplyConfigAsync(Guid controllerId, string profilePath, int slot) =>
            System.Threading.Tasks.Task.FromResult(new ApplyConfigResult(false, "stick-test", slot));

        public EnableRemapResult EnableRemap(Guid? controllerId) => new(false, "stick-test");

        public void DisableRemap(Guid? controllerId)
        {
        }

        public void SelectSlot(Guid controllerId, int slot)
        {
        }

        public int GetCurrentSlot(Guid controllerId) => 0;

        public IReadOnlyList<string> GetAppliedConfigs(Guid controllerId, int slot) => Array.Empty<string>();

        public bool IsKeyPressed(Guid controllerId, int scanCode) => false;

        public bool TryGetState(Guid controllerId, out ControllerStateSnapshot state)
        {
            state = new ControllerStateSnapshot(
                (bool[])Buttons.Clone(),
                (int[])Axes.Clone(),
                Connected: true,
                Environment.TickCount64);
            return true;
        }

        public void Vibrate(Guid controllerId, byte leftMotor, byte rightMotor, ushort durationMs)
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Bundled_Model_Exposes_Separate_Stick_Meshes()
    {
        Assert.True(GamepadModelLoader.TryLoad(out GamepadModel? model, out string message), message);
        Assert.NotNull(model);

        // The DualSense asset models real stick caps (dished top + wall + tapered shaft) and the
        // cap rim rings are merged into the same groups, so the view animates the model meshes
        // directly — no procedural caps are involved.
        List<GamepadModelPart> sticks = model!.Parts
            .Where(part => part.Map.Kind == GamepadPartKind.Stick)
            .ToList();

        Assert.Equal(2, sticks.Count);
        Assert.Contains(sticks, part => part.SourceName == "StickLeft" && part.Map.Name == "StickLeft");
        Assert.Contains(sticks, part => part.SourceName == "StickRight" && part.Map.Name == "StickRight");
        Assert.All(sticks, part => Assert.NotNull(part.Map.ButtonId));
        Assert.All(sticks, part => Assert.InRange(part.TriangleCount, 100, 1200));
    }

    [Fact]
    public void Sticks_Pivot_On_The_Shaft_Base_Not_The_Box_Centre()
    {
        RunOnSta(() =>
        {
            Assert.True(GamepadModelLoader.TryLoad(out GamepadModel? model, out string message), message);

            using var pad = new GamepadViewModel();
            var view = new Gamepad3DView { DataContext = pad, Width = 460, Height = 380 };
            view.Measure(new Size(460, 380));
            view.Arrange(new Rect(0, 0, 460, 380));
            view.UpdateLayout();

            IReadOnlyList<StickTransform> transforms = view.StickTransforms;
            Assert.Equal(2, transforms.Count);
            Assert.All(transforms, transform => Assert.Equal("model", transform.Source));

            foreach (StickTransform transform in transforms)
            {
                GamepadModelPart part = model!.Parts.Single(
                    candidate => candidate.Map.Name == transform.PartId && candidate.IsControl);

                // A real stick pivots on the ball joint: the pivot sits on the mesh axis (X/Y box
                // centre) at the bottom of the shaft (box Z minimum), never the box centre (whose
                // opposite swing clips the shaft into the well) and never the controller origin
                // (which would make the stick orbit the controller).
                Assert.Equal(part.Center.X, transform.Pivot.X, 1e-6);
                Assert.Equal(part.Center.Y, transform.Pivot.Y, 1e-6);
                Assert.Equal(part.Bounds.Z, transform.Pivot.Z, 1e-6);
            }

            StickTransform left = transforms.Single(t => t.PartId == "StickLeft");
            StickTransform right = transforms.Single(t => t.PartId == "StickRight");
            Assert.True(left.Pivot.X < -2.0, $"left pivot should sit on the left: {left.Pivot}");
            Assert.True(right.Pivot.X > 2.0, $"right pivot should sit on the right: {right.Pivot}");
        });
    }

    [Fact]
    public void Poll_Feeds_Analog_Deflection_Into_The_3D_Sticks_With_Natural_Signs()
    {
        Assert.InRange(Gamepad3DView.StickTiltDegrees, 20.0, 25.0);

        RunOnSta(() =>
        {
            var host = new FakeHost();
            host.Axes[Axes.LeftStickX] = 32767;   // pushed right
            host.Axes[Axes.LeftStickY] = 32767;   // pushed up
            host.Axes[Axes.RightStickX] = -32768; // pushed left
            host.Axes[Axes.RightStickY] = -32768; // pushed down

            using var pad = new GamepadViewModel();
            pad.AttachHost(host);
            pad.ControllerId = host.Id;

            var view = new Gamepad3DView { DataContext = pad, Width = 460, Height = 380 };
            view.Measure(new Size(460, 380));
            view.Arrange(new Rect(0, 0, 460, 380));
            view.UpdateLayout();

            pad.PollOnce();

            double tilt = Gamepad3DView.StickTiltDegrees;
            StickTransform left = view.StickTransforms.Single(t => t.PartId == "StickLeft");
            StickTransform right = view.StickTransforms.Single(t => t.PartId == "StickRight");

            // Push up: the view model's screen-space Y is -1, so the X rotation is negative and the
            // top of the cap leans away from the viewer. Push right: +Y rotation.
            Assert.Equal(tilt, left.AxisYDegrees, 3);
            Assert.Equal(-tilt, left.AxisXDegrees, 3);
            Assert.Equal(-tilt, right.AxisYDegrees, 3);
            Assert.Equal(tilt, right.AxisXDegrees, 3);
        });
    }

    [Fact]
    public void Full_Deflection_Saturates_At_The_Tilt_Limit()
    {
        RunOnSta(() =>
        {
            using var pad = new GamepadViewModel();
            var view = new Gamepad3DView { DataContext = pad, Width = 460, Height = 380 };
            view.Measure(new Size(460, 380));
            view.Arrange(new Rect(0, 0, 460, 380));
            view.UpdateLayout();

            // Out-of-range assignments must clamp to exactly one tilt range, never overshoot.
            pad.LeftStickX = 4.0;
            pad.LeftStickY = -4.0;

            StickTransform left = view.StickTransforms.Single(t => t.PartId == "StickLeft");
            Assert.Equal(Gamepad3DView.StickTiltDegrees, left.AxisYDegrees, 6);
            Assert.Equal(-Gamepad3DView.StickTiltDegrees, left.AxisXDegrees, 6);

            pad.LeftStickX = 0;
            pad.LeftStickY = 0;
            left = view.StickTransforms.Single(t => t.PartId == "StickLeft");
            Assert.Equal(0.0, left.AxisYDegrees);
            Assert.Equal(0.0, left.AxisXDegrees);
        });
    }

    [Fact]
    public void Live_Background_Property_Change_Reaches_The_3D_View()
    {
        RunOnSta(() =>
        {
            using var pad = new GamepadViewModel();
            var view = new Gamepad3DView { DataContext = pad, Width = 460, Height = 380 };
            view.Measure(new Size(460, 380));
            view.Arrange(new Rect(0, 0, 460, 380));
            view.UpdateLayout();

            // Reproduces a poll continuation that did not get a UI SynchronizationContext: the
            // PropertyChanged arrives off-thread. The view must marshal it onto its dispatcher
            // instead of throwing on the Freezable and silently leaving the sticks at 0.
            Exception? backgroundFailure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    pad.LeftStickX = 0.5;
                    pad.LeftStickY = -0.5;
                }
                catch (Exception ex)
                {
                    backgroundFailure = ex;
                }
            });
            worker.IsBackground = true;
            worker.Start();
            worker.Join();

            Assert.Null(backgroundFailure);
            Pump(TimeSpan.FromMilliseconds(400));

            StickTransform left = view.StickTransforms.Single(t => t.PartId == "StickLeft");
            Assert.Equal(Gamepad3DView.StickTiltDegrees * 0.5, left.AxisYDegrees, 6);
            Assert.Equal(-Gamepad3DView.StickTiltDegrees * 0.5, left.AxisXDegrees, 6);
        });
    }

    [Fact]
    public void Live_Start_Poll_Applies_Sticks_Without_A_Captured_SynchronizationContext()
    {
        RunOnSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);

            var host = new FakeHost();
            host.Axes[Axes.LeftStickX] = 32767;

            using var pad = new GamepadViewModel();
            pad.AttachHost(host);
            pad.ControllerId = host.Id;

            var view = new Gamepad3DView { DataContext = pad, Width = 460, Height = 380 };
            view.Measure(new Size(460, 380));
            view.Arrange(new Rect(0, 0, 460, 380));
            view.UpdateLayout();

            pad.StartLive();
            try
            {
                Pump(TimeSpan.FromMilliseconds(500));
            }
            finally
            {
                pad.StopLive();
            }

            StickTransform left = view.StickTransforms.Single(t => t.PartId == "StickLeft");
            Assert.Equal(Gamepad3DView.StickTiltDegrees, left.AxisYDegrees, 6);
        });
    }

    [Fact]
    public void Model_Without_Stick_Meshes_Gets_Procedural_Caps_At_StickAnchors()
    {
        RunOnSta(() =>
        {
            GamepadModel model = SingleBodyModel();
            Assert.True(model.NeedsControlOverlay);

            using var pad = new GamepadViewModel();
            var view = new Gamepad3DView(model) { DataContext = pad, Width = 460, Height = 380 };
            view.Measure(new Size(460, 380));
            view.Arrange(new Rect(0, 0, 460, 380));
            view.UpdateLayout();

            Assert.True(view.SceneReady);

            IReadOnlyList<StickTransform> transforms = view.StickTransforms;
            Assert.Equal(2, transforms.Count);
            Assert.All(transforms, transform => Assert.Equal("procedural", transform.Source));

            Rect3D capBounds = GamepadMeshBuilder.BuildStickCapMesh().Bounds;
            Assert.InRange(GamepadMeshBuilder.BuildStickCapMesh().TriangleIndices.Count / 3, 150, 260);
            var capCentre = new Point3D(
                capBounds.X + (capBounds.SizeX / 2),
                capBounds.Y + (capBounds.SizeY / 2),
                capBounds.Z + (capBounds.SizeZ / 2));

            foreach (StickTransform transform in transforms)
            {
                // The cap is anchored with the tunable table...
                AssertNear(StickAnchors.Resolve(transform.PartId, model.Bounds), transform.Placement, 1e-6);

                // ...but it still tilts about its own cap centre, not the anchor at its base.
                AssertNear(capCentre, transform.Pivot, 1e-6);
            }
        });
    }

    [Fact]
    public void Demo_Stick_Preview_Switches_Are_Parsed()
    {
        Assert.Equal(
            RenderPreview.StickDemo.Default,
            RenderPreview.GetStickDemo(new[] { "--render-preview", "out.png" }));
        Assert.Equal(
            RenderPreview.StickDemo.Deflected,
            RenderPreview.GetStickDemo(new[] { "--render-preview", "out.png", "--demo-sticks" }));
        Assert.Equal(
            RenderPreview.StickDemo.Centered,
            RenderPreview.GetStickDemo(new[] { "--render-preview", "out.png", "--demo-sticks-center" }));
    }

    [Fact]
    public void Stick_Drag_Deflection_Follows_The_Pointer_With_Screen_Orientation()
    {
        var viewport = new Size(460, 380);

        // Dragging right deflects +X; dragging up (negative pointer Y) deflects +Y (screen "up").
        Assert.Equal(1.0, Gamepad3DView.DragDeflection(new Vector(207, 0), viewport).X, 3);
        Assert.Equal(1.0, Gamepad3DView.DragDeflection(new Vector(0, -171), viewport).Y, 3);
        Assert.Equal(-1.0, Gamepad3DView.DragDeflection(new Vector(-207, 0), viewport).X, 3);

        // Half of the full-travel distance is half deflection, and both axes combine.
        (double x, double y) = Gamepad3DView.DragDeflection(new Vector(85.5, -85.5), viewport);
        Assert.Equal(0.5, x, 2);
        Assert.Equal(0.5, y, 2);

        // Far overshoots clamp instead of overshooting the tilt range.
        (x, y) = Gamepad3DView.DragDeflection(new Vector(5000, -5000), viewport);
        Assert.Equal(1.0, x);
        Assert.Equal(1.0, y);
        (x, y) = Gamepad3DView.DragDeflection(new Vector(-5000, 5000), viewport);
        Assert.Equal(-1.0, x);
        Assert.Equal(-1.0, y);

        // A degenerate viewport must not divide by zero.
        (x, y) = Gamepad3DView.DragDeflection(new Vector(100, -100), new Size(0, 0));
        Assert.Equal(1.0, x);
        Assert.Equal(1.0, y);
    }

    [Fact]
    public void Orbit_Drag_Rotates_Clamps_And_Resets()
    {
        RunOnSta(() =>
        {
            using var pad = new GamepadViewModel();
            var view = new Gamepad3DView { DataContext = pad, Width = 460, Height = 380 };
            view.Measure(new Size(460, 380));
            view.Arrange(new Rect(0, 0, 460, 380));
            view.UpdateLayout();

            // 默认 3/4 产品视角。
            Assert.Equal(0.0, view.OrbitYawDegrees, 6);
            Assert.Equal(0.0, view.OrbitPitchDegrees, 6);

            // 水平累计旋转（拖 ~520px 即翻到背面），俯仰钳制在 ±75°。
            view.OrbitBy(90.0, 10.0);
            Assert.Equal(90.0, view.OrbitYawDegrees, 6);
            Assert.Equal(10.0, view.OrbitPitchDegrees, 6);

            view.OrbitBy(0.0, 500.0);
            Assert.Equal(Gamepad3DView.OrbitMaxPitchDegrees, view.OrbitPitchDegrees, 6);
            view.OrbitBy(0.0, -1000.0);
            Assert.Equal(-Gamepad3DView.OrbitMaxPitchDegrees, view.OrbitPitchDegrees, 6);

            // 复位回到默认视角。
            view.ResetOrbit();
            Assert.Equal(0.0, view.OrbitYawDegrees, 6);
            Assert.Equal(0.0, view.OrbitPitchDegrees, 6);
        });
    }

    private static GamepadModel SingleBodyModel()
    {
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                new(-10, -5, 0),
                new(10, -5, 0),
                new(10, 5, 0),
                new(-10, 5, 0),
            },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
        };
        mesh.Freeze();

        var body = new GamepadModelPart(
            "Body",
            new GamepadControlMap("Body", null, GamepadPartKind.Body, null),
            mesh,
            Color.FromRgb(0x40, 0x48, 0x58));

        return new GamepadModel(new[] { body }, "synthetic-single-mesh.obj");
    }

    private static void AssertNear(Point3D expected, Point3D actual, double tolerance)
    {
        Assert.True(
            Math.Abs(expected.X - actual.X) <= tolerance &&
            Math.Abs(expected.Y - actual.Y) <= tolerance &&
            Math.Abs(expected.Z - actual.Z) <= tolerance,
            $"expected {expected} but was {actual}");
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>WPF visual objects need an STA thread; xunit runs tests on the pool.</summary>
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("STA test failed: " + failure.Message, failure);
        }
    }
}
