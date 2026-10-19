using System.Text.Json;
using PadShift.App.Services;
using Xunit;

namespace PadShift.Tests;

/// <summary>
/// 更新检查的纯函数部分：GitHub Release JSON 解析与版本比较。网络路径（CheckAsync 的超时与
/// 限流回退）不做单测——CI 与开发机网络环境不可控。
/// </summary>
public class UpdateServiceTests
{
    [Fact]
    public void Parses_Latest_Release_Json_With_The_Expected_Asset()
    {
        const string json = """
            {
              "tag_name": "v9.9.9",
              "body": "release notes",
              "assets": [
                { "name": "notes.txt", "browser_download_url": "https://example/notes.txt" },
                { "name": "Lynx.exe", "browser_download_url": "https://github.com/bbsx123321/lynx/releases/download/v9.9.9/Lynx.exe" }
              ]
            }
            """;

        UpdateCheckResult? result = UpdateService.Parse(json);
        Assert.True(result is not null);
        Assert.Equal("v9.9.9", result!.Value.TagName);
        Assert.Equal("release notes", result.Value.Notes);
        Assert.Equal("https://github.com/bbsx123321/lynx/releases/download/v9.9.9/Lynx.exe", result.Value.DirectUrl);
        Assert.StartsWith(UpdateService.ProxyPrefix, result.Value.ProxyUrl);
        // 当前版本远低于 9.9.9，必然判定为有更新。
        Assert.True(result.Value.IsNewer);
    }

    [Fact]
    public void Missing_Tag_Or_Asset_Returns_Null()
    {
        Assert.Null(UpdateService.Parse("""{"assets": []}"""));
        Assert.Null(UpdateService.Parse("""{"tag_name": "v1.0.0", "assets": []}"""));
        Assert.Null(UpdateService.Parse("not json"));
    }

    [Theory]
    [InlineData("1.1.0", "v1.2.0", true)]
    [InlineData("1.1.0", "v1.1.0", false)]
    [InlineData("1.1.0", "1.0.9", false)]
    [InlineData("1.1.0", "junk", false)]
    public void Version_Comparison_Ignores_The_V_Prefix_And_Fails_Closed(string current, string tag, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewerVersion(current, tag));
    }
}
