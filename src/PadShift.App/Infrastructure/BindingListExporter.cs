using System.Text;
using System.Text.Json;

namespace PadShift.App.Infrastructure;

/// <summary>
/// 把 .rewasd 配置导出为人类可读的映射清单（纯文本，UTF-8）：头部为游戏/描述，正文按
/// 设备 → 层 → 按键列出目标、宏与 Turbo 标记。结构用 JsonDocument 泛化遍历，未知字段原样
/// 跳过，因此对新 schema 前向兼容。
/// </summary>
public static class BindingListExporter
{
    /// <summary>生成映射清单文本；解析失败时抛出（调用方捕获并提示）。</summary>
    public static string Build(string profileJson, string title)
    {
        ArgumentNullException.ThrowIfNull(profileJson);
        using JsonDocument document = JsonDocument.Parse(profileJson);
        JsonElement root = document.RootElement;

        var builder = new StringBuilder();
        builder.AppendLine(title);
        builder.AppendLine(new string('=', title.Length * 2 > 40 ? 80 : 40));
        AppendField(builder, root, "appName");
        AppendField(builder, root, "description");
        AppendProcessNames(builder, root);
        builder.AppendLine();

        if (!root.TryGetProperty("devices", out JsonElement devices) ||
            devices.ValueKind != JsonValueKind.Array ||
            devices.GetArrayLength() == 0)
        {
            builder.AppendLine("(no devices)");
            return builder.ToString();
        }

        int deviceIndex = 0;
        foreach (JsonElement device in devices.EnumerateArray())
        {
            deviceIndex++;
            builder.AppendLine($"── Device {deviceIndex} ──");
            if (!device.TryGetProperty("hardware", out JsonElement hardware) ||
                hardware.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement hw in hardware.EnumerateArray())
            {
                AppendHardware(builder, hw);
            }
        }

        return builder.ToString();
    }

    private static void AppendHardware(StringBuilder builder, JsonElement hardware)
    {
        string type = hardware.TryGetProperty("controllerType", out JsonElement ct) ? ct.GetString() ?? "" : "";
        builder.AppendLine($"  Controller: {type}");
        if (!hardware.TryGetProperty("config", out JsonElement config) ||
            config.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // 主层 = config 根；Shift 层 = config.shifts[]（每个含独立 config 形状）。
        AppendMappingTable(builder, "Main layer", config);

        if (config.TryGetProperty("shifts", out JsonElement shifts) && shifts.ValueKind == JsonValueKind.Array)
        {
            int shift = 1;
            foreach (JsonElement entry in shifts.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object)
                {
                    AppendMappingTable(builder, $"Shift {shift}", entry);
                }

                shift++;
            }
        }
    }

    private static void AppendMappingTable(StringBuilder builder, string heading, JsonElement config)
    {
        builder.AppendLine($"  [{heading}]");
        int rows = 0;
        foreach (JsonProperty pair in config.EnumerateObject())
        {
            if (!pair.Value.ValueKind.Equals(JsonValueKind.Object) ||
                !LooksLikeMapping(pair.Value))
            {
                continue;
            }

            string target = DescribeTarget(pair.Value);
            string extras = DescribeExtras(pair.Value);
            builder.AppendLine($"    {pair.Name}  →  {target}{extras}");
            rows++;
        }

        if (rows == 0)
        {
            builder.AppendLine("    (no bindings)");
        }
    }

    /// <summary>一个映射对象至少要有目标/宏/掩码三类标记之一。</summary>
    private static bool LooksLikeMapping(JsonElement value) =>
        value.TryGetProperty("macro", out _) ||
        value.TryGetProperty("mask", out _) ||
        value.TryGetProperty("gamepad", out _) ||
        value.TryGetProperty("keyboardMapEntry", out _) ||
        value.TryGetProperty("mouseMapEntry", out _) ||
        value.TryGetProperty("gamepadDescription", out _) ||
        value.TryGetProperty("keycodeDescription", out _) ||
        value.TryGetProperty("mouseDescription", out _) ||
        value.TryGetProperty("turbo", out _) ||
        value.TryGetProperty("unmap", out JsonElement unmap) && unmap.ValueKind is JsonValueKind.True;

    private static string DescribeTarget(JsonElement mapping)
    {
        foreach (string key in new[] { "gamepadDescription", "keycodeDescription", "mouseDescription", "description" })
        {
            if (mapping.TryGetProperty(key, out JsonElement element) && element.ValueKind == JsonValueKind.String)
            {
                string? text = element.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        if (mapping.TryGetProperty("macro", out _))
        {
            return "(macro)";
        }

        if (mapping.TryGetProperty("unmap", out JsonElement unmap) && unmap.ValueKind is JsonValueKind.True)
        {
            return "(unmapped)";
        }

        return "(binding)";
    }

    private static string DescribeExtras(JsonElement mapping)
    {
        var extras = new List<string>(3);
        if (mapping.TryGetProperty("turbo", out JsonElement turbo) &&
            turbo.ValueKind is JsonValueKind.True or JsonValueKind.Number or JsonValueKind.String)
        {
            extras.Add("Turbo");
        }

        if (mapping.TryGetProperty("toggle", out JsonElement toggle) &&
            toggle.ValueKind is JsonValueKind.True)
        {
            extras.Add("Toggle");
        }

        if (mapping.TryGetProperty("macro", out _))
        {
            extras.Add("Macro");
        }

        return extras.Count > 0 ? "  [" + string.Join(", ", extras) + "]" : "";
    }

    private static void AppendField(StringBuilder builder, JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String)
        {
            string? text = element.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                builder.AppendLine($"{name}: {text}");
            }
        }
    }

    private static void AppendProcessNames(StringBuilder builder, JsonElement root)
    {
        if (!root.TryGetProperty("processNames", out JsonElement names) ||
            names.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var parts = new List<string>();
        foreach (JsonElement name in names.EnumerateArray())
        {
            if (name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } value)
            {
                parts.Add(value);
            }
        }

        if (parts.Count > 0)
        {
            builder.AppendLine("processNames: " + string.Join(", ", parts));
        }
    }
}
