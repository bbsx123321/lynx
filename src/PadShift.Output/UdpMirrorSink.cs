using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PadShift.Core;

namespace PadShift.Output;

/// <summary>输出调优参数：引擎节拍与 UDP 镜像（均下次启动生效）。</summary>
/// <param name="TickMs">引擎节拍（ms）：Normal 8 / High 4 / Ultra 2。</param>
/// <param name="UdpEnabled">把每帧输出意图镜像为 UDP JSON 广播。</param>
/// <param name="UdpPort">UDP 目标端口（本机回环）。</param>
public readonly record struct OutputTuning(int TickMs, bool UdpEnabled, int UdpPort, bool MaskPaddles)
{
    public static OutputTuning Default => new(TickMs: 4, UdpEnabled: false, UdpPort: 26760, MaskPaddles: false);

    /// <summary>性能档字符串（settings.json 存储）→ 调优；未知值回退 High（默认节拍）。</summary>
    public static OutputTuning FromPerformance(string? performance, bool udpEnabled, int udpPort)
    {
        int tick = (performance ?? "").Trim().ToLowerInvariant() switch
        {
            "normal" => 8,
            "ultra" => 2,
            _ => 4,
        };
        return new OutputTuning(tick, udpEnabled, Math.Clamp(udpPort, 1, 65535), MaskPaddles: false);
    }
}

/// <summary>
/// 输出镜像：把每帧 <see cref="OutputIntent"/> 以 20 Hz 的 JSON UDP 包发到 127.0.0.1 的目标端口，
/// 供模拟器/可视化等第三方工具消费（同类工具 UDP 输出的等价物）。只读镜像，不影响
/// 真实输出路径；网络异常静默丢弃。
/// </summary>
public sealed class UdpMirrorSink : IOutputSink
{
    private readonly object _gate = new();
    private readonly UdpClient _client;
    private readonly IPEndPoint _target;
    private readonly byte[] _packet = new byte[1024];

    private OutputIntent _last = new();
    private long _lastSendMs;

    public UdpMirrorSink(int port)
    {
        _client = new UdpClient();
        _target = new IPEndPoint(IPAddress.Loopback, Math.Clamp(port, 1, 65535));
    }

    public void Submit(OutputIntent intent)
    {
        lock (_gate)
        {
            CopyInto(intent, _last);
            long now = Environment.TickCount64;
            if (now - _lastSendMs < 50)
            {
                return;
            }

            _lastSendMs = now;
            string json = BuildPacket(_last, now);
            _ = Encoding.UTF8.GetBytes(json, _packet.AsSpan());
            try
            {
                _client.Send(_packet, json.Length, _target);
            }
            catch (Exception)
            {
                // UDP 尽力而为：目标不在线是常态。
            }
        }
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            _last.Clear();
        }
    }

    public void Dispose()
    {
        try
        {
            _client.Dispose();
        }
        catch (Exception)
        {
            // 关闭时的套接字异常无关紧要。
        }
    }

    /// <summary>纯函数：快照 → UDP JSON（camelCase，字段含义与 OutputIntent 一一对应）。</summary>
    public static string BuildPacket(OutputIntent snapshot, long timestampMs)
    {
        int padButtons = 0;
        for (int i = 0; i < snapshot.PadButtons.Length && i < 32; i++)
        {
            if (snapshot.PadButtons[i])
            {
                padButtons |= 1 << i;
            }
        }

        return JsonSerializer.Serialize(new
        {
            t = timestampMs,
            lx = snapshot.PadLeftX,
            ly = snapshot.PadLeftY,
            rx = snapshot.PadRightX,
            ry = snapshot.PadRightY,
            lt = snapshot.PadLeftTrigger,
            rt = snapshot.PadRightTrigger,
            buttons = padButtons,
            mouseButtons = snapshot.MouseButtons,
            dx = snapshot.MouseDeltaX,
            dy = snapshot.MouseDeltaY,
            wheel = snapshot.MouseWheel,
        });
    }

    private static void CopyInto(OutputIntent source, OutputIntent target)
    {
        source.KeyboardKeys.AsSpan().CopyTo(target.KeyboardKeys);
        source.PadButtons.AsSpan().CopyTo(target.PadButtons);
        target.MouseButtons = source.MouseButtons;
        target.MouseDeltaX = source.MouseDeltaX;
        target.MouseDeltaY = source.MouseDeltaY;
        target.MouseWheel = source.MouseWheel;
        target.PadLeftX = source.PadLeftX;
        target.PadLeftY = source.PadLeftY;
        target.PadRightX = source.PadRightX;
        target.PadRightY = source.PadRightY;
        target.PadLeftTrigger = source.PadLeftTrigger;
        target.PadRightTrigger = source.PadRightTrigger;
        target.RumbleLeft = source.RumbleLeft;
        target.RumbleRight = source.RumbleRight;
    }
}

/// <summary>
/// 包装一个真实输出槽并旁路镜像到 <see cref="UdpMirrorSink"/>：引擎只看到一个 IOutputSink，
/// UDP 异常不可能影响键鼠/虚拟手柄输出。
/// </summary>
public sealed class MirroringSink : IOutputSink
{
    private readonly IOutputSink _inner;
    private readonly UdpMirrorSink _mirror;

    public MirroringSink(IOutputSink inner, UdpMirrorSink mirror)
    {
        _inner = inner;
        _mirror = mirror;
    }

    public void Submit(OutputIntent intent)
    {
        _inner.Submit(intent);
        _mirror.Submit(intent);
    }

    public void ReleaseAll()
    {
        _inner.ReleaseAll();
        _mirror.ReleaseAll();
    }

    /// <summary>释放镜像；真实槽不在此释放（CompositeSink 的 ownsSinks 语义保持不变）。</summary>
    public void Dispose() => _mirror.Dispose();
}
