using System.Text.Json;
using PadShift.Core;
using PadShift.Output;
using Xunit;

namespace PadShift.Tests;

/// <summary>轮二功能：UDP 镜像包格式、性能档映射、镜像槽的旁路安全性。</summary>
public class Round2FeatureTests
{
    [Fact]
    public void Performance_Tier_Maps_To_Engine_Tick_And_Falls_Back_To_High()
    {
        Assert.Equal(8, OutputTuning.FromPerformance("normal", false, 1234).TickMs);
        Assert.Equal(4, OutputTuning.FromPerformance("high", false, 1234).TickMs);
        Assert.Equal(4, OutputTuning.FromPerformance("High", false, 1234).TickMs);
        Assert.Equal(2, OutputTuning.FromPerformance("ultra", false, 1234).TickMs);
        Assert.Equal(4, OutputTuning.FromPerformance("bogus", false, 1234).TickMs);
        Assert.Equal(4, OutputTuning.FromPerformance(null, false, 1234).TickMs);

        OutputTuning tuning = OutputTuning.FromPerformance("ultra", true, 99999);
        Assert.True(tuning.UdpEnabled);
        Assert.Equal(65535, tuning.UdpPort); // 越界端口钳制
    }

    [Fact]
    public void Udp_Packet_Carries_Every_Analog_And_Button_Field()
    {
        var intent = new OutputIntent
        {
            PadLeftX = -1234,
            PadLeftY = 567,
            PadRightX = 89,
            PadRightY = -12,
            PadLeftTrigger = 40,
            PadRightTrigger = 200,
            MouseButtons = 3,
            MouseDeltaX = -5,
            MouseDeltaY = 6,
            MouseWheel = 2,
        };
        intent.PadButtons[0] = true;
        intent.PadButtons[5] = true;

        using JsonDocument document = JsonDocument.Parse(UdpMirrorSink.BuildPacket(intent, 1234567));
        JsonElement root = document.RootElement;
        Assert.Equal(1234567L, root.GetProperty("t").GetInt64());
        Assert.Equal(-1234, root.GetProperty("lx").GetInt32());
        Assert.Equal(567, root.GetProperty("ly").GetInt32());
        Assert.Equal(89, root.GetProperty("rx").GetInt32());
        Assert.Equal(-12, root.GetProperty("ry").GetInt32());
        Assert.Equal(40, root.GetProperty("lt").GetInt32());
        Assert.Equal(200, root.GetProperty("rt").GetInt32());
        Assert.Equal((1 << 0) | (1 << 5), root.GetProperty("buttons").GetInt32());
        Assert.Equal(3, root.GetProperty("mouseButtons").GetInt32());
        Assert.Equal(-5, root.GetProperty("dx").GetInt32());
        Assert.Equal(6, root.GetProperty("dy").GetInt32());
        Assert.Equal(2, root.GetProperty("wheel").GetInt32());
    }

    private sealed class RecordingSink : IOutputSink
    {
        public int Submits;
        public int Releases;

        public void Submit(OutputIntent intent) => Submits++;

        public void ReleaseAll() => Releases++;

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Mirroring_Sink_Forwards_To_The_Real_Sink_And_Never_Throws_Upstream()
    {
        // 没有接收者的 UDP 端口也不应让镜像槽把异常带回引擎；真实槽的每次 Submit 都必须被转发。
        var real = new RecordingSink();
        using var mirror = new UdpMirrorSink(54321);
        var wrapped = new MirroringSink(real, mirror);

        var intent = new OutputIntent { PadLeftX = 42 };
        for (int i = 0; i < 10; i++)
        {
            wrapped.Submit(intent);
        }

        wrapped.ReleaseAll();
        Assert.Equal(10, real.Submits);
        Assert.Equal(1, real.Releases);
    }

    [Fact]
    public void Default_Tuning_Matches_The_Legacy_Engine_Tick()
    {
        Assert.Equal(4, OutputTuning.Default.TickMs);
        Assert.False(OutputTuning.Default.UdpEnabled);
        Assert.Equal(26760, OutputTuning.Default.UdpPort);
    }
}
