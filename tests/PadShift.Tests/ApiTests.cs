using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace PadShift.Tests;

/// <summary>
/// PadShift.Api 的端到端测试：WebApplicationFactory 起 TestServer，走真实的 Program 装配
/// （含 Dev.FakeRemapHost 兜底），不占端口、不发真实网络请求。
/// </summary>
public sealed class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Controllers_Returns_PascalCase_Json_With_DataType()
    {
        using var response = await _client.GetAsync("/v2.6/ControllerService/Controllers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.True(root.GetArrayLength() > 0);

        var item = root[0];
        Assert.Equal("PeripheralController", item.GetProperty("DataType").GetString());

        // PascalCase：没有 camelCase 镜像字段。
        Assert.False(item.TryGetProperty("dataType", out _));

        var devices = item.GetProperty("Controllers");
        Assert.Equal(JsonValueKind.Array, devices.ValueKind);
        Assert.True(devices.GetArrayLength() > 0);

        var device = devices[0];
        Assert.Equal(JsonValueKind.String, device.GetProperty("ControllerType").ValueKind);
        Assert.Equal(JsonValueKind.String, device.GetProperty("ConnectionType").ValueKind);
        Assert.Equal(JsonValueKind.Number, device.GetProperty("VendorId").ValueKind);
        Assert.Equal(JsonValueKind.Object, device.GetProperty("ManufacturerInfo").ValueKind);
    }

    [Fact]
    public async Task Enable_And_Disable_Change_RemapState()
    {
        var controllerId = await GetControllerIdAsync();

        using var enable = await _client.PostAsync(
            "/v2.6/ControllerService/EnableRemap",
            JsonContent.Create(new { Guid = controllerId, RemapNoToggled = true }));
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

        Assert.Equal("RemapOn", await GetJsonStringAsync($"/v2.6/ControllerService/RemapState/{controllerId}"));

        using var disable = await _client.PostAsync(
            "/v2.6/ControllerService/DisableRemap",
            JsonContent.Create(new { Guid = controllerId }));
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        Assert.Equal("RemapOff", await GetJsonStringAsync($"/v2.6/ControllerService/RemapState/{controllerId}"));
    }

    [Fact]
    public async Task SelectSlot_Then_CurrentSlot_RoundTrips()
    {
        var controllerId = await GetControllerIdAsync();

        using var select = await _client.PostAsync(
            "/v2.6/ControllerService/SelectSlot",
            JsonContent.Create(new { ControllerId = controllerId, Slot = "Slot3" }));
        Assert.Equal(HttpStatusCode.OK, select.StatusCode);
        Assert.True(await select.Content.ReadFromJsonAsync<bool>());

        Assert.Equal("Slot3", await GetJsonStringAsync($"/v2.6/ControllerService/CurrentSlot/{controllerId}"));

        // FakeRemapHost 没有给 Slot3 应用过配置 → 204。
        using var applied = await _client.GetAsync($"/v2.6/ControllerService/AppliedConfigs/{controllerId}?slot=Slot3");
        Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);
    }

    [Fact]
    public async Task Unknown_Route_Returns_404_With_Json_String_Body()
    {
        using var response = await _client.GetAsync("/v2.6/ControllerService/DoesNotExist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("\"", body);
    }

    private async Task<Guid> GetControllerIdAsync()
    {
        using var response = await _client.GetAsync("/v2.6/ControllerService/Controllers");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var device = document.RootElement[0].GetProperty("Controllers")[0];
        return device.GetProperty("Guid").GetGuid();
    }

    private async Task<string?> GetJsonStringAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetString();
    }
}
