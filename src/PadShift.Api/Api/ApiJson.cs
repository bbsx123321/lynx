using System.Text.Json;
using System.Text.Json.Serialization;

namespace PadShift.Api;

/// <summary>
/// 与 the v2.6 wire protocol 一致的线上序列化约定（见 docs/05-api.md §5）：
/// 服务端 <c>PropertyNamingPolicy = null</c> → PascalCase；枚举按名字发字符串；
/// 错误体是 JSON 字符串（不是对象）。
/// </summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            // 关键：the v2.6 wire protocol 用 null 策略，属性名与 C# 属性名一致（PascalCase）。
            PropertyNamingPolicy = null,
            DictionaryKeyPolicy = null,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            // PadShift 知道多少发多少，不知道的字段不写 null 占位。
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // "RemapOn"、"Slot1"、"Both"……全部发名字，和 the v2.6 wire protocol 的 JsonStringEnumConverter 一致。
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>统一响应helper：成功是 200 + JSON；失败是 JSON 字符串 + 非 200 状态码。</summary>
public static class ApiResults
{
    public static IResult Json(object? value) => Results.Json(value, ApiJson.Options);

    public static IResult Error(int statusCode, string message) =>
        Results.Json(message, ApiJson.Options, statusCode: statusCode);
}
