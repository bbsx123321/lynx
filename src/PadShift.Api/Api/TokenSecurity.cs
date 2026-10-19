using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PadShift.Api;

/// <summary>
/// 可选的共享令牌鉴权。<c>PadShift:Api:Lan=true</c> 且配置了 <c>Token</c> 时，
/// 所有请求必须带 <c>X-PadShift-Token</c>；这是相对 the v2.6 wire protocol（完全无鉴权）的有意改进。
/// 回环模式下不加任何认证，保持与 the v2.6 wire protocol 客户端兼容。
/// </summary>
public static class TokenSecurity
{
    public const string HeaderName = "X-PadShift-Token";

    public static void UsePadShiftToken(this WebApplication app, ApiOptions options)
    {
        if (!options.Lan)
        {
            return;
        }

        if (options.Token is null)
        {
            app.Logger.LogWarning(
                "PadShift:Api:Lan=true but PadShift:Api:Token is empty - the API is reachable from the LAN without authentication.");
            return;
        }

        var expected = Encoding.UTF8.GetBytes(options.Token);

        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            var supplied = context.Request.Headers[HeaderName].ToString();
            if (!Matches(supplied, expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(
                    $"Missing or invalid {HeaderName} header.",
                    ApiJson.Options,
                    context.RequestAborted);
                return;
            }

            await next(context);
        });

        app.Logger.LogInformation(
            "PadShift:Api:Lan=true - every request must carry the {Header} header.",
            HeaderName);
    }

    private static bool Matches(string supplied, byte[] expected)
    {
        var actual = Encoding.UTF8.GetBytes(supplied);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
