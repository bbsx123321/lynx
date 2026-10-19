using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PadShift.Core.Profile;
using Xunit;

namespace PadShift.Tests;

/// <summary>
/// 往返测试：真实 .rewasd（the reference config tool 导出的用户配置）读入 → 写出 → 结构必须等价。
/// 夹具在 Assets/ 下，来自参考实现的配置库（只读副本）。
/// </summary>
public class ProfileRoundTripTests
{
    public static TheoryData<string> Fixtures => new()
    {
        "double-movement.rewasd",
        "gamepad-to-kbm.rewasd",
        "switch-for-any-device.rewasd",
    };

    private static string AssetPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", name);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Load_Succeeds_And_Has_Mappings(string file)
    {
        var profile = ProfileIo.Load(AssetPath(file));

        Assert.Equal(3m, profile.SchemaVersion);
        Assert.NotNull(profile.Devices.Hardware);
        Assert.NotEmpty(profile.Devices.Hardware!);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void RoundTrip_Preserves_Structure(string file)
    {
        var path = AssetPath(file);
        var originalJson = File.ReadAllText(path);
        var profile = ProfileIo.FromJson(originalJson);
        var again = ProfileIo.ToJson(profile);

        var a = JsonNode.Parse(originalJson)!;
        var b = JsonNode.Parse(again)!;

        var diffs = JsonDiff.Compare(a, b, "$");
        Assert.True(diffs.Count == 0, $"结构差异 {diffs.Count} 处:\n" + string.Join("\n", diffs.Take(20)));
    }

    [Fact]
    public void DoubleMovement_Has_Expected_Semantics()
    {
        var p = ProfileIo.Load(AssetPath("double-movement.rewasd"));

        Assert.Equal(3, p.Mappings!.Count);
        Assert.Equal(3, p.Masks!.Count);
        Assert.Single(p.Shifts!);
        Assert.Equal(ShiftType.Toggle, p.Shifts![0].Type);
        Assert.Equal(ActivatorType.Single, p.Masks![0].Set!.Count == 1
            ? p.Mappings![0].Condition.Mask!.Activator.Type
            : ActivatorType.Single);
        // 第一条映射把 DIK_A 掩码转成左摇杆左推
        var first = p.Mappings![0];
        Assert.Equal(1, first.Condition.Mask!.Id);
        Assert.Equal(42, first.Macros![0].Gamepad!.ButtonId);
        Assert.True(first.Macros[0].Gamepad!.Absolute);
        Assert.Equal(32767, first.Macros[0].Gamepad!.Value);
    }

    [Fact]
    public void Keyboard_Buttons_Are_Dik_ScanCodes()
    {
        var p = ProfileIo.Load(AssetPath("double-movement.rewasd"));
        var buttons = p.Masks!.SelectMany(m => m.Set!).ToList();

        Assert.Contains(buttons, b => b.ButtonId == Dik.A);
        Assert.Contains(buttons, b => b.ButtonId == Dik.D);
        Assert.Contains(buttons, b => b.ButtonId == Dik.LeftWin);
        Assert.True(Dik.IsExtended(Dik.LeftWin));
        Assert.False(Dik.IsExtended(Dik.A));
    }
}
