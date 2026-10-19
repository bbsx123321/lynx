using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PadShift.Api;

/// <summary>
/// 版本/协议路由，以及「文档里有、但 PadShift 没有对应 IRemapHost 成员」的兜底：
/// 统一 404 + JSON 字符串（每个路由只记一次日志）。
/// </summary>
public static class MetaRoutes
{
    public const string ProtocolVersion = "v2.6";

    private static readonly ConcurrentDictionary<string, byte> Reported = new(StringComparer.OrdinalIgnoreCase);

    public static void Map(WebApplication app)
    {
        app.MapGet("/v2.6/Version/GetVersion", () => ApiResults.Json(new VersionInfoDto(ProtocolVersion)));

        // 协议版本按字面量返回（the reference tool 的 Constants.VERSION_PROTOCOL = "v2.6"）。
        app.MapGet("/v2.6/Info/GetProtocolVersion", () => ApiResults.Json(ProtocolVersion));

        // the reference config tool 的真实版本路由没有 /v2.6 前缀（docs/05-api.md §2 最后一行）。
        app.MapGet("/Version", () => ApiResults.Json(new VersionInfoDto(ProtocolVersion)));

        // 端点路由跑完后仍没匹配到 endpoint → 404。放在 Use 里可以补上 JSON 字符串错误体并记一次日志。
        app.Use(async (HttpContext context, RequestDelegate next) => await NotFoundAsync(context, next));
    }

    private static async Task NotFoundAsync(HttpContext context, RequestDelegate next)
    {
        await next(context);

        if (context.GetEndpoint() is not null
            || context.Response.StatusCode != StatusCodes.Status404NotFound
            || context.Response.HasStarted)
        {
            return;
        }

        var route = $"{context.Request.Method} {context.Request.Path.Value}";
        if (Reported.TryAdd(route, 0))
        {
            context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("PadShift.Api.Routes")
                .LogWarning(
                    "Route {Route} has no 1:1 IRemapHost mapping in PadShift; answering 404 (logged once per route).",
                    route);
        }

        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(
            JsonSerializer.Serialize($"No PadShift route for {route}.", ApiJson.Options),
            context.RequestAborted);
    }
}
