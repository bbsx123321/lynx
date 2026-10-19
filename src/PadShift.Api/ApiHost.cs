using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PadShift.Core.Host;

namespace PadShift.Api;

/// <summary>
/// Builds the PadShift HTTP API (the reference tool v2.6 compatible) around an existing
/// <see cref="IRemapHost"/>. The same wiring is used by the standalone
/// <c>PadShift.Api</c> process and by the WPF client, which hosts the API in-process so a
/// running PadShift shows up as a the reference tool-compatible controller service on loopback.
/// </summary>
/// <remarks>
/// The host is never started here unless <c>startHost</c> is set: the WPF client owns the
/// <see cref="IRemapHost"/> lifecycle (it starts it together with the device watcher), while the
/// standalone API starts its in-memory dev host itself.
/// </remarks>
public static class ApiHost
{
    /// <summary>Creates the API application on a new builder that serves <paramref name="host"/>.</summary>
    public static WebApplication Create(IRemapHost host, ApiOptions options) =>
        Create(WebApplication.CreateBuilder(), host, options);

    /// <summary>
    /// Creates the API application on an existing <paramref name="builder"/>. The caller keeps
    /// ownership of the builder (and therefore of its configuration/logging setup), which is how
    /// <c>Program.cs</c> keeps the appsettings.json / command-line behaviour.
    /// </summary>
    /// <param name="builder">Host builder to configure.</param>
    /// <param name="host">Remap host that every route maps onto.</param>
    /// <param name="options">API options (ports, LAN mode, SSE heartbeat).</param>
    /// <param name="startHost">
    /// True when this call owns the host lifecycle (standalone API with the in-memory fake host).
    /// </param>
    public static WebApplication Create(
        WebApplicationBuilder builder,
        IRemapHost host,
        ApiOptions options,
        bool startHost = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);

        // A caller that already registered a host (engine mode) keeps its registration; otherwise
        // the supplied instance becomes the one every route resolves.
        builder.Services.TryAddSingleton(options);
        builder.Services.TryAddSingleton(host);

        // 请求体解析也用同一套约定（PascalCase / 字符串枚举），和 the reference tool 客户端发出来的 JSON 对齐。
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = null;
            json.SerializerOptions.PropertyNameCaseInsensitive = true;
            json.SerializerOptions.NumberHandling =
                System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString;
        });

        // 默认只绑回环；端口被占时顺序回退（Kestrel 自身没有启动失败换端口的能力，所以先探测）。
        PortSelection selection = ApiPortSelector.Select(options);
        builder.WebHost.UseUrls(selection.Url);

        WebApplication app = builder.Build();

        app.Logger.LogInformation(
            "PadShift API remap host: {Mode}",
            startHost
                ? "built-in Dev.FakeRemapHost (standalone testing, in-memory state)"
                : "IRemapHost registered by the application");

        foreach (int conflict in selection.Conflicts)
        {
            app.Logger.LogWarning(
                "PadShift API: port {Port} is already in use; trying the next candidate.",
                conflict);
        }

        app.Logger.LogInformation("PadShift API bound URL: {Url}", selection.Url);

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            try
            {
                var urls = app.Urls;
                if (urls.Count > 0)
                {
                    app.Logger.LogInformation("PadShift API listening on {Urls}", string.Join(", ", urls));
                }
            }
            catch (Exception exception)
            {
                // TestServer 等宿主可能没有 IServerAddressesFeature；绑定 URL 已经在上一条日志里。
                app.Logger.LogDebug(exception, "Could not read the bound server addresses.");
            }
        });

        if (startHost)
        {
            host.Start();
        }

        app.UsePadShiftToken(options);

        ControllerServiceRoutes.Map(app);
        SseRoutes.Map(app);
        MetaRoutes.Map(app);

        return app;
    }

    /// <summary>Creates and runs the API on a fresh host; completes when the application stops.</summary>
    public static Task RunAsync(IRemapHost host, ApiOptions options, CancellationToken cancellationToken = default) =>
        RunAsync(WebApplication.CreateBuilder(), host, options, startHost: true, cancellationToken);

    /// <summary>Creates and runs the API on <paramref name="builder"/>; completes when it stops.</summary>
    public static async Task RunAsync(
        WebApplicationBuilder builder,
        IRemapHost host,
        ApiOptions options,
        bool startHost = true,
        CancellationToken cancellationToken = default)
    {
        WebApplication app = Create(builder, host, options, startHost);
        try
        {
            await app.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
