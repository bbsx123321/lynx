using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using PadShift.App;
using PadShift.App.Infrastructure;
using PadShift.App.ViewModels;
using PadShift.App.Views;

namespace PadShift.Tests;

/// <summary>
/// Covers the two visual deliverables: the application icon (every ICO size is present and both the
/// WPF and the System.Drawing loader accept the file) and the 3D/2D gamepad selection, including the
/// <c>--safe-graphics</c> fallback and the procedural mesh budget.
/// </summary>
public class VisualAssetTests
{
    private const int ExpectedMeshBudget = 60;

    // ------------------------------------------------------------------ icon

    [Fact]
    public void Icon_Contains_Every_Required_Size()
    {
        (int Width, int Height, byte[] Data)[] entries = ReadIco(IconPath());

        Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, entries.Select(e => e.Width).OrderBy(w => w).ToArray());

        foreach ((int width, int height, byte[] data) in entries)
        {
            Assert.Equal(width, height);
            Assert.True(data.Length > 0);

            if (width >= 256)
            {
                // The 256 px frame is PNG-compressed, the usual convention for large ICO entries.
                Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, data.Take(4).ToArray());
                Assert.Equal(width, PngWidth(data, 16));
                Assert.Equal(height, PngWidth(data, 20));
            }
            else
            {
                // Smaller frames are classic 32 bpp DIBs, which GDI decodes everywhere.
                Assert.Equal(40, BitConverter.ToInt32(data, 0));
                Assert.Equal(width, BitConverter.ToInt32(data, 4));
                Assert.Equal(height * 2, BitConverter.ToInt32(data, 8));
                Assert.Equal(32, BitConverter.ToUInt16(data, 14));
            }
        }
    }

    [Fact]
    public void Marketing_Png_Is_256_Pixels()
    {
        string path = Path.Combine(RepoRoot(), "assets", "icon", "padshift-256.png");
        Assert.True(File.Exists(path), path);

        byte[] png = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
        Assert.Equal(256, PngWidth(png, 16));
        Assert.Equal(256, PngWidth(png, 20));
    }

    [Fact]
    public void Icon_Is_Embedded_And_Decodes_In_Wpf()
    {
        var stream = AppIcon.OpenResource();
        Assert.NotNull(stream);

        using (stream!)
        {
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

            int[] sizes = decoder.Frames.Select(frame => frame.PixelWidth).OrderBy(w => w).ToArray();
            Assert.Contains(256, sizes);
            Assert.Contains(32, sizes);
            Assert.Contains(16, sizes);
        }

        Assert.NotNull(AppIcon.LoadWindowIcon());
    }

    [Fact]
    public void Icon_Loads_Through_The_Tray_Loader()
    {
        using var icon = AppIcon.LoadTrayIcon(32);
        Assert.NotNull(icon);
        Assert.Equal(32, icon!.Width);
        Assert.Equal(32, icon.Height);
    }

    [Fact]
    public void Assembly_Metadata_Identifies_PadShift()
    {
        Assembly assembly = typeof(MainWindow).Assembly;

        Assert.Equal("Lynx", assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title);
        Assert.Equal("Lynx (灵犀)", assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
        Assert.Equal("Lynx", assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company);
        Assert.NotNull(assembly.GetName().Version);
        Assert.True(assembly.GetName().Version! >= new Version(1, 0, 0, 0));
    }

    // ------------------------------------------------------------------ 3D vs 2D selection

    [Theory]
    [InlineData(true, false, true, GamepadViewKind.Model3D)]
    [InlineData(false, false, true, GamepadViewKind.Vector2D)]   // preference off
    [InlineData(true, true, true, GamepadViewKind.Vector2D)]     // --safe-graphics
    [InlineData(true, false, false, GamepadViewKind.Vector2D)]   // scene could not be built
    [InlineData(false, true, false, GamepadViewKind.Vector2D)]
    public void View_Mode_Respects_Preference_SafeGraphics_And_Availability(
        bool prefer3D,
        bool safeGraphics,
        bool available,
        GamepadViewKind expected)
    {
        Assert.Equal(expected, GamepadViewMode.Resolve(prefer3D, safeGraphics, available));
    }

    [Fact]
    public void Safe_Graphics_Switch_Forces_The_2D_Pad()
    {
        // The shell detects the flag before the window is created, so the same call is what
        // MainWindow.ApplyGamepadView evaluates at startup.
        bool safeGraphics = SafeGraphics.IsRequested(new[] { "--safe-graphics" });
        Assert.True(safeGraphics);
        Assert.Equal(GamepadViewKind.Vector2D, GamepadViewMode.Resolve(true, safeGraphics, true));
    }

    // ------------------------------------------------------------------ procedural 3D scene

    [Fact]
    public void ThreeD_Scene_Fits_The_Mesh_Budget()
    {
        IReadOnlyList<GamepadPart> parts = GamepadMeshBuilder.Build();

        Assert.InRange(parts.Count, 10, ExpectedMeshBudget);
        Assert.All(parts, part => Assert.True(part.Mesh.IsFrozen, part.Name + " mesh must be frozen"));
        Assert.All(parts, part => Assert.True(part.TriangleCount > 0, part.Name + " has no triangles"));

        int triangles = GamepadMeshBuilder.TotalTriangles(parts);
        Assert.InRange(triangles, 100, 2000);

        // Every control of the 2D pad is selectable in 3D too.
        long[] expectedButtons =
        [
            GamepadButtons.A, GamepadButtons.B, GamepadButtons.X, GamepadButtons.Y,
            GamepadButtons.LeftBumper, GamepadButtons.RightBumper,
            GamepadButtons.Back, GamepadButtons.Start, GamepadButtons.Guide,
            GamepadButtons.LeftStickClick, GamepadButtons.RightStickClick,
            GamepadButtons.DpadUp, GamepadButtons.DpadDown, GamepadButtons.DpadLeft, GamepadButtons.DpadRight,
            GamepadButtons.LeftTrigger, GamepadButtons.RightTrigger,
            GamepadButtons.Paddle1, GamepadButtons.Paddle2, GamepadButtons.Paddle3, GamepadButtons.Paddle4,
        ];

        foreach (long buttonId in expectedButtons)
        {
            Assert.Contains(parts, part => part.ButtonId == buttonId);
        }
    }

    [Fact]
    public void ThreeD_View_HitTests_Selectable_Buttons()
    {
        var hits = new HashSet<long>();

        RunOnSta(() =>
        {
            var gamepad = new GamepadViewModel();
            var view = new Gamepad3DView { DataContext = gamepad, Width = 460, Height = 380 };
            view.Measure(new Size(460, 380));
            view.Arrange(new Rect(0, 0, 460, 380));
            view.UpdateLayout();

            Assert.True(view.SceneReady);
            Assert.InRange(view.PartCount, 10, ExpectedMeshBudget);

            // A coarse ray sweep over the viewport must land on the face buttons and the menu
            // cluster; this is the same 3D hit test the mouse handler uses.
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
        });

        Assert.Contains(GamepadButtons.A, hits);
        Assert.Contains(GamepadButtons.B, hits);
        Assert.Contains(GamepadButtons.X, hits);
        Assert.Contains(GamepadButtons.Y, hits);
        Assert.Contains(GamepadButtons.Guide, hits);
        Assert.Contains(GamepadButtons.Paddle1, hits);
        Assert.Contains(GamepadButtons.LeftTrigger, hits);
        Assert.Contains(GamepadButtons.RightTrigger, hits);
    }

    // ------------------------------------------------------------------ helpers

    private static string IconPath() => Path.Combine(RepoRoot(), "assets", "icon", "padshift.ico");

    /// <summary>Reads a PNG big-endian 32-bit field (PNG integers are network byte order).</summary>
    private static int PngWidth(byte[] png, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));

    /// <summary>Walks up from the test binaries to the folder that holds PadShift.slnx.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PadShift.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("PadShift.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Parses the ICO directory and returns each frame with its payload.</summary>
    private static (int Width, int Height, byte[] Data)[] ReadIco(string path)
    {
        Assert.True(File.Exists(path), path);
        byte[] bytes = File.ReadAllBytes(path);

        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));

        int count = BitConverter.ToUInt16(bytes, 4);
        var entries = new (int, int, byte[])[count];
        for (int i = 0; i < count; i++)
        {
            int offset = 6 + (16 * i);
            int width = bytes[offset] == 0 ? 256 : bytes[offset];
            int height = bytes[offset + 1] == 0 ? 256 : bytes[offset + 1];
            int length = (int)BitConverter.ToUInt32(bytes, offset + 8);
            int dataOffset = (int)BitConverter.ToUInt32(bytes, offset + 12);

            Assert.InRange(dataOffset, 0, bytes.Length - 1);
            Assert.InRange(length, 1, bytes.Length - dataOffset);
            entries[i] = (width, height, bytes[dataOffset..(dataOffset + length)]);
        }

        return entries;
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
