using System.IO;
using System.Text.Json;
using PadShift.App.Infrastructure;
using PadShift.Core;
using PadShift.Core.Host;
using PadShift.Engine.Hosting;
using Xunit;

namespace PadShift.Tests;

/// <summary>轮三功能：背键在引擎入口被屏蔽、映射清单导出、配置项封面/描述。</summary>
public class Round3FeatureTests
{
    [Fact]
    public void MaskPaddles_Drops_Paddle_Input_At_The_Engine_Inlet()
    {
        var buttons = new bool[256];
        buttons[ButtonIds.A] = true;
        buttons[ButtonIds.Paddle1] = true;
        buttons[ButtonIds.Paddle4] = true;
        var source = new FakeSource(new ControllerStateSnapshot(buttons, new int[8], Connected: true, 0));
        using var host = new RemapHost(
            new IInputSource[] { source },
            new NullSink(),
            new HostOptions { TickMs = 4, MaskPaddles = true });

        host.Start();
        host.PumpForTest(Environment.TickCount64);
        host.PumpForTest(Environment.TickCount64 + 5);
        bool fetched = host.TryGetState(host.Controllers[0].Id, out ControllerStateSnapshot state);
        host.Stop();
        Assert.True(fetched);
        Assert.True(state.Buttons[ButtonIds.A]);
        Assert.False(state.Buttons[ButtonIds.Paddle1]);
        Assert.False(state.Buttons[ButtonIds.Paddle4]);
    }

    [Fact]
    public void Without_MaskPaddles_Paddles_Pass_Through()
    {
        var buttons = new bool[256];
        buttons[ButtonIds.Paddle2] = true;
        var source = new FakeSource(new ControllerStateSnapshot(buttons, new int[8], Connected: true, 0));
        using var host = new RemapHost(
            new IInputSource[] { source },
            new NullSink(),
            new HostOptions { TickMs = 4 });

        host.Start();
        host.PumpForTest(Environment.TickCount64);
        host.PumpForTest(Environment.TickCount64 + 5);
        bool fetched = host.TryGetState(host.Controllers[0].Id, out ControllerStateSnapshot state);
        host.Stop();
        Assert.True(fetched);
        Assert.True(state.Buttons[ButtonIds.Paddle2]);
    }

    [Fact]
    public void Binding_List_Export_Includes_Metadata_And_Mapping_Rows()
    {
        const string json = """
            {
              "appName": "Test Game",
              "description": "A test config",
              "processNames": ["game64"],
              "devices": [
                {
                  "hardware": [
                    {
                      "controllerType": "XboxOne",
                      "config": {
                        "btnA": { "gamepadDescription": "Key: Space", "turbo": true },
                        "btnB": { "macro": "Ctrl+A;pause:100" },
                        "btnX": { "unmap": true }
                      }
                    }
                  ]
                }
              ]
            }
            """;

        string text = BindingListExporter.Build(json, "Config.rewasd");
        Assert.Contains("Test Game", text);
        Assert.Contains("A test config", text);
        Assert.Contains("game64", text);
        Assert.Contains("btnA  →  Key: Space  [Turbo]", text);
        Assert.Contains("btnB  →  (macro)  [Macro]", text);
        Assert.Contains("btnX  →  (unmapped)", text);
        Assert.Contains("Main layer", text);
    }

    [Fact]
    public void Binding_List_Export_Tolerates_Empty_Devices()
    {
        string text = BindingListExporter.Build("""{"devices": []}""", "empty");
        Assert.Contains("(no devices)", text);
    }

    private sealed class FakeSource : IInputSource
    {
        private readonly ControllerStateSnapshot _snapshot;

        public FakeSource(ControllerStateSnapshot snapshot) => _snapshot = snapshot;

        public string DeviceId => "fake";

        public bool IsConnected => true;

        public bool TryPoll(ControllerState state)
        {
            Array.Copy(_snapshot.Buttons, state.Buttons, state.Buttons.Length);
            Array.Copy(_snapshot.Axes, state.Axes, state.Axes.Length);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class NullSink : IOutputSink
    {
        public void Submit(OutputIntent intent)
        {
        }

        public void ReleaseAll()
        {
        }

        public void Dispose()
        {
        }
    }
}
