using System.Globalization;

namespace PadShift.Cli;

/// <summary>
/// Hand-rolled argument parser (no third-party CLI framework). Supports both
/// <c>--name value</c> and <c>--name=value</c>; verbs and option names are case-insensitive.
/// </summary>
public static class CliParser
{
    public const int MinSlot = 1;
    public const int MaxSlot = 4;
    public const int DefaultSlot = 1;

    public static CliParseResult Parse(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
        {
            return CliParseResult.Ok(new CliCommand(CliVerb.Help));
        }

        CliVerb? verb = ParseVerb(args[0]);
        if (verb is null)
        {
            return CliParseResult.Fail($"未知命令“{args[0]}”。可用命令：apply、select_slot、clear_slot、remap、version、help。");
        }

        string? config = null;
        int? slot = null;
        Guid? controller = null;
        int? port = null;
        bool? remapOn = null;

        for (int i = 1; i < args.Count; i++)
        {
            string token = args[i];
            if (string.IsNullOrWhiteSpace(token))
            {
                return CliParseResult.Fail("参数不能为空。");
            }

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                return CliParseResult.Fail($"无法识别的参数“{token}”；选项必须以 -- 开头。");
            }

            string name = token;
            string? inline = null;
            int equals = token.IndexOf('=');
            if (equals >= 0)
            {
                name = token[..equals];
                inline = token[(equals + 1)..];
            }

            switch (name.ToLowerInvariant())
            {
                case "--config":
                    if (config is not null)
                    {
                        return CliParseResult.Fail("--config 只能指定一次。");
                    }

                    if (!TryTakeValue(args, ref i, inline, out string configValue))
                    {
                        return CliParseResult.Fail("--config 需要一个文件路径。");
                    }

                    config = configValue;
                    break;

                case "--slot":
                    if (slot is not null)
                    {
                        return CliParseResult.Fail("--slot 只能指定一次。");
                    }

                    if (!TryTakeValue(args, ref i, inline, out string slotValue)
                        || !int.TryParse(slotValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slotNumber))
                    {
                        return CliParseResult.Fail("--slot 需要一个 1–4 的槽位号。");
                    }

                    slot = slotNumber;
                    break;

                case "--controller":
                    if (controller is not null)
                    {
                        return CliParseResult.Fail("--controller 只能指定一次。");
                    }

                    if (!TryTakeValue(args, ref i, inline, out string controllerValue))
                    {
                        return CliParseResult.Fail("--controller 需要一个控制器 GUID。");
                    }

                    if (!Guid.TryParse(controllerValue, out Guid controllerId))
                    {
                        return CliParseResult.Fail($"“{controllerValue}”不是合法的控制器 GUID。");
                    }

                    controller = controllerId;
                    break;

                case "--port":
                    if (port is not null)
                    {
                        return CliParseResult.Fail("--port 只能指定一次。");
                    }

                    if (!TryTakeValue(args, ref i, inline, out string portValue)
                        || !int.TryParse(portValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int portNumber)
                        || portNumber is < 1 or > 65535)
                    {
                        return CliParseResult.Fail("--port 需要一个 1–65535 的端口号。");
                    }

                    port = portNumber;
                    break;

                case "--on":
                case "--off":
                    if (inline is not null && inline.Length > 0)
                    {
                        return CliParseResult.Fail($"{name} 不接受值。");
                    }

                    if (remapOn is not null)
                    {
                        return CliParseResult.Fail("--on 与 --off 只能指定其中一个。");
                    }

                    remapOn = name.Equals("--on", StringComparison.OrdinalIgnoreCase);
                    break;

                default:
                    return CliParseResult.Fail($"未知选项“{name}”。");
            }
        }

        return verb.Value switch
        {
            CliVerb.Help => ValidatePassthrough(CliVerb.Help, config, slot, controller, remapOn, port),
            CliVerb.Version => ValidatePassthrough(CliVerb.Version, config, slot, controller, remapOn, port),
            CliVerb.Apply => ValidateApply(config, slot, controller, remapOn, port),
            CliVerb.SelectSlot => ValidateSlotOnly(CliVerb.SelectSlot, config, slot, controller, remapOn, port),
            CliVerb.ClearSlot => ValidateSlotOnly(CliVerb.ClearSlot, config, slot, controller, remapOn, port),
            CliVerb.Remap => ValidateRemap(config, slot, controller, remapOn, port),
            _ => CliParseResult.Fail("未知命令。"),
        };
    }

    private static CliVerb? ParseVerb(string text) => text.Trim().ToLowerInvariant() switch
    {
        "help" => CliVerb.Help,
        "version" => CliVerb.Version,
        "apply" => CliVerb.Apply,
        "select_slot" or "select-slot" => CliVerb.SelectSlot,
        "clear_slot" or "clear-slot" => CliVerb.ClearSlot,
        "remap" => CliVerb.Remap,
        _ => null,
    };

    private static CliParseResult ValidatePassthrough(
        CliVerb verb,
        string? config,
        int? slot,
        Guid? controller,
        bool? remapOn,
        int? port)
    {
        if (config is not null || slot is not null || controller is not null || remapOn is not null)
        {
            return CliParseResult.Fail($"{VerbName(verb)} 不接受除 --port 之外的参数。");
        }

        return CliParseResult.Ok(new CliCommand(verb, Port: port));
    }

    private static CliParseResult ValidateApply(string? config, int? slot, Guid? controller, bool? remapOn, int? port)
    {
        if (string.IsNullOrWhiteSpace(config))
        {
            return CliParseResult.Fail("apply 需要 --config <路径>。");
        }

        if (remapOn is not null)
        {
            return CliParseResult.Fail("apply 不接受 --on/--off。");
        }

        int useSlot = slot ?? DefaultSlot;
        if (!IsSlot(useSlot))
        {
            return CliParseResult.Fail($"槽位必须是 {MinSlot}–{MaxSlot}，收到 {useSlot}。");
        }

        return CliParseResult.Ok(new CliCommand(CliVerb.Apply, config, useSlot, controller, false, port));
    }

    private static CliParseResult ValidateSlotOnly(
        CliVerb verb,
        string? config,
        int? slot,
        Guid? controller,
        bool? remapOn,
        int? port)
    {
        if (config is not null || remapOn is not null)
        {
            return CliParseResult.Fail($"{VerbName(verb)} 不接受 --config/--on/--off。");
        }

        if (slot is not { } useSlot)
        {
            return CliParseResult.Fail($"{VerbName(verb)} 需要 --slot <{MinSlot}–{MaxSlot}>。");
        }

        if (!IsSlot(useSlot))
        {
            return CliParseResult.Fail($"槽位必须是 {MinSlot}–{MaxSlot}，收到 {useSlot}。");
        }

        return CliParseResult.Ok(new CliCommand(verb, Slot: useSlot, Controller: controller, Port: port));
    }

    private static CliParseResult ValidateRemap(string? config, int? slot, Guid? controller, bool? remapOn, int? port)
    {
        if (config is not null || slot is not null)
        {
            return CliParseResult.Fail("remap 不接受 --config/--slot。");
        }

        if (remapOn is not { } on)
        {
            return CliParseResult.Fail("remap 需要 --on 或 --off。");
        }

        return CliParseResult.Ok(new CliCommand(CliVerb.Remap, Controller: controller, RemapOn: on, Port: port));
    }

    private static bool TryTakeValue(IReadOnlyList<string> args, ref int index, string? inline, out string value)
    {
        if (inline is not null)
        {
            value = inline;
            return value.Length > 0;
        }

        if (index + 1 >= args.Count)
        {
            value = string.Empty;
            return false;
        }

        string next = args[index + 1];
        if (next.StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = next;
        return value.Length > 0;
    }

    private static bool IsSlot(int slot) => slot is >= MinSlot and <= MaxSlot;

    private static string VerbName(CliVerb verb) => verb switch
    {
        CliVerb.Apply => "apply",
        CliVerb.SelectSlot => "select_slot",
        CliVerb.ClearSlot => "clear_slot",
        CliVerb.Remap => "remap",
        CliVerb.Version => "version",
        _ => "help",
    };
}
