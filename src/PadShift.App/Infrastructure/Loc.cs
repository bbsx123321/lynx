using System.Collections;
using System.Windows;

namespace PadShift.App.Infrastructure;

/// <summary>
/// 读取当前语言资源字典里的界面字符串。运行时走 <see cref="Application"/>（语言热切换后即新语言）；
/// 无 Application 的宿主（单元测试等）回退到一次性预解析的 zh-CN 字符串表。缺失的 key 原样返回，
/// 便于在 UI 上直接发现漏翻；带 {0}/{1} 的消息用 <see cref="F"/> 格式化。
/// </summary>
public static class Loc
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _headlessTable;

    public static string T(string key)
    {
        Application? app = Application.Current;
        if (app is not null)
        {
            return app.TryFindResource(key) as string ?? key;
        }

        return Headless(key);
    }

    /// <summary>
    /// ResourceDictionary 有线程亲和且内容是惰性 BAML：跨线程并发取值会撕裂内部状态。
    /// 无 Application 的宿主在锁内把整本字典一次性物化成纯字符串表，此后读取线程安全。
    /// </summary>
    private static string Headless(string key)
    {
        lock (Gate)
        {
            if (_headlessTable is null)
            {
                var source = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/PadShift.App;component/Localization/Strings.zh-CN.xaml"),
                };
                var table = new Dictionary<string, string>(source.Count, StringComparer.Ordinal);
                foreach (DictionaryEntry entry in source)
                {
                    if (entry.Key is string name && entry.Value is string text)
                    {
                        table[name] = text;
                    }
                }

                _headlessTable = table;
            }

            return _headlessTable.TryGetValue(key, out string? value) ? value : key;
        }
    }

    public static string F(string key, params object[] args)
    {
        string template = T(key);
        try
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, template, args);
        }
        catch (System.FormatException)
        {
            return template;
        }
    }
}
