using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PadShift.Core.Host;

namespace PadShift.Api;

/// <summary>
/// <c>/v2.6/ControllerService/*</c>：与 the v2.6 protocol 的路由、请求体字段、响应形状保持一致的
/// 1:1 <see cref="IRemapHost"/> 映射（见 docs/05-api.md §2）。不在这层做任何映射/持久化。
/// </summary>
public static class ControllerServiceRoutes
{
    private const string Prefix = "/v2.6/ControllerService";

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup(Prefix);

        group.MapGet("/Controllers", GetControllers);
        group.MapPost("/ApplyConfig", ApplyConfigAsync);
        group.MapPost("/EnableRemap", EnableRemap);
        group.MapPost("/DisableRemap", DisableRemap);
        group.MapPost("/SelectSlot", SelectSlot);
        group.MapPost("/SetLayer", SetLayer);
        group.MapPost("/ClearSlot", ClearSlot);
        group.MapGet("/RemapState/{controllerId}", GetRemapState);
        group.MapGet("/CurrentSlot/{controllerId}", GetCurrentSlot);
        group.MapGet("/CurrentLayer/{controllerId}", GetCurrentLayer);
        group.MapGet("/AppliedConfigs/{controllerId}", GetAppliedConfigs);
        group.MapGet("/Shifts/{controllerId}", GetShifts);
        group.MapPost("/Vibrate", Vibrate);
        // the v2.6 protocol 文档里的老名字；和 Vibrate 是同一个语义。
        group.MapPost("/SendGamepadVibration", Vibrate);

        // the v2.6 protocol 的 KeyPressedPoller 在根路径下，不在 ControllerService 分组里。
        app.MapGet("/v2.6/KeyPressedPoller/{controllerId}", KeyPressedPoller);
    }

    private static IResult GetControllers(IRemapHost host)
    {
        var items = new List<PeripheralControllerDto>(host.Controllers.Count);
        foreach (var controller in host.Controllers)
        {
            items.Add(ControllerMapping.ToListDto(host, controller));
        }

        return ApiResults.Json(items);
    }

    private static async Task<IResult> ApplyConfigAsync(ApplyConfigRequest? request, IRemapHost host)
    {
        if (request is null)
        {
            return ApiResults.Error(
                StatusCodes.Status400BadRequest,
                "ApplyConfig requires a JSON body: { ControllerId, ConfigPath, Slot }.");
        }

        var profilePath = request.Path ?? request.ConfigPath;
        if (string.IsNullOrWhiteSpace(profilePath))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "ApplyConfig requires 'Path' (or 'ConfigPath').");
        }

        if (!SlotCodec.TryParse(request.Slot, out var slot))
        {
            return ApiResults.Error(
                StatusCodes.Status422UnprocessableEntity,
                "ApplyConfig requires 'Slot' = Slot1..Slot4 (a numeric slot is 1-based).");
        }

        // the v2.6 protocol 的 ControllerGuid 可为 null（CLI 的 all_gamepad），这里同样表示「所有手柄」。
        var controllerId = request.ControllerGuid ?? request.ControllerId;
        var targets = ResolveTargets(host, controllerId);
        if (targets.Count == 0)
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "No matching controller is connected.");
        }

        foreach (var target in targets)
        {
            ApplyConfigResult result;
            try
            {
                result = await host.ApplyConfigAsync(target.Id, profilePath, slot);
            }
            catch (Exception exception)
            {
                return ApiResults.Error(
                    StatusCodes.Status500InternalServerError,
                    $"ApplyConfig failed for {target.Id}: {exception.Message}");
            }

            if (!result.Success)
            {
                return ApiResults.Error(
                    StatusCodes.Status422UnprocessableEntity,
                    result.Error ?? $"ApplyConfig failed for {target.Id}.");
            }
        }

        // the v2.6 protocol 的 ApplyConfig 语义是「应用配置 + 打开 remap」（响应类型就是 EnableRemapResponse）。
        var enable = host.EnableRemap(controllerId);
        return enable.Success
            ? ApiResults.Json(new EnableRemapResponse(true, true, Array.Empty<object>()))
            : ApiResults.Error(StatusCodes.Status422UnprocessableEntity, enable.Error ?? "EnableRemap after ApplyConfig failed.");
    }

    private static IResult EnableRemap(EnableRemapRequest? request, IRemapHost host)
    {
        // RemapNoToggled 只对 the v2.6 protocol 的「toggle」状态机有意义；PadShift 的 EnableRemap 是幂等的。
        var controllerId = request?.Guid ?? request?.ControllerId;
        if (controllerId is { } id && !ControllerExists(host, id))
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, $"Controller {id} is not connected.");
        }

        var result = host.EnableRemap(controllerId);
        return result.Success
            ? ApiResults.Json(new EnableRemapResponse(true, true, Array.Empty<object>()))
            : ApiResults.Error(StatusCodes.Status422UnprocessableEntity, result.Error ?? "EnableRemap failed.");
    }

    private static IResult DisableRemap(DisableRemapRequest? request, IRemapHost host)
    {
        var controllerId = request?.Guid ?? request?.ControllerId;
        if (controllerId is { } id && !ControllerExists(host, id))
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, $"Controller {id} is not connected.");
        }

        host.DisableRemap(controllerId);
        return Results.Ok();
    }

    private static IResult SelectSlot(SelectSlotRequest? request, IRemapHost host)
    {
        if (request is null)
        {
            return ApiResults.Error(
                StatusCodes.Status400BadRequest,
                "SelectSlot requires a JSON body: { ControllerId, Slot }.");
        }

        var controllerId = request.Guid ?? request.ControllerId;
        if (controllerId is not { } id)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "SelectSlot requires 'ControllerId' (or 'Guid').");
        }

        if (!SlotCodec.TryParse(request.Slot, out var slot))
        {
            return ApiResults.Error(
                StatusCodes.Status422UnprocessableEntity,
                "SelectSlot requires 'Slot' = Slot1..Slot4 (a numeric slot is 1-based).");
        }

        if (!ControllerExists(host, id))
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, $"Controller {id} is not connected.");
        }

        try
        {
            host.SelectSlot(id, slot);
        }
        catch (Exception exception)
        {
            return ApiResults.Error(StatusCodes.Status500InternalServerError, $"SelectSlot failed: {exception.Message}");
        }

        // the v2.6 protocol 这个路由返回 bool。
        return ApiResults.Json(true);
    }

    /// <summary>
    /// PadShift extension: the v2.6 protocol has no SetLayer route (layers change only through profile
    /// shifts), but the radial-menu layer switch and the WPF client both need a host-level call.
    /// </summary>
    private static IResult SetLayer(SetLayerRequest? request, IRemapHost host)
    {
        if (request is null)
        {
            return ApiResults.Error(
                StatusCodes.Status400BadRequest,
                "SetLayer requires a JSON body: { ControllerId, Layer }.");
        }

        var controllerId = request.ControllerId ?? request.Guid;
        if (controllerId is not { } id)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "SetLayer requires 'ControllerId' (or 'Guid').");
        }

        if (request.Layer is not { } layer || layer is < 0 or > 19)
        {
            return ApiResults.Error(
                StatusCodes.Status422UnprocessableEntity,
                "SetLayer requires 'Layer' = 0..19 (0 is the main layer).");
        }

        if (!ControllerExists(host, id))
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, $"Controller {id} is not connected.");
        }

        bool changed;
        try
        {
            changed = host.SetLayer(id, layer);
        }
        catch (Exception exception)
        {
            return ApiResults.Error(StatusCodes.Status500InternalServerError, $"SetLayer failed: {exception.Message}");
        }

        if (!changed)
        {
            return ApiResults.Error(
                StatusCodes.Status422UnprocessableEntity,
                $"The applied profile does not define layer {layer}.");
        }

        return ApiResults.Json(true);
    }

    private static IResult ClearSlot(ClearSlotRequest? request, IRemapHost host)
    {
        if (request is null)
        {
            return ApiResults.Error(
                StatusCodes.Status400BadRequest,
                "ClearSlot requires a JSON body: { ControllerId, Slot }.");
        }

        var controllerId = request.ControllerId ?? request.Guid;
        if (controllerId is not { } id)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "ClearSlot requires 'ControllerId' (or 'Guid').");
        }

        if (!TryParseClearSlot(request, out int slot))
        {
            return ApiResults.Error(
                StatusCodes.Status400BadRequest,
                "ClearSlot requires 'Slot' = Slot1..Slot4 (a numeric slot is 1-based).");
        }

        if (!ControllerExists(host, id))
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, $"Controller {id} is not connected.");
        }

        try
        {
            if (host.GetAppliedConfigs(id, slot).Count == 0)
            {
                return ApiResults.Error(
                    StatusCodes.Status400BadRequest,
                    $"Slot {SlotCodec.FromIndex(slot)} has no applied config for controller {id}.");
            }

            host.ClearSlot(id, slot);
        }
        catch (Exception exception)
        {
            return ApiResults.Error(StatusCodes.Status500InternalServerError, $"ClearSlot failed: {exception.Message}");
        }

        return Results.Ok();
    }

    /// <summary>优先读 <c>Slot</c>；CLI 仍发送 <c>Slots: ["SlotN"]</c>，取数组里第一个可解析项。</summary>
    private static bool TryParseClearSlot(ClearSlotRequest request, out int slot)
    {
        if (SlotCodec.TryParse(request.Slot, out slot))
        {
            return true;
        }

        if (request.Slots is { ValueKind: JsonValueKind.Array } slots)
        {
            foreach (JsonElement item in slots.EnumerateArray())
            {
                if (SlotCodec.TryParse(item, out slot))
                {
                    return true;
                }
            }
        }

        slot = -1;
        return false;
    }

    private static IResult GetRemapState(string controllerId, IRemapHost host)
    {
        if (!TryResolveController(host, controllerId, out var id, out var error))
        {
            return error;
        }

        return ApiResults.Json(host.GetRemapState(id));
    }

    private static IResult GetCurrentSlot(string controllerId, IRemapHost host)
    {
        if (!TryResolveController(host, controllerId, out var id, out var error))
        {
            return error;
        }

        return ApiResults.Json(SlotCodec.FromIndex(host.GetCurrentSlot(id)));
    }

    private static IResult GetCurrentLayer(string controllerId, IRemapHost host)
    {
        if (!TryResolveController(host, controllerId, out var id, out var error))
        {
            return error;
        }

        try
        {
            return ApiResults.Json(host.GetLayer(id));
        }
        catch (Exception exception)
        {
            return ApiResults.Error(StatusCodes.Status500InternalServerError, $"GetLayer failed: {exception.Message}");
        }
    }

    private static IResult GetAppliedConfigs(string controllerId, string? slot, IRemapHost host)
    {
        if (!TryResolveController(host, controllerId, out var id, out var error))
        {
            return error;
        }

        int slotIndex;
        if (string.IsNullOrWhiteSpace(slot))
        {
            // 省略 ?slot= 时按当前槽位查询（the v2.6 protocol 的 ?slot= 必填，这里更宽松）。
            slotIndex = SafeCurrentSlot(host, id);
        }
        else if (!SlotCodec.TryParseName(slot, out slotIndex))
        {
            return ApiResults.Error(
                StatusCodes.Status422UnprocessableEntity,
                $"'{slot}' is not a slot (Slot1..Slot4).");
        }

        IReadOnlyList<string> paths;
        try
        {
            paths = host.GetAppliedConfigs(id, slotIndex);
        }
        catch (Exception exception)
        {
            return ApiResults.Error(
                StatusCodes.Status500InternalServerError,
                $"GetAppliedConfigs failed: {exception.Message}");
        }

        if (paths.Count == 0)
        {
            // the v2.6 protocol 对「该槽位没有用户配置」返回 204。
            return Results.NoContent();
        }

        var items = paths.Select(ControllerMapping.ToAppliedConfigDto).ToArray();
        // 单配置是文档里的对象形状；PadShift 一个槽位可能关联多个路径，此时退化成同形状数组。
        return items.Length == 1 ? ApiResults.Json(items[0]) : ApiResults.Json(items);
    }

    private static IResult GetShifts(string controllerId, IRemapHost host)
    {
        if (!TryResolveController(host, controllerId, out var id, out var error))
        {
            return error;
        }

        try
        {
            return ApiResults.Json(host.GetShifts(id));
        }
        catch (Exception exception)
        {
            return ApiResults.Error(StatusCodes.Status500InternalServerError, $"GetShifts failed: {exception.Message}");
        }
    }

    private static IResult KeyPressedPoller(string controllerId, int? scanCode, IRemapHost host)
    {
        if (!TryResolveController(host, controllerId, out var id, out var error))
        {
            return error;
        }

        if (scanCode is not { } code)
        {
            return ApiResults.Error(
                StatusCodes.Status400BadRequest,
                "KeyPressedPoller requires ?scanCode=<0..65535>.");
        }

        if (code is < 0 or > 0xFFFF)
        {
            return ApiResults.Error(StatusCodes.Status422UnprocessableEntity, "scanCode must be 0..65535.");
        }

        return ApiResults.Json(host.IsKeyPressed(id, code));
    }

    private static IResult Vibrate(VibrateRequest? request, IRemapHost host)
    {
        if (request is null)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Vibrate requires a JSON body.");
        }

        var controllerId = request.ControllerId ?? request.Guid ?? request.GamepadGuid;
        if (controllerId is not { } id)
        {
            return ApiResults.Error(
                StatusCodes.Status400BadRequest,
                "Vibrate requires 'ControllerId' (or 'Guid' / 'GamepadGuid').");
        }

        if (!ControllerExists(host, id))
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, $"Controller {id} is not connected.");
        }

        var duration = request.DurationMs ?? request.Duration ?? 200;
        if (duration is < 1 or > 60000)
        {
            return ApiResults.Error(StatusCodes.Status422UnprocessableEntity, "Duration must be 1..60000 ms.");
        }

        byte left;
        byte right;
        if (request.LeftMotor is not null || request.RightMotor is not null)
        {
            left = request.LeftMotor ?? 0;
            right = request.RightMotor ?? 0;
        }
        else
        {
            var (wantsLeft, wantsRight) = ParseMotors(request.Motors);
            var intensity = (byte)Math.Clamp(request.Intensity ?? 255, 0, 255);
            left = wantsLeft ? intensity : (byte)0;
            right = wantsRight ? intensity : (byte)0;
        }

        try
        {
            host.Vibrate(id, left, right, (ushort)duration);
        }
        catch (Exception exception)
        {
            return ApiResults.Error(StatusCodes.Status500InternalServerError, $"Vibrate failed: {exception.Message}");
        }

        return Results.Ok();
    }

    /// <summary>the v2.6 protocol 的 Motors 是 MotorMask：位 0 = 左、位 1 = 右；也接受 "Left"/"Right"/"Both"/"All"。</summary>
    private static (bool Left, bool Right) ParseMotors(JsonElement? motors)
    {
        if (motors is not { } value)
        {
            return (true, true);
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;
                var left = text.Contains("Left", StringComparison.OrdinalIgnoreCase);
                var right = text.Contains("Right", StringComparison.OrdinalIgnoreCase);
                return left || right ? (left, right) : (true, true);

            case JsonValueKind.Number when value.TryGetInt32(out var mask):
                return mask == 0 ? (true, true) : ((mask & 1) != 0, (mask & 2) != 0);

            default:
                return (true, true);
        }
    }

    private static List<HostController> ResolveTargets(IRemapHost host, Guid? controllerId)
    {
        var controllers = host.Controllers;
        return controllerId is { } id
            ? controllers.Where(controller => controller.Id == id).ToList()
            : controllers.ToList();
    }

    private static bool ControllerExists(IRemapHost host, Guid controllerId) =>
        host.Controllers.Any(controller => controller.Id == controllerId);

    private static bool TryResolveController(
        IRemapHost host,
        string rawId,
        out Guid controllerId,
        [NotNullWhen(false)] out IResult? error)
    {
        error = null;
        if (!Guid.TryParse(rawId, out controllerId))
        {
            error = ApiResults.Error(StatusCodes.Status400BadRequest, $"'{rawId}' is not a controller GUID.");
            return false;
        }

        if (!ControllerExists(host, controllerId))
        {
            error = ApiResults.Error(StatusCodes.Status404NotFound, $"Controller {controllerId} is not connected.");
            return false;
        }

        return true;
    }

    private static int SafeCurrentSlot(IRemapHost host, Guid controllerId)
    {
        try
        {
            return host.GetCurrentSlot(controllerId);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
