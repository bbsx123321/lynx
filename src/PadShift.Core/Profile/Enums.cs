using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PadShift.Core.Profile;

// 枚举与 .rewasd 文件里的字符串一一对应（大小写/下划线都要精确，见 docs/04-config-schema.md）。
// 没有用 JsonStringEnumConverter 的命名策略，因为存在 "360" 这种非标识符值。

public enum ActivatorType { Single, Long, Double, Triple, Release, Start, ZoneLow, ZoneMed, ZoneHigh }

public enum ActivationMode { HoldUntilRelease, Onetime, Repeat, Toggle, Turbo }

public enum ShiftType { Default, Toggle, Unlock, RadialMenu, UnlockKeyboardOnly, UnlockMouseOnly }

public enum GamepadKind { Ds4, One360, XboxOneBT, NintendoSwitchPro, Ds3, KeyboardAndMouse }

public enum HardwareKind { Multimedia, Standard, SendInput }

public enum StickKind { Left, Mouse, Right, Gyro, Additional }

public enum SideKind { Left, Right, Gyro }

public enum AxisKind { X, Y }

/// <summary>把枚举映射到配置文件里的字面量，双向。</summary>
public sealed class EnumTextConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly (T Value, string Text)[] Map = BuildMap();

    private static (T, string)[] BuildMap()
    {
        var pairs = typeof(T) == typeof(ActivatorType) ? new (T, string)[]
        {
            (Cast(ActivatorType.Single), "single"), (Cast(ActivatorType.Long), "long"),
            (Cast(ActivatorType.Double), "double"), (Cast(ActivatorType.Triple), "triple"),
            (Cast(ActivatorType.Release), "release"), (Cast(ActivatorType.Start), "start"),
            (Cast(ActivatorType.ZoneLow), "zone_low"), (Cast(ActivatorType.ZoneMed), "zone_med"),
            (Cast(ActivatorType.ZoneHigh), "zone_high"),
        } : typeof(T) == typeof(ActivationMode) ? new (T, string)[]
        {
            (Cast(ActivationMode.HoldUntilRelease), "hold_until_release"),
            (Cast(ActivationMode.Onetime), "onetime"),
            (Cast(ActivationMode.Repeat), "repeat"),
            (Cast(ActivationMode.Toggle), "toggle"),
            (Cast(ActivationMode.Turbo), "turbo"),
        } : typeof(T) == typeof(ShiftType) ? new (T, string)[]
        {
            (Cast(ShiftType.Default), "default"), (Cast(ShiftType.Toggle), "toggle"),
            (Cast(ShiftType.Unlock), "unlock"), (Cast(ShiftType.RadialMenu), "radialMenu"),
            (Cast(ShiftType.UnlockKeyboardOnly), "unlockKeyboardOnly"),
            (Cast(ShiftType.UnlockMouseOnly), "unlockMouseOnly"),
        } : typeof(T) == typeof(GamepadKind) ? new (T, string)[]
        {
            (Cast(GamepadKind.Ds4), "ds4"), (Cast(GamepadKind.One360), "360"),
            (Cast(GamepadKind.XboxOneBT), "xboxonebt"), (Cast(GamepadKind.NintendoSwitchPro), "nintendoswitchpro"),
            (Cast(GamepadKind.Ds3), "ds3"), (Cast(GamepadKind.KeyboardAndMouse), "keyboardandmouse"),
        } : typeof(T) == typeof(HardwareKind) ? new (T, string)[]
        {
            (Cast(HardwareKind.Multimedia), "multimedia"), (Cast(HardwareKind.Standard), "standard"),
            (Cast(HardwareKind.SendInput), "send_input"),
        } : typeof(T) == typeof(StickKind) ? new (T, string)[]
        {
            (Cast(StickKind.Left), "left"), (Cast(StickKind.Mouse), "mouse"),
            (Cast(StickKind.Right), "right"), (Cast(StickKind.Gyro), "gyro"),
            (Cast(StickKind.Additional), "additional"),
        } : typeof(T) == typeof(SideKind) ? new (T, string)[]
        {
            (Cast(SideKind.Left), "left"), (Cast(SideKind.Right), "right"), (Cast(SideKind.Gyro), "gyro"),
        } : typeof(T) == typeof(AxisKind) ? new (T, string)[]
        {
            (Cast(AxisKind.X), "x"), (Cast(AxisKind.Y), "y"),
        } : Array.Empty<(T, string)>();

        return pairs;
    }

    private static T Cast<E>(E value) where E : Enum => (T)(object)value;

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            foreach (var (value, text) in Map)
            {
                if (string.Equals(text, s, StringComparison.OrdinalIgnoreCase)) return value;
            }
            // 未知值不抛错：映射成第一个枚举，避免一个未来字段毁掉整个配置。
            if (Map.Length > 0) return Map[0].Value;
            return default;
        }

        // 数值形式（旧文件）直接按底层值转换。
        return (T)Enum.ToObject(typeof(T), reader.GetInt64());
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        foreach (var (candidate, text) in Map)
        {
            if (EqualityComparer<T>.Default.Equals(candidate, value))
            {
                writer.WriteStringValue(text);
                return;
            }
        }

        writer.WriteStringValue(value.ToString().ToLowerInvariant());
    }
}
