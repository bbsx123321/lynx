using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace PadShift.Tests;

/// <summary>结构比较：忽略数字字面量差异（3 vs 3.0）与对象键顺序。</summary>
internal static class JsonDiff
{
    public static List<string> Compare(JsonNode? a, JsonNode? b, string path)
    {
        var diffs = new List<string>();
        Walk(a, b, path, diffs);
        return diffs;
    }

    private static void Walk(JsonNode? a, JsonNode? b, string path, List<string> diffs)
    {
        if (a is null && b is null) return;
        if (a is null || b is null)
        {
            diffs.Add($"{path}: 一侧为 null（a={Describe(a)}, b={Describe(b)}）");
            return;
        }

        if (a is JsonObject oa && b is JsonObject ob)
        {
            foreach (var kv in oa)
            {
                if (!ob.TryGetPropertyValue(kv.Key, out var bv))
                {
                    // 显式 null 与"键不存在"语义等价（原版序列化器会写出 null 键）。
                    if (kv.Value is not null) diffs.Add($"{path}.{kv.Key}: 写出后丢失");
                    continue;
                }

                Walk(kv.Value, bv, $"{path}.{kv.Key}", diffs);
            }

            foreach (var kv in ob)
            {
                if (!oa.ContainsKey(kv.Key) && kv.Value is not null)
                {
                    diffs.Add($"{path}.{kv.Key}: 写出后新增");
                }
            }

            return;
        }

        if (a is JsonArray aa && b is JsonArray ab)
        {
            if (aa.Count != ab.Count)
            {
                diffs.Add($"{path}: 数组长度 {aa.Count} != {ab.Count}");
                return;
            }

            for (var i = 0; i < aa.Count; i++)
            {
                Walk(aa[i], ab[i], $"{path}[{i}]", diffs);
            }

            return;
        }

        if (!ValueEquals(a, b))
        {
            diffs.Add($"{path}: {Describe(a)} != {Describe(b)}");
        }
    }

    private static bool ValueEquals(JsonNode a, JsonNode b)
    {
        if (a is JsonValue va && b is JsonValue vb)
        {
            if (va.TryGetValue<decimal>(out var da) && vb.TryGetValue<decimal>(out var db))
            {
                return da == db;
            }

            if (va.TryGetValue<string>(out var sa) && vb.TryGetValue<string>(out var sb))
            {
                return string.Equals(sa, sb, StringComparison.Ordinal);
            }

            if (va.TryGetValue<bool>(out var ba) && vb.TryGetValue<bool>(out var bb))
            {
                return ba == bb;
            }

            if (va.GetValueKind() == System.Text.Json.JsonValueKind.Null &&
                vb.GetValueKind() == System.Text.Json.JsonValueKind.Null)
            {
                return true;
            }
        }

        return a.ToJsonString() == b.ToJsonString();
    }

    private static string Describe(JsonNode? n) =>
        n is null ? "null" : n.GetValueKind() + ":" + n.ToJsonString();
}
