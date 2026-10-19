using System.Text.Json;
using System.Text.Json.Nodes;
using PadShift.App.Infrastructure;
using PadShift.Core.Profile;

namespace PadShift.App.Services;

/// <summary>
/// Writes the calibrated zero-bias into a profile as
/// <c>devices.hardware[].default.gyroBias</c>. <see cref="HardwareDevice.Default"/> is kept as raw
/// JSON by the Core model (it acts as that subtree's "Extra" bag), so the original object is parsed
/// into a <see cref="JsonObject"/>, one member is added and the whole object is written back —
/// unknown fields survive byte-for-byte semantically.
/// </summary>
public static class GyroCalibrationService
{
    public const string BiasPropertyName = "gyroBias";

    /// <summary>Loads, modifies and saves <paramref name="profilePath"/>; returns false with a reason.</summary>
    public static bool TryWriteBias(string? profilePath, long? deviceId, GyroBias bias, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(profilePath))
        {
            error = "请先从配置库加载一个配置，再写回陀螺仪校准";
            return false;
        }

        try
        {
            GamepadProfile profile = ProfileIo.Load(profilePath);
            if (!ApplyToProfile(profile, deviceId, bias, out error))
            {
                return false;
            }

            ProfileIo.Save(profilePath, profile);
            return true;
        }
        catch (Exception ex)
        {
            error = "写回配置失败: " + ex.Message;
            return false;
        }
    }

    /// <summary>Adds the bias to the in-memory profile (exposed for tests and for unsaved profiles).</summary>
    public static bool ApplyToProfile(GamepadProfile profile, long? deviceId, GyroBias bias, out string? error)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(bias);
        error = null;

        HardwareDevice? device = SelectDevice(profile, deviceId);
        if (device is null)
        {
            error = "该配置没有 devices.hardware[] 设备，无法写入陀螺仪校准";
            return false;
        }

        JsonObject target = BuildDefaultObject(device.Default, out bool preserved);
        if (!preserved)
        {
            error = "devices.hardware[].default 不是 JSON 对象；为避免破坏原文件，已取消写入";
            return false;
        }

        target[BiasPropertyName] = new JsonObject
        {
            ["x"] = bias.X,
            ["y"] = bias.Y,
            ["z"] = bias.Z,
            ["driftX"] = bias.DriftX,
            ["driftY"] = bias.DriftY,
            ["driftZ"] = bias.DriftZ,
            ["sampleCount"] = bias.SampleCount,
            ["capturedAt"] = (bias.CapturedAt == default ? DateTimeOffset.UtcNow : bias.CapturedAt).ToString("O"),
            ["source"] = string.IsNullOrWhiteSpace(bias.Source) ? "PadShift" : bias.Source,
        };

        device.Default = JsonSerializer.SerializeToElement(target, ProfileIo.Options);
        return true;
    }

    /// <summary>Reads a previously written bias back (null when absent or malformed).</summary>
    public static GyroBias? ReadBias(GamepadProfile profile, long? deviceId = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        HardwareDevice? device = SelectDevice(profile, deviceId);
        if (device?.Default is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        try
        {
            JsonNode? node = JsonNode.Parse(element.GetRawText());
            if (node is not JsonObject root || root[BiasPropertyName] is not JsonObject bias)
            {
                return null;
            }

            return new GyroBias
            {
                X = ReadDouble(bias, "x"),
                Y = ReadDouble(bias, "y"),
                Z = ReadDouble(bias, "z"),
                DriftX = ReadDouble(bias, "driftX"),
                DriftY = ReadDouble(bias, "driftY"),
                DriftZ = ReadDouble(bias, "driftZ"),
                SampleCount = (int)ReadDouble(bias, "sampleCount"),
                CapturedAt = DateTimeOffset.TryParse(bias["capturedAt"]?.GetValue<string>(), out DateTimeOffset at)
                    ? at
                    : default,
                Source = bias["source"]?.GetValue<string>() ?? "PadShift",
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static HardwareDevice? SelectDevice(GamepadProfile profile, long? deviceId)
    {
        List<HardwareDevice>? hardware = profile.Devices?.Hardware;
        if (hardware is not { Count: > 0 })
        {
            return null;
        }

        if (deviceId is { } wanted)
        {
            foreach (HardwareDevice candidate in hardware)
            {
                if (candidate.Id == wanted)
                {
                    return candidate;
                }
            }
        }

        foreach (HardwareDevice candidate in hardware)
        {
            if (string.Equals(candidate.Name, "gamepad", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return hardware[0];
    }

    private static JsonObject BuildDefaultObject(JsonElement? existing, out bool preserved)
    {
        preserved = true;
        if (existing is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new JsonObject();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            preserved = false;
            return new JsonObject();
        }

        return JsonNode.Parse(element.GetRawText()) as JsonObject ?? new JsonObject();
    }

    private static double ReadDouble(JsonObject obj, string name)
    {
        JsonNode? node = obj[name];
        if (node is null)
        {
            return 0;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.Number => node.GetValue<double>(),
            JsonValueKind.String => double.TryParse(node.GetValue<string>(), out double parsed) ? parsed : 0,
            _ => 0,
        };
    }
}
