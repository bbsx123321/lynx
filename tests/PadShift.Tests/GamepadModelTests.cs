using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using PadShift.App.Infrastructure;
using PadShift.App.ViewModels;
using PadShift.App.Views;

namespace PadShift.Tests;

/// <summary>
/// Covers the downloaded CC0 controller model: provenance metadata, the hand-written OBJ/MTL
/// importer, part-to-control mapping, the static-base overlay fallback and the live 3D view.
/// </summary>
public class GamepadModelTests
{
    private static readonly string[] RequiredControls =
    [
        "A", "B", "X", "Y",
        "DpadUp", "DpadDown", "DpadLeft", "DpadRight",
        "LB", "RB", "LT", "RT",
        "StickLeft", "StickRight",
        "View", "Menu", "Guide",
        "P1", "P2", "P3", "P4",
    ];

    [Fact]
    public void Bundled_Model_And_License_Metadata_Are_Present()
    {
        string? directory = GamepadModelLoader.FindAssetDirectory();
        Assert.NotNull(directory);

        string licensePath = Path.Combine(directory!, "LICENSE.txt");
        Assert.True(File.Exists(licensePath), licensePath);

        string license = File.ReadAllText(licensePath);
        Assert.Contains("CC0", license, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3DControllerModels", license, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CasperH2O", license, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026-10-18", license);

        Assert.True(File.Exists(Path.Combine(directory!, GamepadModelLoader.ObjFileName)));

        // The raw source is kept next to the converted OBJ so the provenance can be re-checked.
        Assert.True(File.Exists(Path.Combine(directory!, "source.blend")));
    }

    [Fact]
    public void Bundled_Model_Parses_With_Controls_And_Performance_Budget()
    {
        Assert.True(GamepadModelLoader.TryLoad(out GamepadModel? model, out string message), message);
        Assert.NotNull(model);

        Assert.True(model!.TriangleCount > 0);
        Assert.InRange(model.TriangleCount, 500, GamepadModelLoader.MaxTriangles);
        Assert.False(model.NeedsControlOverlay);
        Assert.True(model.ControlCount >= 14, "mapped controls: " + model.ControlCount);

        // The source shell names every physical control except the back paddles, which the view
        // overlays from the procedural set.
        foreach (string control in RequiredControls.Where(c => !c.StartsWith('P')))
        {
            Assert.Contains(model.Parts, part => part.Map.Name == control);
        }

        Assert.All(model.Parts, part => Assert.True(part.Mesh.IsFrozen, part.SourceName + " mesh must be frozen"));
    }

    [Fact]
    public void Obj_Importer_Parses_Groups_Materials_And_Normals()
    {
        string directory = Path.Combine(Path.GetTempPath(), "padshift-obj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string objPath = Path.Combine(directory, "test.obj");
        string mtlPath = Path.Combine(directory, "test.mtl");

        try
        {
            File.WriteAllText(mtlPath, """
                newmtl body
                Kd 0.20 0.25 0.30
                newmtl red
                Kd 0.80 0.10 0.10
                newmtl green
                Kd 0.10 0.70 0.15
                """);

            File.WriteAllText(objPath, """
                mtllib test.mtl
                v -10 0 0
                v 10 0 0
                v 10 6 0
                v -10 6 0
                v 0 8 0
                v 6 8 0
                v 6 10 0
                v -6 10 0
                v -8 2 1
                v -6 2 1
                v -7 4 1
                vn 0 0 1
                o MainBody
                usemtl body
                f 1//1 2//1 3//1
                f 1//1 3//1 4//1
                o Button A
                usemtl red
                f 5//1 6//1 7//1
                o Button B
                usemtl red
                f 6//1 7//1 8//1
                o Left Stick
                usemtl green
                f 5//1 6//1 8//1
                o DPad Up
                usemtl body
                f 1//1 2//1 9//1
                o LT
                usemtl body
                f 2//1 3//1 10//1
                o Guide
                usemtl green
                f 3//1 4//1 11//1
                """);

            GamepadModel model = GamepadModelLoader.LoadObj(objPath);

            Assert.Equal(7, model.Parts.Count);
            Assert.Equal(8, model.TriangleCount); // 2 body triangles + 6 controls
            Assert.True(model.Bounds.SizeX > 0);
            Assert.Equal(29.0, model.Bounds.SizeX, 3); // normalised to scene width

            GamepadModelPart body = Assert.Single(model.Parts, p => p.SourceName == "MainBody");
            Assert.Equal("Body", body.Map.Name);
            Assert.False(body.IsControl);
            Assert.Equal(0x33, body.Color.R);
            Assert.Equal(0x40, body.Color.G);

            Assert.Contains(model.Parts, p => p.Map.Name == "A" && p.Color.R > 0xB0);
            Assert.Contains(model.Parts, p => p.Map.Name == "StickLeft");
            Assert.Contains(model.Parts, p => p.Map.Name == "DpadUp");
            Assert.Contains(model.Parts, p => p.Map.Name == "LT");
            Assert.Contains(model.Parts, p => p.Map.Name == "Guide");
            Assert.False(model.NeedsControlOverlay);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Part_Map_Covers_Real_Model_Names()
    {
        (string Source, string Canonical)[] names =
        [
            ("Button A", "A"),
            ("Button_B", "B"),
            ("FaceX", "X"),
            ("button-y", "Y"),
            ("Left Stick", "StickLeft"),
            ("Stick-Right", "StickRight"),
            ("DPad-Up", "DpadUp"),
            ("DirectionalPad_Left", "DpadLeft"),
            ("Shoulder-Left-Trigger", "LT"),
            ("Shoulder-Right-Trigger", "RT"),
            ("Left Bumper", "LB"),
            ("RightShoulder", "RB"),
            ("Special", "Guide"),
            ("Start", "Menu"),
            ("Back", "View"),
            ("Paddle 1", "P1"),
            ("P4", "P4"),
        ];

        foreach ((string source, string canonical) in names)
        {
            Assert.True(GamepadPartMap.TryMap(source, out GamepadControlMap map), source);
            Assert.Equal(canonical, map.Name);
            Assert.NotNull(map.ButtonId);
            Assert.NotNull(map.Label);
        }

        // Shell/decor objects must never become clickable controls.
        Assert.True(GamepadPartMap.IsBodyName("MainBody"));
        Assert.True(GamepadPartMap.IsBodyName("Joystick-Left-Ring"));
        Assert.False(GamepadPartMap.TryMap("MainBody", out _));
        Assert.False(GamepadPartMap.TryMap("Joystick-Left-Ring", out _));
    }

    [Fact]
    public void Single_Mesh_Model_Requests_The_Procedural_Control_Overlay()
    {
        string directory = Path.Combine(Path.GetTempPath(), "padshift-obj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string objPath = Path.Combine(directory, "single.obj");

        try
        {
            File.WriteAllText(objPath, """
                v -10 -4 0
                v 10 -4 0
                v 10 4 0
                v -10 4 0
                o Gamepad
                f 1 2 3
                f 1 3 4
                """);

            GamepadModel model = GamepadModelLoader.LoadObj(objPath);
            Assert.True(model.NeedsControlOverlay);
            Assert.Equal(0, model.ControlCount);
            Assert.Single(model.Parts);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Catalog_Lists_And_Resolves_Every_Bundled_Model()
    {
        Assert.Equal(3, GamepadModelCatalog.Models.Count);
        Assert.Equal(GamepadModelCatalog.DualSense.Id, GamepadModelCatalog.DefaultId);

        // 未知 / 空 id 一律回退默认模型，绝不让设置文件手改后选不中任何项。
        Assert.Equal(GamepadModelCatalog.DefaultId, GamepadModelCatalog.Find(null).Id);
        Assert.Equal(GamepadModelCatalog.DefaultId, GamepadModelCatalog.Find("no-such-pad").Id);
        Assert.Equal(GamepadModelCatalog.Xbox360.Id, GamepadModelCatalog.Find("XBOX360").Id);

        Assert.All(
            GamepadModelCatalog.Models,
            model => Assert.True(
                GamepadModelLoader.TryLoad(model, out GamepadModel? loaded, out string message),
                model.Id + ": " + message));

        Assert.All(GamepadModelCatalog.Models, model =>
        {
            GamepadModelLoader.TryLoad(model, out GamepadModel? loaded, out _);
            Assert.NotNull(loaded);
            Assert.True(loaded!.ControlCount >= 14, model.Id + " controls: " + loaded.ControlCount);
            Assert.True(loaded.TriangleCount <= GamepadModelLoader.MaxTriangles, model.Id + " over budget");
        });
    }

    [Fact]
    public void View_Switches_Between_Bundled_Models_At_Runtime()
    {
        RunOnSta(() =>
        {
            using var pad = new GamepadViewModel();
            var view = new Gamepad3DView { DataContext = pad, Width = 460, Height = 380 };
            view.Measure(new Size(460, 380));
            view.Arrange(new Rect(0, 0, 460, 380));
            view.UpdateLayout();

            Assert.True(view.SceneReady);
            string initial = view.CurrentModelId;

            foreach (GamepadModelInfo model in GamepadModelCatalog.Models)
            {
                Assert.True(view.SwitchModel(model.Id), model.Id + " should build");
                Assert.Equal(model.Id, view.CurrentModelId);
                Assert.True(view.UsingDownloadedModel);
                Assert.Equal(2, view.StickTransforms.Count);
                Assert.All(view.StickTransforms, transform => Assert.Equal("model", transform.Source));
            }

            // 相同 id 是幂等 no-op，未知 id 回退默认模型（若当前就是默认则同样 no-op）。
            Assert.True(view.SwitchModel(initial));
            Assert.True(view.SwitchModel("no-such-pad"));
        });
    }

    [Fact]
    public void Unknown_GamepadModelId_In_Settings_Falls_Back_To_The_Default()
    {
        // Save() 持久化到 settingsPath；必须传临时文件，绝不许碰用户真实的 settings.json。
        string tempPath = Path.Combine(Path.GetTempPath(), "padshift-prefs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings { GamepadModelId = "bogus" };
            var preferences = new PreferencesViewModel(settings, tempPath);
            Assert.Equal(GamepadModelCatalog.DefaultId, preferences.GamepadModelId);

            preferences.GamepadModelId = GamepadModelCatalog.SwitchPro.Id;
            Assert.Equal(GamepadModelCatalog.SwitchPro.Id, preferences.GamepadModelId);

            Assert.True(preferences.Save());
            Assert.Equal(GamepadModelCatalog.SwitchPro.Id, settings.GamepadModelId);
            Assert.Equal(GamepadModelCatalog.SwitchPro.Id, AppSettings.Load(tempPath).GamepadModelId);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void Missing_Model_Reports_Failure_And_Keeps_The_Procedural_Scene()
    {
        string path = Path.Combine(Path.GetTempPath(), "padshift-missing-" + Guid.NewGuid().ToString("N") + ".obj");

        bool loaded = GamepadModelLoader.TryLoad(path, out GamepadModel? model, out string message);

        Assert.False(loaded);
        Assert.Null(model);
        Assert.Contains("failed", message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(GamepadMeshBuilder.Build());
    }

    [Fact]
    public void ThreeD_View_Uses_The_Downloaded_Model_And_Stays_Within_Budget()
    {
        RunOnSta(() =>
        {
            var gamepad = new GamepadViewModel();
            var view = new Gamepad3DView { DataContext = gamepad, Width = 460, Height = 380 };
            view.Measure(new Size(460, 380));
            view.Arrange(new Rect(0, 0, 460, 380));
            view.UpdateLayout();

            Assert.True(view.SceneReady);
            Assert.True(view.UsingDownloadedModel);
            Assert.NotNull(view.ModelSource);
            Assert.InRange(view.PartCount, 10, 60);
            Assert.InRange(view.VisualCount, 2, 60);
            Assert.InRange(view.TriangleCount, 500, GamepadModelLoader.MaxTriangles);

            // The whole model stays clickable: every mapped control is reachable by a ray sweep.
            var hits = new HashSet<long>();
            for (int y = 6; y < 374; y += 4)
            {
                for (int x = 6; x < 454; x += 4)
                {
                    if (view.HitTestButton(new Point(x, y)) is { } buttonId)
                    {
                        hits.Add(buttonId);
                    }
                }
            }

            foreach (long required in new[]
            {
                GamepadButtons.A, GamepadButtons.B, GamepadButtons.X, GamepadButtons.Y,
                GamepadButtons.Guide, GamepadButtons.Paddle1,
                GamepadButtons.LeftTrigger, GamepadButtons.RightTrigger,
            })
            {
                Assert.Contains(required, hits);
            }
        });
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
