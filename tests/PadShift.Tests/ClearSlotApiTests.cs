using System;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace PadShift.Tests;

/// <summary>End-to-end ClearSlot route against the standalone API's in-memory host.</summary>
public class ClearSlotApiTests
{
    private const string Prefix = "/v2.6/ControllerService";

    [Fact]
    public async Task ClearSlot_Removes_The_Applied_Config_And_Rejects_Unknown_Slots()
    {
        using var factory = new WebApplicationFactory<Program>();
        using HttpClient client = factory.CreateClient();
        Guid controllerId = await GetControllerIdAsync(client);

        string path = Path.Combine(Path.GetTempPath(), "padshift-clearslot-" + Guid.NewGuid().ToString("N") + ".rewasd");
        await File.WriteAllTextAsync(path, "{}");

        try
        {
            using var apply = await client.PostAsync(
                $"{Prefix}/ApplyConfig",
                JsonContent.Create(new { ControllerId = controllerId, ConfigPath = path, Slot = "Slot1" }));
            Assert.Equal(HttpStatusCode.OK, apply.StatusCode);

            using var clear = await client.PostAsync(
                $"{Prefix}/ClearSlot",
                JsonContent.Create(new { ControllerId = controllerId, Slot = "Slot1" }));
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);

            // The slot no longer has a config: AppliedConfigs answers 204.
            using var applied = await client.GetAsync($"{Prefix}/AppliedConfigs/{controllerId}?slot=Slot1");
            Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);

            // A well-formed but empty slot is "unknown" to ClearSlot.
            using var empty = await client.PostAsync(
                $"{Prefix}/ClearSlot",
                JsonContent.Create(new { ControllerId = controllerId, Slot = "Slot3" }));
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
            Assert.StartsWith("\"", await empty.Content.ReadAsStringAsync());

            // An out-of-range slot is rejected the same way.
            using var bad = await client.PostAsync(
                $"{Prefix}/ClearSlot",
                JsonContent.Create(new { ControllerId = controllerId, Slot = "Slot9" }));
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.StartsWith("\"", await bad.Content.ReadAsStringAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<Guid> GetControllerIdAsync(HttpClient client)
    {
        using var response = await client.GetAsync($"{Prefix}/Controllers");
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement device = document.RootElement[0].GetProperty("Controllers")[0];
        return device.GetProperty("Guid").GetGuid();
    }
}
