using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using PadShift.Core.Host;

namespace PadShift.Api;

/// <summary>
/// <c>GET /v2.6/Events</c>：SSE。帧格式与 the v2.6 protocol 相同（<c>event: &lt;EventName&gt;</c> +
/// <c>data: &lt;PascalCase JSON&gt;</c>），60 s 心跳。每个订阅者一个有界队列：
/// 客户端读不动就断开它，绝不阻塞 <see cref="IRemapHost.Event"/> 的其它订阅者。
/// </summary>
public static class SseRoutes
{
    private const string RoutePath = "/v2.6/Events";
    private const int SubscriberQueueCapacity = 256;

    private static readonly object HeartbeatPayload = new();

    public static void Map(WebApplication app) => app.MapGet(RoutePath, HandleAsync);

    private static async Task HandleAsync(
        HttpContext context,
        IRemapHost host,
        ApiOptions options,
        ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger("PadShift.Api.Events");
        var cancellation = context.RequestAborted;
        var response = context.Response;

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";

        var subscriber = new Subscriber();
        void OnHostEvent(object? sender, HostEvent hostEvent) => subscriber.Push(hostEvent);

        host.Event += OnHostEvent;
        try
        {
            // 注释行让客户端/代理立刻拿到响应头。
            await WriteAsync(response, ": padshift events stream\n\n", cancellation);

            while (!cancellation.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                wait.CancelAfter(TimeSpan.FromSeconds(options.SseHeartbeatSeconds));

                try
                {
                    var hostEvent = await subscriber.ReadAsync(wait.Token);
                    var (eventName, payload) = Describe(hostEvent);
                    await WriteAsync(response, Format(eventName, payload), cancellation);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    await WriteAsync(response, Format("Heartbeat", HeartbeatPayload), cancellation);
                }
            }
        }
        catch (ChannelClosedException)
        {
            // 队列写满（慢客户端）或流已结束：直接断开这个订阅者。
            log.LogDebug("SSE subscriber dropped: it could not keep up with IRemapHost.Event.");
        }
        catch (OperationCanceledException)
        {
            // 客户端主动断开，正常收尾。
        }
        catch (IOException exception)
        {
            log.LogDebug(exception, "SSE client connection failed.");
        }
        finally
        {
            host.Event -= OnHostEvent;
            subscriber.Dispose();
        }
    }

    private static (string Name, object Payload) Describe(HostEvent hostEvent) => hostEvent switch
    {
        ControllerConnectedEvent connected => (
            "ControllerConnected",
            new ControllerConnectedPayload(ControllerMapping.ToListDto(connected.Controller))),
        ControllerDisconnectedEvent disconnected => (
            "ControllerDisconnected",
            new ControllerDisconnectedPayload(disconnected.ControllerId)),
        RemapStateChangedEvent remap => (
            "RemapStateChanged",
            new RemapStateChangedPayload(remap.ControllerId, remap.State)),
        SlotChangedEvent slot => (
            "SlotChanged",
            new SlotChangedPayload(slot.ControllerId, SlotCodec.TryFromIndex(slot.Slot))),
        ShiftChangedEvent shift => (
            "ShiftChanged",
            new ShiftChangedPayload(shift.ControllerId, shift.Shift, shift.Name)),
        ConfigAppliedEvent applied => (
            "ConfigApplied",
            new ConfigAppliedPayload(applied.ControllerId, applied.ConfigPath, SlotCodec.TryFromIndex(applied.Slot))),
        SlotClearedEvent cleared => (
            "SlotCleared",
            new SlotClearedPayload(cleared.ControllerId, SlotCodec.TryFromIndex(cleared.Slot))),
        BatteryLevelChangedEvent battery => (
            "BatteryLevelChanged",
            new BatteryLevelChangedPayload(battery.ControllerId, battery.Percent)),
        // 未映射的 HostEvent 用类型名兜底，仍按运行时类型序列化，不丢字段。
        _ => (hostEvent.GetType().Name, hostEvent),
    };

    private static string Format(string eventName, object payload)
    {
        var json = JsonSerializer.Serialize(payload, payload.GetType(), ApiJson.Options);
        return $"event: {eventName}\ndata: {json}\n\n";
    }

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken cancellationToken)
    {
        await response.WriteAsync(text, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>一个有界事件队列；满了就 TryComplete，读循环收到 ChannelClosedException 后退出。</summary>
    private sealed class Subscriber : IDisposable
    {
        private readonly Channel<HostEvent> _channel = Channel.CreateBounded<HostEvent>(
            new BoundedChannelOptions(SubscriberQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });

        public void Push(HostEvent hostEvent)
        {
            if (!_channel.Writer.TryWrite(hostEvent))
            {
                _channel.Writer.TryComplete();
            }
        }

        public ValueTask<HostEvent> ReadAsync(CancellationToken cancellationToken) =>
            _channel.Reader.ReadAsync(cancellationToken);

        public void Dispose() => _channel.Writer.TryComplete();
    }
}
