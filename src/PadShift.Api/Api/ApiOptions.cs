using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PadShift.Api;

/// <summary>
/// <c>PadShift:Api</c> 配置节。默认只绑回环 127.0.0.1:35474，端口被占时顺序回退 35475、35476；
/// <see cref="Lan"/> 为 true 时改绑 <c>http://*:port</c>（the reference tool 的 <c>EnabledOverLocal</c> 等价物），
/// 并要求 <c>X-PadShift-Token</c> 头（前提是配置了 <see cref="Token"/>）。
/// </summary>
public sealed class ApiOptions
{
    public const string SectionName = "PadShift:Api";

    public static IReadOnlyList<int> DefaultPorts { get; } = [35474, 35475, 35476];

    /// <summary>候选端口，按顺序探测，取第一个能绑定的。</summary>
    public int[] Ports { get; init; } = [.. DefaultPorts];

    /// <summary>true 时绑定所有网卡；默认 false（仅回环）。</summary>
    public bool Lan { get; init; }

    /// <summary>LAN 模式下的共享令牌；为空表示不鉴权（会打一条警告）。</summary>
    public string? Token { get; init; }

    /// <summary>SSE 心跳间隔（秒）。the reference tool 固定 60 s，这里可配置以便自测。</summary>
    public int SseHeartbeatSeconds { get; init; } = 60;

    public static ApiOptions Load(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var heartbeat = section.GetValue<int?>("SseHeartbeatSeconds") ?? 60;
        var token = section.GetValue<string?>("Token");

        return new ApiOptions
        {
            Ports = ReadPorts(section.GetSection("Ports")),
            Lan = section.GetValue<bool?>("Lan") ?? false,
            Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
            SseHeartbeatSeconds = Math.Clamp(heartbeat, 1, 3600),
        };
    }

    public string BuildUrl(int port) => Lan ? $"http://*:{port}" : $"http://127.0.0.1:{port}";

    /// <summary>同时支持 JSON 数组（<c>"Ports": [35474, 35475]</c>）和逗号串（<c>"Ports": "35474,35475"</c>）。</summary>
    private static int[] ReadPorts(IConfigurationSection section)
    {
        var raw = section.Value;
        var values = !string.IsNullOrWhiteSpace(raw) && !section.GetChildren().Any()
            ? raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : section.GetChildren()
                .Select(child => child.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray();

        var ports = new List<int>();
        foreach (var value in values)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                && port is > 0 and < 65536
                && !ports.Contains(port))
            {
                ports.Add(port);
            }
        }

        return ports.Count > 0 ? [.. ports] : [.. DefaultPorts];
    }
}

/// <summary>一次端口探测的结果：选中的端口、实际 URL、以及被占用的候选端口。</summary>
public sealed record PortSelection(int Port, string Url, IReadOnlyList<int> Conflicts);

public static class ApiPortSelector
{
    /// <summary>
    /// 顺序探测候选端口。Kestrel 没有「绑定失败自动换端口」的能力，所以先按 Kestrel 的绑定方式
    /// 试绑一次（LAN 模式用 dual-mode 的 IPv6Any，回环模式用 IPv4 loopback）；
    /// 候选端口全被占时退回端口 0（系统分配），保证进程仍能启动。
    /// </summary>
    public static PortSelection Select(ApiOptions options)
    {
        var conflicts = new List<int>();
        foreach (var port in options.Ports)
        {
            if (IsFree(port, options.Lan))
            {
                return new PortSelection(port, options.BuildUrl(port), conflicts);
            }

            conflicts.Add(port);
        }

        return new PortSelection(0, options.BuildUrl(0), conflicts);
    }

    private static bool IsFree(int port, bool lan)
    {
        // 必须用裸 Socket：TcpListener 默认带 SO_REUSEADDR，会把「已被其它服务占用」误判成空闲。
        if (lan)
        {
            using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
            {
                // Kestrel 绑定 http://*:port 用的就是 dual-mode 的 IPv6 通配地址。
                DualMode = true,
            };
            return TryBind(socket, new IPEndPoint(IPAddress.IPv6Any, port));
        }

        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            if (!TryBind(socket, new IPEndPoint(IPAddress.Loopback, port)))
            {
                return false;
            }
        }

        // Windows 允许在某个服务只监听 [::]:port 时再绑定 127.0.0.1:port（本机的 the reference tool
        // Engine 就是这样占着 35474 的）。这种情况下 IPv4 客户端会被我们的进程接管，而
        // localhost 解析到 ::1 的客户端仍会打到 the reference tool。回环上只要任一地址已有服务在听，
        // 就视为冲突并换下一个候选端口。
        return !IsListening(IPAddress.Loopback, port) && !IsListening(IPAddress.IPv6Loopback, port);
    }

    /// <summary>不带副作用的「端口上有服务在听」探测：非阻塞 connect + 150 ms 轮询。</summary>
    private static bool IsListening(IPAddress address, int port)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                Blocking = false,
            };

            try
            {
                socket.Connect(new IPEndPoint(address, port));
                return true;
            }
            catch (SocketException exception) when (exception.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress)
            {
                bool writable = socket.Poll(150_000, SelectMode.SelectWrite);
                bool failed = socket.Poll(0, SelectMode.SelectError);
                return writable && !failed;
            }
            catch (SocketException)
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryBind(Socket socket, IPEndPoint endpoint)
    {
        try
        {
            socket.Bind(endpoint);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
