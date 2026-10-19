using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace PadShift.App.Services;

/// <summary>一次 GitHub Release 检查的结果；<see cref="Parse"/> 是纯函数以便单测。</summary>
public readonly record struct UpdateCheckResult(
    string TagName,
    string AssetUrl,
    string? Notes,
    bool IsNewer)
{
    /// <summary>直链（github.com）。</summary>
    public string DirectUrl => AssetUrl;

    /// <summary>国内加速镜像（gh-proxy.com 前缀）。两者内容一致，浏览器任选其一下载。</summary>
    public string ProxyUrl => UpdateService.ProxyPrefix + AssetUrl;
}

/// <summary>
/// 软件内更新检查：查询 GitHub 最新 Release，比对程序集版本，并给出直链与 gh-proxy 加速
/// 链接。只做"检查 + 打开下载页"，不落地替换自身（单文件自解压 exe 无法覆盖运行中的自己）。
/// </summary>
public static class UpdateService
{
    public const string Repository = "bbsx123321/lynx";

    public const string ProxyPrefix = "https://gh-proxy.com/";

    public static readonly Uri Website = new("https://lynx.bg0.top/");

    private static readonly Uri LatestApi = new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>当前程序集版本（如 "1.1.0"），与 csproj 的 &lt;Version&gt; 一致。</summary>
    public static string CurrentVersion()
    {
        Version v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return v.Build >= 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
    }

    /// <summary>
    /// 查询 GitHub 最新 Release。返回 null 表示网络不可达 / 限流（GitHub 匿名 API 每小时 60 次），
    /// 调用方按"检查失败"提示，绝不能把网络问题当成"已是最新"。
    /// </summary>
    public static async Task<UpdateCheckResult?> CheckAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("PadShift/" + CurrentVersion());
            using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return Parse(json);
        }
        catch (Exception)
        {
            // 超时 / DNS / 代理环境：统一按"检查失败"处理。
            return null;
        }
    }

    /// <summary>解析 releases/latest 的 JSON；没有下载资产或版本不可比时返回 null。</summary>
    public static UpdateCheckResult? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            string tag = root.TryGetProperty("tag_name", out JsonElement tagElement)
                ? tagElement.GetString() ?? ""
                : "";
            if (tag.Length == 0)
            {
                return null;
            }

            string? url = null;
            if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out JsonElement name) &&
                        name.GetString() == "Lynx.exe" &&
                        asset.TryGetProperty("browser_download_url", out JsonElement download))
                    {
                        url = download.GetString();
                        break;
                    }
                }
            }

            if (url is null)
            {
                return null;
            }

            string? notes = root.TryGetProperty("body", out JsonElement body)
                ? body.GetString()
                : null;

            return new UpdateCheckResult(tag, url, notes, IsNewerVersion(CurrentVersion(), tag));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>纯函数："v1.2.0" 对 "1.1.0" 返回 true；解析失败按无更新处理。</summary>
    public static bool IsNewerVersion(string current, string tag)
    {
        string cleaned = tag.TrimStart('v', 'V');
        return Version.TryParse(cleaned, out Version? remote)
            && Version.TryParse(current, out Version? local)
            && remote > local;
    }

    /// <summary>在默认浏览器打开（官网 / 下载页）；失败静默，浏览器打开失败无善后可做。</summary>
    public static void OpenInBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true });
        }
        catch (Exception)
        {
            // 无默认浏览器 / 关联被破坏：不打断调用方。
        }
    }
}
