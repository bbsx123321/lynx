using PadShift.Api;
using PadShift.Api.Dev;
using PadShift.Core.Host;

var builder = WebApplication.CreateBuilder(args);

var apiOptions = ApiOptions.Load(builder.Configuration);

// 宿主解析：应用已经把 IRemapHost 注册进容器就用它（引擎模式）；否则用内存 FakeRemapHost，
// 让 API 可以脱离引擎独立启动，方便联调/集成测试。
var hostRegistered = builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IRemapHost));
IRemapHost host = new FakeRemapHost();

// 路由装配 / 端口探测 / 日志都收在 ApiHost 里：独立进程和 WPF 客户端（进程内 API）共用同一份。
ApiHost.RunAsync(builder, host, apiOptions, startHost: !hostRegistered).GetAwaiter().GetResult();

/// <summary>WebApplicationFactory&lt;Program&gt; 需要可访问的入口类型（集成测试用）。</summary>
public partial class Program
{
}
